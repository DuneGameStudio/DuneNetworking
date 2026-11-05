using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuneSession.SocketConnectors;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;
using Xunit;

namespace DuneSession.Tests
{
    public class SessionTests
    {
        // --- Connection Tests ---
        [Fact]
        public void Connection_Construction_NullSocket_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new Connection(null!));
        }

        [Fact]
        public void Connection_IsConnected_TrueAfterConstruction()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            Assert.True(conn.IsConnected);
            conn.Dispose();
        }

        [Fact]
        public void Connection_Transport_NotNull()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            Assert.NotNull(conn.Transport);
            conn.Dispose();
        }

        [Fact]
        public async Task Connection_DisconnectAsync_Idempotent()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);

            conn.DisconnectAsync();
            conn.DisconnectAsync(); // should not throw

            // The FIN settles asynchronously on some platforms (e.g. Windows),
            // so wait for the disconnected state instead of assuming the
            // transition is synchronous.
            await WaitFor(() => !conn.IsConnected, TimeSpan.FromSeconds(5));
            conn.Dispose();
        }

        [Fact]
        public void Connection_DisconnectAsync_WhenDisposed_NoOp()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            conn.Dispose();

            conn.DisconnectAsync(); // should not throw
        }

        [Fact]
        public void Connection_Dispose_Idempotent()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            conn.Dispose();
            conn.Dispose();
        }

        // --- ClientConnector Tests ---
        [Fact]
        public void ClientConnector_Dispose_Idempotent()
        {
            var connector = new ClientConnector();
            connector.Dispose();
            connector.Dispose();
        }

        [Fact]
        public void ClientConnector_ConnectAsync_InvalidAddress_FiresFailure()
        {
            var connector = new ClientConnector();
            SocketError? error = null;
            connector.OnConnectFailed += e => error = e;

            bool result = connector.ConnectAsync("not-valid-address", 1234);

            Assert.False(result);
            Assert.NotNull(error);
            connector.Dispose();
        }

        [Fact]
        public async Task ClientConnector_ConnectAsync_NonExistentServer_FiresFailure()
        {
            var connector = new ClientConnector();
            var tcs = new TaskCompletionSource<SocketError>();
            connector.OnConnectFailed += e => tcs.TrySetResult(e);

            connector.ConnectAsync("127.0.0.1", 59877);

            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(SocketError.Success, error);
            connector.Dispose();
        }

        [Fact]
        public void ClientConnector_ConnectAsync_WhileConnecting_ReturnsFalse()
        {
            var connector = new ClientConnector();
            // Second call while first is still connecting should return false:
            connector.ConnectAsync("127.0.0.1", 59878);
            bool second = connector.ConnectAsync("127.0.0.1", 59878);
            Assert.False(second);
            connector.Dispose();
        }

        /// <summary>
        /// Verifies that <c>ConnectAsync</c> successfully connects to a listening server
        /// and fires <c>OnConnected</c> with a valid <c>IConnection</c>.
        /// </summary>
        [Fact]
        public async Task ClientConnector_ConnectAsync_ValidServer_ConnectsSuccessfully()
        {
            // Start a real listener
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var connector = new ClientConnector();
            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnConnected += conn => tcs.TrySetResult(conn);

            // Attempt connection
            bool started = connector.ConnectAsync("127.0.0.1", port);
            Assert.True(started);

            // Accept the connection on server side
            var serverSocket = await Task.Run(() => listener.Accept());

            // Wait for OnConnected
            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(connection);
            Assert.True(connection.IsConnected);
            Assert.NotNull(connection.Transport);
            Assert.True(connector.IsConnected);

            // Cleanup
            connection.Dispose();
            serverSocket.Close();
            listener.Close();
            connector.Dispose();
        }

        // --- ServerConnector Tests ---

        [Fact]
        public void ServerConnector_Construction_ZeroBacklog_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerConnector(0));
        }

        [Fact]
        public void ServerConnector_Dispose_Idempotent()
        {
            var connector = new ServerConnector();
            connector.Dispose();
            connector.Dispose();
        }

        [Fact]
        public void ServerConnector_StopListening_Idempotent()
        {
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);
            connector.StopListening();
            connector.StopListening();
            connector.Dispose();
        }

        [Fact]
        public void ServerConnector_AcceptConnection_WhenNotListening_NoOp()
        {
            var connector = new ServerConnector();
            connector.AcceptConnection(); // should not throw when not listening
            connector.Dispose();
        }

        [Fact]
        public void ServerConnector_StopListening_ClosesSocket()
        {
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);
            connector.StopListening();
            Assert.False(connector.IsListening);
            connector.Dispose();
        }

        [Fact]
        public void ServerConnector_StartListening_AlreadyListening_NoOp()
        {
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);
            connector.StartListening("127.0.0.1", 0); // should not throw
            connector.Dispose();
        }

        // --- ServerConnector Integration Tests ---

        [Fact]
        public async Task ServerConnector_AcceptConnection_RealClient_FiresOnConnected()
        {
            // Start a probe listener to get a free port
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => tcs.TrySetResult(conn);
            connector.AcceptConnection();

            // Connect a real client
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(connection);
            Assert.True(connection.IsConnected);

            connection.Dispose();
            client.Close();
            connector.Dispose();
        }

        [Fact]
        public async Task ServerConnector_AcceptConnection_MultipleClients_Sequential()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(2);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var accepted = new System.Collections.Generic.List<IConnection>();
            connector.OnClientConnected += conn => accepted.Add(conn);

            // First client
            var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client1.Connect(new IPEndPoint(IPAddress.Loopback, port));
            connector.AcceptConnection();
            await Task.Delay(100);

            // Second client
            var client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client2.Connect(new IPEndPoint(IPAddress.Loopback, port));
            connector.AcceptConnection();
            await Task.Delay(100);

            Assert.Equal(2, accepted.Count);
            foreach (var conn in accepted)
                conn.Dispose();
            client1.Close();
            client2.Close();
            connector.Dispose();
        }

        [Fact]
        public async Task ServerConnector_AcceptConnection_WithData_ReceiveAndSend()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => tcs.TrySetResult(conn);
            connector.AcceptConnection();

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Set up server receive BEFORE sending data
            var recvTcs = new TaskCompletionSource<Segment>();
            connection.Transport.OnPacketReceived += (t, seg) => recvTcs.TrySetResult(seg);
            connection.Transport.ReceiveAsync();

            // Client sends data to server (with 2-byte length header)
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 3);
            byte[] payload = new byte[] { 0xAA, 0xBB, 0xCC };
            client.Send(header);
            client.Send(payload);

            var segment = await recvTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, segment.Memory.Length);
            Assert.Equal(0xAA, segment.Memory.Span[0]);
            segment.Release();

            connection.Dispose();
            client.Close();
            connector.Dispose();
        }

        [Fact]
        public void ServerConnector_Dispose_WhileListening_CleansUp()
        {
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);
            Assert.True(connector.IsListening);

            connector.Dispose();

            Assert.False(connector.IsListening);
        }

        // --- ClientConnector Integration Tests ---

        [Fact]
        public async Task ClientConnector_Dispose_WithActiveConnection_CleansUp()
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var connector = new ClientConnector();
            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnConnected += conn => tcs.TrySetResult(conn);
            connector.ConnectAsync("127.0.0.1", port);

            var serverSocket = await Task.Run(() => listener.Accept());
            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(connection);

            // Dispose connector while connection is active
            connector.Dispose();

            Assert.False(connector.IsConnected);

            // Liveness pin: the disposed connector must have closed the connection's
            // socket — the peer side must observe termination, not a live socket.
            var readDone = new TaskCompletionSource<bool>();
            _ = Task.Run(() => { try { serverSocket.Receive(new byte[16]); } catch { } readDone.TrySetResult(true); });
            await readDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

            serverSocket.Close();
            listener.Close();
        }

        // --- Additional Connection Tests ---

        [Fact]
        public void Connection_Dispose_DisposesTransport()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            conn.Dispose();

            Assert.True(conn.Transport.IsDisposed);
            pair.Client.Close();
            pair.Server.Close();
        }

        [Fact]
        public async Task Connection_IsConnected_False_AfterDisconnect()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);
            Assert.True(conn.IsConnected);

            conn.DisconnectAsync();

            // After disconnect, IsConnected must become false; the FIN settles
            // asynchronously on some platforms (e.g. Windows), so wait with a
            // bound rather than assuming a synchronous transition.
            await WaitFor(() => !conn.IsConnected, TimeSpan.FromSeconds(5));
            conn.Dispose();
        }

        // --- Concurrency Tests ---

        [Fact]
        public void Connection_DisconnectAsync_RacingWithDispose()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);

            // Unmasked (F3): neither call may throw — an escape faults the task
            // and Wait rethrows it into the test.
            var t1 = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                    conn.DisconnectAsync();
            });
            var t2 = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                    conn.Dispose();
            });
            System.Threading.Tasks.Task.WhenAll(t1, t2).Wait(3000);

            Assert.True(conn.Transport.IsDisposed);
        }

        /// <summary>
        /// Unmasked close-protocol hammer: racing AcceptConnection against
        /// StopListening/Dispose must never let an exception escape. The close holds
        /// for a start that is between claiming and issuing, and a start whose
        /// re-read sees the close bails instead of issuing, so the accept call can
        /// never observe a closed socket. (The version of this test that preceded
        /// the protocol wrapped every call in a try/catch — an escape was
        /// swallowed; it is unmasked now for the same reason the F3 hammer is.)
        /// No accept-failure report may fire on the way down either: a stop is not
        /// an error, and nothing is raised after dispose.
        /// </summary>
        [Fact]
        public async Task ServerConnector_AcceptConnection_RacingStopListening_NoThrowNoLateEvent()
        {
            for (int i = 0; i < 50; i++)
            {
                var connector = new ServerConnector();
                connector.StartListening("127.0.0.1", 0);
                int failures = 0;
                connector.OnAcceptFailed += _ => Interlocked.Increment(ref failures);

                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int k = 0; k < 5; k++)
                        connector.AcceptConnection();
                });
                var t2 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int k = 0; k < 4; k++)
                        connector.StopListening();
                    connector.Dispose();
                });

                // An escape (the removed ObjectDisposedException from the issue)
                // faults a task and rethrows here.
                await System.Threading.Tasks.Task.WhenAll(t1, t2)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                // Let any aborted in-flight accept settle, then assert the
                // contract: nothing armed, nothing fired on the way down.
                await WaitFor(() => !connector.IsAcceptPending, TimeSpan.FromSeconds(5));
                Assert.Equal(0, failures);
            }
        }

        // --- Phase 1: Real Socket Integration Tests (Anti-Cheating) ---

        /// <summary>
        /// Verifies that DisconnectAsync is idempotent and safe to call after
        /// remote FIN has already disconnected the connection.
        /// </summary>
        [Fact]
        public async Task Connection_DisconnectAsync_AfterRemoteFIN_NoOp()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);

            // Simulate remote FIN by closing server side
            pair.Server.Shutdown(SocketShutdown.Both);
            pair.Server.Close();

            // Trigger receive to detect FIN
            var finDetected = false;
            conn.Transport.OnPacketReceiveFailed += (t, e) => { if (e == TransportError.SocketDisconnected) finDetected = true; };
            conn.Transport.ReceiveAsync();
            await WaitFor(() => finDetected, TimeSpan.FromSeconds(5));

            // ACT - Disconnect after already disconnected should not throw
            conn.DisconnectAsync();

            // ASSERT
            Assert.False(conn.IsConnected);
            // Transport still not disposed - only Dispose() does that
            Assert.False(conn.Transport.IsDisposed);
        }

        /// <summary>
        /// Verifies that ClientConnector.ConnectAsync fires OnConnectFailed with
        /// SocketError when given an invalid IP address format (FormatException path).
        /// Covers the FormatException catch block at ClientConnector.cs:82-88.
        /// </summary>
        [Fact]
        public void ClientConnector_ConnectAsync_InvalidAddress_FiresConnectFailed()
        {
            var connector = new ClientConnector();
            SocketError? error = null;
            connector.OnConnectFailed += e => error = e;

            // ACT - Invalid IP format triggers FormatException in IPAddress.Parse
            bool result = connector.ConnectAsync("not-a-valid-ip-address", 1234);

            // ASSERT
            Assert.False(result, "Should return false for invalid address");
            Assert.NotNull(error);
            Assert.Equal(SocketError.SocketError, error.Value); // FormatException maps to SocketError

            connector.Dispose();
        }

        /// <summary>
        /// Verifies that ClientConnector.ConnectAsync fires OnConnectFailed when
        /// connecting to a port with no listening server (SocketException path).
        /// Covers the SocketException catch block at ClientConnector.cs:68-73.
        /// </summary>
        [Fact]
        public async Task ClientConnector_ConnectAsync_RefusedConnection_FiresConnectFailed()
        {
            var connector = new ClientConnector();
            var tcs = new TaskCompletionSource<SocketError>();
            connector.OnConnectFailed += e => tcs.TrySetResult(e);

            // ACT - Connect to closed port (no listener)
            connector.ConnectAsync("127.0.0.1", 59999);

            // ASSERT - Wait for async failure
            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(SocketError.Success, error);
            Assert.True(
                error == SocketError.ConnectionRefused || error == SocketError.TimedOut,
                $"Expected ConnectionRefused or TimedOut, got {error}");

            connector.Dispose();
        }

        /// <summary>
        /// Deterministic pin for the close-protocol bail: an accept start whose
        /// re-read finds the listener already stopped settles silently — no throw,
        /// no accept-failure report (a stop is not an error), nothing left armed.
        /// This is the half of the argument the hammer above exercises statistically.
        /// </summary>
        /// <remarks>
        /// <para>
        /// HISTORICAL NOTE: this position previously held a test that closed the
        /// listening socket from outside the connector and asserted
        /// OnAcceptFailed — it pinned a defensive ObjectDisposedException catch at
        /// the accept call. The catch is removed: for a close the connector itself
        /// issues (StopListening or Dispose), the throw is unreachable by
        /// construction — the close holds for a start between claiming and
        /// issuing, and a start whose re-read sees the close bails instead of
        /// issuing (the pin above, plus the racing hammer). A socket closed from
        /// outside the connector is out of protocol — the connector's flags say
        /// nothing about it — and such a close now escapes the accept call, the
        /// same way the transport's do.
        /// </para>
        /// </remarks>
        [Fact]
        public void ServerConnector_AcceptConnection_AfterStop_SilentBail()
        {
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);
            int failures = 0;
            connector.OnAcceptFailed += _ => Interlocked.Increment(ref failures);

            connector.StopListening();

            // ACT: the claim is taken, then the re-read sees the stop.
            connector.AcceptConnection();

            Assert.Equal(0, failures); // no report — a stop is not an error
            Assert.False(connector.IsAcceptPending); // nothing left armed

            connector.Dispose();
        }

        /// <summary>
        /// Verifies that ServerConnector.ProcessAccept fires OnAcceptFailed when
        /// the accept operation completes with an error (failure branch in ProcessAccept).
        /// Covers the else branch at ServerConnector.cs:149-152.
        /// Note: This is difficult to trigger on loopback without error injection.
        /// </summary>
        [Fact]
        public async Task ServerConnector_ProcessAccept_ErrorCompletion_FiresAcceptFailed()
        {
            // Covers the else branch at ServerConnector.cs:149-152.
            // On loopback, AcceptAsync completes synchronously and succeeds, so the
            // failure branch is unreachable by normal traffic. We drive the *real*
            // private ProcessAccept with the real private acceptEventArgs whose state
            // we set to a failed accept (SocketError != Success, AcceptSocket == null).
            // The real body then fires the real OnAcceptFailed event.
            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", 0);

            var tcs = new TaskCompletionSource<SocketError>();
            connector.OnAcceptFailed += e => tcs.TrySetResult(e);

            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // The real SocketAsyncEventArgs the connector uses for accepts.
            var acceptEA = (SocketAsyncEventArgs)typeof(ServerConnector)
                .GetField("acceptEventArgs", NonPubInstance)!.GetValue(connector)!;

            // The real private ProcessAccept(SocketAsyncEventArgs e) method.
            var processAccept = typeof(ServerConnector)
                .GetMethod("ProcessAccept", NonPubInstance)!;

            // Simulate a completed accept that CARRIED A FAILURE (the genuine else-branch
            // condition at ServerConnector.cs:144). Real field values on the real SAEA.
            acceptEA.SocketError = SocketError.ConnectionReset;
            acceptEA.AcceptSocket = null;

            // Invoke the real method; its real branching logic runs against the state above.
            processAccept.Invoke(connector, new object[] { acceptEA });

            // Observable assertion: the real event carries the real injected error.
            // If the else branch had not run, the event would never fire and this times out.
            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SocketError.ConnectionReset, error);

            connector.Dispose();
        }

        // --- ServerConnector: state machine + accepted-socket leak (N11/N12/N13/N14) ---

        /// <summary>
        /// Live-path pin for the accepted-socket handling across a stop (N11). The
        /// client connects while the accept is armed, then the listener stops. The
        /// accept settles in one of two worlds, and both are asserted:
        ///   - the accept completed before the stop: the connector closed the
        ///     accepted socket itself (or handed it out, in which case the test
        ///     disposes it) and the client observes termination;
        ///   - the stop aborted the accept first: no accepted socket ever
        ///     existed, so the connector held nothing to close, and the client's
        ///     connection is left in the closed listener's backlog by the
        ///     operating system — the test ends it via the client side.
        /// All waits are bounded, so this test cannot hang.
        /// </summary>
        [Fact]
        public async Task ServerConnector_AcceptDuringStop_ClosesAcceptedSocket()
        {
            // Probe for a free port
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => tcs.TrySetResult(conn);
            connector.AcceptConnection();

            // The connector subscribed to the accept event in its constructor, so
            // it processes the outcome before this handler runs — this handler
            // observes the settled state. SocketError (which the connector never
            // touches) distinguishes the worlds: Success means an accepted socket
            // existed; OperationAborted means the stop aborted the accept before
            // it produced one.
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var acceptEventArgs = (SocketAsyncEventArgs)typeof(ServerConnector)
                .GetField("acceptEventArgs", NonPubInstance)!.GetValue(connector)!;
            var acceptOutcome = new TaskCompletionSource<SocketError>();
            acceptEventArgs.Completed += (_, e) => acceptOutcome.TrySetResult(e.SocketError);

            // Client connects while the listener is alive, then the listener stops.
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            connector.StopListening();

            // Closing the listener always settles the accept (completed or
            // aborted), so the outcome always arrives.
            SocketError acceptError = await acceptOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // A delivery (the rare race where the callback won the stop) hands
            // ownership to the test: dispose it, which ends the read below.
            if (tcs.Task.IsCompleted)
                tcs.Task.Result.Dispose();

            var buf = new byte[16];
            var readDone = new TaskCompletionSource<bool>();
            _ = Task.Run(() => { try { client.Receive(buf); } catch { } readDone.TrySetResult(true); });

            var settled = await Task.WhenAny(readDone.Task, Task.Delay(5000));
            if (settled == readDone.Task)
            {
                // The socket the connector held was closed: the client observed
                // termination. (N11 pin for the accept-completed world.)
                Assert.False(connector.IsAcceptPending);
            }
            else
            {
                // The read never terminated: only correct when the stop aborted
                // the accept before it produced a socket — then the connector
                // held nothing, and the client's connection sits in the closed
                // listener's backlog, owned by the operating system. (A Success
                // outcome with a still-open connection would be a leaked
                // accepted socket: the bug this test exists to catch.)
                Assert.Equal(SocketError.OperationAborted, acceptError);
                Assert.False(tcs.Task.IsCompleted);
                Assert.False(connector.IsAcceptPending);
                client.Close();
                await readDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            client.Close();
            connector.Dispose();
        }

        /// <summary>
        /// Deterministic pin for the accepted-socket leak (N11): drives the real
        /// ProcessAccept with a completed, successful accept (live AcceptSocket) after
        /// the listener has stopped — the exact early-return window. The socket must be
        /// disposed, detached from the SAEA, and nothing delivered.
        /// </summary>
        [Fact]
        public void ServerConnector_ProcessAccept_StoppedListener_DisposesAcceptedSocket()
        {
            // Probe for a free port
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var delivered = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => delivered.TrySetResult(conn);

            using var pair = MakeSocketPair();

            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // The real SocketAsyncEventArgs the connector uses for accepts, and the
            // real private ProcessAccept (same idiom as the error-completion test above).
            var acceptEA = (SocketAsyncEventArgs)typeof(ServerConnector)
                .GetField("acceptEventArgs", NonPubInstance)!.GetValue(connector)!;
            var processAccept = typeof(ServerConnector).GetMethod("ProcessAccept", NonPubInstance)!;

            // A completed, successful accept carrying a live socket — then stop the
            // listener so ProcessAccept takes the dead-listener path.
            acceptEA.SocketError = SocketError.Success;
            acceptEA.AcceptSocket = pair.Server;
            connector.StopListening();

            processAccept.Invoke(connector, new object[] { acceptEA });

            Assert.Null(acceptEA.AcceptSocket); // detached from the SAEA
            // ...and the socket is disposed (any operation now throws ODE).
            Assert.Throws<ObjectDisposedException>(() => pair.Server.Send(new byte[1]));
            Assert.False(delivered.Task.IsCompleted); // nothing handed out

            connector.Dispose();
        }

        /// <summary>
        /// Pin for the double-start quirk (N12b): a re-entrant StartListening must be a
        /// no-op that leaves the state intact, so StopListening actually closes and the
        /// port is released. (Previously the re-entrant call cleared the flag, so a
        /// later StopListening never closed and the port stayed bound until Dispose.)
        /// </summary>
        [Fact]
        public void ServerConnector_DoubleStartListening_StopListening_ReleasesPort()
        {
            // Probe for a free port
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);
            connector.StartListening("127.0.0.1", port); // re-entrant: no-op

            Assert.True(connector.IsListening);
            connector.StopListening();
            Assert.False(connector.IsListening);

            // The port must actually be free: a plain bind (no ReuseAddress) on a
            // still-listening port throws.
            var check = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            check.Bind(new IPEndPoint(IPAddress.Loopback, port));
            check.Close();

            connector.Dispose();
        }

        /// <summary>
        /// Pin for the failed-start path (N13): a throwing StartListening must close the
        /// socket (liveness follows the state transition, not the Dispose backstop) and
        /// leave the state Unbound. The failure is forced by binding to a non-local
        /// address (EADDRNOTAVAIL) — deterministic on every platform, unlike an
        /// occupied-port probe (SO_REUSEADDR makes re-bind semantics platform-specific).
        /// </summary>
        [Fact]
        public void ServerConnector_StartListening_Failure_ClosesSocketAndResetsState()
        {
            var connector = new ServerConnector();
            // 192.0.2.1 (TEST-NET-1) is not assigned to any local interface:
            // the Bind itself throws SocketException, exercising the catch path.
            Assert.Throws<SocketException>(() => connector.StartListening("192.0.2.1", 8080));
            Assert.False(connector.IsListening);

            // The connector's socket is closed by the catch — not left open.
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(ServerConnector).GetField("socket", NonPubInstance);
            var internalSocket = (Socket)socketField!.GetValue(connector)!;
            Assert.Throws<ObjectDisposedException>(
                () => internalSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1));

            connector.Dispose(); // clean, idempotent
        }

        /// <summary>
        /// Pin for the no-subscriber hand-off (R2 half): with no OnClientConnected
        /// subscriber, nobody owns the accepted socket — the connector must close it,
        /// and the client must observe termination.
        /// </summary>
        [Fact]
        public async Task ServerConnector_ProcessAccept_NoSubscriber_ClosesAcceptedSocket()
        {
            // Probe for a free port
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            probe.Listen(1);
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);
            // No OnClientConnected subscriber attached.

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            connector.AcceptConnection();

            // The connector created a Connection with no owner and must have disposed
            // it ⇒ the client observes termination.
            var buf = new byte[16];
            var readDone = new TaskCompletionSource<bool>();
            _ = Task.Run(() => { try { client.Receive(buf); } catch { } readDone.TrySetResult(true); });
            await readDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

            client.Close();
            connector.Dispose();
        }

        // --- ServerConnector: IsAcceptPending + dispose protocol (F2) ---

        /// <summary>
        /// F2 protocol pin: IsAcceptPending is false idle, true from arming until the
        /// delivery completes, and false after Dispose.
        /// </summary>
        [Fact]
        public async Task ServerConnector_IsAcceptPending_ReflectsAcceptLifecycle()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);
            Assert.False(connector.IsAcceptPending);

            connector.AcceptConnection();
            Assert.True(connector.IsAcceptPending); // armed, in flight

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => tcs.TrySetResult(conn);

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var delivered = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await WaitFor(() => !connector.IsAcceptPending, TimeSpan.FromSeconds(5));

            connector.StopListening();
            connector.Dispose();
            Assert.False(connector.IsAcceptPending);

            delivered.Dispose();
            client.Close();
        }

        /// <summary>
        /// F2 pin: re-arming while an accept is armed must be a silent no-op (the
        /// ungated second AcceptAsync used to throw an uncaught
        /// InvalidOperationException), and exactly one accept must be armed.
        /// </summary>
        [Fact]
        public async Task ServerConnector_AcceptConnection_ReArmWhilePending_IsNoOp()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            int delivered = 0;
            var tcs = new TaskCompletionSource<bool>();
            connector.OnClientConnected += _ => { delivered++; tcs.TrySetResult(true); };

            connector.AcceptConnection();
            connector.AcceptConnection(); // no-op: the first accept is still armed

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);

            Assert.Equal(1, delivered);
            connector.Dispose();
            client.Close();
        }

        /// <summary>
        /// F2 protocol pin: StopListening aborts the in-flight accept, whose completion
        /// clears the flag — this is what makes the drain-then-dispose wait terminate.
        /// </summary>
        [Fact]
        public async Task ServerConnector_StopListening_PendingAccept_ClearsFlagPromptly()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);
            connector.AcceptConnection();
            Assert.True(connector.IsAcceptPending);

            connector.StopListening(); // closes the listener, aborts the in-flight accept

            await WaitFor(() => !connector.IsAcceptPending, TimeSpan.FromSeconds(5));
            connector.Dispose();
        }

        /// <summary>
        /// F2 pin (S3 safety): a Dispose after a delivered accept must not touch the
        /// handed-off socket — the dying SAEA still references it, and the connector
        /// must leave it to the Connection that owns it.
        /// </summary>
        [Fact]
        public async Task ServerConnector_DisposeAfterDelivery_DoesNotTouchHandedOffSocket()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn => tcs.TrySetResult(conn);
            connector.AcceptConnection();

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitFor(() => !connector.IsAcceptPending, TimeSpan.FromSeconds(5));

            connector.StopListening();
            connector.Dispose(); // the disposed SAEA still references the handed-off socket

            // The app still owns the connection: it must still round-trip data.
            var recvTcs = new TaskCompletionSource<Segment>();
            connection.Transport.OnPacketReceived += (t, seg) => recvTcs.TrySetResult(seg);
            connection.Transport.ReceiveAsync();

            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 3);
            byte[] payload = new byte[] { 0xAA, 0xBB, 0xCC };
            client.Send(header);
            client.Send(payload);

            var segment = await recvTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, segment.Memory.Length);
            segment.Release();

            connection.Dispose();
            client.Close();
        }

        /// <summary>
        /// F2 pin (re-entrancy): a Dispose called from inside the delivery handler —
        /// the one case where draining is impossible — must be safe: no exception
        /// (a throwing handler is swallowed by the handoff catch, which disposes the
        /// connection, so liveness is the proof) and the delivered connection
        /// survives for the app.
        /// </summary>
        [Fact]
        public async Task ServerConnector_DisposeFromInsideOnClientConnected_IsSafe()
        {
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close();

            var connector = new ServerConnector();
            connector.StartListening("127.0.0.1", port);

            var tcs = new TaskCompletionSource<IConnection>();
            connector.OnClientConnected += conn =>
            {
                tcs.TrySetResult(conn);
                connector.Dispose(); // re-entrant dispose from inside the delivery
            };
            connector.AcceptConnection();

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var connection = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // If the re-entrant Dispose had thrown, the handoff catch would have
            // disposed the connection — the round-trip proves it was left alive.
            var recvTcs = new TaskCompletionSource<Segment>();
            connection.Transport.OnPacketReceived += (t, seg) => recvTcs.TrySetResult(seg);
            connection.Transport.ReceiveAsync();

            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 3);
            byte[] payload = new byte[] { 0xAA, 0xBB, 0xCC };
            client.Send(header);
            client.Send(payload);

            var segment = await recvTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, segment.Memory.Length);
            segment.Release();

            connection.Dispose();
            client.Close();
        }

        /// <summary>
        /// The accept slot is an atomic claim, not a check: two racing callers
        /// must never both pass the gate and let the second AcceptAsync throw an
        /// uncaught InvalidOperationException on a busy event object. Exactly one
        /// accept ends up armed.
        /// </summary>
        [Fact]
        public async Task ServerConnector_AcceptConnection_RacingCallers_OnlyOneStarts()
        {
            for (int i = 0; i < 50; i++)
            {
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close();

                var connector = new ServerConnector();
                connector.StartListening("127.0.0.1", port);

                var t1 = System.Threading.Tasks.Task.Run(() => connector.AcceptConnection());
                var t2 = System.Threading.Tasks.Task.Run(() => connector.AcceptConnection());
                await System.Threading.Tasks.Task.WhenAll(t1, t2); // an escape fails the test
                await Task.Delay(1);

                Assert.True(connector.IsAcceptPending); // exactly one accept armed
                connector.Dispose();
            }
        }

        /// <summary>
        /// The listening slot is an atomic claim, not a check: two racing
        /// StartListening callers would otherwise both drive a non-idempotent
        /// bind on the same socket, and a failure on the loser's side could
        /// close the socket the winner is using. With the claim, exactly one
        /// start runs. The pin asserts the contract either way: no exception
        /// escapes either caller, and the connector ends up listening.
        /// </summary>
        [Fact]
        public async Task ServerConnector_StartListening_RacingCallers_SingleBind()
        {
            for (int i = 0; i < 25; i++)
            {
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close();

                var connector = new ServerConnector();
                var t1 = System.Threading.Tasks.Task.Run(() => connector.StartListening("127.0.0.1", port));
                var t2 = System.Threading.Tasks.Task.Run(() => connector.StartListening("127.0.0.1", port));
                await System.Threading.Tasks.Task.WhenAll(t1, t2); // an escape fails the test

                Assert.True(connector.IsListening); // exactly one bind won
                connector.Dispose();
            }
        }

        /// <summary>
        /// Stress pin for the claim-first reorder of StartListening: in the old
        /// check-before-claim order, two racing callers on DIFFERENT ports both
        /// passed the "not listening" check before either claimed, and the loser
        /// then drove a second SetSocketOption/Bind/Listen on the socket the winner
        /// had already bound — re-binding it to a different endpoint (the server
        /// silently moved), or closing it on a throw while the flag still said
        /// listening. With the claim first, the loser bails under the claim and the
        /// winner binds: no exception escapes either caller, the connector ends up
        /// listening, and the socket is open on exactly one of the two requested
        /// ports — never a third endpoint, never dead under a true flag.
        /// </summary>
        [Fact]
        public async Task ServerConnector_StartListening_RacingStart_NoZombie()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(ServerConnector).GetField("socket", NonPubInstance)!;
            var rng = new Random(42);

            for (int i = 0; i < 50; i++)
            {
                // Two free loopback ports, the bind-probe-and-close idiom the
                // rest of the suite uses to find ports.
                var probeA = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probeA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int portA = ((IPEndPoint)probeA.LocalEndPoint!).Port;
                probeA.Close();

                var probeB = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probeB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int portB = ((IPEndPoint)probeB.LocalEndPoint!).Port;
                probeB.Close();

                var connector = new ServerConnector();

                var t1 = System.Threading.Tasks.Task.Run(() => connector.StartListening("127.0.0.1", portA));
                if (i % 3 != 0)
                    Thread.SpinWait(rng.Next(1, 50)); // vary the interleaving point
                var t2 = System.Threading.Tasks.Task.Run(() => connector.StartListening("127.0.0.1", portB));

                // An escape from either start faults a task and rethrows here.
                await System.Threading.Tasks.Task.WhenAll(t1, t2)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(connector.IsListening); // the winner bound

                // The socket is live and bound to exactly one of the two requested
                // ports: the loser bailed under the claim instead of re-binding the
                // winner's socket to its own endpoint. Reading the local endpoint is
                // the liveness probe — a closed socket throws ObjectDisposedException
                // here and fails the test (its Handle stays stale after Close, so it
                // is not a usable closed check).
                var socket = (Socket)socketField.GetValue(connector)!;
                var endPoint = (IPEndPoint)socket.LocalEndPoint!;
                Assert.True(
                    endPoint.Port == portA || endPoint.Port == portB,
                    $"socket bound to port {endPoint.Port}, expected {portA} or {portB}");

                connector.StopListening();
                connector.Dispose();
            }
        }

        /// <summary>
        /// Stress pin for the start-racing-dispose window: with Dispose racing a
        /// StartListening on an already-listening connector, the dispose holds for
        /// the start's claim before it closes, so the start either bails (its
        /// re-read under the claim sees the disposed mark or the listening flag) or
        /// binds on a socket the dispose has not yet closed — it can never throw on
        /// a dead socket — and the flag can never be left true on a closed socket
        /// (the zombie the old order produced: a start that passed the check before
        /// the dispose's clear, then bound after the dispose's close). The outcome
        /// is pinned to the one the protocol settles to, no matter which side won:
        /// IsListening false, the socket closed, and no exception from the start.
        /// </summary>
        [Fact]
        public async Task ServerConnector_StartListening_RacingDispose_NoThrowNoZombie()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(ServerConnector).GetField("socket", NonPubInstance)!;
            var rng = new Random(42);

            for (int i = 0; i < 50; i++)
            {
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close();

                var probe2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe2.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port2 = ((IPEndPoint)probe2.LocalEndPoint!).Port;
                probe2.Close();

                var connector = new ServerConnector();
                connector.StartListening("127.0.0.1", port); // establish listening

                var t1 = System.Threading.Tasks.Task.Run(() => connector.StartListening("127.0.0.1", port2));
                if (i % 3 != 0)
                    Thread.SpinWait(rng.Next(1, 50)); // vary the interleaving point
                var t2 = System.Threading.Tasks.Task.Run(() => connector.Dispose());

                // An escape from the start (a close that outran its bind) faults the
                // task and rethrows here.
                await System.Threading.Tasks.Task.WhenAll(t1, t2)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                // Never a zombie: the flag is false (the dispose's clear or the
                // start's undo) and the socket is closed, no matter which side
                // won. The closed probe is an operation that throws
                // ObjectDisposedException on a closed socket — the same idiom the
                // failed-start test above uses (a closed socket's Handle stays
                // stale, so the handle value is not a usable closed check).
                Assert.False(connector.IsListening);
                var socket = (Socket)socketField.GetValue(connector)!;
                Assert.Throws<ObjectDisposedException>(
                    () => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1));
            }
        }

        /// <summary>
        /// Stress pin for the stop side of the close protocol: StopListening holds
        /// for a start's claim BEFORE it clears the flag (the flag is the stop's
        /// only mark, and a start reads it under its own claim — clearing first let
        /// a racing start read "not listening", bind on the socket about to die,
        /// and leave the flag true on a closed socket). The race settles to the
        /// stopped state either way: the start bails (it reads the flag still set
        /// while the stop holds) or it throws ObjectDisposedException because the
        /// stop completed first and its bind hit the closed socket — a caller error
        /// on a dead socket, not a race, and the only escape the protocol allows.
        /// </summary>
        [Fact]
        public async Task ServerConnector_StartListening_RacingStop_NoZombie()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(ServerConnector).GetField("socket", NonPubInstance)!;
            var rng = new Random(42);

            for (int i = 0; i < 50; i++)
            {
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close();

                var probe2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe2.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port2 = ((IPEndPoint)probe2.LocalEndPoint!).Port;
                probe2.Close();

                var connector = new ServerConnector();
                connector.StartListening("127.0.0.1", port); // establish listening

                Exception? startEx = null;
                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    try { connector.StartListening("127.0.0.1", port2); }
                    catch (Exception ex) { startEx = ex; }
                });
                if (i % 3 != 0)
                    Thread.SpinWait(rng.Next(1, 50)); // vary the interleaving point
                var t2 = System.Threading.Tasks.Task.Run(() => connector.StopListening());

                await System.Threading.Tasks.Task.WhenAll(t1, t2)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                // The only escape the protocol allows from the start is the
                // caller-error throw on a socket the stop already closed; any
                // other exception (a close that outran a bind on a live socket)
                // fails the test.
                if (startEx != null)
                    Assert.IsType<ObjectDisposedException>(startEx);

                // Never a zombie, no matter which side won: the flag is false
                // and the socket is closed (the closed probe throws
                // ObjectDisposedException — a closed socket's Handle stays
                // stale, so the handle value is not a usable closed check).
                Assert.False(connector.IsListening);
                var socket = (Socket)socketField.GetValue(connector)!;
                Assert.Throws<ObjectDisposedException>(
                    () => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1));

                connector.Dispose();
            }
        }

        // --- ClientConnector: late completion + unified failure (N1/N2/N3/N4) ---

        /// <summary>
        /// Pin for the orphan window (N1): a connect still in flight when Dispose runs
        /// must be abandoned silently — no OnConnected, no OnConnectFailed after
        /// dispose, no connection delivered.
        /// </summary>
        [Fact]
        public async Task ClientConnector_Dispose_MidConnect_FiresNothing()
        {
            var connector = new ClientConnector();
            var connectedTcs = new TaskCompletionSource<bool>();
            var failedTcs = new TaskCompletionSource<bool>();
            connector.OnConnected += _ => connectedTcs.TrySetResult(true);
            connector.OnConnectFailed += _ => failedTcs.TrySetResult(true);

            // 10.255.255.1 (TEST-NET-3) is non-routable: the connect stays in flight
            // (SYN retransmits) until we abort it — exactly the window to dispose into.
            bool started = connector.ConnectAsync("10.255.255.1", 9);
            Assert.True(started);

            connector.Dispose(); // disposes the in-flight socket, which aborts the op

            // The aborted operation completes shortly with an error; the late
            // completion must be dropped at the guard, not raised as an event.
            await Task.Delay(500);
            Assert.False(connectedTcs.Task.IsCompleted);
            Assert.False(failedTcs.Task.IsCompleted);
            Assert.False(connector.IsConnected);
        }

        /// <summary>
        /// Pin for the unified failure path (N3/N4): each failure shape fires
        /// OnConnectFailed exactly once, and a throwing subscriber on the synchronous
        /// throw path must not propagate out of ConnectAsync.
        /// </summary>
        [Fact]
        public async Task ClientConnector_FailAttempt_SingleFire_AllPaths()
        {
            // Path 1: invalid address — synchronous FormatException throw path.
            {
                var connector = new ClientConnector();
                var fired = new TaskCompletionSource<SocketError>();
                connector.OnConnectFailed += err => fired.TrySetResult(err);
                Assert.False(connector.ConnectAsync("not-an-address", 80));
                var err = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotEqual(SocketError.Success, err);
                connector.Dispose();
            }

            // Path 2: refused connection on loopback — async error completion path.
            {
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close(); // free port, no listener ⇒ refused

                var connector = new ClientConnector();
                int count = 0;
                var fired = new TaskCompletionSource<SocketError>();
                connector.OnConnectFailed += err => { count++; fired.TrySetResult(err); };
                Assert.True(connector.ConnectAsync("127.0.0.1", port));
                var err = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotEqual(SocketError.Success, err);

                // Exactly one fire: give a spurious second fire a chance to land.
                await Task.Delay(200);
                Assert.Equal(1, count);
                connector.Dispose();
            }

            // Path 3: throwing subscriber on the synchronous throw path — the guarded
            // fire inside FailAttempt must swallow it, not let it escape ConnectAsync.
            {
                var connector = new ClientConnector();
                connector.OnConnectFailed += _ => throw new InvalidOperationException("subscriber boom");
                // A throwing subscriber must not propagate out of ConnectAsync (the
                // guarded fire inside FailAttempt swallows it) — a throw here fails the test.
                bool result = connector.ConnectAsync("not-an-address", 80);
                Assert.False(result);
                connector.Dispose();
            }
        }

        /// <summary>
        /// Stress pin for the slipped-in windows (F1/F5): with Dispose racing the
        /// middle of ConnectAsync, no exception may escape (the raced field read used
        /// to throw an uncaught NullReferenceException) and no live socket may be
        /// orphaned in the field afterwards (the slipped-in socket used to outlive
        /// both Dispose and the late callback). Each iteration races a ConnectAsync on
        /// a thread-pool thread against Dispose from here, with a small random offset
        /// so the interleaving lands at different points in the setup window.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Also pins the removed ObjectDisposedException window: a start whose
        /// re-read passes must not have a dispose land between the re-read and the
        /// ConnectAsync issue — Dispose holds for the claim, so that interleaving
        /// no longer exists. An escape from the issue would fault the connect task
        /// and rethrow here.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ClientConnector_ConnectAsync_RacingDispose_NoLeakNoThrow()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(ClientConnector).GetField("socket", NonPubInstance)!;

            // Refused loopback port: fails fast, so the raced windows (guard→socket,
            // socket→operation start) are exercised at high frequency.
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close(); // free port, no listener ⇒ refused

            var rng = new Random(42);

            for (int i = 0; i < 500; i++)
            {
                var connector = new ClientConnector();
                int failCount = 0;
                bool connectedFired = false;
                connector.OnConnected += _ => connectedFired = true;
                connector.OnConnectFailed += _ => Interlocked.Increment(ref failCount);

                var connectTask = Task.Run(() => connector.ConnectAsync("127.0.0.1", port));
                if (i % 3 != 0)
                    Thread.SpinWait(rng.Next(1, 50));
                connector.Dispose();
                // An escape (e.g. the old NullReferenceException) rethrows here and fails the test.
                _ = await connectTask;

                await Task.Delay(1); // let any late completion land
                Assert.False(connectedFired); // a refused port can never hand a connection out
                Assert.True(failCount <= 1);
                Assert.False(connector.IsConnected);

                var orphan = socketField.GetValue(connector) as Socket;
                if (orphan != null)
                {
                    // A socket left in the field after Dispose must be dead, not live.
                    Assert.Throws<ObjectDisposedException>(() => orphan.Receive(new byte[1]));
                }
            }
        }

        // --- ClientConnector: a successful reconnect disposes the connection it replaces ---

        /// <summary>
        /// Deterministic pin for the replaced-connection leak: a connection whose
        /// peer closed (the FIN is observed by its in-flight receive, so
        /// IsConnected reads false) is still held in the connector's connection
        /// field — nothing closes it, because closing is the owner's (the
        /// application's) job. When the application reconnects, the success path
        /// overwrites that field; the replaced connection must be disposed there
        /// — its still-open half-closed socket closed, its transport disposed,
        /// and its exactly-once Disconnected report fired — not dropped.
        /// </summary>
        [Fact]
        public async Task ClientConnector_Reconnect_DisposesReplacedConnection()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(Connection).GetField("socket", NonPubInstance)!;

            // Probe for a free port, then start a real listener on it.
            var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Close(); // free port for the real listener

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
            listener.Listen(1);

            var connector = new ClientConnector();
            var tcs1 = new TaskCompletionSource<IConnection>();
            connector.OnConnected += conn => tcs1.TrySetResult(conn);

            bool started1 = connector.ConnectAsync("127.0.0.1", port);
            Assert.True(started1);

            var serverSocket = await Task.Run(() => listener.Accept());
            var conn1 = await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(conn1.IsConnected);

            int disconnectedCount = 0;
            conn1.Disconnected += () => Interlocked.Increment(ref disconnectedCount);

            // Arm a receive: the transport needs a receive in flight to observe
            // the peer's FIN and mark itself dead — that is what turns
            // IsConnected from true to false with nothing closing the socket.
            conn1.Transport.ReceiveAsync();

            // The peer closes: the FIN is observed, the transport is marked
            // dead, the connection reads disconnected — but its socket stays
            // open (closing is the application's job, and the application here
            // never disposes its reference).
            serverSocket.Shutdown(SocketShutdown.Both);
            serverSocket.Close();
            await WaitFor(() => !conn1.IsConnected, TimeSpan.FromSeconds(10));

            // The connector still holds the dead-but-undisposed connection, and
            // its IsConnected reads false, so a reconnect passes the
            // "already connected" check.
            Assert.False(connector.IsConnected);

            // A fresh listener on the same port for the replacement.
            listener.Close();
            var listener2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener2.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener2.Bind(new IPEndPoint(IPAddress.Loopback, port));
            listener2.Listen(1);

            var tcs2 = new TaskCompletionSource<IConnection>();
            connector.OnConnected += conn => tcs2.TrySetResult(conn);

            // The reconnect: the success path must dispose what the field held.
            bool started2 = connector.ConnectAsync("127.0.0.1", port);
            Assert.True(started2);

            var serverSocket2 = await Task.Run(() => listener2.Accept());
            var conn2 = await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The replacement is a distinct connection, handed out normally.
            Assert.NotSame(conn1, conn2);
            Assert.True(connector.IsConnected);

            // The replaced connection's socket was closed by the replacement:
            // a closed socket's handle stays stale, and an operation on it
            // throws ObjectDisposedException — the established closed probe.
            var oldSocket = (Socket)socketField.GetValue(conn1)!;
            Assert.Throws<ObjectDisposedException>(
                () => oldSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1));

            // The whole replaced connection was settled: the transport (its
            // segment pools and event objects) is disposed, and the
            // exactly-once Disconnected report fired — from the replacement,
            // and from nowhere else.
            Assert.True(conn1.Transport.IsDisposed);
            Assert.Equal(1, disconnectedCount);

            serverSocket2.Close();
            listener2.Close();
            connector.Dispose();
        }

        /// <summary>
        /// Race pin for the dispose-path interaction: with the connector's
        /// Dispose racing a successful reconnect (whose success path disposes
        /// the replaced connection), no exception may escape from either side,
        /// the replaced connection's socket must end up closed no matter which
        /// side won — the replacement's success path disposed it, or the
        /// connector's Dispose did — and its Disconnected report must fire
        /// exactly once. The replaced connection's Dispose runs its own
        /// protocol on its own socket/transport (all state internal to it) and
        /// is idempotent on both objects, so every interleaving disposes it
        /// effectively once and never leaks it.
        /// </summary>
        [Fact]
        public async Task ClientConnector_Reconnect_RacingDispose_OldSocketClosed()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(Connection).GetField("socket", NonPubInstance)!;
            var rng = new Random(42);

            for (int i = 0; i < 50; i++)
            {
                // Probe for a free port, then start a real listener on it.
                var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
                probe.Close(); // free port for the real listener

                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
                listener.Listen(1);

                var connector = new ClientConnector();
                var tcs1 = new TaskCompletionSource<IConnection>();
                connector.OnConnected += conn => tcs1.TrySetResult(conn);

                Assert.True(connector.ConnectAsync("127.0.0.1", port));
                var serverSocket = await Task.Run(() => listener.Accept());
                var conn1 = await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(10));

                int disconnectedCount = 0;
                conn1.Disconnected += () => Interlocked.Increment(ref disconnectedCount);

                // Arm a receive so the peer's close is observed and the
                // connection's IsConnected flips — the reconnect's
                // "already connected" check then passes.
                conn1.Transport.ReceiveAsync();
                serverSocket.Shutdown(SocketShutdown.Both);
                serverSocket.Close();
                await WaitFor(() => !conn1.IsConnected, TimeSpan.FromSeconds(10));

                // A fresh listener on the same port for the replacement.
                listener.Close();
                var listener2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener2.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                listener2.Bind(new IPEndPoint(IPAddress.Loopback, port));
                listener2.Listen(1);
                // Drain the backlog as the replacement connects; a close of the
                // listener would otherwise leave the accept throwing in the
                // background (a socket call — guarded at the boundary).
                _ = Task.Run(() =>
                {
                    try { listener2.Accept().Close(); } catch { }
                });

                // Race the successful reconnect against Dispose with a random
                // spin offset, as in the other hammers: whichever side wins,
                // the replaced connection must end disposed.
                var t1 = Task.Run(() => connector.ConnectAsync("127.0.0.1", port));
                if (i % 3 != 0)
                    Thread.SpinWait(rng.Next(1, 50));
                var t2 = Task.Run(() => connector.Dispose());
                // Any escape (from either side of the race) rethrows here and
                // fails the test.
                await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));

                // The old connection's socket must be closed whichever side
                // won; wait for it, as the Connection race pin does (the
                // late completion may still be settling).
                var oldSocket = (Socket)socketField.GetValue(conn1)!;
                await WaitFor(() =>
                {
                    try { oldSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1); return false; }
                    catch (ObjectDisposedException) { return true; }
                    catch { return false; }
                }, TimeSpan.FromSeconds(10));

                // Exactly-once report no matter which disposer won: the
                // report runs inside the dispose that closes the socket, so
                // once the probe passes, the count is settled.
                Assert.Equal(1, disconnectedCount);
                Assert.False(connector.IsConnected);

                listener2.Close();
            }
        }

        // --- Connection: Dispose racing a started disconnect (the issue window) ---

        /// <summary>
        /// Stress pin for the dispose/disconnect hold: with Dispose racing a
        /// DisconnectAsync that has claimed the slot, no exception may escape
        /// from either call — a close must never outrun the caller's window
        /// between its re-read and its issue — and the socket must end up
        /// closed no matter which side won. A responsive loopback peer settles
        /// the graceful close quickly, so this pins the contract around the
        /// former hole (no throw, no hang, exactly-once report, dead socket)
        /// rather than the unresponsive-peer leak itself.
        /// </summary>
        [Fact]
        public async Task Connection_DisconnectAsync_RacingDispose_SocketClosedNoThrow()
        {
            const System.Reflection.BindingFlags NonPubInstance =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var socketField = typeof(Connection).GetField("socket", NonPubInstance)!;
            var rng = new Random(42);

            for (int i = 0; i < 300; i++)
            {
                using var pair = MakeSocketPair();
                var conn = new Connection(pair.Client);
                int count = 0;
                conn.Disconnected += () => Interlocked.Increment(ref count);

                var disposeTask = Task.Run(() =>
                {
                    if (i % 3 != 0)
                        Thread.SpinWait(rng.Next(1, 50));
                    conn.Dispose();
                });

                // An escape (a close that outran the caller's issue) rethrows here.
                conn.DisconnectAsync();
                await disposeTask;

                // The socket must be closed no matter which side won: a raced
                // dispose closes through the protocol, a winning graceful close
                // settles through its completion. A one-byte send is the probe:
                // it throws ObjectDisposedException on a closed socket and
                // cannot block on a live loopback one.
                var orphan = socketField.GetValue(conn) as Socket;
                await WaitFor(() =>
                {
                    try { orphan!.Send(new byte[1]); return false; }
                    catch (ObjectDisposedException) { return true; }
                    catch { return false; }
                }, TimeSpan.FromSeconds(5));

                Assert.Equal(1, count);
                conn.Dispose(); // release the transport where the race left it
            }
        }

        // --- Connection: Disconnected — exactly once, natural or forced (N6) ---

        /// <summary>
        /// Pin for the graceful path (N6): DisconnectAsync → FIN completes → Disconnected
        /// fires exactly once; a subsequent Dispose must not re-fire.
        /// </summary>
        [Fact]
        public async Task Connection_DisconnectAsync_FiresDisconnectedOnce()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);

            int count = 0;
            var fired = new TaskCompletionSource<bool>();
            conn.Disconnected += () => { count++; fired.TrySetResult(true); };

            conn.DisconnectAsync();
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(conn.IsConnected);

            conn.Dispose(); // force path must not re-fire
            await Task.Delay(200);
            Assert.Equal(1, count);
        }

        /// <summary>
        /// Pin for the force path (N6): Dispose immediately after DisconnectAsync —
        /// the FIN is in flight (or just completed); Disconnected must fire exactly once
        /// either way, and the wait must never hang.
        /// </summary>
        [Fact]
        public async Task Connection_DisposeDuringInFlightDisconnect_FiresDisconnectedOnce()
        {
            using var pair = MakeSocketPair();
            var conn = new Connection(pair.Client);

            int count = 0;
            var fired = new TaskCompletionSource<bool>();
            conn.Disconnected += () => { count++; fired.TrySetResult(true); };

            conn.DisconnectAsync(); // FIN in flight (may complete synchronously on loopback)
            conn.Dispose();        // immediately: the force-completion path

            await fired.Task.WaitAsync(TimeSpan.FromSeconds(5)); // must never hang
            Assert.Equal(1, count);
        }

        /// <summary>
        /// F3 stress pin: racing DisconnectAsync against Dispose must never let
        /// ObjectDisposedException escape the public method, and the Disconnected
        /// report must still fire exactly once — Dispose's force report covers the
        /// case where it wins the race, so awaiters never hang.
        /// </summary>
        [Fact]
        public async Task Connection_DisconnectAsync_RacingDispose_NoThrowReportOnce()
        {
            // 100 iterations: the pin's value is the no-throw / exactly-once
            // assertion on every iteration, and a smaller count keeps the
            // socket churn this test adds to the suite moderate.
            for (int i = 0; i < 100; i++)
            {
                using var pair = MakeSocketPair();
                var conn = new Connection(pair.Client);

                int count = 0;
                var fired = new TaskCompletionSource<bool>();
                conn.Disconnected += () => { count++; fired.TrySetResult(true); };

                var t = System.Threading.Tasks.Task.Run(() => conn.DisconnectAsync());
                if (i % 3 != 0)
                    Thread.SpinWait(1 + (i % 7)); // vary the interleaving point
                conn.Dispose();
                await t; // an escape (e.g. the old ObjectDisposedException) fails the test

                await fired.Task.WaitAsync(TimeSpan.FromSeconds(5)); // report must never hang
                await Task.Delay(1);
                Assert.Equal(1, count); // exactly once: natural or forced, never both
            }
        }

        /// <summary>
        /// F3-style stress pin, transport side: racing a transport operation
        /// (receive or send) against Connection.Dispose must never let an
        /// ObjectDisposedException escape a public method — the dispose race is
        /// settled by the mutual-confirmation protocol, so the losing side gets
        /// a report, not a throw — and the Disconnected report fires exactly
        /// once, so awaiters never hang. The dispose's close (close protocol)
        /// runs over the in-flight operation and aborts it, and its settling
        /// completion is what clears the transport's in-flight claims.
        /// </summary>
        [Fact]
        public async Task Connection_TransportOp_RacingDispose_NoThrowSettles()
        {
            for (int i = 0; i < 100; i++)
            {
                using var pair = MakeSocketPair();
                var conn = new Connection(pair.Client);

                int disconnectedCount = 0;
                var fired = new TaskCompletionSource<bool>();
                conn.Disconnected += () => { disconnectedCount++; fired.TrySetResult(true); };

                // One transport start may report at most once, however it
                // loses the race: entry check, re-read bail, or the settling
                // completion of an issued operation.
                int reports = 0;
                conn.Transport.OnPacketReceiveFailed += (t, e) => Interlocked.Increment(ref reports);
                conn.Transport.OnPacketSendFailed += (t, s, e) => { Interlocked.Increment(ref reports); s.Release(); };

                Task starter;
                if (i % 2 == 0)
                {
                    starter = System.Threading.Tasks.Task.Run(() => conn.Transport.ReceiveAsync());
                }
                else
                {
                    starter = System.Threading.Tasks.Task.Run(() =>
                    {
                        if (conn.Transport.TryReserveSendPacket(out var seg))
                        {
                            seg.Memory.Span[0] = 1;
                            conn.Transport.SendAsync(seg, 1);
                        }
                    });
                }
                if (i % 3 != 0)
                    Thread.SpinWait(1 + (i % 7)); // vary the interleaving point
                conn.Dispose();
                await starter; // an escape fails the test

                await fired.Task.WaitAsync(TimeSpan.FromSeconds(5)); // must never hang
                Assert.True(conn.Transport.IsDisposed);

                // Settle whatever the dispose left in flight: closing the peer
                // ends the operation, and its settling completion closes the
                // socket the dispose deferred to it.
                pair.Server.Close();
                await WaitFor(() => !conn.Transport.ReceiveArmed && !conn.Transport.SendArmed, TimeSpan.FromSeconds(5));

                Assert.Equal(1, disconnectedCount); // exactly once, never both
                Assert.True(reports <= 1, $"a single start reported {reports} times");
            }
        }

        // --- Connection teardown race stress (P2) ---
        // The close/disconnect protocol is lock-free and its safety argument
        // lives in the code comments. These tests pin that argument: they drive
        // only the public contract (DisconnectAsync / Dispose, the transport's
        // ops and the Disconnected report) on real loopback sockets, so a future
        // edit that breaks "a close never outruns a claimed starter" trips one.
        // WaitFor throws on timeout, so a deadlock in the protocol fails loudly.

        [Fact]
        public async Task Connection_DisconnectAndDispose_Raced_ReportsExactlyOnce()
        {
            const int iterations = 100;
            for (int i = 0; i < iterations; i++)
            {
                SocketPairHelper pair = MakeSocketPair();
                int reported = 0;
                try
                {
                    var conn = new Connection(pair.Client);
                    conn.Disconnected += () => Interlocked.Increment(ref reported);

                    // Arm a receive so the close has an in-flight transport op to
                    // run over: a receive stays pending until the socket dies.
                    conn.Transport.ReceiveAsync();

                    // Race the two teardown entry points on different threads.
                    // WaitAsync guards against a regression that deadlocks the race.
                    var settle = Task.WhenAll(
                        Task.Run(() => conn.DisconnectAsync()),
                        Task.Run(() => conn.Dispose()));
                    await settle.WaitAsync(TimeSpan.FromSeconds(5));

                    await WaitFor(
                        () => !conn.IsConnected && conn.Transport.IsDisposed && reported >= 1,
                        TimeSpan.FromSeconds(5));
                }
                finally
                {
                    pair.Dispose();
                }
                Assert.True(reported == 1, $"Disconnected reported {reported} times on iteration {i}");
            }
        }

        [Fact]
        public async Task Connection_DisconnectAsync_WithInFlightReceive_SettlesOnce()
        {
            using var pair = MakeSocketPair();
            int reported = 0;
            var conn = new Connection(pair.Client);
            conn.Disconnected += () => Interlocked.Increment(ref reported);

            conn.Transport.ReceiveAsync(); // in-flight receive
            conn.DisconnectAsync();

            // The graceful close runs over the in-flight receive and settles it.
            await WaitFor(() => !conn.IsConnected && reported >= 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, reported);
            conn.Dispose();
        }

        [Fact]
        public async Task Connection_Dispose_WithInFlightReceive_SettlesOnce()
        {
            using var pair = MakeSocketPair();
            int reported = 0;
            var conn = new Connection(pair.Client);
            conn.Disconnected += () => Interlocked.Increment(ref reported);

            conn.Transport.ReceiveAsync(); // in-flight receive
            conn.Dispose(); // force close runs over the in-flight receive; must not hang

            await WaitFor(() => conn.Transport.IsDisposed && reported >= 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, reported);
            Assert.False(conn.IsConnected);
        }

        [Fact]
        public async Task Connection_DisconnectThenDispose_NeverDoubleReports()
        {
            using var pair = MakeSocketPair();
            int reported = 0;
            var conn = new Connection(pair.Client);
            conn.Disconnected += () => Interlocked.Increment(ref reported);

            conn.DisconnectAsync();
            await WaitFor(() => !conn.IsConnected && reported >= 1, TimeSpan.FromSeconds(5));
            conn.Dispose(); // force path after the natural one: must not re-fire
            await Task.Delay(100);
            Assert.Equal(1, reported);
        }

        // --- Helper ---
        private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (!condition())
            {
                if (DateTime.UtcNow - start > timeout)
                    throw new TimeoutException($"Condition not met within {timeout}");
                await Task.Delay(50);
            }
        }

        private static SocketPairHelper MakeSocketPair()
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var server = listener.Accept();
            listener.Close();

            return new SocketPairHelper(client, server);
        }

        private class SocketPairHelper : IDisposable
        {
            public Socket Client { get; }
            public Socket Server { get; }
            public SocketPairHelper(Socket client, Socket server)
            {
                Client = client;
                Server = server;
            }

            public void Dispose()
            {
                try { Client.Shutdown(SocketShutdown.Both); } catch { }
                try { Client.Close(); } catch { }
                try { Server.Shutdown(SocketShutdown.Both); } catch { }
                try { Server.Close(); } catch { }
            }
        }
    }
}
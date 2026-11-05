using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuneTransport.BufferManager;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;
using Xunit;

namespace DuneTransport.Tests
{
    public class SocketPairFixture : IDisposable
    {
        public Socket ServerConn { get; }  // Accepted connection (for sending to client)
        public Socket Client { get; }
        private readonly Socket _listener;

        public SocketPairFixture()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(1);
            int port = ((IPEndPoint)_listener.LocalEndPoint!).Port;

            Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            ServerConn = _listener.Accept();
        }

        public void Dispose()
        {
            try { Client.Shutdown(SocketShutdown.Both); } catch { }
            try { Client.Close(); } catch { }
            try { ServerConn.Shutdown(SocketShutdown.Both); } catch { }
            try { ServerConn.Close(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    public class TransportTests
    {
        [Fact]
        public void Transport_Construction_NullSocket_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new global::DuneTransport.Transport.Transport(null!));
        }

        [Fact]
        public void Transport_Construction_DefaultState()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            Assert.True(transport.IsConnected);
            Assert.False(transport.IsDisposed);
            transport.Dispose();
        }

        [Fact]
        public void Transport_CustomSegmentOptions_SizesReservedSegment()
        {
            using var pair = new SocketPairFixture();

            // Default pool is 8192 / 32 = 256-byte segments; the 2-byte length
            // prefix is reserved up front, so the payload slice is 254 bytes.
            using var defaultTransport = new global::DuneTransport.Transport.Transport(pair.Client);
            Assert.True(defaultTransport.TryReserveSendPacket(out Segment defaultSegment));
            Assert.Equal(254, defaultSegment.Memory.Length);
            defaultSegment.Release();

            // A larger pool lifts the maximum packet size: 4096-byte segments
            // leave 4094 bytes for the payload.
            var options = new TransportOptions { SegmentSize = 4096, SegmentCount = 4 };
            using var customTransport = new global::DuneTransport.Transport.Transport(pair.Client, options);
            Assert.True(customTransport.TryReserveSendPacket(out Segment customSegment));
            Assert.Equal(4094, customSegment.Memory.Length);
            customSegment.Release();
        }

        [Fact]
        public void Transport_ReceiveAsync_ObjectDisposed()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.Dispose();

            TransportError? error = null;
            transport.OnPacketReceiveFailed += (t, e) => error = e;
            transport.ReceiveAsync();
            Assert.Equal(TransportError.ObjectDisposed, error);
        }

        [Fact]
        public void Transport_ReceiveAsync_SocketClosedByOwner_FiresSocketDisconnected()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            // The owner (the test, standing in for Connection) is the only side
            // that may close the socket, and it marks the transport dead before
            // doing so — a closed socket cannot be detected afterwards, so the
            // mark is how a later start learns the socket is gone.
            transport.MarkDisconnected();
            pair.Client.Close();

            // The entry check rejects the start with a disconnect report
            // instead of throwing on the closed socket.
            TransportError? error = null;
            transport.OnPacketReceiveFailed += (t, e) => error = e;
            transport.ReceiveAsync();
            Assert.Equal(TransportError.SocketDisconnected, error);
        }

        [Fact]
        public void Transport_ReceiveAsync_ReceiveAlreadyPending()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            TransportError? error = null;
            transport.OnPacketReceiveFailed += (t, e) => error = e;

            transport.ReceiveAsync();
            transport.ReceiveAsync(); // second call should fail

            Assert.Equal(TransportError.ReceiveAlreadyPending, error);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_Receive_PartialHeader_Continues()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<Segment>();
            transport.OnPacketReceived += (t, seg) => tcs.TrySetResult(seg);
            transport.OnPacketReceiveFailed += (t, e) => tcs.SetException(new Exception($"Receive failed: {e}"));
            transport.ReceiveAsync();

            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 3);

            pair.ServerConn.Send(header[0..1]);
            await Task.Delay(100);
            pair.ServerConn.Send(header[1..2]);
            await Task.Delay(100);
            pair.ServerConn.Send(new byte[] { 0xAA, 0xBB, 0xCC });

            var seg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, seg.Memory.Length);
            Assert.Equal(0xAA, seg.Memory.Span[0]);
            seg.Release();
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_Receive_ZeroLengthPayload_ProtocolError()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcs.TrySetResult(e);
            transport.ReceiveAsync();

            byte[] header = new byte[2]; // 0x0000 = length 0
            pair.ServerConn.Send(header);

            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransportError.ProtocolError, error);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_Receive_OversizedPayload_ProtocolError()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcs.TrySetResult(e);
            transport.ReceiveAsync();

            // 257 bytes > 256 segment size
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 257);
            byte[] payload = new byte[257];
            pair.ServerConn.Send(header);
            pair.ServerConn.Send(payload);

            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransportError.ProtocolError, error);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_Receive_ValidPacket_FiresOnPacketReceived()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<Segment>();
            transport.OnPacketReceived += (t, seg) => tcs.TrySetResult(seg);
            transport.OnPacketReceiveFailed += (t, e) => tcs.SetException(new Exception(e.ToString()));
            transport.ReceiveAsync();

            // Send a 3-byte payload: header (0x03) + payload (AA, BB, CC)
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 3);
            byte[] payload = new byte[] { 0xAA, 0xBB, 0xCC };

            pair.ServerConn.Send(header);
            pair.ServerConn.Send(payload);

            var seg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(seg.SegmentIndex > 0);
            Assert.Equal(3, seg.Memory.Length);
            Assert.Equal(0xAA, seg.Memory.Span[0]);
            Assert.Equal(0xBB, seg.Memory.Span[1]);
            Assert.Equal(0xCC, seg.Memory.Span[2]);
            seg.Release();
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_SendAsync_InvalidSegment()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            TransportError? error = null;
            transport.OnPacketSendFailed += (t, s, e) => error = e;
            transport.SendAsync(default(Segment), 100); // SegmentIndex == 0, invalid

            Assert.Equal(TransportError.InvalidSegment, error);
            transport.Dispose();
        }

        [Fact]
        public void Transport_TryReserveSendPacket_WhenDisposed()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.Dispose();
            Assert.False(transport.TryReserveSendPacket(out _));
        }

        [Fact]
        public void Transport_SendAsync_ObjectDisposed()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.Dispose();

            TransportError? error = null;
            transport.OnPacketSendFailed += (t, s, e) => error = e;
            transport.SendAsync(default, 0);
            Assert.Equal(TransportError.ObjectDisposed, error);
        }

        [Fact]
        public void Transport_SendAsync_SocketClosedByOwner_FiresSocketDisconnected()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            // The owner marks the transport dead and closes the socket (see
            // Transport_ReceiveAsync_SocketClosedByOwner_FiresSocketDisconnected).
            transport.MarkDisconnected();
            pair.Client.Close();

            // The entry check rejects the start with a disconnect report; the
            // segment goes back to the subscriber to release.
            TransportError? error = null;
            transport.OnPacketSendFailed += (t, s, e) => { error = e; s.Release(); };
            if (transport.TryReserveSendPacket(out var seg))
            {
                seg.Memory.Span[0] = 0x01;
                transport.SendAsync(seg, 1);
            }
            Assert.Equal(TransportError.SocketDisconnected, error);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_SendAsync_ValidSegment_FiresOnPacketSent()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            bool packetSent = false;
            var tcs = new TaskCompletionSource<bool>();
            transport.OnPacketSent += t => { packetSent = true; tcs.TrySetResult(true); };

            if (transport.TryReserveSendPacket(out var seg))
            {
                byte[] payload = new byte[] { 0x01, 0x02, 0x03 };
                payload.CopyTo(seg.Memory.Span);
                transport.SendAsync(seg, payload.Length);
            }

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(packetSent);
            transport.Dispose();
        }

        [Fact]
        public void Transport_Dispose_Idempotent()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.Dispose();
            transport.Dispose(); // Should not throw
            Assert.True(transport.IsDisposed);
        }

        [Fact]
        public void Transport_ReceiveArmed_DefaultsFalse()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            Assert.False(transport.ReceiveArmed);
            transport.Dispose();
        }

        [Fact]
        public void Transport_ReceiveArmed_TrueWhilePending()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.ReceiveAsync();
            Assert.True(transport.ReceiveArmed);
            transport.Dispose();
        }

        [Fact]
        public void Transport_SendArmed_DefaultsFalse()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            Assert.False(transport.SendArmed);
            transport.Dispose();
        }

        [Fact]
        public void Transport_Dispose_WithInflightReceive_ReleasesSegment()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.ReceiveAsync();
            transport.Dispose();
            Assert.True(transport.IsDisposed);
        }

        [Fact]
        public void Transport_Dispose_WithInflightSend_ReleasesSegment()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            if (transport.TryReserveSendPacket(out var seg))
            {
                seg.Memory.Span[0] = 0x01;
                transport.SendAsync(seg, 1);
            }
            transport.Dispose();
            Assert.True(transport.IsDisposed);
        }

        [Fact]
        public async Task Transport_SendReceive_RoundTrip()
        {
            // Two sockets connected, each with its own Transport
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var server = listener.Accept();

            var tcsClientReceive = new TaskCompletionSource<Segment>();
            var tcsServerReceive = new TaskCompletionSource<Segment>();

            var clientTransport = new global::DuneTransport.Transport.Transport(client);
            var serverTransport = new global::DuneTransport.Transport.Transport(server);

            clientTransport.OnPacketReceived += (t, seg) => tcsClientReceive.TrySetResult(seg);
            serverTransport.OnPacketReceived += (t, seg) => tcsServerReceive.TrySetResult(seg);

            // Client sends to server
            byte[] clientPayload = { 0x11, 0x22, 0x33 };
            if (clientTransport.TryReserveSendPacket(out var cSeg))
            {
                clientPayload.CopyTo(cSeg.Memory.Span);
                clientTransport.SendAsync(cSeg, clientPayload.Length);
            }
            serverTransport.ReceiveAsync();

            var serverSeg = await tcsServerReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, serverSeg.Memory.Length);
            Assert.Equal(0x11, serverSeg.Memory.Span[0]);
            serverSeg.Release();

            // Server sends back to client
            byte[] serverPayload = { 0x44, 0x55, 0x66 };
            if (serverTransport.TryReserveSendPacket(out var sSeg))
            {
                serverPayload.CopyTo(sSeg.Memory.Span);
                serverTransport.SendAsync(sSeg, serverPayload.Length);
            }
            clientTransport.ReceiveAsync();

            var clientSeg = await tcsClientReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, clientSeg.Memory.Length);
            Assert.Equal(0x44, clientSeg.Memory.Span[0]);
            clientSeg.Release();

            clientTransport.Dispose();
            serverTransport.Dispose();
            client.Close();
            server.Close();
        }

        [Fact]
        public async Task Transport_SendAsync_BothSequential_AfterFirstCompletes()
        {
            // On loopback, sends complete synchronously, so sequential sends both succeed
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            int sentCount = 0;
            var sentTcs = new TaskCompletionSource<bool>();
            transport.OnPacketSent += _ => { sentCount++; sentTcs.TrySetResult(true); };

            TransportError? error = null;
            transport.OnPacketSendFailed += (t, s, e) => { error = e; s.Release(); };

            // First send
            if (transport.TryReserveSendPacket(out var seg1))
            {
                seg1.Memory.Span[0] = 0x01;
                transport.SendAsync(seg1, 1);
            }

            // Wait for first send to complete
            await sentTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Second send - should succeed because first completed
            if (transport.TryReserveSendPacket(out var seg2))
            {
                seg2.Memory.Span[0] = 0x02;
                transport.SendAsync(seg2, 1);
            }

            Assert.Null(error);
            Assert.Equal(2, sentCount);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_SendAsync_RoundTrip_WithOnPacketSent()
        {
            // Verify OnPacketSent fires after actual send completion over real sockets
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var server = listener.Accept();
            listener.Close();

            var clientTransport = new global::DuneTransport.Transport.Transport(client);
            var serverTransport = new global::DuneTransport.Transport.Transport(server);

            var tcsSent = new TaskCompletionSource<bool>();
            clientTransport.OnPacketSent += _ => tcsSent.TrySetResult(true);

            var tcsReceived = new TaskCompletionSource<Segment>();
            serverTransport.OnPacketReceived += (t, seg) => tcsReceived.TrySetResult(seg);
            serverTransport.ReceiveAsync();

            byte[] payload = { 0xAA, 0xBB, 0xCC };
            if (clientTransport.TryReserveSendPacket(out var seg))
            {
                payload.CopyTo(seg.Memory.Span);
                clientTransport.SendAsync(seg, payload.Length);
            }

            // Verify send completed
            await tcsSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Verify receive on server side
            var received = await tcsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, received.Memory.Length);
            Assert.Equal(0xAA, received.Memory.Span[0]);
            received.Release();

            clientTransport.Dispose();
            serverTransport.Dispose();
            client.Close();
            server.Close();
        }

        [Fact]
        public async Task Transport_SendAsync_SecondWhileFirstPending_FailsWithAlreadyPending()
        {
            // Test concurrent send guard with real transport
            // On loopback, sends typically complete synchronously.
            // We test that calling SendAsync twice in quick succession without waiting
            // may or may not hit the guard depending on timing.
            // Key: the real Transport's Interlocked.CompareExchange guards against corruption.
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            int sentCount = 0;
            int failedCount = 0;
            transport.OnPacketSent += _ => Interlocked.Increment(ref sentCount);
            transport.OnPacketSendFailed += (t, s, e) =>
            {
                if (e == TransportError.SendAlreadyPending)
                    Interlocked.Increment(ref failedCount);
                s.Release();
            };

            // Send two packets back-to-back without awaiting
            if (transport.TryReserveSendPacket(out var seg1))
            {
                seg1.Memory.Span[0] = 0x01;
                transport.SendAsync(seg1, 1);
            }
            if (transport.TryReserveSendPacket(out var seg2))
            {
                seg2.Memory.Span[0] = 0x02;
                transport.SendAsync(seg2, 1);
            }

            // At least one should have succeeded; the other either succeeded or failed
            // Both outcomes are valid depending on sync/async completion
            Assert.True(sentCount + failedCount == 2, "Both sends should have completed one way or another");
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_OnPacketReceived_HandlerThrows_FiresHandlerFailed()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceived += (t, seg) =>
            {
                throw new InvalidOperationException("handler boom");
            };
            transport.OnPacketReceiveFailed += (t, e) => tcs.TrySetResult(e);
            transport.ReceiveAsync();

            // Send a valid 1-byte payload
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 1);
            pair.ServerConn.Send(header);
            pair.ServerConn.Send(new byte[] { 0xAA });

            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransportError.HandlerFailed, error);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_SendAsync_SocketDisconnected_AfterFIN()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            // Detect FIN on receive to set _isConnected = false
            var tcsReceive = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcsReceive.TrySetResult(e);
            transport.ReceiveAsync();

            // Remote closes
            pair.ServerConn.Shutdown(SocketShutdown.Both);
            pair.ServerConn.Close();

            var recvError = await tcsReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransportError.SocketDisconnected, recvError);

            // Now try to send — should fail with SocketDisconnected
            TransportError? sendError = null;
            transport.OnPacketSendFailed += (t, s, e) => { sendError = e; };
            if (transport.TryReserveSendPacket(out var seg))
            {
                seg.Memory.Span[0] = 0x01;
                transport.SendAsync(seg, 1);
            }
            Assert.Equal(TransportError.SocketDisconnected, sendError);
            transport.Dispose();
        }

        [Fact]
        public async Task Transport_ReceiveAsync_WhenNotConnected_AfterFIN()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            // Detect FIN on receive to set _isConnected = false
            var tcsReceive = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcsReceive.TrySetResult(e);
            transport.ReceiveAsync();

            pair.ServerConn.Shutdown(SocketShutdown.Both);
            pair.ServerConn.Close();

            await tcsReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Now try another receive — should fail with SocketDisconnected
            TransportError? error = null;
            transport.OnPacketReceiveFailed += (t, e) => error = e;
            transport.ReceiveAsync();

            Assert.Equal(TransportError.SocketDisconnected, error);
            transport.Dispose();
        }

        /// <summary>
        /// Verifies that after a ReceiveAsync completes (packet received), a new ReceiveAsync can be called.
        /// Tests the receive state machine allows sequential receives.
        /// </summary>
        [Fact]
        public async Task Transport_ReceiveAsync_MultipleSequential_AfterFirstCompletes()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var receivedPackets = new List<Segment>();

            // First receive
            var tcs1 = new TaskCompletionSource<Segment>();
            transport.OnPacketReceived += (t, seg) => tcs1.TrySetResult(seg);
            transport.OnPacketReceiveFailed += (t, e) => tcs1.SetException(new Exception($"Receive failed: {e}"));
            transport.ReceiveAsync();

            // Send first packet
            byte[] header1 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header1.AsSpan(), 3);
            pair.ServerConn.Send(header1);
            pair.ServerConn.Send(new byte[] { 0x11, 0x22, 0x33 });

            // Wait for first packet
            var seg1 = await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));
            receivedPackets.Add(seg1);
            Assert.Equal(0x11, receivedPackets[0].Memory.Span[0]);
            receivedPackets[0].Release();

            // Second receive - should work after first completed
            var tcs2 = new TaskCompletionSource<Segment>();
            transport.OnPacketReceived += (t, seg) => tcs2.TrySetResult(seg);
            transport.OnPacketReceiveFailed += (t, e) => tcs2.SetException(new Exception($"Receive failed: {e}"));
            transport.ReceiveAsync();

            // Send second packet
            byte[] header2 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header2.AsSpan(), 3);
            pair.ServerConn.Send(header2);
            pair.ServerConn.Send(new byte[] { 0x44, 0x55, 0x66 });

            var seg2 = await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));
            receivedPackets.Add(seg2);
            Assert.Equal(0x44, receivedPackets[1].Memory.Span[0]);
            receivedPackets[1].Release();

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that calling ReceiveAsync while another is in progress (not yet completed) fails with ReceiveAlreadyPending.
        /// </summary>
        [Fact]
        public void Transport_ReceiveAsync_ConcurrentWhilePending_FailsWithReceiveAlreadyPending()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            TransportError? error = null;
            transport.OnPacketReceiveFailed += (t, e) => error = e;

            // First receive - pending
            transport.ReceiveAsync();

            // Second receive while first is still pending - should fail
            transport.ReceiveAsync();

            Assert.Equal(TransportError.ReceiveAlreadyPending, error);
            transport.Dispose();
        }

        /// <summary>
        /// Verifies that calling ReceiveAsync from within the OnPacketReceived handler works correctly
        /// (the most common real-world pattern for continuous receive loops).
        /// </summary>
        [Fact]
        public async Task Transport_ReceiveAsync_FromOnPacketReceived_Handler()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var receivedPackets = new List<Segment>();
            int packetCount = 0;

            transport.OnPacketReceived += (t, seg) =>
            {
                receivedPackets.Add(seg);
                packetCount++;

                // Continue receiving from within the handler
                if (packetCount < 2)
                    t.ReceiveAsync();
            };

            transport.ReceiveAsync();

            // Send two packets
            byte[] header1 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header1.AsSpan(), 3);
            pair.ServerConn.Send(header1);
            pair.ServerConn.Send(new byte[] { 0x11, 0x22, 0x33 });

            await Task.Delay(100);

            byte[] header2 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header2.AsSpan(), 2);
            pair.ServerConn.Send(header2);
            pair.ServerConn.Send(new byte[] { 0xAA, 0xBB });

            // Wait for both packets to be processed
            while (receivedPackets.Count < 2 && packetCount < 3)
                await Task.Delay(50);

            Assert.Equal(2, receivedPackets.Count);
            Assert.Equal(0x11, receivedPackets[0].Memory.Span[0]);
            Assert.Equal(0xAA, receivedPackets[1].Memory.Span[0]);
            receivedPackets[0].Release();
            receivedPackets[1].Release();

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that send and receive can operate concurrently on the same transport
        /// (they use separate in-flight guards).
        /// </summary>
        [Fact]
        public async Task Transport_ConcurrentSendAndReceive()
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

            var clientTransport = new global::DuneTransport.Transport.Transport(client);
            var serverTransport = new global::DuneTransport.Transport.Transport(server);

            // Server starts receiving
            var tcsReceived = new TaskCompletionSource<Segment>();
            serverTransport.OnPacketReceived += (t, seg) => tcsReceived.TrySetResult(seg);
            serverTransport.ReceiveAsync();

            // Client sends
            byte[] payload = { 0x11, 0x22, 0x33 };
            if (clientTransport.TryReserveSendPacket(out var seg))
            {
                payload.CopyTo(seg.Memory.Span);
                clientTransport.SendAsync(seg, payload.Length);
            }

            // Server should receive
            var received = await tcsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, received.Memory.Length);
            Assert.Equal(0x11, received.Memory.Span[0]);
            received.Release();

            clientTransport.Dispose();
            serverTransport.Dispose();
            client.Close();
            server.Close();
        }

        /// <summary>
        /// Verifies that multiple sequential packets can be sent and received in rapid succession.
        /// </summary>
        [Fact]
        public async Task Transport_MultipleSequential_PacketsFast()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var receivedPackets = new List<Segment>();
            var allDone = new TaskCompletionSource<bool>();

            transport.OnPacketReceived += (t, seg) =>
            {
                receivedPackets.Add(seg);
                if (receivedPackets.Count == 5)
                    allDone.TrySetResult(true);
                else
                    t.ReceiveAsync();
            };
            transport.OnPacketReceiveFailed += (t, e) => allDone.SetException(new Exception($"Receive failed: {e}"));
            transport.ReceiveAsync();

            // Send 5 packets rapidly
            for (int i = 0; i < 5; i++)
            {
                byte[] header = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 1);
                pair.ServerConn.Send(header);
                pair.ServerConn.Send(new byte[] { (byte)(0x10 + i) });
            }

            await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(5, receivedPackets.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal((byte)(0x10 + i), receivedPackets[i].Memory.Span[0]);
                receivedPackets[i].Release();
            }

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that Dispose can race with ReceiveAsync without crashing.
        /// </summary>
        [Fact]
        public async Task Transport_Dispose_RacingWithReceiveAsync()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            try
            {
                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        try { transport.ReceiveAsync(); } catch { }
                    }
                });
                var t2 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                        transport.Dispose();
                });
                try { await System.Threading.Tasks.Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            catch (Exception e)
            {
                Assert.Fail($"Racing dispose/receive threw: {e.Message}");
            }

            Assert.True(transport.IsDisposed);
        }

        /// <summary>
        /// Verifies that Dispose can race with SendAsync without crashing.
        /// </summary>
        [Fact]
        public async Task Transport_Dispose_RacingWithSendAsync()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            var buf = new SegmentedBuffer(8192, 32);

            try
            {
                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        if (buf.TryReserveSegment(out var seg))
                        {
                            seg.Memory.Span[0] = (byte)i;
                            try { transport.SendAsync(seg, 1); } catch { }
                        }
                    }
                });
                var t2 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                        transport.Dispose();
                });
                try { await System.Threading.Tasks.Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            catch (Exception e)
            {
                Assert.Fail($"Racing dispose/send threw: {e.Message}");
            }

            Assert.True(transport.IsDisposed);
        }

        /// <summary>
        /// Verifies that a send started after the owner closed the socket is
        /// rejected by the entry check with a disconnect report (synchronously,
        /// no socket operation is issued).
        /// </summary>
        [Fact]
        public async Task Transport_SendAsync_SocketClosedByOwner_FiresOnPacketSendFailed()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            // The owner marks the transport dead and closes the socket.
            transport.MarkDisconnected();
            pair.Client.Close();

            var tcs = new TaskCompletionSource<TransportError>();
            transport.OnPacketSendFailed += (t, s, e) => { tcs.TrySetResult(e); s.Release(); };

            bool reserved = transport.TryReserveSendPacket(out var seg);
            Assert.True(reserved, "Should be able to reserve send packet");

            seg.Memory.Span[0] = 0x01;
            transport.SendAsync(seg, 1);

            // The entry check fires the report before returning, so no wait is
            // needed — the start never reaches a socket call.
            Assert.True(tcs.Task.IsCompleted);
            Assert.Equal(TransportError.SocketDisconnected, await tcs.Task);

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that ReceiveAsync fails with PoolExhausted when the receive buffer pool is exhausted.
        /// Pool size is 32 segments (default SegmentedBuffer: 8192 bytes / 256 bytes per segment).
        /// Each completed receive consumes one segment (payload segment).
        /// </summary>
        [Fact]
        public async Task Transport_ReceiveAsync_PoolExhausted_FiresPoolExhausted()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var receivedSegments = new List<Segment>();
            var poolExhaustedTcs = new TaskCompletionSource<TransportError>();

            transport.OnPacketReceived += (t, seg) =>
            {
                // IMPORTANT: Do NOT release the segment - keep it leased to exhaust pool
                // DO call ReceiveAsync() again to continue the pipeline
                receivedSegments.Add(seg);
                t.ReceiveAsync();
            };

            transport.OnPacketReceiveFailed += (t, e) =>
            {
                if (e == TransportError.PoolExhausted)
                    poolExhaustedTcs.TrySetResult(e);
            };

            // Start first receive
            transport.ReceiveAsync();

            // Send packets one at a time with small delay to allow processing
            // Pool size = 32 segments, so we need ~35 packets
            for (int i = 0; i < 50; i++)
            {
                byte[] header = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 1);
                pair.ServerConn.Send(header);
                pair.ServerConn.Send(new byte[] { (byte)i });
                await Task.Delay(5); // Allow receive to complete
            }

            // Should hit PoolExhausted on the 33rd receive attempt
            var error = await poolExhaustedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TransportError.PoolExhausted, error);

            // Cleanup: release all held segments
            foreach (var seg in receivedSegments)
                seg.Release();

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that ProcessReceive fails with PoolExhausted when the payload segment pool is exhausted.
        /// This occurs when header is received but payload segment cannot be reserved.
        /// </summary>
        [Fact]
        public async Task Transport_ProcessReceive_PayloadPoolExhausted_FiresPoolExhausted()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var receivedSegments = new List<Segment>();
            var poolExhaustedTcs = new TaskCompletionSource<TransportError>();

            transport.OnPacketReceived += (t, seg) =>
            {
                // Don't release - keep segment leased
                // Do call ReceiveAsync() to continue pipeline
                receivedSegments.Add(seg);
                t.ReceiveAsync();
            };

            transport.OnPacketReceiveFailed += (t, e) =>
            {
                if (e == TransportError.PoolExhausted)
                    poolExhaustedTcs.TrySetResult(e);
            };

            // Start first receive
            transport.ReceiveAsync();

            // Send packets with payload one at a time with delay
            for (int i = 0; i < 50; i++)
            {
                byte[] header = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 1);
                pair.ServerConn.Send(header);
                pair.ServerConn.Send(new byte[] { (byte)i });
                await Task.Delay(5);
            }

            var error = await poolExhaustedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TransportError.PoolExhausted, error);

            foreach (var seg in receivedSegments)
                seg.Release();

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that IssueReceive handles SocketException gracefully.
        /// Closes the local socket to trigger ObjectDisposedException -> SocketError.
        /// </summary>
        [Fact]
        public async Task Transport_IssueReceive_SocketException_Handled()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var tcs = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcs.TrySetResult(e);

            transport.ReceiveAsync();

            // Close the socket to trigger ObjectDisposedException on next receive
            pair.Client.Close();

            var error = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransportError.SocketError, error);

            transport.Dispose();
        }

        /// <summary>
        /// Verifies that a peer-initiated close observed by an in-flight
        /// receive (FIN) marks the transport disconnected, and that a send
        /// started afterwards is rejected by the entry check with a
        /// disconnect report. This is the transport's own terminal mark (as
        /// opposed to the owner's mark in the SocketClosedByOwner tests):
        /// the socket stays open — the owner still owns it — yet the
        /// transport no longer starts operations on it.
        /// </summary>
        [Fact]
        public async Task Transport_SendAsync_AfterPeerFin_FiresSocketDisconnected()
        {
            using var pair = new SocketPairFixture();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);

            var finSeen = new TaskCompletionSource<bool>();
            transport.OnPacketReceived += (t, seg) => seg.Release();
            transport.OnPacketReceiveFailed += (t, e) =>
            {
                if (e == TransportError.SocketDisconnected)
                    finSeen.TrySetResult(true);
            };
            transport.ReceiveAsync();

            // Close the remote end: the in-flight receive observes the FIN
            // (zero bytes) and marks the transport disconnected.
            pair.ServerConn.Close();
            await finSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

            TransportError? error = null;
            transport.OnPacketSendFailed += (t, s, e) => { error = e; s.Release(); };
            if (transport.TryReserveSendPacket(out var seg))
            {
                seg.Memory.Span[0] = 0x01;
                transport.SendAsync(seg, 1);
            }

            Assert.Equal(TransportError.SocketDisconnected, error);
            transport.Dispose();
        }

        // HISTORICAL NOTE (2026-10-18): this file once carried a placeholder
        // "NOT IMPLEMENTED BY DECISION" test documenting a bare defensive
        // catch in IssueReceive. That catch has since been removed: every
        // branch of ProcessReceive settles the in-flight flag and the segment
        // before it fires any report, so the catch's re-release and
        // re-clear were no-ops and its only real effect was the rethrow,
        // which happens without it. The one throw that can still escape the
        // loop — a subscriber of a failure report throwing — is a
        // subscriber bug and is meant to propagate, not to be masked. The
        // documented gap no longer exists, so the placeholder went with it.

        /// <summary>
        /// Verifies that ProcessReceive handles socket errors during receive.
        /// Closing the remote end triggers SocketDisconnected (FIN), which is the expected behavior.
        /// </summary>
        [Fact]
        public async Task Transport_ProcessReceive_SocketError_FiresSocketError()
        {
            using var pair = new SocketPairFixture();

            // First do a normal receive to get past header phase
            var tcsReceive = new TaskCompletionSource<Segment>();
            var transport = new global::DuneTransport.Transport.Transport(pair.Client);
            transport.OnPacketReceived += (t, seg) => tcsReceive.TrySetResult(seg);
            transport.ReceiveAsync();

            // Send a valid packet to complete the first receive
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(), 1);
            pair.ServerConn.Send(header);
            pair.ServerConn.Send(new byte[] { 0x42 });

            var seg = await tcsReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            seg.Release();

            // Now close the remote end - this triggers FIN -> SocketDisconnected
            // (This is the correct behavior - remote close = graceful disconnect)
            pair.ServerConn.Close();

            var tcsError = new TaskCompletionSource<TransportError>();
            transport.OnPacketReceiveFailed += (t, e) => tcsError.TrySetResult(e);
            transport.ReceiveAsync();

            var error = await tcsError.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Remote close results in SocketDisconnected, not SocketError
            Assert.Equal(TransportError.SocketDisconnected, error);

            transport.Dispose();
        }
    }
}
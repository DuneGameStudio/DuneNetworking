using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.Transport;


namespace DuneSession.SocketConnectors
{
    /// <summary>
    /// Asynchronous TCP client connector implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manages the socket lifecycle for outbound connections. Uses an in-flight guard
    /// to prevent simultaneous connection attempts.
    /// </para>
    /// <para>
    /// A successful reconnect disposes the connection it replaces — captured before
    /// the field is overwritten, so no interleaving loses it — and a failed attempt
    /// never touches the previous one (its Disconnected report is exactly once and
    /// must not fire as a side effect of a failed reconnect). The dispose is
    /// idempotent, so an application holding its own reference to the replaced
    /// connection is unaffected by the double dispose.
    /// </para>
    /// <para>
    /// Events are never raised after <see cref="Dispose"/>: an in-flight connect is
    /// abandoned — its socket is disposed (which aborts the operation), and the late
    /// completion exits at the in-flight guard without raising anything.
    /// </para>
    /// <para>
    /// Dispose follows the close protocol: it holds for a connect start that is
    /// between claiming and issuing (a close must never outrun that window), then
    /// disposes the socket, running over an in-flight connect. A start whose
    /// re-read sees the disposed flag bails instead of issuing, so the socket
    /// call can never observe a disposed socket.
    /// </para>
    /// </remarks>
    public class ClientConnector : IClientConnector
    {
        private volatile Socket? socket;
        private volatile IConnection? connection;
        private readonly TransportOptions _options;
        /// <summary>Connection attempt state: 0 = idle, 1 = claimed (a start is
        /// running: between claiming and issuing — a transient with no blocking call
        /// inside), 2 = in flight (the connect is in the kernel, or its completion is
        /// being processed). The claim is a compare-and-set from idle, so a state-1
        /// slot always has a live owning thread inside its window — the dispose below
        /// holds for it, and the hold always resolves because that window holds no
        /// blocking call. A dispose may never outrun a state-1 start, but may safely
        /// run over a state-2 operation, whose aborted completion exits silently at
        /// the guard. Accessed only through Interlocked and Volatile.</summary>
        private int connectingState;
        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public bool IsConnected => connection?.IsConnected ?? false;

        public event Action<IConnection>? OnConnected;

        public event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Creates a new client connector.
        /// </summary>
        /// <remarks>
        /// Call <see cref="Dispose"/> when no longer needed.
        /// </remarks>
        public ClientConnector() : this(new TransportOptions())
        {
        }

        /// <summary>
        /// Creates a new client connector with custom segment pool sizing for its connections.
        /// </summary>
        /// <param name="options">Segment pool sizing applied to each <see cref="Connection"/> this connector produces. Must not be null.</param>
        public ClientConnector(TransportOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public bool ConnectAsync(string address, int port)
        {
            // Guard against calls after Dispose — prevents ObjectDisposedException
            if (Volatile.Read(ref _disposed) == 1)
                return false;

            if (IsConnected)
                return false;

            // Claim with a compare-and-set from idle only: a losing caller must not
            // write the slot — a plain exchange would clobber a state-2 (in-flight)
            // slot with its own 1 and leave a claim with no owner for Dispose's
            // hold to wait on forever.
            if (Interlocked.CompareExchange(ref connectingState, 1, 0) != 0)
                return false;

            // A fresh SocketAsyncEventArgs per attempt: reusing one across overlapping
            // operations collides the callbacks (both share the object, and the first
            // to run clears the guard, so the other exits silently).
            var connectEventArgs = new SocketAsyncEventArgs();
            connectEventArgs.Completed += OnConnectCompleted;

            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var s = socket;
                connectEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port);

                // Mutual confirmation with Dispose: Dispose holds for our claim
                // before it disposes the socket, and we re-read the disposed flag
                // after claiming. At least one side always sees the other, so a
                // start that proceeds is guaranteed to find the socket alive —
                // the dispose cannot land between this re-read and the issue.
                // (The local read above is non-null for the same reason: it
                // happens inside our claim, and Dispose cannot dispose and null
                // the field until its hold sees the claim released.) If Dispose
                // already ran, nothing is started: clean up locally and bail.
                if (Volatile.Read(ref _disposed) == 1)
                {
                    connectEventArgs.Dispose();
                    FailAttempt(SocketError.SocketError);
                    return false;
                }

                bool pending = s.ConnectAsync(connectEventArgs);
                if (pending)
                {
                    // The operation is now in the kernel: truly in flight, and a
                    // dispose may safely run over it (it aborts and the late
                    // completion exits at the guard). This transition is what
                    // releases any dispose that was holding for our claim.
                    Interlocked.Exchange(ref connectingState, 2);
                    return true;
                }

                ProcessConnect(connectEventArgs);
                return true;
            }
            catch (SocketException)
            {
                connectEventArgs.Dispose();
                FailAttempt(SocketError.SocketError);
                return false;
            }
            catch (FormatException)
            {
                connectEventArgs.Dispose();
                FailAttempt(SocketError.SocketError);
                return false;
            }
        }

        private void OnConnectCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessConnect(e);
        }

        /// <summary>
        /// Processes a completed connection attempt.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On success: disposes the connection the field held (if any), wraps the
        /// socket in a <see cref="Connection"/>, and fires <see cref="OnConnected"/> —
        /// unless the connector was disposed in the meantime, in which case the
        /// connection is handed back to nothing.
        /// On failure: <see cref="FailAttempt"/> — which deliberately does not
        /// dispose the previous connection (see the success path below).
        /// </para>
        /// </remarks>
        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            // Single winner per attempt. A late completion after Dispose exits
            // here — the socket (if any) already belongs to Dispose. State 0
            // means Dispose cleared the slot first; both 1 (synchronous
            // completion) and 2 (in flight) are legitimate completions.
            if (Interlocked.Exchange(ref connectingState, 0) == 0)
            {
                e.Dispose();
                return;
            }

            // Dispose the per-attempt SocketAsyncEventArgs to release its pooled buffer
            e.Dispose();

            if (e.SocketError != SocketError.Success)
            {
                FailAttempt(e.SocketError);
                return;
            }

            // A successful connect replaces whatever connection the field held,
            // and the replaced connection must be settled, not dropped: after a
            // remote FIN the transport is marked dead and IsConnected reads
            // false, but nothing closes the socket — closing is the owner's
            // (the application's) job — so an application that reconnects
            // without disposing its own reference would lose the connector's
            // only reference to a dead-but-undisposed connection: two segment
            // pools, two socket event objects, and a still-open half-closed
            // socket, all waiting on the application's reference or the
            // finalizer. Capture the reference BEFORE the overwrite, so no
            // interleaving can lose it, and dispose it here — on the success
            // path only. A failed attempt must NOT dispose the previous
            // connection: it may be dead-but-undisposed, and disposing it
            // would fire its exactly-once Disconnected event as a side effect
            // of a failed reconnect — a surprise. The application keeps
            // ownership of a connection its reconnect failed to replace.
            //
            // Safe against the dispose protocol: the replaced connection's
            // Dispose runs that connection's own protocol on its own
            // socket/transport — every flag it touches is internal to the old
            // connection (its own disposed guard, its own disconnect claim,
            // its own transport state); the only state shared with this
            // connector is the `connection` field, which was captured before
            // the overwrite, so no reference is lost. Dispose is idempotent
            // on both objects, so if the connector's own Dispose races this
            // path the orderings are: (a) Dispose reads the field before the
            // overwrite — it disposes the previous, and this path's
            // previous?.Dispose() is then a no-op; (b) Dispose reads it after
            // — it disposes the new connection, and this path's disposed
            // re-read disposes the new one too (no-op) and hands nothing out,
            // while the previous was already disposed by the path. Every
            // interleaving disposes the previous connection effectively once
            // and never leaks it.
            IConnection? previous = connection;
            var conn = new Connection(e.ConnectSocket, _options);
            connection = conn;
            socket = null; // the connection owns the socket now

            previous?.Dispose();

            // Dispose may have run between operation completion and this callback:
            // hand nothing out in that case.
            if (Volatile.Read(ref _disposed) == 1)
            {
                connection = null;
                conn.Dispose();
                return;
            }

            try
            {
                OnConnected?.Invoke(conn);
            }
            catch
            {
                connection = null;
                conn.Dispose();
            }
        }

        /// <summary>
        /// Single cleanup for every failure path: disposes the attempt socket, clears
        /// the in-flight guard, and fires <see cref="OnConnectFailed"/> — subscriber-
        /// guarded, and never after <see cref="Dispose"/>.
        /// </summary>
        private void FailAttempt(SocketError error)
        {
            // Clear the slot first: on the bail path this is what releases a
            // Dispose that is holding for our claim, before the socket dispose
            // below runs (racing disposes are harmless — disposing an
            // already-disposed socket is a no-op).
            Interlocked.Exchange(ref connectingState, 0);

            socket?.Dispose();
            socket = null;

            if (Volatile.Read(ref _disposed) == 1)
                return;

            try
            {
                OnConnectFailed?.Invoke(error);
            }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            // Close protocol: hold for any start that is between claiming and
            // issuing — a close must never outrun that window, or the issue that
            // follows it would throw on the dead socket — and then proceed,
            // running over an in-flight connect. The held window contains no
            // blocking call, so the hold always resolves; an in-flight connect
            // is never waited for, because aborting it is safe (its late
            // completion exits at the guard).
            var spin = new SpinWait();
            while (Volatile.Read(ref connectingState) == 1)
                spin.SpinOnce();

            connection?.Dispose();
            connection = null;

            // Abandon any in-flight connect: the socket dispose below aborts the
            // pending operation, and the cleared slot makes the late completion
            // exit silently in ProcessConnect.
            Interlocked.Exchange(ref connectingState, 0);
            socket?.Dispose();
            socket = null;
        }
    }
}

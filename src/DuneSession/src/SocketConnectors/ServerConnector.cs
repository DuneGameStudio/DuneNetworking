using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.Transport;


namespace DuneSession.SocketConnectors
{
    /// <summary>
    /// Asynchronous TCP server connector implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manages the listening socket lifecycle. Supports one-shot accept: after each
    /// accepted connection, the application must call <see cref="AcceptConnection"/> again.
    /// </para>
    /// <para>
    /// Closing the socket is idempotent, so every exit point closes unconditionally
    /// instead of tracking which path already closed — the invariant is "the socket
    /// ends up closed and nothing live is left behind", not "closed exactly once by
    /// exactly one line".
    /// </para>
    /// <para>
    /// Dispose protocol: before <see cref="Dispose"/>, call <see cref="StopListening"/>
    /// and wait (bounded) for <see cref="IsAcceptPending"/> to become false. An accept
    /// that completed but has not been delivered when <see cref="Dispose"/> runs leaves
    /// its accepted socket open until finalization — the connector reports the state,
    /// it does not drain for the caller.
    /// </para>
    /// <para>
    /// StopListening and Dispose follow the close protocol: they hold for any accept
    /// start that is between claiming and issuing, and for any start (bind/listen)
    /// that holds its claim — a close must never outrun either window, or the socket
    /// call that follows it would run on a socket about to die — then close, running
    /// over an in-flight accept. A start proceeds only from state 0 (never started)
    /// and bails on 1 (listening) or 2 (dead — a stop, a dispose, or a failed start
    /// closed the socket, and 2 is terminal), so a socket call can never observe a
    /// closed socket.
    /// Dispose writes its disposed mark and the dead state before the hold;
    /// StopListening holds before writing the state, because the state is its
    /// delivery mark and a racing start reads it under its own claim — writing
    /// dead first would let that start bind on the socket about to die and write
    /// listening over it, leaving the state saying listening on a closed socket.
    /// </para>
    /// </remarks>
    public class ServerConnector : IServerConnector
    {
        private readonly Socket socket;
        private readonly SocketAsyncEventArgs acceptEventArgs;
        private readonly int _listenBacklog;
        private readonly TransportOptions _options;

        /// <summary>Socket lifecycle state: 0 = never started (socket fresh),
        /// 1 = listening, 2 = dead (the socket was closed — by a stop, a
        /// dispose, or a failed start — and, being a single readonly instance,
        /// can never be used again). 2 is terminal: written only by the paths
        /// that close the socket, and never reset, so a start that proceeds
        /// only from 0 can never drive a call into a closed socket. Writers:
        /// start success (0→1 under its claim), stop (1→2 after its hold),
        /// dispose (→2 before its hold), failed start (→2 after its close).
        /// Readers: the IsListening property (== 1), the start (proceeds only
        /// from 0), the stop (enters only from 1), accept/ProcessAccept (gated
        /// on 1).</summary>
        private volatile int isListening;
        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <summary>Accept state: 0 = idle, 1 = claimed (an accept start is running:
        /// between claiming and issuing — a transient with no blocking call inside),
        /// 2 = armed or parked (in the kernel, or completed and not yet delivered).
        /// The claim is a compare-and-set from idle, so a state-1 slot always has a
        /// live owning thread inside its window — the closes below hold for it, and
        /// the hold always resolves because that window holds no blocking call. A
        /// close (StopListening or Dispose) may never outrun a state-1 start, but
        /// may safely run over a state-2 accept, whose aborted completion settles
        /// through ProcessAccept's dead-listener path. Accessed only through
        /// Interlocked and Volatile.</summary>
        private int acceptingState;

        /// <summary>Start-in-progress claim: 0 = idle, 1 = a start (bind/listen) is
        /// running. Starting is a check-then-act on a non-idempotent bind: two racing
        /// callers can both see "never started", both drive the bind, and a failure on
        /// the loser's side closes the socket the winner is using. The claim lets
        /// exactly one start run at a time, and its reads of the lifecycle state and
        /// the disposed mark run under the claim, so they see the truth. The closes
        /// (StopListening and Dispose) hold for a claimed start — a close must never
        /// outrun that window — and the hold always resolves because the window holds
        /// no blocking call. Accessed only through Interlocked and Volatile.</summary>
        private int startingState;

        /// <inheritdoc />
        public bool IsListening => isListening == 1;

        /// <inheritdoc />
        public bool IsAcceptPending => Volatile.Read(ref acceptingState) != 0;

        // Subscribers run on socket-completion threads and must not throw:
        // a throw escapes the completion callback and crashes the process.
        public event Action<IConnection>? OnClientConnected;
        public event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Creates a new server connector with a custom listen backlog and optional segment pool sizing.
        /// </summary>
        /// <param name="listenBacklog">Maximum length of the pending connections queue. Must be > 0. Defaults to 128.</param>
        /// <param name="options">Optional segment pool sizing for the <see cref="Connection"/>s this connector produces. Null uses the default pool.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="listenBacklog"/> is less than 1.</exception>
        public ServerConnector(int listenBacklog = 128, TransportOptions? options = null)
        {
            if (listenBacklog < 1) throw new ArgumentOutOfRangeException(nameof(listenBacklog));
            _listenBacklog = listenBacklog;
            _options = options ?? new TransportOptions();

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            acceptEventArgs = new SocketAsyncEventArgs();
            acceptEventArgs.Completed += OnAcceptCompleted;
        }

        /// <inheritdoc />
        public bool TryStartListening(string address, int port, out SocketError error)
        {
            // Claim the start first, then check: every state read below runs under
            // the claim, so a caller that claims sees the truth. A finished start
            // wrote its state before releasing the claim, so a caller that reads
            // under the claim reads the finished state. Checking before claiming is
            // not an option: two racing callers could both read "never started"
            // before either claimed, and the loser would then drive a second
            // SetSocketOption/Bind/Listen on the socket the winner had just bound —
            // re-binding it to a different endpoint, or closing it on a failure
            // while the state still said listening. A caller that loses the claim
            // returns without touching anything.
            if (Interlocked.Exchange(ref startingState, 1) != 0)
            {
                error = SocketError.SocketError;
                return false;
            }

            // Mutual confirmation with the closes: StopListening and Dispose hold
            // for this claim before they close the socket (the close protocol), so
            // no close can land between this re-read and the socket work below —
            // at least one side always sees the other. A start that finds the
            // disposed mark set, or the state already past "never started" (a start
            // that finished under its own claim wrote it before releasing), settles
            // without touching the socket and without an event: a stop is not an
            // error — the same silent dead-listener treatment the accept's re-read
            // gives its issue. Both bails release the claim on the way out.
            if (Volatile.Read(ref _disposed) == 1)
            {
                Interlocked.Exchange(ref startingState, 0);
                error = SocketError.SocketError;
                return false;
            }

            // Proceed ONLY from "never started" (0). 1 = already listening (a
            // start that finished under its own claim wrote it before releasing);
            // 2 = dead (a stop, a dispose, or a failed start closed the socket —
            // a single readonly instance, so it can never listen again). Reading
            // 0 under the claim guarantees a live socket: a stop or dispose that
            // commits while we hold the claim is held by its own hold, and one
            // that finished before the claim left the state at 2, not 0.
            if (isListening != 0)
            {
                Interlocked.Exchange(ref startingState, 0);
                error = SocketError.SocketError;
                return false;
            }

            // Parse outside the socket work: a bad address is a caller error that
            // must leave the socket untouched and retryable, while the close in the
            // socket-work catch below is for a socket the start has already touched.
            // The parse still runs under the claim, so it releases the claim on the
            // way out. TryParse plus an explicit port-range and address-family check
            // is exception-free: the listening socket is InterNetwork (IPv4), so an
            // IPv6 literal is rejected at the value level, before the socket work
            // can see a family mismatch. On failure the call reports InvalidArgument
            // — no throw and no event; the out parameter is the start's only
            // failure channel.
            if (!IPAddress.TryParse(address, out IPAddress? parsed)
                || port < 0 || port > 65535
                || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                Interlocked.Exchange(ref startingState, 0);
                error = SocketError.InvalidArgument;
                return false;
            }

            IPEndPoint endPoint = new IPEndPoint(parsed!, port);

            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                socket.Bind(endPoint);
                socket.Listen(_listenBacklog);
            }
            catch (SocketException ex)
            {
                // The socket may be partially bound — close it before recording the
                // death and releasing the claim: a caller that claims in the gap
                // then sees the dead state in its own checks and bails instead of
                // binding over a live socket. Releasing first would let a third
                // caller claim and bind in that gap, and this close would land on
                // its fresh binding. The failure is reported, not thrown: the OS
                // code rides out in `error`.
                //
                // The close kills the connector permanently (the socket is a single
                // readonly instance), so record the death in the state (2, not 0)
                // before the release: a retry that read "never started" would drive
                // its bind into the closed socket and throw.
                //
                // Only SocketException can reach this catch: the endpoint is
                // pre-validated (IPv4 literal, in-range port), and a closed socket
                // is unreachable here — the state was 0 under the claim, and a
                // stop or dispose that commits while we hold the claim is held by
                // its own hold. Anything else escaping would be a protocol
                // violation — better to fail loudly than mask it with a catch-all
                // value.
                try { socket.Close(); } catch { }
                isListening = 2;
                Interlocked.Exchange(ref startingState, 0);
                error = ex.SocketErrorCode;
                return false;
            }

            // Write the state first, then re-read the disposed mark — write-then-
            // read mutual confirmation with Dispose. A Dispose that ran in the gap
            // between the re-read above and this bind wrote its own disposed claim
            // first, so at least one side sees the other: if this re-read finds it
            // set, the bind raced a dispose, so undo the state (the socket is being
            // closed) and return. Re-reading before the write would let a Dispose
            // whose clear lands in between leave the state saying listening on a
            // closed socket — the stale state this ordering removes.
            isListening = 1;
            if (Volatile.Read(ref _disposed) == 1)
            {
                // The racing dispose closes the socket — record the death (2),
                // never 0: a "never started" reading on a closed socket is what
                // lets a late start drive a bind into it.
                isListening = 2;
                Interlocked.Exchange(ref startingState, 0);
                error = SocketError.SocketError;
                return false;
            }
            Interlocked.Exchange(ref startingState, 0);
            error = SocketError.Success;
            return true;
        }

        /// <inheritdoc />
        public void StopListening()
        {
            if (isListening != 1)
                return;

            // Close protocol, owner side — with the hold before the state write,
            // unlike Dispose. The state is this close's delivery mark, and a
            // racing start reads it under its own claim: if "dead" went up first,
            // a start that read "never started" earlier could still bind on the
            // socket about to die and write "listening" over it — listening on a
            // closed socket. Holding first, a start that races this close reads
            // the state still listening and bails; if the stop has already
            // completed, the late start's own re-reads settle it. The state write
            // below (after the hold) records "dead" — a value that survives, since
            // a bare clear to 0 would read as "never started" to a late start,
            // which would then drive a bind into the closed socket. Dispose can
            // keep mark-then-hold because its separate disposed claim is what the
            // start's post-bind re-read confirms against.
            //
            // The hold waits for any accept start that is between claiming and
            // issuing — a close must never outrun that window, or the issue that
            // follows it would throw on the dead socket — and for any start
            // (bind/listen) holding its claim the same way. Then it marks and
            // closes, running over an in-flight accept, whose aborted completion
            // settles through ProcessAccept's dead-listener path. Both held windows
            // contain no blocking call (the start's is a parse plus three fast
            // socket calls on a live socket), so the hold always resolves — and a
            // close that waited for a start guarantees that start's bind ran on a
            // live socket, so a throw from a start that races a close is
            // unreachable.
            var spin = new SpinWait();
            while (Volatile.Read(ref acceptingState) == 1 || Volatile.Read(ref startingState) == 1)
                spin.SpinOnce();
            isListening = 2;
            try { socket.Close(); } catch { }
        }

        /// <inheritdoc />
        public void AcceptConnection()
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            if (isListening != 1)
                return;

            // Claim the accept slot with a compare-and-set from idle only: a losing
            // caller must not write the slot — a plain exchange would clobber a
            // state-2 (armed) slot with its own 1 and then return, leaving a
            // state-1 claim that no thread owns, which a close holding for that
            // window would wait on forever. (A plain check would not close the
            // window either — two callers could both read "not claimed" before
            // either sets the flag — and two starts on one event object would
            // throw.)
            if (Interlocked.CompareExchange(ref acceptingState, 1, 0) != 0)
                return;

            // Mutual confirmation with the closes: StopListening and Dispose hold
            // for this claim before they close the socket, and the start re-reads
            // their marks after claiming. At least one side always sees the other, so a
            // start that proceeds is guaranteed to find the socket alive — a close
            // can never land between this re-read and the issue. If a close
            // already ran, the listener is gone: settle without issuing and without
            // a report — the dead-listener completion takes the same silent orphan
            // path, so a stop is not an error.
            if (Volatile.Read(ref _disposed) == 1 || isListening != 1)
            {
                Interlocked.Exchange(ref acceptingState, 0);
                return;
            }

            acceptEventArgs.AcceptSocket = null;

            bool pending;
            try
            {
                pending = socket.AcceptAsync(acceptEventArgs);
            }
            catch (SocketException ex)
            {
                Interlocked.Exchange(ref acceptingState, 0);
                FireAcceptFailedIfAlive(ex.SocketErrorCode);
                return;
            }

            if (pending)
            {
                // The operation is now in the kernel: armed. A close may safely
                // run over it (it aborts and settles through ProcessAccept). This
                // transition is what releases any close that was holding for our
                // claim.
                Interlocked.Exchange(ref acceptingState, 2);
                return;
            }

            ProcessAccept(acceptEventArgs);
        }

        private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        /// <summary>Fires <see cref="OnAcceptFailed"/> but never after <see cref="Dispose"/>. The subscriber must not throw.</summary>
        private void FireAcceptFailedIfAlive(SocketError error)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            OnAcceptFailed?.Invoke(error);
        }

        /// <summary>
        /// Processes a completed accept operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On success: wraps the accepted socket in a <see cref="Connection"/> and fires
        /// <see cref="OnClientConnected"/>. On failure: fires <see cref="OnAcceptFailed"/>.
        /// If nobody takes the accepted socket (listener gone, or no subscriber), it is
        /// closed here — disposing the event object (the <see cref="SocketAsyncEventArgs"/>)
        /// does not close it.
        /// </para>
        /// </remarks>
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 1 || isListening != 1)
            {
                // The listener is gone and nobody will take this socket.
                Interlocked.Exchange(ref acceptingState, 0);
                CloseOrphanedAccept(e);
                return;
            }

            if (e.SocketError != SocketError.Success || e.AcceptSocket == null)
            {
                Interlocked.Exchange(ref acceptingState, 0);
                FireAcceptFailedIfAlive(e.SocketError);
                return;
            }

            // No subscriber means no owner: close the socket directly. No Connection
            // was ever created on this path, so there is nothing else to dispose.
            if (OnClientConnected == null)
            {
                Interlocked.Exchange(ref acceptingState, 0);
                CloseOrphanedAccept(e);
                return;
            }

            // The operation is complete and the socket is being handed off, so the
            // event object is free again: clear the state before the delivery fires,
            // both so a re-arm from inside the handler is not gated, and so the
            // dispose protocol can proceed while the application takes the connection.
            Interlocked.Exchange(ref acceptingState, 0);
            IConnection connection = new Connection(e.AcceptSocket, _options);
            // Contract: the subscriber must not throw; a throw escapes the completion
            // callback and crashes the process.
            OnClientConnected(connection);
        }

        /// <summary>Closes an accepted socket that nobody owns, and detaches it from the event object.</summary>
        private static void CloseOrphanedAccept(SocketAsyncEventArgs e)
        {
            Socket? accepted = e.AcceptSocket;
            e.AcceptSocket = null;
            accepted?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            // Close protocol, owner side: the disposed mark set above is this
            // close's mark, and the close follows it — holding for any accept
            // start that is between claiming and issuing (a close must never
            // outrun that window, or the issue that follows it would throw on the
            // dead socket) and for any start (bind/listen) holding its claim the
            // same way. Then it closes, running over an in-flight accept, whose
            // aborted completion settles through ProcessAccept's dead-listener
            // path. Both held windows contain no blocking call (the start's is a
            // parse plus three fast socket calls on a live socket), so the hold
            // always resolves — and a dispose that waited for a start guarantees
            // that start's bind ran on a live socket, so a throw from a start that
            // races a close is unreachable.
            isListening = 2;
            var spin = new SpinWait();
            while (Volatile.Read(ref acceptingState) == 1 || Volatile.Read(ref startingState) == 1)
                spin.SpinOnce();
            Interlocked.Exchange(ref acceptingState, 0);
            acceptEventArgs.Completed -= OnAcceptCompleted;
            acceptEventArgs.Dispose();

            // One unconditional close covers every start state: listening, never
            // started, failed start. Idempotent — a second close is a no-op.
            try { socket.Close(); } catch { }
        }
    }
}

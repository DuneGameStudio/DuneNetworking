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
    /// Closing the socket is idempotent, so the code below closes unconditionally at
    /// every exit point instead of tracking which path already closed — the invariant
    /// is "the socket ends up closed and nothing live is left behind", not "closed
    /// exactly once by exactly one line".
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
    /// call that follows it would throw on a socket about to die — then close, running
    /// over an in-flight accept. A start whose re-read sees the stop or the dispose
    /// bails instead of issuing, so a socket call can never observe a closed socket.
    /// Dispose writes its disposed mark before the hold; StopListening holds before
    /// clearing the flag, because the flag is its only mark and a racing start reads
    /// it under its own claim — clearing first would let that start bind on the
    /// socket about to die and leave the flag true on a closed socket.
    /// </para>
    /// </remarks>
    public class ServerConnector : IServerConnector
    {
        private readonly Socket socket;
        private readonly SocketAsyncEventArgs acceptEventArgs;
        private readonly int _listenBacklog;
        private readonly TransportOptions _options;

        /// <summary>Listening flag: 0 = not listening, 1 = listening.</summary>
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
        /// callers both see "not listening", both drive the bind, and a failure on the
        /// loser's side closes the socket the winner is using. The claim lets exactly
        /// one start run at a time, and its reads of the listening and disposed marks
        /// run under the claim, so they see the truth. The closes (StopListening and
        /// Dispose) hold for a claimed start — a close must never outrun that window —
        /// and the hold always resolves because the window holds no blocking call.
        /// Accessed only through Interlocked and Volatile.</summary>
        private int startingState;

        /// <inheritdoc />
        public bool IsListening => isListening == 1;

        /// <inheritdoc />
        public bool IsAcceptPending => Volatile.Read(ref acceptingState) != 0;

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
        public void StartListening(string address, int port)
        {
            // Claim the start FIRST, then check. The listening and disposed reads
            // run under the claim, so a caller that claims sees the truth: a start
            // that finished set the listening flag before releasing the claim, so
            // a caller that reads it under the claim reads the finished state, and
            // a caller that reads a stale "not listening" — the old check-before-
            // claim window, where two racing callers both passed the check before
            // either claimed and the loser then drove a second SetSocketOption/Bind/
            // Listen on the socket the winner had just bound (re-binding it to a
            // different endpoint, or closing it on a throw while the flag still
            // said listening) — can no longer exist. A caller that loses the claim
            // returns without touching anything.
            if (Interlocked.Exchange(ref startingState, 1) != 0)
                return;

            // Mutual confirmation with the closes: StopListening and Dispose hold
            // for our claim before they close the socket (the close protocol), so a
            // close cannot land between this re-read and the socket work below — at
            // least one side always sees the other. A start that finds the disposed
            // mark set, or the connector already listening (a start that finished
            // under its own claim set the flag before releasing), settles without
            // touching the socket and without a report — a stop is not an error, the
            // same silent dead-listener treatment the accept's re-read gives its
            // issue. Both bails release the claim on the way out.
            if (Volatile.Read(ref _disposed) == 1 || isListening == 1)
            {
                Interlocked.Exchange(ref startingState, 0);
                return;
            }

            // Parse outside the socket work: a bad address is a caller error that
            // must leave the socket untouched and retryable, while the close in the
            // socket-work catch below exists for a socket the start has already
            // touched. The parse still runs under the claim, so it releases the
            // claim on the way out — bookkeeping plus rethrow at a true external
            // boundary, not a mask.
            IPEndPoint endPoint;
            try
            {
                endPoint = new IPEndPoint(IPAddress.Parse(address), port);
            }
            catch
            {
                Interlocked.Exchange(ref startingState, 0);
                throw;
            }

            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                socket.Bind(endPoint);
                socket.Listen(_listenBacklog);
            }
            catch
            {
                // The socket may be partially bound — close it BEFORE releasing the
                // claim. A caller that claims in the gap then sees a dead socket in
                // its own checks and bails instead of binding over a live one; the
                // old order (release first) let a third caller claim and bind in that
                // gap and then have this close land on its fresh binding. The
                // listening flag was never set, so nothing else to undo.
                try { socket.Close(); } catch { }
                Interlocked.Exchange(ref startingState, 0);
                throw;
            }

            // Write the flag FIRST, then re-read the disposed flag — write-then-read
            // mutual confirmation with Dispose. A Dispose that ran in the gap between
            // the re-read above and this bind wrote its own disposed-claim first, so
            // at least one side sees the other: if the re-read finds it set, the bind
            // raced a dispose, so undo the flag (the socket is being closed) and
            // return. Re-reading before the write would let a Dispose whose clear
            // lands in between leave the flag true on a closed socket — the zombie
            // this ordering removes.
            isListening = 1;
            if (Volatile.Read(ref _disposed) == 1)
            {
                isListening = 0;
                Interlocked.Exchange(ref startingState, 0);
                return;
            }
            Interlocked.Exchange(ref startingState, 0);
        }

        /// <inheritdoc />
        public void StopListening()
        {
            if (isListening != 1)
                return;

            // Close protocol, owner side — but with the hold BEFORE the mark,
            // unlike Dispose. The flag is this close's only mark, and a racing
            // start reads it under its own claim: if the mark went down first,
            // that start would read "not listening", bind on the socket that is
            // about to die, set the flag, and then this close would land on it —
            // the flag true on a closed socket. Holding first, a start that
            // races this close reads the flag still set and bails, or the stop
            // has already completed and the start's own checks settle it;
            // Dispose can keep mark-then-hold because its separate disposed
            // claim is what the start's post-bind re-read confirms against. The
            // hold waits for any accept start that is between claiming and
            // issuing — a close must never outrun that window, or the issue
            // that follows it would throw on the dead socket — and for any
            // start (bind/listen) holding its claim the same way. Then it marks
            // and closes, running over an in-flight accept, whose aborted
            // completion settles through ProcessAccept's dead-listener path.
            // Both held windows contain no blocking call (the start's is a
            // parse plus three fast socket calls on a live socket), so the
            // hold always resolves — and a close that waited for a start
            // guarantees that start's bind ran on a live socket, so a throw
            // from a start that races a close is unreachable.
            var spin = new SpinWait();
            while (Volatile.Read(ref acceptingState) == 1 || Volatile.Read(ref startingState) == 1)
                spin.SpinOnce();
            isListening = 0;
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
            // for our claim before they close the socket, and we re-read their
            // marks after claiming. At least one side always sees the other, so a
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

        /// <summary>Fires <see cref="OnAcceptFailed"/> with a throwing-subscriber guard, but never after <see cref="Dispose"/>.</summary>
        private void FireAcceptFailedIfAlive(SocketError error)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            try
            {
                OnAcceptFailed?.Invoke(error);
            }
            catch { }
        }

        /// <summary>
        /// Processes a completed accept operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On success: wraps the accepted socket in a <see cref="Connection"/> and fires
        /// <see cref="OnClientConnected"/>. On failure: fires <see cref="OnAcceptFailed"/>.
        /// If nobody takes the accepted socket (listener gone, or no subscriber), it is
        /// closed here — SAEA disposal does not close it.
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

            // The operation is complete and the socket is being handed off, so the SAEA
            // is free again: clear the flag BEFORE the delivery fires, both so a re-arm
            // from inside the handler is not gated, and so the dispose protocol can
            // proceed while the app takes the connection.
            Interlocked.Exchange(ref acceptingState, 0);
            IConnection connection = new Connection(e.AcceptSocket, _options);
            try
            {
                OnClientConnected(connection);
            }
            catch
            {
                // Subscriber failed to take the connection — dispose to prevent socket leak
                connection.Dispose();
            }
        }

        /// <summary>Closes an accepted socket that nobody owns, and detaches it from the SAEA.</summary>
        private static void CloseOrphanedAccept(SocketAsyncEventArgs e)
        {
            Socket? accepted = e.AcceptSocket;
            e.AcceptSocket = null;
            accepted?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            // Close protocol, owner side: the disposed flag above is the mark,
            // and this close follows it — holding for any accept start that is
            // between claiming and issuing (a close must never outrun that
            // window, or the issue that follows it would throw on the dead
            // socket) and for any start (bind/listen) holding its claim the
            // same way, because a start that races this dispose would otherwise
            // have its bind run on the socket about to die. Then it closes,
            // running over an in-flight accept, whose aborted completion settles
            // through ProcessAccept's dead-listener path. Both held windows
            // contain no blocking call (the start's is a parse plus three fast
            // socket calls on a live socket), so the hold always resolves — and
            // a dispose that waited for a start guarantees that start's bind ran
            // on a live socket, so a throw from a start that races a close is
            // unreachable.
            isListening = 0;
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

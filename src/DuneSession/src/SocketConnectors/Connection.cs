using System;
using System.Net.Sockets;
using System.Threading;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;

namespace DuneSession.SocketConnectors
{
    /// <summary>
    /// An active socket connection with an embedded <see cref="ITransport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a connected socket and provides the <see cref="Transport"/> property for
    /// sending and receiving data. Reports connection state through
    /// <see cref="IsConnected"/> and closes the socket when asked via
    /// <see cref="DisconnectAsync"/> — it never decides what a transport failure
    /// means; that is the application's job.
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> is safe to call at any time. The socket's close
    /// follows the close protocol: it holds for any start that is between
    /// claiming and issuing — a transport operation or a disconnect (a close
    /// must never outrun that window) — then closes, running over in-flight
    /// operations; aborting one is safe, and for a receive against a live peer
    /// the close is what settles it. A disconnect that is already in the kernel
    /// settles through its own completion, which runs the same close; Dispose's
    /// hold ends the moment the caller's socket call returns, so the handle is
    /// released then, not when the peer answers.
    /// </para>
    /// </remarks>
    public class Connection : IConnection
    {
        private readonly Socket socket;

        /// <summary>Connection state flag: 1 = connected, 0 = disconnected.</summary>
        private volatile int connectedState;

        /// <summary>Disconnect state: 0 = no disconnect, 1 = claimed (a start is
        /// running: between claiming and the socket call returning — a transient
        /// with no blocking call inside, except the call itself), 2 = the socket
        /// call has settled (in the kernel, or processed synchronously). A
        /// connection disconnects at most once, so 2 is terminal; only the bail
        /// that settles instead of issuing clears the claim. The claim is a
        /// compare-and-set from idle, so a state-1 slot always has a live owning
        /// thread inside its window — Dispose holds for it before closing, and
        /// the hold always resolves because no close can be in flight while the
        /// slot is claimed, so the window's socket call runs on a live socket
        /// and returns. Accessed only through Interlocked and Volatile.</summary>
        private int disconnectingState;

        /// <inheritdoc />
        public bool IsConnected => connectedState == 1 && Transport.IsConnected;

        /// <inheritdoc />
        // Subscribers must not throw: on the FIN-completion thread a throw escapes
        // the callback and crashes the process.
        public event Action? Disconnected;

        /// <summary>Disconnect report guard: 0 = not yet reported, 1 = reported.</summary>
        private int _disconnected;

        /// <inheritdoc />
        public ITransport Transport { get; }

        /// <summary>The concrete transport: the connection needs its
        /// owner-protocol members (not part of the app-facing interface).</summary>
        private readonly Transport transport;

        // The event object for the graceful disconnect (a SocketAsyncEventArgs):
        // a connection disconnects at most once, so one object serves it.
        private readonly SocketAsyncEventArgs disconnectAsyncSocketAsyncEventArgs;
        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <summary>
        /// Creates a new connection wrapping the given socket.
        /// </summary>
        /// <param name="socket">An already-connected socket.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Creates a <see cref="Transport"/> for the socket. The connection does not
        /// subscribe to any transport events — reporting stays with the transport.
        /// </para>
        /// </remarks>
        public Connection(Socket socket) : this(socket, new TransportOptions())
        {
        }

        /// <summary>
        /// Creates a new connection wrapping the given socket with custom segment pool sizing.
        /// </summary>
        /// <param name="socket">An already-connected socket. Must not be null.</param>
        /// <param name="options">Segment pool sizing for the embedded transport. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> or <paramref name="options"/> is null.</exception>
        public Connection(Socket socket, TransportOptions options)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            TransportOptions resolvedOptions = options ?? throw new ArgumentNullException(nameof(options));
            connectedState = 1;

            transport = new Transport(socket, resolvedOptions);
            Transport = transport;

            disconnectAsyncSocketAsyncEventArgs = new SocketAsyncEventArgs();
            disconnectAsyncSocketAsyncEventArgs.Completed += OnDisconnected;
        }

        /// <inheritdoc />
        public void DisconnectAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            // Check the connected state after the disposed guard: a
            // disposed or already-disconnected connection must not start
            // an async disconnect.
            if (connectedState != 1)
                return;

            // Claim with a compare-and-set from idle only: a losing caller must
            // not write the slot — a plain exchange would clobber a settled
            // state 2 with its own 1 and leave a claim with no owner for
            // Dispose's hold to wait on forever.
            if (Interlocked.CompareExchange(ref disconnectingState, 1, 0) != 0)
                return;

            // Mutual confirmation against Dispose: Dispose holds for this claim
            // before it closes the socket, and the start re-reads the disposed
            // flag after claiming. At least one side always sees the other, so a
            // start that proceeds is guaranteed to find the socket alive. If
            // Dispose already ran, the transport is marked dead, so this start
            // never reaches a socket call. This bail still closes the socket in
            // that case — it may reach the close before Dispose's own close
            // does, and racing closes are harmless (closing an already-closed
            // socket is a no-op) — so it marks the transport first: the mark
            // must precede every close, and Dispose's mark may not have run yet
            // on its own thread. Clearing the slot first releases any Dispose
            // holding for this claim; the bail's close is the owner's.
            if (Volatile.Read(ref _disposed) == 1)
            {
                Interlocked.Exchange(ref disconnectingState, 0);
                transport.MarkDisconnected();
                transport.CloseSocket();
                return;
            }

            disconnectAsyncSocketAsyncEventArgs.DisconnectReuseSocket = false;

            // The re-reads passed: no mark is set and the socket is live. Issue
            // with the claim still held — a close waits for the claimed state,
            // so no close can land between here and the issue.
            bool pending = socket.DisconnectAsync(disconnectAsyncSocketAsyncEventArgs);

            // The socket call has returned: the window is over. This
            // transition is what releases any close that was holding for our
            // claim. On the pending path the operation is in the kernel and a
            // close may safely run over it; on the synchronous path the
            // completion runs next, but the slot moves first so a hold
            // releases the moment the call returned.
            Interlocked.Exchange(ref disconnectingState, 2);

            if (!pending)
                OnDisconnected(this, disconnectAsyncSocketAsyncEventArgs);
        }

        /// <summary>
        /// Handles disconnect completion: transitions to disconnected, closes the
        /// socket, and reports it. The exchange below is bookkeeping only, not a
        /// gate — closing an already-closed socket is a no-op and the report is
        /// exactly-once, so a completion that lands after Dispose (or after
        /// another completion) still delivers the contract exactly once.
        /// </summary>
        private void OnDisconnected(object? sender, SocketAsyncEventArgs e)
        {
            Interlocked.Exchange(ref connectedState, 0);

            // Close protocol: mark the transport dead first (its entry checks
            // then reject any further operation start with a report instead of
            // throwing on a closed socket), then close through the transport:
            // it holds for any starter that is between claiming and issuing
            // (a close must never outrun that window) and then closes, running
            // over in-flight operations — aborting one is safe, and for a
            // receive against a live peer the close is what settles it, so
            // the close must not wait for in-flight operations either.
            transport.MarkDisconnected();
            transport.CloseSocket();

            // The disconnect's event object is ours and its operation has
            // settled: unwire and dispose it (idempotent — the Dispose path
            // does the same, and disposing an event object twice is a no-op).
            disconnectAsyncSocketAsyncEventArgs.Completed -= OnDisconnected;
            disconnectAsyncSocketAsyncEventArgs.Dispose();

            ReportDisconnected();
        }

        /// <summary>
        /// Fires <see cref="Disconnected"/> exactly once — the CAS makes the natural
        /// path (FIN completion) and the force path (<see cref="Dispose"/>) mutually exclusive.
        /// </summary>
        private void ReportDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0)
                return;

            // Contract: the subscriber must not throw; on the FIN-completion thread
            // a throw escapes the callback and crashes the process.
            Disconnected?.Invoke();
        }

        /// <summary>
        /// Releases all resources held by this connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Disposes the transport (which marks it dead), then closes the socket
        /// through the close protocol: the close holds for any starter that is
        /// between claiming and issuing, then closes, running over in-flight
        /// operations. If a disconnect was started, Dispose holds for that
        /// claim window and then closes itself — the hold ends as soon as the
        /// caller's socket call returns, so a close can never land between the
        /// caller's re-read and its issue. The disconnect's completion settles
        /// through its own handler either way; the disconnect's event object is
        /// disposed here only when no completion will come for it.
        /// Idempotent: calling multiple times is safe.
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            // Mark disconnected before unwiring anything: IsConnected must read
            // false the moment Dispose begins.
            Interlocked.Exchange(ref connectedState, 0);

            // If a disconnect was started, close through the same protocol:
            // hold for the claim window — a close must never outrun a caller
            // that is between its re-read and its issue, and that window holds
            // no blocking call other than the socket call, which cannot run
            // while a close is in flight — then close, running over an
            // in-flight disconnect, whose aborted completion settles through
            // its own handler. The hold ends at the claim settling to 2 (the
            // caller's call returned) or 0 (the caller bailed and closed on
            // its own); either way the socket ends up closed, and racing
            // closes are no-ops. The event object is disposed here only when
            // no completion will come for it — a settled one still needs it
            // and disposes it itself.
            if (Volatile.Read(ref disconnectingState) != 0)
            {
                var spin = new SpinWait();
                while (Volatile.Read(ref disconnectingState) == 1)
                    spin.SpinOnce();
                if (Volatile.Read(ref disconnectingState) == 0)
                {
                    disconnectAsyncSocketAsyncEventArgs.Completed -= OnDisconnected;
                    disconnectAsyncSocketAsyncEventArgs.Dispose();
                }
                Transport.Dispose();
                transport.CloseSocket();
                ReportDisconnected();
                return;
            }

            disconnectAsyncSocketAsyncEventArgs.Completed -= OnDisconnected;
            disconnectAsyncSocketAsyncEventArgs.Dispose();
            Transport.Dispose();

            // Close protocol, owner side. Transport.Dispose above marked the
            // transport dead, and this close follows the mark: it holds for
            // any starter that is between claiming and issuing — a close must
            // never outrun that window, or the issue that follows it would
            // throw on the dead socket — and then closes, running over
            // in-flight operations. Aborting one is safe (it settles through
            // a branch), and for a receive against a live peer this close is
            // precisely what settles it, so it must not wait for that either.
            transport.CloseSocket();

            // Force completion: a consumer awaiting the Disconnected report must
            // never hang.
            ReportDisconnected();
        }
    }
}

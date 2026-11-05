using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using DuneTransport.BufferManager;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Transport
{
    /// <summary>
    /// Asynchronous socket transport with two-phase (header/payload) receive and length-prefixed send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a connected <see cref="Socket"/> and provides an event-based API for
    /// sending and receiving framed packets. Each packet is framed with a 2-byte
    /// little-endian length prefix.
    /// </para>
    /// <para>
    /// <b>Receive flow:</b> Reads the 2-byte header first, allocates a correctly-sized
    /// payload segment, then receives the payload. Delivers the complete segment to
    /// the subscriber via <see cref="OnPacketReceived"/>.
    /// </para>
    /// <para>
    /// <b>Send flow:</b> Application reserves a segment via <see cref="TryReserveSendPacket"/>,
    /// writes payload data, calls <see cref="SendAsync"/> which prepends the length header
    /// and transmits.
    /// </para>
    /// <para>
    /// Only one send and one receive may be in flight simultaneously. Operations are
    /// guarded with <see cref="Interlocked.CompareExchange"/> flags.
    /// </para>
    /// </remarks>
    public class Transport : ITransport
    {
        /// <summary>The size of the length-prefixed header in bytes (2 bytes, little-endian).</summary>
        private const int HeaderSize = 2;

        private readonly Socket socket;

        /// <summary>Segment pool for receiving data. Segments are leased during receive operations.</summary>
        private SegmentedBuffer receiveBuffer { get; }

        /// <summary>Segment pool for sending data. Segments are leased for outgoing packets.</summary>
        private SegmentedBuffer sendBuffer { get; }

        /// <summary>Tracks whether we are currently receiving a header or payload.</summary>
        private enum ReceivePhase { Header, Payload }

        /// <summary>The segment currently being filled during an active receive operation.</summary>
        private Segment currentReceivingSegment;

        /// <summary>The segment currently being transmitted during an active send operation.</summary>
        private Segment currentSendingSegment;

        /// <summary>Current phase of the multi-part receive operation.</summary>
        private ReceivePhase phase;

        /// <summary>Bytes received in the current phase.</summary>
        private int receivedBytes;

        /// <summary>Total bytes expected for the current phase.</summary>
        private int expectedBytes;

        private readonly SocketAsyncEventArgs sendEventArgs;
        private readonly SocketAsyncEventArgs receiveEventArgs;

        /// <summary>Dispose guard: 0 = not disposed, 1 = disposed. Accessed via <see cref="Volatile.Read"/>.</summary>
        private int _disposed;

        /// <summary>Send operation state: 0 = none, 1 = claimed (a start is
        /// running: between claiming and issuing — a transient with no blocking
        /// call inside), 2 = in flight (issued, awaiting its completion).
        /// The state is cleared only by the operation's completion (or the
        /// bail that settles instead of issuing), never by <see cref="Dispose"/>.
        /// The two nonzero values matter: a socket close may never outrun a
        /// state-1 starter (it would throw in the issue), but may safely run
        /// over a state-2 operation — and for a receive against a live peer,
        /// the close is what settles it, so a close must never wait for
        /// state 2 either.</summary>
        private int _sendOpState;

        /// <summary>Receive operation state: 0 = none, 1 = claimed (a start is
        /// running: between claiming and issuing — a transient with no blocking
        /// call inside), 2 = in flight (issued, awaiting its completion).
        /// The state is cleared only by the operation's completion (or the
        /// bail that settles instead of issuing), never by <see cref="Dispose"/>.
        /// The two nonzero values matter: a socket close may never outrun a
        /// state-1 starter (it would throw in the issue), but may safely run
        /// over a state-2 operation — and for a receive against a live peer,
        /// the close is what settles it, so a close must never wait for
        /// state 2 either.</summary>
        private int _receiveOpState;

        private volatile bool _isConnected = true;

        /// <inheritdoc />
        public bool IsConnected => _isConnected;

        /// <inheritdoc />
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        /// <summary>
        /// Marks the transport dead on behalf of the socket's owner, which
        /// must call this before closing the socket — on a clean
        /// disconnect's completion, or when a disconnect start bails out
        /// because a dispose already claimed the connection. The entry
        /// checks and re-reads then reject any operation start with a
        /// disconnect report instead of throwing on the closed socket — a
        /// closed socket cannot be detected after the fact, so the mark is
        /// the only way a start can know the socket is gone.
        /// </summary>
        internal void MarkDisconnected()
        {
            _isConnected = false;
        }

        /// <inheritdoc />
        public bool ReceiveArmed => Volatile.Read(ref _receiveOpState) != 0;

        /// <inheritdoc />
        public bool SendArmed => Volatile.Read(ref _sendOpState) != 0;

        public event Action<ITransport>? OnPacketSent;
        public event Action<ITransport, Segment, TransportError>? OnPacketSendFailed;

        public event Action<ITransport, Segment>? OnPacketReceived;
        public event Action<ITransport, TransportError>? OnPacketReceiveFailed;

        /// <summary>
        /// Creates a new transport for the given socket.
        /// </summary>
        /// <param name="socket">An already-connected socket to wrap.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Initializes separate segment pools for sending and receiving. Registers
        /// async I/O completion handlers on <see cref="SocketAsyncEventArgs"/> instances.
        /// </para>
        /// <para>
        /// The socket should be connected before calling this constructor.
        /// Call <see cref="Dispose"/> to release resources when the transport is no longer needed.
        /// </para>
        /// </remarks>
        public Transport(Socket socket) : this(socket, new TransportOptions())
        {
        }

        /// <summary>
        /// Creates a new transport for the given socket with custom segment pool sizing.
        /// </summary>
        /// <param name="socket">An already-connected socket to wrap. Must not be null.</param>
        /// <param name="options">Segment pool sizing for the send and receive pools. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> or <paramref name="options"/> is null.</exception>
        public Transport(Socket socket, TransportOptions options)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            TransportOptions resolvedOptions = options ?? throw new ArgumentNullException(nameof(options));
            sendBuffer = resolvedOptions.CreatePool();
            receiveBuffer = resolvedOptions.CreatePool();

            sendEventArgs = new SocketAsyncEventArgs();
            receiveEventArgs = new SocketAsyncEventArgs();

            sendEventArgs.Completed += OnPacketSentEventHandler;
            receiveEventArgs.Completed += OnPacketReceivedEventHandler;
        }

        /// <inheritdoc cref="ITransport.ReceiveAsync"/>
        public void ReceiveAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                OnPacketReceiveFailed?.Invoke(this, TransportError.ObjectDisposed);
                return;
            }
            if (!IsConnected)
            {
                OnPacketReceiveFailed?.Invoke(this, TransportError.SocketDisconnected);
                return;
            }
            if (Interlocked.CompareExchange(ref _receiveOpState, 1, 0) != 0)
            {
                OnPacketReceiveFailed?.Invoke(this, TransportError.ReceiveAlreadyPending);
                return;
            }

            if (!receiveBuffer.TryReserveSegment(out Segment newSegment))
            {
                Interlocked.Exchange(ref _receiveOpState, 0);
                OnPacketReceiveFailed?.Invoke(this, TransportError.PoolExhausted);
                return;
            }

            currentReceivingSegment = newSegment;
            phase = ReceivePhase.Header;
            expectedBytes = HeaderSize;
            receivedBytes = 0;

            // Mutual confirmation, re-read side. A dispose or a disconnect
            // that ran in the gap between the entry check above and here has
            // marked this transport dead, so the re-read sees it and bails:
            // release the segment, settle the claim, close the socket (the
            // wait inside holds for a starter on the other side, so none is
            // outrun), dispose the event object (the sweep may have skipped
            // it, seeing our claim; no later operation will ever use it —
            // every future start is rejected by the entry checks), and
            // report. Issuing instead would throw in the socket call; the
            // re-read makes that state unreachable.
            if (Volatile.Read(ref _disposed) == 1)
            {
                currentReceivingSegment.Release();
                Interlocked.Exchange(ref _receiveOpState, 0);
                CloseSocket();
                receiveEventArgs.Dispose();
                OnPacketReceiveFailed?.Invoke(this, TransportError.ObjectDisposed);
                return;
            }
            if (!IsConnected)
            {
                // Settle without closing. The mark may be this transport's
                // own (a socket error or a FIN settled the connection) and
                // the socket still open; its owner closes it later through
                // its own protocol, and a close here would be what makes
                // the owner's next socket call throw. If the mark is the
                // owner's (a disconnect running), the owner's close is
                // already on its way. Either way the close is the owner's.
                currentReceivingSegment.Release();
                Interlocked.Exchange(ref _receiveOpState, 0);
                OnPacketReceiveFailed?.Invoke(this, TransportError.SocketDisconnected);
                return;
            }

            // The re-reads passed: no mark is set and the socket is live.
            // Issue with the claim still held — a close waits for the claimed
            // state, so no close can land between here and the issue.
            IssueReceive();
        }

        /// <summary>
        /// Issues the next socket receive operation, handling synchronous completions inline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Enters a loop where synchronous completions are processed immediately without
        /// returning to the caller. This avoids thread pool overhead for fast connections.
        /// </para>
        /// <para>
        /// On a socket error, the current segment is released and the operation
        /// state is cleared before returning. A throw in the socket call itself
        /// is unreachable: the caller re-reads the dead marks after claiming,
        /// and a close holds for our claimed state across the re-read and the
        /// issue, so this only ever runs on a live socket.
        /// </para>
        /// </remarks>
        private void IssueReceive()
        {
            receiveEventArgs.SetBuffer(
                currentReceivingSegment.Memory.Slice(receivedBytes, expectedBytes - receivedBytes));

            // No outer catch: the socket call only throws SocketException
            // (caught below; the throw in a dispose race is unreachable by
            // the re-read above), SetBuffer cannot throw with a valid
            // slice, and ProcessReceive either returns or fires its
            // reports with the flag and segment already settled. A throw
            // escaping this loop can only come from a subscriber of a
            // failure report — a bug that must propagate, not be masked.
            while (true)
            {
                bool pending;
                try
                {
                    pending = socket.ReceiveAsync(receiveEventArgs);
                }
                catch (SocketException)
                {
                    // The same socket error as the completion branch, observed
                    // synchronously: the connection is dead, so mark it (the
                    // completion branch does the same), settle, and report.
                    currentReceivingSegment.Release();
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    _isConnected = false;
                    OnPacketReceiveFailed?.Invoke(this, TransportError.SocketError);
                    return;
                }

                if (pending)
                {
                    // The operation is now in the kernel: truly in flight,
                    // and a close may safely run over it (it aborts and
                    // settles through a branch). This transition is what
                    // releases any close that was waiting for our claim.
                    Interlocked.Exchange(ref _receiveOpState, 2);
                    return;
                }

                if (!ProcessReceive(receiveEventArgs)) return;

                receiveEventArgs.SetBuffer(
                    currentReceivingSegment.Memory.Slice(receivedBytes, expectedBytes - receivedBytes));
            }
        }

        private void OnPacketReceivedEventHandler(object? sender, SocketAsyncEventArgs e)
        {
            // The completed operation is no longer in the kernel. If it
            // settles, ProcessReceive has cleared the state by the time it
            // returns; if it returns true, the state is still set and this
            // re-issue must hold a claim for its whole duration.
            if (ProcessReceive(e))
            {
                // Re-issue with the same shape as a fresh start: claim,
                // re-read, issue. The claim is what holds any close off
                // while the issue is being set up — a close waiting on it
                // cannot close the socket between our re-read and the
                // issue. The re-read bails instead of issuing if a close
                // already marked the transport dead while this completion
                // was being processed.
                Interlocked.Exchange(ref _receiveOpState, 1);
                if (Volatile.Read(ref _disposed) == 1)
                {
                    currentReceivingSegment.Release();
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    CloseSocket();
                    receiveEventArgs.Dispose();
                    OnPacketReceiveFailed?.Invoke(this, TransportError.ObjectDisposed);
                    return;
                }
                if (!IsConnected)
                {
                    // Settle without closing — same reasoning as the fresh
                    // start: the mark may be this transport's own (either
                    // direction's error or FIN branch), in which case the
                    // socket is still open and its owner closes it later
                    // through its own protocol. If the mark is the
                    // owner's, the owner's close is already on its way.
                    currentReceivingSegment.Release();
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    OnPacketReceiveFailed?.Invoke(this, TransportError.SocketDisconnected);
                    return;
                }
                IssueReceive();
            }
        }

        /// <summary>
        /// Processes a completed receive operation, handling the two-phase (header/payload) receive pipeline.
        /// </summary>
        /// <param name="onReceived">The completed socket async event arguments.</param>
        /// <returns><c>true</c> if more data should be received (partial phase); otherwise <c>false</c>.</returns>
        /// <remarks>
        /// <para>
        /// Two-phase receive:
        /// <list type="number">
        ///   <item><b>Header phase:</b> Reads 2 bytes (little-endian payload length). Validates length, reserves payload segment.</item>
        ///   <item><b>Payload phase:</b> Reads the declared payload length. On completion, slices segment and fires <see cref="OnPacketReceived"/>.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Early exits: dispose race (silent release), socket error, graceful close (FIN).
        /// </para>
        /// </remarks>
        private bool ProcessReceive(SocketAsyncEventArgs onReceived)
        {
            // Dispose raced with this callback. Release is idempotent, so a sweep
            // that already released this segment is a no-op. Close the socket
            // (the close protocol holds for any claimed starter, then closes)
            // and dispose the event object: the teardown side marked the
            // transport dead and its close is settled by the completions, so
            // this one settles it. The event object is this transport's own;
            // disposing it is idempotent, so a sweep that ran first is safe.
            if (Volatile.Read(ref _disposed) == 1)
            {
                Interlocked.Exchange(ref _receiveOpState, 0);
                currentReceivingSegment.Release();
                CloseSocket();
                receiveEventArgs.Dispose();
                return false;
            }

            // Socket-level error → release, bubble, stop. The connection is
            // dead (a hard error tears it down), so mark it; the socket
            // itself is left to its owner to close.
            if (onReceived.SocketError != SocketError.Success)
            {
                Interlocked.Exchange(ref _receiveOpState, 0);
                currentReceivingSegment.Release();
                _isConnected = false;
                OnPacketReceiveFailed?.Invoke(this, TransportError.SocketError);
                return false;
            }

            // Graceful close (FIN) — regardless of current phase. The
            // connection is dead; the socket is left open for its owner to
            // close (the owner's close follows the close protocol, so it
            // cannot outrun a starter).
            if (onReceived.BytesTransferred == 0)
            {
                Interlocked.Exchange(ref _receiveOpState, 0);
                currentReceivingSegment.Release();
                _isConnected = false;
                OnPacketReceiveFailed?.Invoke(this, TransportError.SocketDisconnected);
                return false;
            }

            receivedBytes += onReceived.BytesTransferred;

            // Partial phase — keep reassembling with the same segment.
            if (receivedBytes < expectedBytes)
            {
                return true;
            }

            if (phase == ReceivePhase.Header)
            {
                ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(currentReceivingSegment.Memory.Span);

                // Done with the header segment regardless of the next branch.
                currentReceivingSegment.Release();

                // Protocol violation: zero-length payload.
                if (payloadLength == 0)
                {
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    OnPacketReceiveFailed?.Invoke(this, TransportError.ProtocolError);
                    return false;
                }

                // Protocol violation: payload larger than a segment.
                if (payloadLength > receiveBuffer.SegmentSize)
                {
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    OnPacketReceiveFailed?.Invoke(this, TransportError.ProtocolError);
                    return false;
                }

                // Reserve payload segment.
                if (!receiveBuffer.TryReserveSegment(out Segment payloadSegment))
                {
                    Interlocked.Exchange(ref _receiveOpState, 0);
                    OnPacketReceiveFailed?.Invoke(this, TransportError.PoolExhausted);
                    return false;
                }

                currentReceivingSegment = payloadSegment;
                phase = ReceivePhase.Payload;
                expectedBytes = payloadLength;
                receivedBytes = 0;
                return true;
            }

            // Payload complete. Slice to actual payload length.
            currentReceivingSegment.Memory = currentReceivingSegment.Memory.Slice(0, expectedBytes);

            // Hand off ownership: capture locally and clear the field, then clear
            // the in-flight flag before the subscriber runs so a handler that
            // re-arms via ReceiveAsync finds the slot free. A racing Dispose
            // releases whatever the field holds without clearing it (release is
            // idempotent), so it either releases this segment — harmless, the
            // pool is frozen once disposed, nothing can re-lease it — or a
            // default.
            var segmentToDeliver = currentReceivingSegment;
            currentReceivingSegment = default;
            Interlocked.Exchange(ref _receiveOpState, 0);

            try
            {
                OnPacketReceived?.Invoke(this, segmentToDeliver);
            }
            catch
            {
                // Handler bug — make sure the segment goes back to the pool.
                // Release is idempotent, so it is safe even if the
                // handler released before throwing.
                segmentToDeliver.Release();
                OnPacketReceiveFailed?.Invoke(this, TransportError.HandlerFailed);
            }

            return false;
        }

        /// <inheritdoc cref="ITransport.TryReserveSendPacket(out Segment)"/>
        public bool TryReserveSendPacket(out Segment segment)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                segment = default;
                return false;
            }
            if (!sendBuffer.TryReserveSegment(out segment))
                return false;

            segment.Memory = segment.Memory.Slice(HeaderSize);
            return true;
        }

        /// <inheritdoc cref="ITransport.SendAsync(Segment, int)"/>
        public void SendAsync(Segment packet, int packetSize)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                OnPacketSendFailed?.Invoke(this, packet, TransportError.ObjectDisposed);
                return;
            }

            if (!IsConnected)
            {
                OnPacketSendFailed?.Invoke(this, packet, TransportError.SocketDisconnected);
                return;
            }

            if (Interlocked.CompareExchange(ref _sendOpState, 1, 0) != 0)
            {
                OnPacketSendFailed?.Invoke(this, packet, TransportError.SendAlreadyPending);
                return;
            }

            if (!sendBuffer.GetRegisteredMemory(packet.SegmentIndex, packetSize + HeaderSize, out Memory<byte> memory))
            {
                Interlocked.Exchange(ref _sendOpState, 0);
                OnPacketSendFailed?.Invoke(this, packet, TransportError.InvalidSegment);
                return;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(memory.Span.Slice(0, HeaderSize), (ushort)packetSize);

            currentSendingSegment = packet;

            // Mutual confirmation, re-read side. A dispose or a disconnect
            // that ran in the gap between the entry check above and here has
            // marked this transport dead, so the re-read sees it and bails:
            // settle the claim, close the socket (the wait inside holds for a
            // starter on the other side, so none is outrun), dispose the
            // event object (the sweep may have skipped it, seeing our
            // claim; no later operation will ever use it), and hand the
            // segment to the subscriber to release, matching the other
            // send-failure reports. Issuing instead would throw in the
            // socket call; the re-read makes that state unreachable.
            if (Volatile.Read(ref _disposed) == 1)
            {
                currentSendingSegment = default;
                Interlocked.Exchange(ref _sendOpState, 0);
                CloseSocket();
                sendEventArgs.Dispose();
                OnPacketSendFailed?.Invoke(this, packet, TransportError.ObjectDisposed);
                return;
            }
            if (!IsConnected)
            {
                // Settle without closing — same reasoning as the receive
                // side: the mark may be this transport's own (either
                // direction's error or FIN branch), in which case the
                // socket is still open and its owner closes it later
                // through its own protocol. If the mark is the owner's,
                // the owner's close is already on its way.
                currentSendingSegment = default;
                Interlocked.Exchange(ref _sendOpState, 0);
                OnPacketSendFailed?.Invoke(this, packet, TransportError.SocketDisconnected);
                return;
            }

            // The re-reads passed: no mark is set and the socket is live.
            // Issue with the claim still held — a close waits for the
            // claimed state, so no close can land between here and the
            // issue. The claim becomes in-flight only once the operation
            // is actually in the kernel; a close may run over that.
            try
            {
                sendEventArgs.SetBuffer(memory);
                if (socket.SendAsync(sendEventArgs))
                {
                    Interlocked.Exchange(ref _sendOpState, 2);
                }
                else
                {
                    ProcessSend(sendEventArgs);
                }
            }
            catch (SocketException)
            {
                // The same socket error as the completion branch, observed
                // synchronously: the connection is dead, so mark it (the
                // completion branch does the same), settle, and hand the
                // segment to the subscriber.
                var failed = currentSendingSegment;
                currentSendingSegment = default;
                Interlocked.Exchange(ref _sendOpState, 0);
                _isConnected = false;
                OnPacketSendFailed?.Invoke(this, failed, TransportError.SocketError);
            }
        }

        private void OnPacketSentEventHandler(object? sender, SocketAsyncEventArgs e)
        {
            ProcessSend(e);
        }

        /// <summary>
        /// Processes a completed send operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On socket error: fires <see cref="OnPacketSendFailed"/> with the segment (ownership transfers to subscriber).
        /// On success: releases the segment, fires <see cref="OnPacketSent"/>.
        /// On a dispose race: releases the segment and closes the socket and
        /// event object the teardown side left to this completion.
        /// </para>
        /// </remarks>
        private void ProcessSend(SocketAsyncEventArgs onSent)
        {
            // Dispose raced with this callback. Release is idempotent, so a sweep
            // that already released this segment is a no-op. Close the socket
            // (the close protocol holds for any claimed starter, then closes)
            // and dispose the event object: the teardown side marked the
            // transport dead and its close is settled by the completions, so
            // this one settles it. The event object is this transport's own;
            // disposing it is idempotent, so a sweep that ran first is safe.
            if (Volatile.Read(ref _disposed) == 1)
            {
                var seg = currentSendingSegment;
                currentSendingSegment = default;
                Interlocked.Exchange(ref _sendOpState, 0);
                seg.Release();
                CloseSocket();
                sendEventArgs.Dispose();
                return;
            }

            // Socket-level error → the connection is dead (a hard error tears
            // it down), so mark it; the segment goes to the subscriber (or
            // back to the pool if nobody listens) and the socket is left to
            // its owner to close.
            if (onSent.SocketError != SocketError.Success)
            {
                var seg = currentSendingSegment;
                currentSendingSegment = default;
                Interlocked.Exchange(ref _sendOpState, 0);
                _isConnected = false;
                if (OnPacketSendFailed != null)
                    OnPacketSendFailed(this, seg, TransportError.SocketError);
                else
                    seg.Release();
                return;
            }

            var sentSeg = currentSendingSegment;
            currentSendingSegment = default;
            Interlocked.Exchange(ref _sendOpState, 0);
            sentSeg.Release();
            OnPacketSent?.Invoke(this);
        }

        /// <summary>
        /// Closes the socket on behalf of its owner, following the close
        /// protocol. The caller has already marked this transport dead
        /// (disposed, or marked disconnected) before calling, and the mark
        /// precedes every close — so a starter whose re-read already ran saw
        /// the mark and bailed, and a starter whose re-read has not run yet
        /// is in the claimed state, which the wait below holds for. A close
        /// must never outrun the claimed (claimed-not-yet-issued) state: the
        /// issue that follows it would throw on the dead socket. That state
        /// contains no blocking call — a claim is followed by an issue or a
        /// bail — so it always resolves as soon as its holder runs, and the
        /// wait is a plain reading spin: lock-free, and unable to deadlock,
        /// because an already-issued (in-flight) operation is never waited
        /// for. A close runs over in-flight operations instead: aborting one
        /// is safe — it settles through a branch — and for a receive against
        /// a live peer, the close is precisely what settles it. Closing an
        /// already-closed socket is a no-op, so racing closers are harmless.
        /// </summary>
        internal void CloseSocket()
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref _receiveOpState) == 1 || Volatile.Read(ref _sendOpState) == 1)
                spin.SpinOnce();
            socket.Close();
        }

        /// <summary>
        /// Releases all resources held by this transport.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Idempotent: calling <see cref="Dispose"/> multiple times is safe.
        /// </para>
        /// <para>
        /// Releases whatever the operation fields still hold. Release is
        /// idempotent, so a racing completion that releases the same segment
        /// first is safe, and a field that already holds a default is a no-op.
        /// The operation states are deliberately NOT cleared here: they are
        /// the claim a concurrent socket teardown holds for, and the settling
        /// completions clear them as they run.
        /// </para>
        /// <para>
        /// Unwires and disposes the async event objects only when no operation
        /// is claimed or in flight — a live or pending completion needs its
        /// event object and disposes it itself when it settles (disposal is
        /// idempotent, so whichever side disposes first simply wins).
        /// </para>
        /// <para>
        /// Does NOT close the underlying socket — that is the responsibility
        /// of the caller (typically
        /// <see cref="DuneSession.SocketConnectors.Connection"/>), which calls
        /// the close protocol after this runs. A completion that settles after
        /// this Dispose runs the same close protocol in turn.
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            // Sweep whatever the in-flight fields still hold — release without
            // clearing, the error-branch idiom of this file. A racing
            // completion re-releasing is a no-op; a field that holds a
            // default is a no-op.
            currentReceivingSegment.Release();
            currentSendingSegment.Release();

            // Unwire and dispose each direction's event object when that
            // direction has no claim: a running or pending completion needs
            // its event object and disposes it itself when it settles. With
            // no claim, no completion will ever come for that direction, so
            // this side disposes — per direction, so an idle direction is
            // not stranded because the other one is still in flight.
            // Disposing an idle event object cannot throw, and double
            // dispose is a no-op, so this needs no guard.
            if (Volatile.Read(ref _receiveOpState) == 0)
            {
                receiveEventArgs.Completed -= OnPacketReceivedEventHandler;
                receiveEventArgs.Dispose();
            }
            if (Volatile.Read(ref _sendOpState) == 0)
            {
                sendEventArgs.Completed -= OnPacketSentEventHandler;
                sendEventArgs.Dispose();
            }

            _isConnected = false;
        }
    }
}

using System;
using DuneTransport.BufferManager;

namespace DuneTransport.Transport.Interface
{
    /// <summary>
    /// Asynchronous socket transport with length-prefixed framing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manages the raw socket I/O for sending and receiving packets. Uses a 2-byte
    /// little-endian length prefix for framing. Only one send and one receive may be
    /// in flight simultaneously — attempting a second operation while one is pending
    /// fires the appropriate error event.
    /// </para>
    /// <para>
    /// Segment ownership transfers to event subscribers:
    /// <list type="bullet">
    ///   <item><see cref="OnPacketReceived"/> — subscriber receives the segment, must release.</item>
    ///   <item><see cref="OnPacketSendFailed"/> — subscriber receives the segment, decides retry or release.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public interface ITransport : IDisposable
    {
        /// <summary>Gets whether the underlying socket is currently connected.</summary>
        bool IsConnected { get; }

        /// <summary>Gets whether this transport has been disposed.</summary>
        bool IsDisposed { get; }

        /// <summary>
        /// Gets whether a receive operation is currently in-flight on this transport.
        /// </summary>
        /// <remarks>
        /// True while <see cref="ReceiveAsync"/> has been called but the operation has not
        /// yet completed (via <see cref="OnPacketReceived"/> or <see cref="OnPacketReceiveFailed"/>).
        /// Use this instead of maintaining a separate "receive armed" flag at the application layer.
        /// </remarks>
        bool ReceiveArmed { get; }

        /// <summary>
        /// Gets whether a send operation is currently in-flight on this transport.
        /// </summary>
        /// <remarks>
        /// True while <see cref="SendAsync"/> has been called but the operation has not
        /// yet completed (via <see cref="OnPacketSent"/> or <see cref="OnPacketSendFailed"/>).
        /// Use this instead of maintaining a separate "send armed" flag at the application layer.
        /// </remarks>
        bool SendArmed { get; }

        /// <summary>
        /// Raised when a packet send completes successfully.
        /// </summary>
        /// <remarks>
        /// The segment has already been released back to the pool. No segment is passed
        /// to the subscriber — the event signals send completion only.
        /// </remarks>
        event Action<ITransport>? OnPacketSent;

        /// <summary>
        /// Raised when a send attempt fails. Segment ownership TRANSFERS to
        /// the subscriber — the segment is NOT released by Transport. The
        /// subscriber owns the segment and must either:
        /// <list type="bullet">
        ///   <item>Call <c>SendAsync()</c> again to retry (same segment, same data).</item>
        ///   <item>Call <c>Release()</c> to give up and return it to the pool.</item>
        /// </list>
        /// The <see cref="TransportError"/> argument indicates why the send failed.
        /// All error types — transient and terminal — follow this same ownership
        /// contract; the application decides what to do based on the error code.
        /// </summary>
        event Action<ITransport, Segment, TransportError>? OnPacketSendFailed;

        /// <summary>
        /// Raised when a complete framed packet has been received. Segment
        /// ownership TRANSFERS to the subscriber — the subscriber is
        /// responsible for calling <c>Release()</c> when done.
        /// </summary>
        event Action<ITransport, Segment>? OnPacketReceived;

        /// <summary>
        /// Raised when a receive attempt fails. Any segment Transport had
        /// rented has already been released; no segment is passed to the
        /// subscriber. The <see cref="TransportError"/> argument indicates
        /// which branch fired.
        /// </summary>
        event Action<ITransport, TransportError>? OnPacketReceiveFailed;

        /// <summary>
        /// Begins an asynchronous send of the given segment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only one send may be in flight at a time. If a previous send has not
        /// yet completed (via <see cref="OnPacketSent"/> or
        /// <see cref="OnPacketSendFailed"/>), calling this method again will fail
        /// with <c>TransportError.SendAlreadyPending</c>.
        /// </para>
        /// <para>
        /// <b>API Caveat — Retry contract:</b> When <see cref="OnPacketSendFailed"/>
        /// fires, the send operation state is cleared and the transport becomes
        /// technically capable of accepting a new send. However, the application MUST
        /// NOT call <c>SendAsync()</c> with a different packet until it has resolved
        /// the failed one — either by retrying the same segment or releasing it.
        /// Sending a new packet while a previous failure is unresolved is an
        /// application-level error: the library provides no ordering or queuing
        /// guarantees, and packets may arrive out of intended sequence.
        /// </para>
        /// </remarks>
        void SendAsync(Segment packet, int packetSize);

        /// <summary>
        /// Begins an asynchronous receive operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only one receive may be in flight at a time. If a previous receive has not
        /// yet completed (via <see cref="OnPacketReceived"/> or
        /// <see cref="OnPacketReceiveFailed"/>), calling this method again will fail
        /// with <c>TransportError.ReceiveAlreadyPending</c>.
        /// </para>
        /// <para>
        /// On success, <see cref="OnPacketReceived"/> fires with the complete packet segment.
        /// On failure, <see cref="OnPacketReceiveFailed"/> fires with the error code.
        /// </para>
        /// </remarks>
        void ReceiveAsync();

        /// <summary>
        /// Attempts to reserve a segment from the send buffer for preparing a packet.
        /// </summary>
        /// <param name="segment">
        /// When this method returns, contains the reserved segment if the operation succeeded;
        /// otherwise contains a default <see cref="Segment"/>. The memory slice is pre-sliced
        /// past the header so the caller writes directly into the payload area.
        /// </param>
        /// <returns><c>true</c> if a segment was successfully reserved; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// <para>
        /// The reserved segment memory is sliced to skip the 2-byte length header.
        /// The caller writes packet data directly into <paramref name="segment"/>, sets
        /// the packet size, then calls <see cref="SendAsync"/> to transmit.
        /// </para>
        /// <para>
        /// If the send fails (<see cref="OnPacketSendFailed"/>), segment ownership transfers
        /// to the subscriber. If the send succeeds, the segment is automatically released.
        /// </para>
        /// </remarks>
        bool TryReserveSendPacket(out Segment segment);
    }
}
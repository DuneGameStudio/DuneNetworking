using System;
using DunePresentation.Packet;
using DunePresentation.Packet.Interfaces;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;

namespace DunePresentation.Peer.Interfaces
{
    /// <summary>
    /// A connected peer that can serialize, encrypt, send, decrypt, and deserialize packets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app-facing handle to one connection: it wraps an <see cref="IConnection"/> with
    /// packet registry lookup and optional encryption, and is the main send/receive path
    /// for application packets.
    /// </para>
    /// <para>
    /// <b>Send path:</b> <see cref="SerializeAndEncrypt{T}"/> reserves a segment, serializes
    /// the fields, writes the presentation header, and encrypts (when configured);
    /// <see cref="Send"/> then transmits the prepared segment.
    /// </para>
    /// <para>
    /// <b>Receive path:</b> the application receives a raw segment from the connection's
    /// transport, calls <see cref="DecryptAndDeserialize"/> to decrypt, look up the handler,
    /// and deserialize, then invokes the returned handler with the packet.
    /// </para>
    /// <para>
    /// User-supplied code (field serialization, the encryptor) is contracted never to
    /// throw. The pipeline contains no try/catch around it: a throw propagates to the
    /// caller in the ordinary C# way and leaks the segment being processed.
    /// </para>
    /// </remarks>
    public interface IPeer : IDisposable
    {
        /// <summary>Gets the underlying connection this peer operates on.</summary>
        IConnection Connection { get; }

        /// <summary>Gets whether the underlying connection is currently active.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Serializes and optionally encrypts a packet, returning the prepared segment.
        /// </summary>
        /// <typeparam name="T">The packet type to serialize.</typeparam>
        /// <param name="packet">The packet instance to serialize.</param>
        /// <returns>
        /// A tuple of (segment, error). On success the segment is ready to transmit and the error
        /// member is <see cref="PacketError.None"/>. On failure the segment is null and the error
        /// names the step that failed: <see cref="PacketError.PoolExhausted"/> (no send segment was
        /// available) or <see cref="PacketError.SerializationError"/> (the fields could not be written).
        /// </returns>
        /// <remarks>
        /// <para>
        /// Pipeline: reserve a send segment, serialize the fields, write the presentation
        /// header, then encrypt in place (when an encryptor is configured). On a returned
        /// failure any reserved segment has already been released. The encryptor is
        /// contracted never to throw; a throw escapes to the caller with the segment
        /// unreleased — a pool leak nothing here can undo.
        /// </para>
        /// </remarks>
        (Segment? Segment, PacketError Error) SerializeAndEncrypt<T>(T packet) where T : IPacket;

        /// <summary>
        /// Decrypts, deserializes, and looks up the handler for an incoming packet.
        /// </summary>
        /// <param name="segment">A segment received from the transport.</param>
        /// <returns>
        /// A tuple of (result, error). On success the result is a (packet, handler) pair and the
        /// error member is <see cref="PacketError.None"/>. On failure the result is null and the
        /// error names the step that failed: <see cref="PacketError.RegistryError"/> (the packet ID
        /// has no registered handler) or <see cref="PacketError.DeserializeError"/> (the fields could
        /// not be read). On every path the segment is released.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Four-step pipeline:
        /// <list type="number">
        ///   <item>Decrypt in place (when an encryptor is configured)</item>
        ///   <item>Read the presentation header</item>
        ///   <item>Look up the packet ID in the registry</item>
        ///   <item>Create the packet and deserialize its fields</item>
        /// </list>
        /// </para>
        /// <para>
        /// Faults in the first two steps (an encryptor throw, a segment too small for
        /// the header) are not mapped to an error code — they propagate to the caller
        /// and the segment is never released. User-supplied encryptor and packet
        /// implementations are contracted never to throw.
        /// </para>
        /// </remarks>
        ((IPacket Packet, Action<IPacket> Handler)? Result, PacketError Error) DecryptAndDeserialize(Segment segment);

        /// <summary>
        /// Transmits a prepared segment to the remote peer.
        /// </summary>
        /// <param name="segment">The segment prepared by <see cref="SerializeAndEncrypt{T}"/>.</param>
        /// <param name="packetSize">The packet size in bytes, including the 2-byte presentation header. The transport prepends its own length prefix when transmitting.</param>
        void Send(Segment segment, int packetSize);

        /// <summary>
        /// Initiates a graceful disconnect of the underlying connection.
        /// </summary>
        void DisconnectAsync();
    }
}

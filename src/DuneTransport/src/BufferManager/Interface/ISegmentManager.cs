using System;
using DuneTransport.Transport.Interface;

namespace DuneTransport.BufferManager.Interface
{
    /// <summary>
    /// Contract for objects that own a <see cref="Segment"/> during serialization or deserialization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides the segment lifecycle: reserve a segment, serialize/deserialize, then release.
    /// The <see cref="Serialize"/> and <see cref="Deserialize"/> default methods orchestrate
    /// the full lifecycle, calling the abstract hooks (<see cref="OnSerialize"/>, <see cref="OnDeserialize"/>)
    /// at the appropriate points.
    /// </para>
    /// <para>
    /// Segment ownership is critical: after <see cref="Serialize"/> succeeds, the segment
    /// is handed to the transport. After <see cref="Deserialize"/> completes (success or failure),
    /// the segment is automatically released.
    /// </para>
    /// </remarks>
    public interface ISegmentManager
    {
        /// <summary>
        /// The current <see cref="Segment"/> associated with this instance.
        /// </summary>
        /// <remarks>
        /// Set during <see cref="Serialize"/> (reserve) or by the transport during receive.
        /// Cleared or released after the operation completes.
        /// </remarks>
        Segment segment { get; set; }

        /// <summary>
        /// The total serialized size of the packet, including any framing headers.
        /// </summary>
        int PacketSize { get; set; }

        /// <summary>
        /// Called by <see cref="Serialize"/> to perform the actual serialization.
        /// </summary>
        /// <returns><c>true</c> if serialization succeeded; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// <para>
        /// Implementations should write to <see cref="segment"/> and set <see cref="PacketSize"/> to
        /// the total number of bytes written (including any framing the implementor owns). On a
        /// <c>false</c> return, <see cref="Serialize"/> releases the segment automatically.
        /// </para>
        /// <para>
        /// <b>Contract: this method MUST NOT throw.</b> Report failure only by returning <c>false</c>.
        /// A throw escapes <see cref="Serialize"/> before the segment is released, leaking the pool
        /// segment — this is a contract violation by the implementor. Wrap any operation that can
        /// throw (encoding, encryption, buffer writes) in a try/catch and convert it to a
        /// <c>false</c> return.
        /// </para>
        /// <para>
        /// Framing headers, encryption, and any post-reserve transformation of the segment that is
        /// not owned by the implementor are NOT this method's concern — they are the caller's
        /// responsibility, performed after <see cref="Serialize"/> returns <see cref="SerializeResult.Ok"/>.
        /// Do not write them here.
        /// </para>
        /// </remarks>
        bool OnSerialize();

        /// <summary>
        /// Called by <see cref="Deserialize"/> to perform the actual deserialization.
        /// </summary>
        /// <returns><c>true</c> if deserialization succeeded; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// <para>
        /// The segment is released by <see cref="Deserialize"/> regardless of the return value
        /// (success or failure alike).
        /// </para>
        /// <para>
        /// <b>Contract: this method MUST NOT throw.</b> Report failure only by returning <c>false</c>.
        /// A throw escapes <see cref="Deserialize"/> before the segment is released, leaking the pool
        /// segment — this is a contract violation by the implementor. Wrap any operation that can
        /// throw (decoding, decryption, malformed-input handling) in a try/catch and convert it to
        /// a <c>false</c> return.
        /// </para>
        /// </remarks>
        bool OnDeserialize();

        /// <summary>
        /// Orchestrates the full serialization lifecycle: reserve, then serialize into the segment.
        /// </summary>
        /// <param name="transport">
        /// The transport to reserve a send segment from.
        /// </param>
        /// <returns>
        /// A <see cref="SerializeResult"/> indicating the outcome:
        /// <list type="bullet">
        ///   <item><see cref="SerializeResult.Ok"/> — segment reserved and serialized; ownership transfers to caller.</item>
        ///   <item><see cref="SerializeResult.PoolExhausted"/> — no segment available; <see cref="OnSerialize"/> not called.</item>
        ///   <item><see cref="SerializeResult.SerializeFailed"/> — segment reserved but <see cref="OnSerialize"/> returned <c>false</c>; segment released.</item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>
        /// Lifecycle:
        /// <list type="number">
        ///   <item>Reserve a segment from <paramref name="transport"/>.</item>
        ///   <item>Call <see cref="OnSerialize"/> to write packet data into the segment.</item>
        ///   <item>If serialization fails, release the segment and return <see cref="SerializeResult.SerializeFailed"/>.</item>
        ///   <item>On success, return <see cref="SerializeResult.Ok"/>. Ownership of <see cref="segment"/> transfers to
        ///   the caller, which is responsible for any framing/encryption and for handing the
        ///   segment to the transport.</item>
        /// </list>
        /// </para>
        /// <para>
        /// This method only orchestrates reserve → serialize → handoff. It deliberately performs
        /// no post-serialization work (no header writing, no encryption): those are caller
        /// concerns that belong above the transport layer. Keeping them out of this protected
        /// region ensures the segment lifecycle is the only thing this method owns, and the only
        /// thing that can throw inside it is <see cref="OnSerialize"/> — which the contract above
        /// forbids from throwing. Callers that post-process the returned segment must guard their
        /// own work and release on failure.
        /// </para>
        /// </remarks>
        SerializeResult Serialize(ITransport transport)
        {
            if (!transport.TryReserveSendPacket(out Segment newSegment))
                return SerializeResult.PoolExhausted;

            segment = newSegment;

            if (!OnSerialize())
            {
                segment.Release();
                return SerializeResult.SerializeFailed;
            }

            return SerializeResult.Ok;
        }

        /// <summary>
        /// Orchestrates the full deserialization lifecycle: callback, deserialize, release.
        /// </summary>
        /// <param name="beforeDeserialize">
        /// Optional callback invoked before deserialization begins, with the current segment and size.
        /// </param>
        /// <returns>
        /// A <see cref="DeserializeResult"/> indicating the outcome:
        /// <list type="bullet">
        ///   <item><see cref="DeserializeResult.Ok"/> — deserialization succeeded; segment released.</item>
        ///   <item><see cref="DeserializeResult.DeserializeFailed"/> — <see cref="OnDeserialize"/> returned <c>false</c>; segment released.</item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>
        /// Lifecycle:
        /// <list type="number">
        ///   <item>Invoke <paramref name="beforeDeserialize"/> if provided.</item>
        ///   <item>Call <see cref="OnDeserialize"/> to read data from the segment.</item>
        ///   <item>Release the segment (regardless of success or failure).</item>
        /// </list>
        /// </para>
        /// </remarks>
        DeserializeResult Deserialize(Action<Segment, int>? beforeDeserialize = null)
        {
            beforeDeserialize?.Invoke(segment, PacketSize);

            bool result = OnDeserialize();
            segment.Release();
            return result ? DeserializeResult.Ok : DeserializeResult.DeserializeFailed;
        }
    }
}

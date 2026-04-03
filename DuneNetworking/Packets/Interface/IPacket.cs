using System.Buffers;
using DuneNetworking.ByteArrayManager;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Packets
{
    /// <summary>
    ///     Base interface for all packet types. Defines both the send path
    ///     (serialize into a SegmentedBuffer segment) and the receive path
    ///     (deserialize from a ReadOnlySequence).
    ///
    ///     Send path:  Serialize(buffer) → OnSerialize() → Send(transport)
    ///     Receive path: OnDeserialize(payload)
    /// </summary>
    public interface IPacket
    {
        /// <summary>
        ///     Packet type identifier.
        /// </summary>
        ushort Id { get; }

        /// <summary>
        ///     The segment reserved for this packet's serialized data.
        /// </summary>
        Segment Segment { get; set; }

        /// <summary>
        ///     Number of actually used bytes within the segment.
        /// </summary>
        int PacketSize { get; set; }

        /// <summary>
        ///     Implement to write packet fields into Segment.Memory.
        /// </summary>
        void OnSerialize();

        /// <summary>
        ///     Reserves a segment from the buffer and calls OnSerialize.
        /// </summary>
        void Serialize(SegmentedBuffer buffer)
        {
            if (buffer.ReserveMemory(out Segment newSegment))
            {
                Segment = newSegment;
            }

            OnSerialize();
        }

        /// <summary>
        ///     Writes the length header, sends via the transport, and releases the segment.
        /// </summary>
        void Send(ITransport transport)
        {
            transport.SendAsync(transport.sendBuffer.GetRegisteredMemory(Segment.SegmentIndex, PacketSize));
            Segment.Release();
        }

        /// <summary>
        ///     Deserialize packet fields from the payload.
        ///     The payload excludes the 2-byte length header and the 2-byte packet ID
        ///     (the ID has already been read by the registry).
        ///
        ///     The sequence points into the ring buffer and is valid only for
        ///     the duration of this call.
        /// </summary>
        void OnDeserialize(ReadOnlySequence<byte> payload);
    }
}

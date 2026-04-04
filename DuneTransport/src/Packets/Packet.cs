using System.Buffers;
using DuneTransport.Buffer;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Packets
{
    /// <summary>
    ///     Carries a parsed packet from the parser thread to the deserialization thread.
    ///     Payload points into the source ring buffer and remains valid until Release is called.
    /// </summary>
    public readonly struct Packet
    {
        /// <summary>
        ///     Packet payload (excludes the 2-byte length header).
        ///     Points directly into the ring buffer memory.
        /// </summary>
        public readonly ReadOnlySequence<byte> Payload;

        /// <summary>
        ///     Total frame size (2 + payload length). Used for release accounting.
        /// </summary>
        public readonly int TotalFrameSize;

        /// <summary>
        ///     The ring buffer that owns the memory. Release is called here
        ///     after deserialization completes.
        /// </summary>
        public readonly RingBuffer SourceBuffer;

        /// <summary>
        ///     The transport that received this packet.
        /// </summary>
        public readonly ITransport Source;

        public Packet(
            ReadOnlySequence<byte> payload,
            int totalFrameSize,
            RingBuffer sourceBuffer,
            ITransport source)
        {
            Payload = payload;
            TotalFrameSize = totalFrameSize;
            SourceBuffer = sourceBuffer;
            Source = source;
        }
    }
}

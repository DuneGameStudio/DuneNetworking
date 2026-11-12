using System;
using DuneTransport.BufferManager.Interface;

namespace DunePresentation.Packet.Interfaces
{
    /// <summary>
    /// A network packet with a unique identifier and field-level serialization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extends <see cref="ISegmentManager"/> with packet-specific behavior. The
    /// <see cref="ISegmentManager"/> lifecycle handles segment reservation and release;
    /// <see cref="IPacket"/> adds the <see cref="PacketId"/> and field serialization.
    /// </para>
    /// <para>
    /// The field methods operate on the span positioned after the 2-byte
    /// presentation header — the header itself is written by the peer's send
    /// pipeline, not by the packet. The OnSerialize/OnDeserialize default
    /// implementations bridge the field methods to the segment lifecycle and
    /// maintain <see cref="PacketSize"/> (header included).
    /// </para>
    /// </remarks>
    public interface IPacket : ISegmentManager
    {
        /// <summary>Gets the unique packet type identifier.</summary>
        ushort PacketId { get; }

        /// <summary>
        /// Serializes packet fields into the provided buffer.
        /// </summary>
        /// <param name="buffer">The destination buffer (positioned after the presentation header).</param>
        /// <param name="bytesWritten">The number of bytes written to the buffer.</param>
        void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten);

        /// <summary>
        /// Deserializes packet fields from the provided buffer.
        /// </summary>
        /// <param name="buffer">The source buffer (positioned after the presentation header).</param>
        /// <param name="length">The number of valid bytes in the buffer.</param>
        /// <returns><c>true</c> if deserialization succeeded; otherwise <c>false</c>.</returns>
        bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length);

        /// <inheritdoc cref="ISegmentManager.OnSerialize"/>
        bool ISegmentManager.OnSerialize()
        {
            WriteFieldsToBuffer(segment.Memory.Span.Slice(PresentationHeader.Size), out int bytesWritten);
            PacketSize = PresentationHeader.Size + bytesWritten;
            return true;
        }

        /// <inheritdoc cref="ISegmentManager.OnDeserialize"/>
        bool ISegmentManager.OnDeserialize()
        {
            int userLen = PacketSize - PresentationHeader.Size;
            return ReadFieldsFromBuffer(
                segment.Memory.Span.Slice(PresentationHeader.Size, userLen), userLen);
        }
    }
}

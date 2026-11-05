using System;
using System.Buffers.Binary;

namespace DunePresentation.Packet
{
    /// <summary>
    /// Writes and reads the 2-byte little-endian packet ID header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every packet on the wire is prefixed with this header for type identification.
    /// The header immediately follows the transport's length prefix.
    /// </para>
    /// </remarks>
    internal static class PresentationHeader
    {
        /// <summary>Header size in bytes (2-byte little-endian packet ID).</summary>
        public const int Size = 2;

        /// <summary>
        /// Writes a packet ID to the buffer as a 2-byte little-endian value.
        /// </summary>
        /// <param name="buffer">Destination buffer (must be at least <see cref="Size"/> bytes).</param>
        /// <param name="packetId">The packet identifier to write.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
        public static void Write(Span<byte> buffer, ushort packetId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            BinaryPrimitives.WriteUInt16LittleEndian(buffer, packetId);
        }

        /// <summary>
        /// Reads a packet ID from the buffer as a 2-byte little-endian value.
        /// </summary>
        /// <param name="buffer">Source buffer (must be at least <see cref="Size"/> bytes).</param>
        /// <param name="packetId">When this method returns, contains the read packet ID.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
        public static void Read(ReadOnlySpan<byte> buffer, out ushort packetId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            packetId = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        }
    }
}

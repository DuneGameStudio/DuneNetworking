using System;
using System.Buffers.Binary;

namespace DunePresentation.Packet
{
    internal static class PresentationHeader
    {
        public const int Size = 2;

        public static void Write(Span<byte> buffer, ushort packetId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            BinaryPrimitives.WriteUInt16LittleEndian(buffer, packetId);
        }

        public static void Read(ReadOnlySpan<byte> buffer, out ushort packetId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            packetId = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        }
    }
}

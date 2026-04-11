using System;
using System.Buffers.Binary;

namespace DunePresentation
{
    internal enum PacketType : byte
    {
        Message = 0,
        Request = 1,
        Response = 2
    }

    internal static class PresentationHeader
    {
        public const int Size = 5;

        public static void Write(Span<byte> buffer, PacketType type,
                                 ushort packetId, ushort correlationId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            buffer[0] = (byte)((byte)type & 0x03);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(1), packetId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(3), correlationId);
        }

        public static void Read(ReadOnlySpan<byte> buffer, out PacketType type,
                                out ushort packetId, out ushort correlationId)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"Buffer too small for presentation header (need {Size} bytes).", nameof(buffer));

            type = (PacketType)(buffer[0] & 0x03);
            packetId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            correlationId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(3));
        }
    }
}

using System;

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

        public static void Write(Span<byte> buffer, PacketType type, bool encrypted,
                                 ushort packetId, ushort correlationId)
        {
            byte flags = (byte)((byte)type & 0x03);
            if (encrypted)
                flags |= 0x04;

            buffer[0] = flags;
            BitConverter.TryWriteBytes(buffer.Slice(1), packetId);
            BitConverter.TryWriteBytes(buffer.Slice(3), correlationId);
        }

        public static void Read(ReadOnlySpan<byte> buffer, out PacketType type,
                                out bool encrypted, out ushort packetId,
                                out ushort correlationId)
        {
            byte flags = buffer[0];
            type = (PacketType)(flags & 0x03);
            encrypted = (flags & 0x04) != 0;
            packetId = BitConverter.ToUInt16(buffer.Slice(1));
            correlationId = BitConverter.ToUInt16(buffer.Slice(3));
        }
    }
}

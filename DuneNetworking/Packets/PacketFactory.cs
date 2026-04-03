using System;

namespace DuneNetworking.Packets
{
    public static class PacketFactory
    {
        public static IRequestResponse ToIPacket(Memory<byte> packet)
        {
            int packetId = BitConverter.ToUInt16(packet.Span.Slice(2, 2));

            return AssignPacketHandler(packetId);
        }

        public static IRequestResponse AssignPacketHandler(int packetId)
        {
            return packetId switch
            {
                // (int)PacketID.AccountAuthentication => new AccountAuthenticationPacket(),
                _ => throw new ArgumentException("Invalid packet ID"),
            };
        }
    }
}
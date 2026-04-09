using System;

namespace DunePresentation.Interface
{
    public interface IPacket
    {
        ushort PacketId { get; }

        void Serialize(Span<byte> buffer, out int bytesWritten);

        bool Deserialize(ReadOnlySpan<byte> buffer, int length);
    }

    public interface IRequest<TResponse> : IPacket
        where TResponse : IResponse, new()
    {
    }

    public interface IResponse : IPacket
    {
    }

    public interface IMessage : IPacket
    {
    }
}

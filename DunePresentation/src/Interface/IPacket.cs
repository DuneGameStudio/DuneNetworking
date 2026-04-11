using System;
using DuneTransport.BufferManager.Interface;

namespace DunePresentation.Interface
{
    public interface IPacket : ISegmentManager
    {
        ushort PacketId { get; }

        void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten);

        bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length);

        bool ISegmentManager.OnSerialize()
        {
            WriteFieldsToBuffer(segment.Memory.Span.Slice(PresentationHeader.Size), out int bytesWritten);
            PacketSize = PresentationHeader.Size + bytesWritten;
            return true;
        }

        bool ISegmentManager.OnDeserialize()
        {
            int userLen = PacketSize - PresentationHeader.Size;
            return ReadFieldsFromBuffer(
                segment.Memory.Span.Slice(PresentationHeader.Size, userLen), userLen);
        }
    }

    public interface IRequest : IPacket
    {
    }

    public interface IResponse : IPacket
    {
    }

    public interface IMessage : IPacket
    {
    }
}

using System;
using DuneTransport.Transport.Interface;

namespace DuneTransport.BufferManager.Interface
{
    public interface ISegmentManager
    {
        Segment segment { get; set; }

        int PacketSize { get; set; }

        bool OnSerialize();

        bool OnDeserialize();

        void OnSend(ITransport transport)
        {
            transport.SendAsync(segment, PacketSize);
        }

        bool Serialize(ITransport transport, Action<Segment, int>? afterSerialize = null)
        {
            if (!transport.TryReserveSendPacket(out Segment newSegment))
                return false;

            segment = newSegment;

            if (!OnSerialize())
            {
                segment.Release();
                return false;
            }

            afterSerialize?.Invoke(segment, PacketSize);
            return true;
        }

        bool Deserialize(Action<Segment, int>? beforeDeserialize = null)
        {
            beforeDeserialize?.Invoke(segment, PacketSize);

            bool result = OnDeserialize();
            segment.Release();
            return result;
        }
    }
}

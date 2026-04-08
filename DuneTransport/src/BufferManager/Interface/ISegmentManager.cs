using DuneTransport.Transport.Interface;

namespace DuneTransport.BufferManager.Interface
{
    public interface ISegmentManager
    {
        ushort Id { get; set; }
        
        Segment segment { get; set; }
        
        public int PacketSize { get; set; }
        
        void OnDeserialize();
        
        void OnSerialize();
        
        void OnSend(ITransport transport)
        {
            transport.SendAsync(segment, PacketSize);
        }
        
        void Serialize(SegmentedBuffer segmentedBuffer)
        {
            if (segmentedBuffer.TryReserveSegment(out Segment newSegment))
            {
                segment = newSegment;
                OnSerialize();
            }
        }
        
        void Deserialize()
        {
            OnDeserialize();
            segment.Release();
        }
    }
}
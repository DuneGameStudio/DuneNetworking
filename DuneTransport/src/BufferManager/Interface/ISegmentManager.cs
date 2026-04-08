using DuneTransport.Transport.Interface;

namespace DuneTransport.BufferManager.Interface
{
    public interface ISegmentManager
    {
        ushort Id { get; set; }
        
        Segment segment { get; set; }
        
        public int PacketSize { get; set; }
        
        bool OnDeserialize();
        
        bool OnSerialize();
        
        void OnSend(ITransport transport)
        {
            transport.SendAsync(segment, PacketSize);
        }
        
        bool Serialize(ITransport transport)
        {
            if (!transport.TryReserveSendPacket(out Segment newSegment)) 
                return false;
            
            segment = newSegment;
            
            return OnSerialize();
        }
        
        bool Deserialize()
        {
            if (!OnDeserialize()) 
                return false;
            
            segment.Release();
            return true;
        }
    }
}
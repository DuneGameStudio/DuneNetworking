using DuneTransport.ByteArrayManager.Interface;

namespace DuneTransport.Packets.Interface
{
    public interface IRequestResponse : ISegmentManager
    {
        ushort Id { get; set; }

        public void Execute(ISession gameSession);
    }
}
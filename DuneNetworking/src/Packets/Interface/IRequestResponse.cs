using DuneTransport.ByteArrayManager.Interface;

namespace DuneNetworking.Packets.Interface
{
    public interface IRequestResponse : ISegmentManager
    {
        ushort Id { get; set; }

        public void Execute(ISession gameSession);
    }
}
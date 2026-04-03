using DuneNetworking.ByteArrayManager;

namespace DuneNetworking.Packets
{
    public interface IRequestResponse : SegmentManager
    {
        ushort Id { get; set; }

        public void Execute(ISession gameSession);
    }
}
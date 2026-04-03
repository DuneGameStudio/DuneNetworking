using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Packets
{
    /// <summary>
    ///     A packet that follows the request-response pattern.
    ///     Extends IPacket with an Execute method that receives the source
    ///     transport, allowing the handler to send a response.
    ///
    ///     Receive path: OnDeserialize(payload) → Execute(source)
    /// </summary>
    public interface IRequestResponse : IPacket
    {
        /// <summary>
        ///     Execute game logic for this packet. The source transport
        ///     is provided so the handler can send responses.
        /// </summary>
        void Execute(ITransport source);
    }
}

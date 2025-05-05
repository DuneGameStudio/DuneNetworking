using System;
using FramedNetworkingSolution.Transport.Interface;

namespace FramedNetworkingSolution.SocketConnectors.Interface
{
    public interface IServer : IDisposable
    {
        event EventHandler<ITransport> onNewClientConnection;
        void Initialize(string address, int port);
        void StartListening();
        void StopListening();
        
        // void AcceptConnection();
        void StartAcceptingConnections();
        void StopAcceptingConnections();
        void StopServer();
    }
}
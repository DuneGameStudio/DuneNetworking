using System;
using DuneTransport.Transport.Interface;

namespace DuneTransport.SocketConnectors.Interface
{
    public interface IServer : IDisposable
    {
        event EventHandler<ITransport> onNewClientConnection;
        void Initialize(string address, int port);
        void StartListening();
        void StopListening();
        void StartAcceptingConnections();
        void StopAcceptingConnections();
        void StopServer();
    }
}

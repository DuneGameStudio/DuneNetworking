using System;
using DuneTransport.Transport.Interface;

namespace DuneNetworking.SocketConnectors.Interface
{
    public interface IServer : IDisposable
    {
        event EventHandler<ITransport> OnNewClientConnection;
        void Initialize(string address, int port);
        void StartListening();
        void StopListening();
        
        void StartAcceptingConnections();
        void StopAcceptingConnections();
        void StopServer();
    }
}
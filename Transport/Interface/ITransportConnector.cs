
using System;
using System.Net.Sockets;

namespace DuneNetworking.Transport.Interface
{
    public interface ITransportConnector
    {
        event EventHandler<SocketAsyncEventArgs> OnAttemptConnectResultEventHandler;
        event EventHandler<SocketAsyncEventArgs> OnDisconnectedEventHandler;
        void Initialize(string address, int port);
        void AttemptConnectAsync();
        void DisconnectAsync();
    }
}
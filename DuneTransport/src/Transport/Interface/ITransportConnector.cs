using System;
using System.Net.Sockets;

namespace DuneTransport.Transport.Interface
{
    public interface ITransportConnector
    {
        Socket socket { get; set; }
        event EventHandler<SocketAsyncEventArgs> OnAttemptConnectResultEventHandler;
        event EventHandler<SocketAsyncEventArgs> OnDisconnectedEventHandler;
        void Initialize(string address, int port);
        void AttemptConnectAsync();
        void DisconnectAsync();
    }
}

using System;
using System.Net.Sockets;
using DuneTransport.Transport.Interface;

namespace DuneTransport.SocketConnectors.Interface
{
    public interface IClient : IDisposable
    {
        event Action<object, bool, ITransport?>? OnAttemptConnectResponseHandler;
        event EventHandler<SocketAsyncEventArgs>? OnDisconnectedHandler;

        void AttemptConnectAsync(string address, int port);
        void OnAttemptConnectResponse(object sender, SocketAsyncEventArgs tryConnectEventArgs);
        void Disconnect();
        void OnDisconnected(object sender, SocketAsyncEventArgs onDisconnected);
    }
}

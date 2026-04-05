using System;
using DuneTransport.Transport.Interface;

namespace DuneNetworking.SocketConnectors.Interface
{
    public interface IClient : IDisposable
    {
        bool IsConnected { get; }

        event Action<bool>? OnConnectResult;
        event Action? OnDisconnected;

        void ConnectAsync(string address, int port);
        void Disconnect();
    }
}

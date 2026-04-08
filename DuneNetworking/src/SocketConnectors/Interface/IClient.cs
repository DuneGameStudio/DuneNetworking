using System;
using System.Net.Sockets;

namespace DuneNetworking.SocketConnectors.Interface
{
    public interface IClient : IDisposable
    {
        bool IsConnected { get; }

        event Action<IConnection>? OnConnected;
        event Action<SocketError>? OnConnectFailed;

        bool ConnectAsync(string address, int port);
    }
}

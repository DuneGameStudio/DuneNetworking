using System;
using DuneTransport.Transport.Interface;

namespace DuneSession.SocketConnectors.Interface
{
    public interface IConnection : IDisposable
    {
        bool IsConnected { get; }
        ITransport Transport { get; }

        event Action? OnDisconnected;
        event Action? OnDisconnectRequested;

        void DisconnectAsync();
    }
}

using System;
using System.Collections.Generic;
using DuneTransport.Transport.Interface;

namespace DuneNetworking.SocketConnectors.Interface
{
    public interface IServer : IDisposable
    {
        bool IsListening { get; }
        
        // Expose the count or the active sessions if the app layer needs to query them.
        int ActiveConnectionCount { get; }
        
        event Action<ITransport>? OnClientConnected;
        
        void StartListening(string address, int port);
        
        // Halts new connections, keeps existing clients alive.
        void StopListening();
        
        // Halts new connections and forcefully disconnects all active clients.
        void StopServer();
    }
}
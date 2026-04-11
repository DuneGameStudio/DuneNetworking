using System;
using DunePresentation.Packet.Interfaces;

namespace DunePresentation.Peer.Interfaces
{
    public interface IPeer : IDisposable
    {
        event Action? OnDisconnected; 
        
        bool IsConnected { get; }

        void StartReceiving();

        bool Send<T>(T packet) where T : IPacket;

        void Disconnect();
    }
}

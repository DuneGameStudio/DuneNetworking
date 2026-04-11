using System;
using DunePresentation.Packet.Interfaces;

namespace DunePresentation.Peer.Interfaces
{
    public interface IPeer : IDisposable
    {
        bool IsConnected { get; }

        event Action? OnDisconnected;

        bool Send<T>(T packet) where T : IPacket;

        void Disconnect();
    }
}

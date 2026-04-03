using System;
using System.Net.Sockets;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Packets
{
    public interface ISession
    {
        Guid guid { get; set; }
        ITransport transport { get; set; }

        Action<SocketAsyncEventArgs, Guid>? OnDisconnectedHandler { get; set; }
    }
}

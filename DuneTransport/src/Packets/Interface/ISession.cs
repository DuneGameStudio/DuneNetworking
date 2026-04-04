using System;
using System.Net.Sockets;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Packets.Interface
{
    public interface ISession
    {
        Guid guid { get; set; }
        ITransport transport { get; set; }

        Action<SocketAsyncEventArgs, Guid>? OnDisconnectedHandler { get; set; }
    }
}

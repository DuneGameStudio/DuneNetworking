using System;
using System.Net.Sockets;
using DuneTransport.ByteArrayManager;
using DuneTransport.Transport.Interface;

namespace DuneNetworking.Packets.Interface
{
    public interface ISession
    {
        public Guid guid { get; set; }
        public ITransport transport { get; set; }

        public Action<SocketAsyncEventArgs, Guid>? OnDisconnectedHandler { get; set; }
        public Action<SocketAsyncEventArgs, Guid, Segment>? OnPacketReceivedHandler { get; set; }  
    }
}
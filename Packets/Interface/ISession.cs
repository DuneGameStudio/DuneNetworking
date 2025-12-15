using System;
using System.Net.Sockets;
using DuneNetworking.ByteArrayManager;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Packets
{
    public interface ISession
    {
        public Guid guid { get; set; }
        public ITransport transport { get; set; }

        public Action<SocketAsyncEventArgs, Guid>? OnDisconnectedHandler { get; set; }
        public Action<SocketAsyncEventArgs, Guid, Segment>? OnPacketReceivedHandler { get; set; }  
    }
}
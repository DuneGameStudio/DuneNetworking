using System;
using System.Net.Sockets;
using DuneTransport.ByteArrayManager;

namespace DuneTransport.Transport.Interface
{
    public interface ITransport : IDisposable
    {
        bool IsConnected { get; }
        SegmentedBuffer receiveBuffer { get; }
        SegmentedBuffer sendBuffer { get; }

        event EventHandler<SocketAsyncEventArgs> OnPacketSent;
        event Action<ITransport, SocketAsyncEventArgs, Segment> OnPacketReceived;
        event EventHandler EventArgsOnDisconnected; // Notifies the Networking layer to trigger reconnects

        void ReceiveAsync(int bufferSize = 2);
        void SendAsync(Memory<byte> memory);
        void DisconnectAsync();
    }
}
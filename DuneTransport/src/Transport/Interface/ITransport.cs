using System;
using System.Net.Sockets;
using DuneTransport.BufferManager;

namespace DuneTransport.Transport.Interface
{
    public interface ITransport : IDisposable
    {
        bool IsConnected { get; set;  }
        SegmentedBuffer receiveBuffer { get; }
        SegmentedBuffer sendBuffer { get; }

        event EventHandler<SocketAsyncEventArgs>? OnPacketSent;
        event EventHandler<Segment>? OnPacketSendFailed;
        event Action<ITransport, SocketAsyncEventArgs, Segment>? OnPacketReceived;
        event Action<ITransport>? OnPacketReceiveFailed;
        event Action OnDisconnectRequested;

        void ReceiveAsync(int bufferSize = 2);
        void SendAsync(Segment packet, int packetSize);
        bool TryReserveSendPacket(out Segment segment);
    }
}
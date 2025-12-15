using System;
using System.Net.Sockets;
using DuneNetworking.ByteArrayManager;

namespace DuneNetworking.Transport.Interface
{
    public interface ITransport : ITransportConnector, IDisposable
    {
        public Socket socket { get; set; }
        SegmentedBuffer receiveBuffer { get; set; }
        SegmentedBuffer sendBuffer { get; set; }
        event EventHandler<SocketAsyncEventArgs> OnPacketSentEventHandler;
        event Action<object, SocketAsyncEventArgs, Segment> OnPacketReceived;

        void ReceiveAsync(int bufferSize = 2);
        void SendAsync(Memory<byte> memory);
    }
}
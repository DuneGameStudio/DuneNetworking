using System;
using System.Net.Sockets;
using DuneTransport.Buffer;
using DuneTransport.ByteArrayManager;

namespace DuneTransport.Transport.Interface
{
    public interface ITransport : ITransportConnector, IDisposable
    {
        /// <summary>
        ///     Ring buffer for the receive path (zero-copy).
        /// </summary>
        RingBuffer RingBuffer { get; }

        /// <summary>
        ///     Segmented buffer for the send path (unchanged).
        /// </summary>
        SegmentedBuffer sendBuffer { get; set; }

        event EventHandler<SocketAsyncEventArgs> OnPacketSentEventHandler;

        /// <summary>
        ///     Kicks off the receive chain. Call once after connection is established.
        /// </summary>
        void StartReceiving();

        void SendAsync(Memory<byte> memory);
    }
}

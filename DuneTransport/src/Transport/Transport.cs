using System;
using System.Diagnostics;
using System.Net.Sockets;
using DuneTransport.BufferManager;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Transport
{
    public class Transport : ITransport
    {
        private readonly Socket socket;

        public SegmentedBuffer receiveBuffer { get; }
        public SegmentedBuffer sendBuffer { get; }

        private Segment currentReceivingSegment;
        private Segment currentSendingSegment;

        private readonly SocketAsyncEventArgs sendEventArgs;
        private readonly SocketAsyncEventArgs receiveEventArgs;

        public bool IsConnected { get; set; } = true;

        public event EventHandler<SocketAsyncEventArgs>? OnPacketSent;
        public event EventHandler<Segment>? OnPacketSendFailed;

        public event Action<ITransport, SocketAsyncEventArgs, Segment>? OnPacketReceived;
        public event Action<ITransport>? OnPacketReceiveFailed;

        public event Action? OnDisconnectRequested;

        public Transport(Socket socket)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));

            sendBuffer = new SegmentedBuffer();
            receiveBuffer = new SegmentedBuffer();
            
            sendEventArgs = new SocketAsyncEventArgs();
            receiveEventArgs = new SocketAsyncEventArgs();

            sendEventArgs.Completed += OnPacketSentEventHandler;
            receiveEventArgs.Completed += OnPacketReceivedEventHandler;
        }

        public void ReceiveAsync(int bufferSize = 2)
        {
            if (!IsConnected) return;

            if (receiveBuffer.TryReserveSegment(out Segment newSegment, bufferSize))
            {
                currentReceivingSegment = newSegment;
                receiveEventArgs.SetBuffer(newSegment.Memory);

                try
                {
                    if (!socket.ReceiveAsync(receiveEventArgs))
                    {
                        ProcessReceive(receiveEventArgs);
                    }
                }
                catch (ObjectDisposedException)
                {
                    Debug.WriteLine("ObjectDisposedException");
                    OnPacketReceiveFailed?.Invoke(this);
                }
                catch (SocketException ex)
                {
                    Debug.WriteLine($"ReceiveAsync | SocketException: {ex.Message}", "error");
                    OnPacketReceiveFailed?.Invoke(this);
                }
            }
            else
            {
                Debug.WriteLine("ReceiveAsync | Failed to reserve memory.", "error");
                OnPacketReceiveFailed?.Invoke(this);
            }
        }

        private void OnPacketReceivedEventHandler(object? sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
        }

        private void ProcessReceive(SocketAsyncEventArgs onReceived)
        {
            if (onReceived.SocketError != SocketError.Success || onReceived.BytesTransferred == 0)
            {
                // BytesTransferred == 0 means the remote endpoint gracefully closed the connection.
                Debug.WriteLine("Connection dropped or zero bytes received.", "log");
                OnDisconnectRequested?.Invoke();
                return;
            }

            switch (onReceived.BytesTransferred)
            {
                case 2:
                    // We received the header. The header contains the length of the upcoming payload.
                    ushort payloadLength = BitConverter.ToUInt16(onReceived.MemoryBuffer.Span);

                    currentReceivingSegment.Release();

                    ReceiveAsync(payloadLength);
                    return;
                default:
                    // We received the payload.
                    OnPacketReceived?.Invoke(this, onReceived, currentReceivingSegment);
                    return;
            }
        }

        public void SendAsync(Segment packet, int packetSize)
        {
            if (!IsConnected)
            {
                Debug.WriteLine("SendAsync | Cannot send, pipeline is disconnected.", "error");
                return;
            }

            if (!sendBuffer.GetRegisteredMemory(packet.SegmentIndex, packetSize, out Memory<byte> memory))
            {
                OnPacketSendFailed?.Invoke(this, currentSendingSegment);
                return;
            }

            BitConverter.TryWriteBytes(memory.Span, (ushort)(memory.Length - 2));

            sendEventArgs.SetBuffer(memory);

            try
            {
                currentSendingSegment = packet;
                if (!socket.SendAsync(sendEventArgs))
                {
                    ProcessSend(sendEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("ObjectDisposedException");
                OnPacketSendFailed?.Invoke(this, currentSendingSegment);
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"SendAsync | SocketException: {ex.Message}", "error");
                OnPacketSendFailed?.Invoke(this, currentSendingSegment);
            }
        }

        private void OnPacketSentEventHandler(object? sender, SocketAsyncEventArgs e)
        {
            ProcessSend(e);
        }

        private void ProcessSend(SocketAsyncEventArgs onSent)
        {
            if (onSent.SocketError != SocketError.Success)
            {
                OnPacketSendFailed?.Invoke(this, currentSendingSegment);
                return;
            }

            currentSendingSegment.Release();
            OnPacketSent?.Invoke(this, onSent);
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    sendEventArgs.Completed -= OnPacketSentEventHandler;
                    receiveEventArgs.Completed -= OnPacketReceivedEventHandler;
                    
                    sendEventArgs.Dispose();
                    receiveEventArgs.Dispose();
                    
                    socket.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using DuneTransport.ByteArrayManager;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Transport
{
    public class Transport : ITransport
    {
        private readonly Socket _socket;
        
        public SegmentedBuffer receiveBuffer { get; }
        public SegmentedBuffer sendBuffer { get; }
        
        private Segment _currentReceivingSegment;
        
        private readonly SocketAsyncEventArgs _sendEventArgs;
        private readonly SocketAsyncEventArgs _receiveEventArgs;

        // 0 = disconnected, 1 = connected. Used to ensure thread-safe teardown.
        private int _connectedState = 1; 
        
        public bool IsConnected => _connectedState == 1;

        public event EventHandler<SocketAsyncEventArgs>? OnPacketSent;
        public event Action<ITransport, SocketAsyncEventArgs, Segment>? OnPacketReceived;
        public event EventHandler? EventArgsOnDisconnected;

        public Transport(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));

            sendBuffer = new SegmentedBuffer();
            receiveBuffer = new SegmentedBuffer();
            
            _sendEventArgs = new SocketAsyncEventArgs();
            _receiveEventArgs = new SocketAsyncEventArgs();

            _sendEventArgs.Completed += OnPacketSentEventHandler;
            _receiveEventArgs.Completed += OnPacketReceivedEventHandler;
        }

        public void ReceiveAsync(int bufferSize = 2)
        {
            if (!IsConnected) return;

            if (receiveBuffer.ReserveMemory(out Segment newSegment, bufferSize))
            {
                _currentReceivingSegment = newSegment;
                _receiveEventArgs.SetBuffer(newSegment.Memory);

                try
                {
                    if (!_socket.ReceiveAsync(_receiveEventArgs))
                    {
                        ProcessReceive(_receiveEventArgs);
                    }
                }
                catch (ObjectDisposedException)
                {
                    DisconnectAsync();
                }
                catch (SocketException ex)
                {
                    Debug.WriteLine($"ReceiveAsync | SocketException: {ex.Message}", "error");
                    DisconnectAsync();
                }
            }
            else
            {
                Debug.WriteLine("ReceiveAsync | Failed to reserve memory.", "error");
                DisconnectAsync(); // If memory fails, the pipeline is broken.
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
                DisconnectAsync();
                return;
            }

            switch (onReceived.BytesTransferred)
            {
                case 2:
                    // We received the header. The header contains the length of the upcoming payload.
                    ushort payloadLength = BitConverter.ToUInt16(onReceived.MemoryBuffer.Span);

                    _currentReceivingSegment.Release();

                    ReceiveAsync(payloadLength);
                    return;
                default:
                    // We received the payload.
                    OnPacketReceived?.Invoke(this, onReceived, _currentReceivingSegment);
                    return;
            }
        }

        public void SendAsync(Memory<byte> memory)
        {
            if (!IsConnected)
            {
                Debug.WriteLine("SendAsync | Cannot send, pipeline is disconnected.", "error");
                return;
            }

            // Write the 2-byte length prefix into the start of the payload.
            BitConverter.TryWriteBytes(memory.Span, (ushort)(memory.Length - 2));

            _sendEventArgs.SetBuffer(memory);

            try
            {
                if (!_socket.SendAsync(_sendEventArgs))
                {
                    ProcessSend(_sendEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                DisconnectAsync();
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"SendAsync | SocketException: {ex.Message}", "error");
                DisconnectAsync();
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
                DisconnectAsync();
                return;
            }

            OnPacketSent?.Invoke(this, onSent);
        }

        public void DisconnectAsync()
        {
            // Interlocked ensures this teardown sequence executes exactly once, 
            // even if called concurrently by the user and a failed receive loop.
            if (Interlocked.Exchange(ref _connectedState, 0) == 1)
            {
                try
                {
                    // Shutdown flushes pending operations gracefully.
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                {
                    // Ignore exceptions during shutdown; the socket may already be dead.
                }
                finally
                {
                    _socket.Close();
                    
                    // Notify upper layers to begin teardown or auto-reconnect.
                    EventArgsOnDisconnected?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #region IDisposable
        
        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Debug.WriteLine("Disposing Transport", "log");
                    
                    DisconnectAsync(); // Ensure socket is closed

                    _socket.Dispose();
                    _sendEventArgs.Dispose();
                    _receiveEventArgs.Dispose();
                }
                _disposedValue = true;
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
using System;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using DuneTransport.Buffer;
using DuneTransport.ByteArrayManager;
using DuneTransport.Threading;
using DuneTransport.Transport.Interface;

namespace DuneTransport.Transport
{
    public class Transport : ITransport
    {
        //  Core State
        public Socket socket { get; set; }
        public RingBuffer RingBuffer { get; }
        public SegmentedBuffer sendBuffer { get; set; }

        private readonly NetworkEngine _engine;
        
        //  SocketAsyncEventArgs
        private readonly SocketAsyncEventArgs sendEventArgs;
        private readonly SocketAsyncEventArgs receiveEventArgs;
        private readonly SocketAsyncEventArgs connectEventArgs;
        private readonly SocketAsyncEventArgs disconnectEventArgs;

        
        //  Receive Suspension
        private readonly object _receiveLock = new object();
        private bool _receiveSuspended;

        
        //  Constructor
        public Transport(Socket socket, NetworkEngine engine, int receiveBufferCapacity = 262144)
        {
            this.socket = socket;
            _engine = engine;

            RingBuffer = new RingBuffer(receiveBufferCapacity);
            RingBuffer.RegisterOnSpaceFreed(OnBufferSpaceFreed);

            sendBuffer = new SegmentedBuffer();

            sendEventArgs = new SocketAsyncEventArgs();
            receiveEventArgs = new SocketAsyncEventArgs();
            connectEventArgs = new SocketAsyncEventArgs();
            disconnectEventArgs = new SocketAsyncEventArgs();

            sendEventArgs.Completed += OnPacketSentEventHandler;
            receiveEventArgs.Completed += OnReceiveCompleted;
            connectEventArgs.Completed += OnAttemptConnectResultEventHandler;
            disconnectEventArgs.Completed += OnDisconnectedEventHandler;
        }

        
        //  Receive Path (new)
        /// <summary>
        ///     Kicks off the receive chain. Call once after the connection is established.
        /// </summary>
        public void StartReceiving()
        {
            lock (_receiveLock)
            {
                Memory<byte> writable = RingBuffer.GetWritableMemory();

                if (writable.Length == 0)
                {
                    _receiveSuspended = true;
                    return;
                }
                
                _receiveSuspended = false;
                receiveEventArgs.SetBuffer(writable);

                if (!socket.ReceiveAsync(receiveEventArgs))
                {
                    OnReceiveCompleted(this, receiveEventArgs);
                }
            }
        }

        /// <summary>
        ///     IOCP completion callback for asynchronous receives.
        /// </summary>
        private void OnReceiveCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.BytesTransferred == 0 || args.SocketError != SocketError.Success)
            {
                DisconnectAsync();
                return;
            }

            RingBuffer.CommitWrite(args.BytesTransferred);
            _engine.SignalDataReceived(this);
            StartReceiving();
        }

        /// <summary>
        ///     Called by the ring buffer on every Release (from the deserialization thread).
        ///     Resumes a suspended receive if the buffer was previously full.
        /// </summary>
        private void OnBufferSpaceFreed()
        {
            lock (_receiveLock)
            {
                if (!_receiveSuspended)
                    return;

                Memory<byte> writable = RingBuffer.GetWritableMemory();

                if (writable.Length == 0)
                    return;

                _receiveSuspended = false;
                receiveEventArgs.SetBuffer(writable);

                if (socket.ReceiveAsync(receiveEventArgs))
                    return;
            }

            OnReceiveCompleted(this, receiveEventArgs);
        }
        
        //  Send Path
        public event EventHandler<SocketAsyncEventArgs>? OnPacketSentEventHandler;

        public void SendAsync(Memory<byte> memory)
        {
            if (socket.Connected)
            {
                BitConverter.TryWriteBytes(memory.Span, (ushort)(memory.Length - 2));

                sendEventArgs.SetBuffer(memory);

                if (!socket.SendAsync(sendEventArgs))
                {
                    OnPacketSentEventHandler?.Invoke(socket, sendEventArgs);
                }
            }
            else
            {
                Debug.WriteLine("Send | Client Is Not Connected", "error");
            }
        }
        
        //  ITransportConnector
        private IPEndPoint? iPEndPoint;

        public event EventHandler<SocketAsyncEventArgs>? OnAttemptConnectResultEventHandler;
        public event EventHandler<SocketAsyncEventArgs>? OnDisconnectedEventHandler;

        public void Initialize(string address, int port)
        {
            iPEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
        }

        public void AttemptConnectAsync()
        {
            connectEventArgs.RemoteEndPoint = iPEndPoint;

            if (!socket.ConnectAsync(connectEventArgs))
            {
                OnAttemptConnectResultEventHandler?.Invoke(socket, connectEventArgs);
            }
        }

        public void DisconnectAsync()
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            if (!socket.DisconnectAsync(disconnectEventArgs))
            {
                OnDisconnectedEventHandler?.Invoke(socket, disconnectEventArgs);
            }
        }

        
        //  IDisposable
        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Debug.WriteLine("Disposing Transport");
                    socket.Dispose();
                    sendEventArgs.Dispose();
                    receiveEventArgs.Dispose();
                    connectEventArgs.Dispose();
                    disconnectEventArgs.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}

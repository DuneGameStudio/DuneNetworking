using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.Transport.Interface;

namespace DuneSession.SocketConnectors
{
    public class Connection : IConnection
    {
        private readonly Socket socket;
        private volatile int connectedState;
        private volatile int disconnectingState;

        public bool IsConnected => connectedState == 1;
        public ITransport Transport { get; }

        public event Action? OnDisconnectRequested;
        public event Action? OnDisconnected;

        private readonly SocketAsyncEventArgs disconnectAsyncSocketAsyncEventArgs;

        public Connection(Socket socket)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            connectedState = 1;

            Transport = new DuneTransport.Transport.Transport(socket);
            Transport.OnDisconnectRequested += HandleDisconnectRequested;
            
            disconnectAsyncSocketAsyncEventArgs = new SocketAsyncEventArgs();
            disconnectAsyncSocketAsyncEventArgs.Completed += OnDisconnect;
        }

        private void HandleDisconnectRequested()
        {
            OnDisconnectRequested?.Invoke();
        }

        public void DisconnectAsync()
        {
            if (Interlocked.Exchange(ref disconnectingState, 1) != 0)
                return;
            
            disconnectAsyncSocketAsyncEventArgs.DisconnectReuseSocket = false;
            
            if (!socket.DisconnectAsync(disconnectAsyncSocketAsyncEventArgs))
            {
                OnDisconnect(this, disconnectAsyncSocketAsyncEventArgs);
            }
        }

        private void OnDisconnect(object? sender, SocketAsyncEventArgs e)
        {
            if (Interlocked.Exchange(ref connectedState, 0) != 1)
                return;

            Transport.IsConnected = false;
            
            socket.Close();
            OnDisconnected?.Invoke();
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Transport.OnDisconnectRequested -= HandleDisconnectRequested;
                    disconnectAsyncSocketAsyncEventArgs.Completed -= OnDisconnect;
                    
                    disconnectAsyncSocketAsyncEventArgs.Dispose();
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

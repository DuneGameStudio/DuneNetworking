using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DuneNetworking.SocketConnectors.Interface;

namespace DuneNetworking.SocketConnectors
{
    public class ClientConnector : IClient
    {
        private Socket? socket;
        private readonly SocketAsyncEventArgs connectEventArgs;
        private IConnection? connection;
        private volatile int connectingState;

        public bool IsConnected => connection?.IsConnected ?? false;

        public event Action<IConnection>? OnConnected;
        public event Action<SocketError>? OnConnectFailed;

        public ClientConnector()
        {
            connectEventArgs = new SocketAsyncEventArgs();
            connectEventArgs.Completed += OnConnectCompleted;
        }

        public bool ConnectAsync(string address, int port)
        {
            if (IsConnected)
                return false;
            
            if (Interlocked.Exchange(ref connectingState, 1) != 0)
                return false;

            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                
                connectEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
                
                if (!socket.ConnectAsync(connectEventArgs))
                    ProcessConnect(connectEventArgs);
                
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"ConnectAsync Exception {e}");
                
                socket?.Dispose();
                Interlocked.Exchange(ref connectingState, 0);
                
                return false;
            }
        }

        private void OnConnectCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessConnect(e);
        }

        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            if (Interlocked.Exchange(ref connectingState, 0) != 1)
                return;

            if (e.SocketError == SocketError.Success)
            {
                connection = new Connection(e.ConnectSocket);
                OnConnected?.Invoke(connection);
            }
            else
            {
                socket?.Dispose();
                OnConnectFailed?.Invoke(e.SocketError);
            }
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    connectEventArgs.Completed -= OnConnectCompleted;
                    
                    connectEventArgs.Dispose();
                    connection?.Dispose();
                    socket?.Dispose();
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

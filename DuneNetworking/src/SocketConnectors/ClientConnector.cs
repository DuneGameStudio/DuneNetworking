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
        private readonly Socket _socket;
        private readonly SocketAsyncEventArgs _connectEventArgs;

        // 0 = not connected, 1 = connected. Thread-safe one-time disconnect guard.
        private int _connectedState;

        public bool IsConnected => _connectedState == 1;

        public event Action<bool>? OnConnectResult;
        public event Action? OnDisconnected;

        public ClientConnector()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            _connectEventArgs = new SocketAsyncEventArgs();
            _connectEventArgs.Completed += OnConnectCompleted;
        }

        public void ConnectAsync(string address, int port)
        {
            _connectEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port);

            try
            {
                if (!_socket.ConnectAsync(_connectEventArgs))
                {
                    ProcessConnect(_connectEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                // Socket already disposed during teardown
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"ConnectAsync | SocketException: {ex.Message}", "Error");
                OnConnectResult?.Invoke(false);
            }
        }

        private void OnConnectCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessConnect(e);
        }

        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                Interlocked.Exchange(ref _connectedState, 1);
                OnConnectResult?.Invoke(true);
            }
            else
            {
                Debug.WriteLine($"ProcessConnect | Connection failed: {e.SocketError}", "Error");
                OnConnectResult?.Invoke(false);
            }
        }

        public void Disconnect()
        {
            if (Interlocked.Exchange(ref _connectedState, 0) == 1)
            {
                try
                {
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                {
                    // Ignore -- socket may already be dead.
                }
                finally
                {
                    _socket.Close();
                    OnDisconnected?.Invoke();
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
                    Disconnect();

                    _socket.Dispose();
                    _connectEventArgs.Dispose();
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

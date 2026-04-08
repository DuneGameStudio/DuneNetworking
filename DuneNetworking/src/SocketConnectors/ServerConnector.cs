using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DuneNetworking.SocketConnectors.Interface;

namespace DuneNetworking.SocketConnectors
{
    public class ServerConnector : IServer
    {
        private readonly Socket socket;
        private readonly SocketAsyncEventArgs acceptEventArgs;

        private volatile int isListening;

        public bool IsListening => isListening == 1;

        public event Action<IConnection>? OnClientConnected;
        public event Action<SocketError>? OnAcceptFailed;

        public ServerConnector()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            acceptEventArgs = new SocketAsyncEventArgs();
            acceptEventArgs.Completed += OnAcceptCompleted;
        }

        public void StartListening(string address, int port)
        {
            if (Interlocked.Exchange(ref isListening, 1) != 0)
            {
                Debug.WriteLine("StartListening | Server is already running.", "Error");
                return;
            }

            try
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(address), port);

                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                socket.Bind(endPoint);
                socket.Listen((int)SocketOptionName.MaxConnections);
                
                Debug.WriteLine($"Server started listening on {address}:{port}", "log");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StartListening | Failed to start server: {ex.Message}", "Error");
                isListening = 0;
            }
        }

        public void StopListening()
        {
            if (Interlocked.Exchange(ref isListening, 0) != 1)
                return;
            
            try
            {
                socket.Close();
                Debug.WriteLine("Server stopped listening for new connections.", "log");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopListening | Error during shutdown: {ex.Message}", "Error");
            }
        }
        
        public void AcceptConnection()
        {
            if (!IsListening) 
                return;

            acceptEventArgs.AcceptSocket = null;

            try
            {
                if (!socket.AcceptAsync(acceptEventArgs))
                {
                    ProcessAccept(acceptEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("ObjectDisposedException");
                OnAcceptFailed?.Invoke(SocketError.SocketError);
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"AcceptConnection | SocketException: {ex.Message}", "Error");
                OnAcceptFailed?.Invoke(ex.SocketErrorCode);
            }
        }

        private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (!IsListening) 
                return;
            
            if (e.SocketError == SocketError.Success && e.AcceptSocket != null)
            {
                IConnection connection = new Connection(e.AcceptSocket);
                OnClientConnected?.Invoke(connection);
            }
            else
            {
                OnAcceptFailed?.Invoke(e.SocketError);
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
                    acceptEventArgs.Completed -= OnAcceptCompleted;
                    
                    socket.Dispose();
                    acceptEventArgs.Dispose();
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

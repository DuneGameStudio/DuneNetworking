using System;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using DuneNetworking.Threading;
using DuneNetworking.SocketConnectors.Interface;

namespace DuneNetworking.SocketConnectors
{
    public class ClientConnector : IClient
    {
        private readonly Socket socket;
        private readonly NetworkEngine _engine;

        public bool IsConnected => socket.Connected;

        private readonly SocketAsyncEventArgs connectEventArgs;
        private readonly SocketAsyncEventArgs disconnectEventArgs;

        /// <param name="engine">Shared NetworkEngine that owns parser/deserializer threads.</param>
        public ClientConnector(NetworkEngine engine)
        {
            _engine = engine;

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            connectEventArgs = new SocketAsyncEventArgs();
            disconnectEventArgs = new SocketAsyncEventArgs();

            connectEventArgs.Completed += OnAttemptConnectResponse;
            disconnectEventArgs.Completed += OnDisconnected;
        }

        #region IClient

        public event Action<object, bool, Transport.Interface.ITransport?>? OnAttemptConnectResponseHandler;
        public event EventHandler<SocketAsyncEventArgs>? OnDisconnectedHandler;

        public void AttemptConnectAsync(string address, int port)
        {
            connectEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port);

            if (!socket.ConnectAsync(connectEventArgs))
            {
                OnAttemptConnectResponse(socket, connectEventArgs);
            }
        }

        public void OnAttemptConnectResponse(object sender, SocketAsyncEventArgs connectEventArgs)
        {
            if (connectEventArgs.SocketError == SocketError.Success)
            {
                var transport = new Transport.Transport(connectEventArgs.ConnectSocket, _engine);
                OnAttemptConnectResponseHandler?.Invoke(sender, true, transport);
                // Caller is responsible for calling transport.StartReceiving()
            }
            else
            {
                OnAttemptConnectResponseHandler?.Invoke(sender, false, null);
                Debug.WriteLine("Session Try Connect Failed", "log");
            }
        }

        public void Disconnect()
        {
            socket.Shutdown(SocketShutdown.Both);

            if (!socket.DisconnectAsync(disconnectEventArgs))
            {
                OnDisconnected(socket, disconnectEventArgs);
            }
        }

        public void OnDisconnected(object sender, SocketAsyncEventArgs onDisconnected)
        {
            socket.Close();
            OnDisconnectedHandler?.Invoke(sender, onDisconnected);
        }

        #endregion

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    socket.Dispose();
                    connectEventArgs.Dispose();
                    disconnectEventArgs.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}

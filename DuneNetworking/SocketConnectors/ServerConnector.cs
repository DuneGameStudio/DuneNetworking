using System;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using DuneNetworking.Threading;
using DuneNetworking.Transport.Interface;
using DuneNetworking.SocketConnectors.Interface;

namespace DuneNetworking.SocketConnectors
{
    public class ServerConnector : IServer
    {
        private readonly Socket socket;
        private readonly NetworkEngine _engine;

        private bool isListening;
        private bool isAccepting;

        private readonly SocketAsyncEventArgs onNewClientConnectionEventArgs;

        /// <param name="engine">Shared NetworkEngine that owns parser/deserializer threads.</param>
        public ServerConnector(NetworkEngine engine)
        {
            _engine = engine;

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            onNewClientConnectionEventArgs = new SocketAsyncEventArgs();
            onNewClientConnectionEventArgs.Completed += OnNewConnection;
        }

        #region IServer

        public event EventHandler<ITransport>? onNewClientConnection;

        private IPEndPoint? iPEndPoint;

        public void Initialize(string address, int port)
        {
            iPEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            socket.Bind(iPEndPoint);
        }

        public void StartListening()
        {
            if (!isListening)
            {
                socket.Listen(-1);
                isListening = true;
            }
            else
            {
                Debug.WriteLine("StartListenForConnections | Server Already Started.", "Error");
            }
        }

        public void StopListening()
        {
            if (isListening)
            {
                socket.Listen(0);
                socket.Shutdown(SocketShutdown.Both);
                isListening = false;
            }
            else
            {
                Debug.WriteLine("StartListenForConnections | Server Has Already Stopped.", "Error");
            }
        }

        void AcceptConnection()
        {
            if (isListening)
            {
                if (!socket.AcceptAsync(onNewClientConnectionEventArgs))
                {
                    OnNewConnection(socket, onNewClientConnectionEventArgs);
                }
            }
            else
            {
                Debug.WriteLine("StartAcceptingConnections | Server is Not Listening.", "Error");
                StopAcceptingConnections();
            }
        }

        public void StartAcceptingConnections()
        {
            isAccepting = true;
            AcceptConnection();
        }

        public void StopAcceptingConnections()
        {
            isAccepting = false;
        }

        private void OnNewConnection(object sender, SocketAsyncEventArgs newClientConnectionEventArgs)
        {
            var transport = new Transport.Transport(newClientConnectionEventArgs.AcceptSocket, _engine);
            transport.StartReceiving();

            onNewClientConnection?.Invoke(sender, transport);

            onNewClientConnectionEventArgs.AcceptSocket = null;

            if (isAccepting)
            {
                AcceptConnection();
            }
        }

        public void StopServer()
        {
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        }

        #endregion

        #region IDisposable

        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    socket.Dispose();
                    onNewClientConnectionEventArgs.Dispose();
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

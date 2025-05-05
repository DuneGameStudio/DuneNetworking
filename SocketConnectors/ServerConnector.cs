using System;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using FramedNetworkingSolution.Transport.Interface;
using FramedNetworkingSolution.SocketConnectors.Interface;

namespace FramedNetworkingSolution.SocketConnectors
{
    public class ServerConnector : IServer
    {
        /// <summary>
        ///     The Server's Socket.
        /// </summary>
        private readonly Socket socket;

        /// <summary>
        ///     Server Connection Listening State.
        /// </summary>
        private bool isListening;

        /// <summary>
        ///     Server New Client Accepting State.
        /// </summary>
        private bool isAccepting;

        /// <summary>
        ///     Wrapper Class For The Event That Fires When a New Client Connects.
        /// </summary>
        private readonly SocketAsyncEventArgs onNewClientConnectionEventArgs;

        /// <summary>
        ///     Initializes The Server To Accept Connections Asynchronously.
        /// </summary>
        public ServerConnector()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            onNewClientConnectionEventArgs = new SocketAsyncEventArgs();

            onNewClientConnectionEventArgs.Completed += OnNewConnection;
        }

        #region IServer
        /// <summary>
        ///     The Event That Fires When a New Client Connects.
        /// </summary>
        public event EventHandler<ITransport>? onNewClientConnection;

        /// <summary>
        ///     Represents the server's IP endpoint, including the IP address and port number,
        ///     used to bind the socket for network communication.
        /// </summary>
        private IPEndPoint? iPEndPoint;

        /// <summary>
        ///     Initializes the server by setting up the network endpoint and binding the server socket
        ///     using the specified IP address and port number.
        /// </summary>
        /// <param name="address">The IP address for the server to bind to.</param>
        /// <param name="port">The port number for the server to listen on.</param>
        public void Initialize(string address, int port)
        {
            iPEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            socket.Bind(iPEndPoint);
        }

        /// <summary>
        ///     Starts the server socket to listen for incoming connections.
        ///     Ensures the listening state is set and prevents redundant invocations
        ///     if the server is already in a listening state.
        /// </summary>
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

        /// <summary>
        ///     If The Server is Running Listen For Incoming Connections.
        /// </summary>
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

        /// <summary>
        ///     If The Server is Running Start Accepting Connection Requests Asynchronously Using SocketAsyncEventArgs.
        /// </summary>
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

        /// <summary>
        ///     Enables the server to begin accepting client connection requests asynchronously.
        ///     This sets the accepting state to active and triggers the handling of incoming connections.
        /// </summary>
        public void StartAcceptingConnections()
        {
            isAccepting = true;
            AcceptConnection();
        }

        /// <summary>
        ///     Stops accepting any new connections
        /// </summary>
        public void StopAcceptingConnections()
        {
            isAccepting = false;
        }

        /// <summary>
        ///     On New Client Connection Accepted.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="newClientConnectionEventArgs"></param>
        private void OnNewConnection(object sender, SocketAsyncEventArgs newClientConnectionEventArgs)
        {
            onNewClientConnection?.Invoke(sender, new Transport.Transport(onNewClientConnectionEventArgs.AcceptSocket));

            onNewClientConnectionEventArgs.AcceptSocket = null;

            if (isAccepting)
            {
                AcceptConnection();
            }
        }

        /// <summary>
        ///     Shuts down the server and closes the socket.
        /// </summary>
        public void StopServer()
        {
            socket.Shutdown(SocketShutdown.Both); // Stops sending and receiving.  
            socket.Close();
        }
        #endregion

        #region IDisposable
        /// <summary>
        ///     Disposed State
        /// </summary>
        private bool _disposedValue;

        /// <summary>
        ///     Releases all resources used by the current instance of the server connector class.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    socket.Dispose();
                    onNewClientConnectionEventArgs.Dispose();
                }
                _disposedValue = true;
            }
        }

        /// <summary>
        ///     Closes the Server Socket and Disposes it.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
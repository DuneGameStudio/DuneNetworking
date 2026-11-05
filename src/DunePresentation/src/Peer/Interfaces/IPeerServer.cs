using System;
using System.Net.Sockets;

namespace DunePresentation.Peer.Interfaces
{
    /// <summary>
    /// Server-side peer factory — listens for and accepts connections, producing <see cref="IPeer"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a <see cref="DuneSession.SocketConnectors.Interface.IServerConnector"/> with packet registry
    /// and encryption. On each accepted connection, fires <see cref="OnPeerConnected"/> with the
    /// configured <see cref="IPeer"/> for that connection.
    /// </para>
    /// </remarks>
    public interface IPeerServer : IDisposable
    {
        /// <summary>Gets whether the server is currently listening for connections.</summary>
        bool IsListening { get; }

        /// <summary>
        /// Raised when a client peer connection is accepted.
        /// </summary>
        event Action<IPeer>? OnPeerConnected;

        /// <summary>Raised when a connection accept fails.</summary>
        event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Begins listening for incoming connections on the specified endpoint.
        /// </summary>
        /// <param name="address">The IP address to bind to.</param>
        /// <param name="port">The port to listen on.</param>
        void StartListening(string address, int port);

        /// <summary>Stops listening for new connections.</summary>
        void StopListening();

        /// <summary>
        /// Accepts the next pending client connection.
        /// </summary>
        /// <remarks>
        /// <para>One-shot model: call again after each accept to continue listening.</para>
        /// </remarks>
        void AcceptConnection();
    }
}
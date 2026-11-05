using System;
using System.Net.Sockets;

namespace DunePresentation.Peer.Interfaces
{
    /// <summary>
    /// Client-side peer factory — connects to a remote server and produces <see cref="IPeer"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a <see cref="DuneSession.SocketConnectors.Interface.IClientConnector"/> with packet registry
    /// and encryption. On successful connection, fires <see cref="OnPeerConnected"/> with the
    /// configured <see cref="IPeer"/> for the connection.
    /// </para>
    /// </remarks>
    public interface IPeerClient : IDisposable
    {
        /// <summary>Gets whether a peer connection is currently active.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Raised when a peer connection is established.
        /// </summary>
        event Action<IPeer>? OnPeerConnected;

        /// <summary>Raised when a connection attempt fails.</summary>
        event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Initiates an asynchronous connection to the specified endpoint.
        /// </summary>
        /// <param name="address">The target IP address.</param>
        /// <param name="port">The target port.</param>
        /// <returns><c>true</c> if the connection attempt was initiated; otherwise <c>false</c> if already connected or connecting.</returns>
        bool ConnectAsync(string address, int port);
    }
}
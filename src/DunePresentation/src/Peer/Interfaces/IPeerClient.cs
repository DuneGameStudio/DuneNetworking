using System;
using System.Net.Sockets;

namespace DunePresentation.Peer.Interfaces
{
    /// <summary>
    /// Client-side peer factory — connects to a remote server and produces <see cref="IPeer"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a <see cref="DuneSession.SocketConnectors.Interface.IClientConnector"/> with the packet
    /// registry and optional encryption. When a connection is established, a new <see cref="IPeer"/>
    /// is created for it and raised through <see cref="OnPeerConnected"/>.
    /// </para>
    /// </remarks>
    public interface IPeerClient : IDisposable
    {
        /// <summary>Gets whether a peer connection is currently active.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Raised when a peer connection is established.
        /// </summary>
        /// <remarks>
        /// Subscribers run on the socket-completion thread and must not throw: a throw
        /// escapes the completion callback and crashes the process.
        /// </remarks>
        event Action<IPeer>? OnPeerConnected;

        /// <summary>
        /// Raised when a connection attempt fails.
        /// </summary>
        /// <remarks>
        /// Subscribers run on the socket-completion thread and must not throw: a throw
        /// escapes the completion callback and crashes the process.
        /// </remarks>
        event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Initiates an asynchronous connection to the specified endpoint.
        /// </summary>
        /// <param name="address">The target address as an IPv4 literal (hostnames and IPv6 literals are not accepted).</param>
        /// <param name="port">The target port (0-65535).</param>
        /// <param name="error">On <c>false</c>, why the attempt was not started: an invalid address or port, a synchronous socket failure, or a state rejection (already connected, a connect already in progress, or disposed). A synchronous rejection fires no event — the out parameter is its only channel.</param>
        /// <returns><c>true</c> if the attempt was started (its outcome arrives later through <see cref="OnPeerConnected"/> or <see cref="OnConnectFailed"/>); <c>false</c> if the call was declined.</returns>
        bool TryConnect(string address, int port, out SocketError error);
    }
}
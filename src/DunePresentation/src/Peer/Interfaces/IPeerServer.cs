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
        /// <remarks>
        /// Subscribers run on the socket-completion thread and must not throw: a throw
        /// escapes the completion callback and crashes the process.
        /// </remarks>
        event Action<IPeer>? OnPeerConnected;

        /// <summary>
        /// Raised when a connection accept fails.
        /// </summary>
        /// <remarks>
        /// Subscribers run on the socket-completion thread and must not throw: a throw
        /// escapes the completion callback and crashes the process.
        /// </remarks>
        event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Begins listening for incoming connections on the specified endpoint.
        /// </summary>
        /// <param name="address">The IPv4 address to bind to (must parse as an IPv4 literal; hostnames and IPv6 literals are not accepted).</param>
        /// <param name="port">The port to listen on (0-65535).</param>
        /// <param name="error">On <c>false</c>, why the socket is not listening (bad address or port, already listening, another start in progress, disposed, or an OS bind error). No event fires for a declined start.</param>
        /// <returns><c>true</c> if the server is listening; <c>false</c> if the start was declined or the bind failed.</returns>
        bool TryStartListening(string address, int port, out SocketError error);

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
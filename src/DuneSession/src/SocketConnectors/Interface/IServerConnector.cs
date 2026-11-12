using System;
using System.Net.Sockets;

namespace DuneSession.SocketConnectors.Interface
{
    /// <summary>
    /// Asynchronous TCP server connector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listens for and accepts inbound TCP connections. Accepts are one-shot: after
    /// each accepted connection, the application calls <see cref="AcceptConnection"/>
    /// again to accept the next one.
    /// </para>
    /// </remarks>
    public interface IServerConnector : IDisposable
    {
        /// <summary>Gets whether the server is currently listening for connections.</summary>
        bool IsListening { get; }

        /// <summary>Gets whether an accept is armed, or has completed but not yet been delivered.</summary>
        /// <remarks>
        /// <para>
        /// Dispose protocol: before <see cref="IDisposable.Dispose"/>, call
        /// <see cref="StopListening"/> and wait (bounded) for this to become false.
        /// If <see cref="IDisposable.Dispose"/> runs while an accept is pending, an
        /// accepted socket that completed but was not yet delivered may remain open
        /// until finalization.
        /// </para>
        /// </remarks>
        bool IsAcceptPending { get; }

        /// <summary>
        /// Raised when a client connection is successfully accepted.
        /// </summary>
        /// <remarks>
        /// The <see cref="IConnection"/> provides access to the underlying <see cref="DuneTransport.Transport.Interface.ITransport"/>
        /// for communication with the connected client.
        /// </remarks>
        event Action<IConnection>? OnClientConnected;

        /// <summary>
        /// Raised when a connection accept attempt fails.
        /// </summary>
        event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Begins listening for incoming connections on the specified endpoint.
        /// </summary>
        /// <param name="address">The IPv4 address to bind to. Must parse as an IPv4 literal — hostnames and IPv6 literals are not accepted.</param>
        /// <param name="port">The port to listen on, 0 through 65535.</param>
        /// <param name="error">On <c>false</c>, why the socket is not listening: <c>SocketError.InvalidArgument</c> (bad address or port), the OS error of the bind or listen, or the catch-all <c>SocketError</c> value for a state rejection (already listening, another start in progress, or disposed — the netstandard2.1 <c>SocketError</c> set, which mirrors the Windows Sockets API error codes, has no "already" member, so state rejections share the catch-all; <see cref="IsListening"/> is the authoritative state check). On <c>true</c>, <c>SocketError.Success</c>.</param>
        /// <returns><c>true</c> if the socket is listening (then call <see cref="AcceptConnection"/>); <c>false</c> if the start was declined or the bind failed — the reason is in <paramref name="error"/>. No event fires for a declined start; the out parameter is its only failure channel.</returns>
        /// <remarks>
        /// <para>
        /// Calling <see cref="TryStartListening"/> while already listening returns <c>false</c>
        /// with the catch-all <c>SocketError</c> value.
        /// </para>
        /// </remarks>
        bool TryStartListening(string address, int port, out SocketError error);

        /// <summary>
        /// Accepts the next pending client connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One-shot model: after each accepted (or failed) connection, call again to accept the next.
        /// Does nothing if the server is not listening.
        /// </para>
        /// <para>
        /// On success, fires <see cref="OnClientConnected"/>. On failure, fires <see cref="OnAcceptFailed"/>.
        /// </para>
        /// </remarks>
        void AcceptConnection();

        /// <summary>
        /// Stops listening for new connections.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Closes the listening socket. Existing accepted connections are not affected.
        /// </para>
        /// </remarks>
        void StopListening();
    }
}

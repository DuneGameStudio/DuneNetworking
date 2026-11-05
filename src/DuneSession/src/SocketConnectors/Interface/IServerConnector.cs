using System;
using System.Net.Sockets;

namespace DuneSession.SocketConnectors.Interface
{
    /// <summary>
    /// Asynchronous TCP server connector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listens for and accepts inbound TCP connections. Uses a one-shot accept model:
    /// after each accepted connection, the application must call <see cref="AcceptConnection"/>
    /// again to accept the next connection.
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
        /// <param name="address">The IP address to bind to.</param>
        /// <param name="port">The port to listen on.</param>
        /// <remarks>
        /// <para>
        /// After calling this, call <see cref="AcceptConnection"/> to begin accepting connections.
        /// Calling <see cref="StartListening"/> while already listening has no effect.
        /// </para>
        /// </remarks>
        void StartListening(string address, int port);

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

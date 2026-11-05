using System;
using System.Net.Sockets;

namespace DuneSession.SocketConnectors.Interface
{
    /// <summary>
    /// Asynchronous TCP client connector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initiates outbound TCP connections. Only one connection attempt may be in flight
    /// at a time — calling <see cref="ConnectAsync"/> while a connection is pending returns <c>false</c>.
    /// </para>
    /// <para>
    /// On success, fires <see cref="OnConnected"/> with the resulting <see cref="IConnection"/>.
    /// On failure, fires <see cref="OnConnectFailed"/> with the socket error code.
    /// </para>
    /// </remarks>
    public interface IClientConnector : IDisposable
    {
        /// <summary>Gets whether an active connection currently exists.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Raised when a connection is successfully established.
        /// </summary>
        /// <remarks>
        /// The <see cref="IConnection"/> provides access to the underlying <see cref="DuneTransport.Transport.Interface.ITransport"/>
        /// for sending and receiving data.
        /// </remarks>
        event Action<IConnection>? OnConnected;

        /// <summary>
        /// Raised when a connection attempt fails.
        /// </summary>
        event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Initiates an asynchronous connection to the specified endpoint.
        /// </summary>
        /// <param name="address">The target IP address.</param>
        /// <param name="port">The target port.</param>
        /// <returns><c>true</c> if the connection attempt was initiated; otherwise <c>false</c> if already connected or a connection is in progress.</returns>
        /// <remarks>
        /// <para>
        /// Only one connection attempt may be in flight at a time. Calling while a
        /// previous attempt is pending returns <c>false</c> immediately.
        /// </para>
        /// </remarks>
        bool ConnectAsync(string address, int port);
    }
}

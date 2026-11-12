using System;
using System.Net.Sockets;

namespace DuneSession.SocketConnectors.Interface
{
    /// <summary>
    /// Asynchronous TCP client connector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initiates outbound TCP connections. Only one connection attempt may be in
    /// flight at a time — calling <see cref="TryConnect"/> while a connection is
    /// pending returns <c>false</c> (with the catch-all <c>SocketError</c> value).
    /// </para>
    /// <para>
    /// On success, fires <see cref="OnConnected"/> with the resulting
    /// <see cref="IConnection"/>. On a failed attempt, fires
    /// <see cref="OnConnectFailed"/> with the socket error code. The events carry
    /// asynchronous network outcomes only: a synchronous rejection (bad address or
    /// port, already connected, an attempt in progress, or disposed) is reported
    /// through <see cref="TryConnect"/>'s out parameter instead, with no event.
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
        /// <param name="address">The target IPv4 address. Must parse as an IPv4 literal — hostnames and IPv6 literals are not accepted.</param>
        /// <param name="port">The target port, 0 through 65535.</param>
        /// <param name="error">On <c>false</c>, why the attempt was not started: <c>SocketError.InvalidArgument</c> (bad address or port), the OS error of a synchronous socket failure, or the catch-all <c>SocketError</c> value for a state rejection (already connected, an attempt in progress, or disposed — the netstandard2.1 <c>SocketError</c> set, which mirrors the Windows Sockets API error codes, has no "already" member, so state rejections share the catch-all; <see cref="IsConnected"/> is the authoritative state check). On <c>true</c>, <c>SocketError.Success</c>.</param>
        /// <returns><c>true</c> if the connection attempt started (its outcome arrives through <see cref="OnConnected"/>/<see cref="OnConnectFailed"/>); <c>false</c> if the call was rejected — the rejection is reported through <paramref name="error"/> with no event.</returns>
        /// <remarks>
        /// <para>
        /// Only one connection attempt may be in flight at a time. Calling while a
        /// previous attempt is pending returns <c>false</c> immediately.
        /// </para>
        /// </remarks>
        bool TryConnect(string address, int port, out SocketError error);
    }
}

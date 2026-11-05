using System;
using DuneSession.SocketConnectors;
using DuneTransport.Transport.Interface;

namespace DuneSession.SocketConnectors.Interface
{
    /// <summary>
    /// Wrapper around an established socket connection with embedded <see cref="ITransport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides the connection lifecycle (disconnect, dispose) and exposes
    /// the transport for packet I/O. The connection reports its state and closes
    /// the socket when asked — it does not interpret transport failures; the
    /// application layer decides what a failure means.
    /// </para>
    /// </remarks>
    public interface IConnection : IDisposable
    {
        /// <summary>Gets whether the connection is currently active.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the transport layer for sending and receiving packet data.
        /// </summary>
        ITransport Transport { get; }

        /// <summary>
        /// Initiates an asynchronous graceful disconnect.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sends TCP FIN and waits for peer acknowledgment, closing the socket when
        /// the disconnect completes. Does nothing if already disconnected or disposed.
        /// </para>
        /// </remarks>
        void DisconnectAsync();

        /// <summary>
        /// Raised exactly once when the connection is definitively over: the graceful
        /// disconnect completed, or the connection was disposed (force completion, so
        /// an awaiter can never hang). A report, not a wait — the connection owns no
        /// threads, timers, or tasks; consumers add their own timeout in their layer.
        /// </summary>
        event Action? Disconnected;
    }
}

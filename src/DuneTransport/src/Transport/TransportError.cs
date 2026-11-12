namespace DuneTransport.Transport
{
    /// <summary>
    /// Reason code surfaced with the transport's failure events.
    /// </summary>
    public enum TransportError
    {
        /// <summary>Underlying socket I/O failed — an operating-system-level socket error (SocketException) observed synchronously or on completion, including an operation on a disposed socket.</summary>
        SocketError,

        /// <summary>A segment pool had no free segment when one was needed.</summary>
        PoolExhausted,

        /// <summary>The peer sent a malformed frame (the header declares a payload length of 0, or one greater than the receive segment size).</summary>
        ProtocolError,

        /// <summary>The caller passed a segment the send buffer cannot resolve (bad segment index or oversized length).</summary>
        InvalidSegment,

        /// <summary>The transport was disposed; the operation was rejected, or a completion settled against the dispose mark.</summary>
        ObjectDisposed,

        /// <summary>The socket is disconnected — a hard error or a graceful close (FIN) settled the connection.</summary>
        SocketDisconnected,

        /// <summary>ReceiveAsync was called while a previous receive is still in flight.</summary>
        ReceiveAlreadyPending,

        /// <summary>SendAsync was called while a previous send is still in flight.</summary>
        SendAlreadyPending
    }
}

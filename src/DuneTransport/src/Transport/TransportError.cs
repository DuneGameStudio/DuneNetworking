namespace DuneTransport.Transport
{
    /// <summary>
    /// Reason code surfaced with Transport failure events.
    /// </summary>
    public enum TransportError
    {
        /// <summary>Underlying socket I/O failed (OS-level SocketException, ObjectDisposedException on the socket, etc.).</summary>
        SocketError,

        /// <summary>Segment pool had no free segment when one was needed.</summary>
        PoolExhausted,

        /// <summary>Peer sent a malformed frame (header says payload length is 0 or greater than the receive segment size).</summary>
        ProtocolError,

        /// <summary>Caller passed a Segment the send buffer cannot resolve (bad SegmentIndex or oversized length).</summary>
        InvalidSegment,

        /// <summary>User subscriber of OnPacketReceived threw an exception. The segment has been released on the caller's behalf.</summary>
        HandlerFailed,

        /// <summary>The Transport was disposed while the operation was pending. Reserved for boundary cases.</summary>
        ObjectDisposed,

        /// <summary>The socket was disconnected.</summary>
        SocketDisconnected,

        /// <summary>ReceiveAsync was called while a previous receive is still in flight.</summary>
        ReceiveAlreadyPending,

        /// <summary>SendAsync was called while a previous send is still in flight.</summary>
        SendAlreadyPending
    }
}

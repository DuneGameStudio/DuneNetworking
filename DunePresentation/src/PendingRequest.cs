using System;

namespace DunePresentation
{
    internal delegate bool ResponseHandler(ReadOnlySpan<byte> userData, int length);

    internal struct PendingRequest
    {
        public ResponseHandler HandleResponse;
        public Action? OnFailed;
        public long DeadlineTick; // Stopwatch.GetTimestamp() ticks; 0 = no timeout
    }
}

using System;
using DunePresentation.Interface;

namespace DunePresentation
{
    internal struct PendingRequest
    {
        public Func<IResponse> ResponseFactory;
        public Action<IResponse> OnResponse;
        public Action? OnFailed;
        public long DeadlineTick; // Environment.TickCount64 ms; 0 = no timeout
    }
}

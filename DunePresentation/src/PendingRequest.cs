using System;
using System.Threading;
using System.Threading.Tasks;
using DunePresentation.Interface;

namespace DunePresentation
{
    internal struct PendingRequest
    {
        public TaskCompletionSource<IResponse> Tcs;

        public Func<IResponse> ResponseFactory;

        public CancellationTokenRegistration CancellationRegistration;
    }
}

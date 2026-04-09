using System;
using System.Threading;
using System.Threading.Tasks;

namespace DunePresentation.Interface
{
    public interface IPeer : IDisposable
    {
        bool IsConnected { get; }

        event Action? OnDisconnected;

        Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
            where TResponse : IResponse, new();

        void Disconnect();
    }
}

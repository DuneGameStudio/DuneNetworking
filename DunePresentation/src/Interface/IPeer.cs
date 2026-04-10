using System;

namespace DunePresentation.Interface
{
    public interface IPeer : IDisposable
    {
        bool IsConnected { get; }

        event Action? OnDisconnected;

        void SendRequest<TRequest, TResponse>(
            TRequest request,
            Action<TResponse> onResponse,
            Action? onFailed = null)
            where TRequest : IRequest<TResponse>
            where TResponse : IResponse, new();

        void Disconnect();
    }
}

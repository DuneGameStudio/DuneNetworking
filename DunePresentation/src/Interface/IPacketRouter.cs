using System;

namespace DunePresentation.Interface
{
    public interface IPacketRouter
    {
        void RegisterRequestHandler<TRequest, TResponse>(
            Action<TRequest, Action<TResponse>> handler)
            where TRequest : IRequest<TResponse>, new()
            where TResponse : IResponse, new();
    }
}

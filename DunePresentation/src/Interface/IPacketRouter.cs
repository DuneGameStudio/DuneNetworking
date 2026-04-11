using System;

namespace DunePresentation.Interface
{
    public interface IPacketRouter
    {
        void RegisterRequestHandler<TRequest, TResponse>(
            Action<TRequest, Action<TResponse>> handler)
            where TRequest : IRequest, new()
            where TResponse : IResponse;
    }
}

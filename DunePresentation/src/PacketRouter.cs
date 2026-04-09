using System;
using System.Collections.Generic;
using DunePresentation.Interface;
using DuneTransport.Transport.Interface;

namespace DunePresentation
{
    internal interface IRequestHandlerInvoker
    {
        void Invoke(ReadOnlySpan<byte> payload, int length,
                    ushort correlationId, ITransport transport,
                    IPacketEncryptor? encryptor);
    }

    internal class RequestHandlerInvoker<TRequest, TResponse> : IRequestHandlerInvoker
        where TRequest : IRequest<TResponse>, new()
        where TResponse : IResponse, new()
    {
        private readonly Action<TRequest, Action<TResponse>> _handler;

        public RequestHandlerInvoker(Action<TRequest, Action<TResponse>> handler)
        {
            _handler = handler;
        }

        public void Invoke(ReadOnlySpan<byte> payload, int length,
                           ushort correlationId, ITransport transport,
                           IPacketEncryptor? encryptor)
        {
            var request = new TRequest();
            if (!request.Deserialize(payload, length))
                return;

            Action<TResponse> reply = response =>
            {
                if (!transport.TryReserveSendPacket(out var segment))
                    return;

                var span = segment.Memory.Span;

                response.Serialize(span.Slice(PresentationHeader.Size), out int bytesWritten);

                bool encrypted = false;
                if (encryptor != null)
                {
                    var userData = span.Slice(PresentationHeader.Size, bytesWritten);
                    encryptor.Encrypt(userData, userData);
                    encrypted = true;
                }

                PresentationHeader.Write(span, PacketType.Response, encrypted,
                                         response.PacketId, correlationId);

                transport.SendAsync(segment, PresentationHeader.Size + bytesWritten);
            };

            _handler(request, reply);
        }
    }

    public class PacketRouter : IPacketRouter
    {
        private readonly Dictionary<ushort, IRequestHandlerInvoker> _handlers =
            new Dictionary<ushort, IRequestHandlerInvoker>();

        public void RegisterRequestHandler<TRequest, TResponse>(
            Action<TRequest, Action<TResponse>> handler)
            where TRequest : IRequest<TResponse>, new()
            where TResponse : IResponse, new()
        {
            var packetId = new TRequest().PacketId;
            _handlers[packetId] = new RequestHandlerInvoker<TRequest, TResponse>(handler);
        }

        internal bool TryGetHandler(ushort packetId, out IRequestHandlerInvoker invoker)
        {
            return _handlers.TryGetValue(packetId, out invoker!);
        }
    }
}

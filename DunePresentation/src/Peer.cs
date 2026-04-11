using System;
using System.Diagnostics;
using System.Net.Sockets;
using DunePresentation.Interface;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;
using DuneTransport.Transport.Interface;

namespace DunePresentation
{
    public class Peer : IPeer
    {
        private readonly IConnection _connection;
        private readonly PacketRouter _router;
        private readonly IPacketEncryptor? _encryptor;
        private readonly long _timeoutTicks;

        public bool IsConnected => _connection.IsConnected;

        public event Action? OnDisconnected;

        public Peer(IConnection connection, IPacketRouter router, PeerConfiguration config)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = (PacketRouter)(router ?? throw new ArgumentNullException(nameof(router)));
            _encryptor = config.Encryptor;

            if (config.RequestTimeout > TimeSpan.Zero)
                _timeoutTicks = (long)(config.RequestTimeout.TotalSeconds * Stopwatch.Frequency);

            _router.OnRequestTimedOut += HandleRequestTimedOut;

            _connection.Transport.OnPacketReceived += HandlePacketReceived;
            _connection.Transport.OnPacketReceiveFailed += HandleReceiveFailed;
            _connection.OnDisconnected += HandleDisconnected;

            _connection.Transport.ReceiveAsync();
        }

        public void SendRequest<TRequest, TResponse>(
            TRequest request,
            Action<TResponse> onResponse,
            Action? onFailed = null)
            where TRequest : IRequest
            where TResponse : IResponse, new()
        {
            var pending = new PendingRequest
            {
                HandleResponse = (userData, length) =>
                {
                    var response = new TResponse();
                    if (!response.ReadFieldsFromBuffer(userData, length))
                        return false;
                    onResponse(response);
                    return true;
                },
                OnFailed = onFailed
            };

            if (_timeoutTicks > 0)
                pending.DeadlineTick = Stopwatch.GetTimestamp() + _timeoutTicks;

            ushort correlationId = _router.Track(pending);

            if (!SendPacket(request, PacketType.Request, correlationId, _connection.Transport))
            {
                if (_router.TryComplete(correlationId, out _))
                    onFailed?.Invoke();
            }
        }

        public void Disconnect()
        {
            _connection.DisconnectAsync();
        }

        private bool SendPacket<TPacket>(TPacket packet, PacketType type,
                                         ushort correlationId, ITransport transport)
            where TPacket : IPacket
        {
            ushort packetId = packet.PacketId;
            var encryptor = _encryptor;

            if (!packet.Serialize(transport, (seg, size) =>
            {
                var span = seg.Memory.Span;
                PresentationHeader.Write(span, type, packetId, correlationId);

                if (encryptor != null)
                    encryptor.Encrypt(span.Slice(0, size), span);
            }))
                return false;

            packet.OnSend(transport);
            return true;
        }

        private void HandlePacketReceived(ITransport transport, SocketAsyncEventArgs args,
                                          Segment segment)
        {
            try
            {
                var span = segment.Memory.Span;

                if (span.Length < PresentationHeader.Size)
                    return;

                if (_encryptor != null)
                    _encryptor.Decrypt(span, span);

                PresentationHeader.Read(span, out PacketType type,
                                        out ushort packetId, out ushort correlationId);

                int userDataLength = span.Length - PresentationHeader.Size;
                var userData = span.Slice(PresentationHeader.Size, userDataLength);

                switch (type)
                {
                    case PacketType.Response:
                        HandleResponse(userData, userDataLength, correlationId);
                        break;

                    case PacketType.Request:
                        HandleRequest(userData, userDataLength, packetId, correlationId,
                                      transport);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Peer.HandlePacketReceived | Exception:\n{ex}", "error");
            }
            finally
            {
                segment.Release();
                transport.ReceiveAsync();
            }
        }

        private void HandleResponse(ReadOnlySpan<byte> userData, int length, ushort correlationId)
        {
            if (!_router.TryComplete(correlationId, out PendingRequest pending))
                return;

            if (!pending.HandleResponse(userData, length))
                pending.OnFailed?.Invoke();
        }

        private void HandleRequest(ReadOnlySpan<byte> userData, int length,
                                   ushort packetId, ushort correlationId,
                                   ITransport transport)
        {
            if (!_router.TryGetHandler(packetId, out HandlerEntry entry))
                return;

            IRequest request = entry.Factory();
            if (!request.ReadFieldsFromBuffer(userData, length))
                return;

            try
            {
                entry.Invoke(request, response =>
                    SendPacket(response, PacketType.Response, correlationId, transport));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Peer.HandleRequest | Handler threw for PacketId {packetId}:\n{ex}",
                    "error");
            }
        }

        private void HandleRequestTimedOut(PendingRequest pending)
        {
            pending.OnFailed?.Invoke();
        }

        private void FailAllPending()
        {
            var drained = _router.DrainPending();
            foreach (var pending in drained)
                pending.OnFailed?.Invoke();
        }

        private void HandleReceiveFailed(ITransport transport)
        {
            Debug.WriteLine("Peer.HandleReceiveFailed | Receive failed, disconnecting.", "error");
            _connection.DisconnectAsync();
        }

        private void HandleDisconnected()
        {
            FailAllPending();
            OnDisconnected?.Invoke();
        }

        #region IDisposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _connection.Transport.OnPacketReceived -= HandlePacketReceived;
                    _connection.Transport.OnPacketReceiveFailed -= HandleReceiveFailed;
                    _connection.OnDisconnected -= HandleDisconnected;

                    _router.Dispose();
                    _router.OnRequestTimedOut -= HandleRequestTimedOut;

                    FailAllPending();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}

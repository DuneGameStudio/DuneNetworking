using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
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
        private readonly long _timeoutTicks; // Stopwatch ticks; 0 = no timeout

        private readonly ConcurrentDictionary<ushort, PendingRequest> _pendingRequests = new ConcurrentDictionary<ushort, PendingRequest>();

        private int _nextCorrelationId;
        private Timer? _timeoutTimer;

        public bool IsConnected => _connection.IsConnected;

        public event Action? OnDisconnected;

        public Peer(IConnection connection, PacketRouter router, PeerConfiguration config)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _encryptor = config.Encryptor;
            _timeoutTicks = (long)(config.RequestTimeout.TotalSeconds * Stopwatch.Frequency);

            _connection.Transport.OnPacketReceived += HandlePacketReceived;
            _connection.Transport.OnPacketReceiveFailed += HandleReceiveFailed;
            _connection.OnDisconnected += HandleDisconnected;

            if (_timeoutTicks > 0)
                _timeoutTimer = new Timer(SweepTimedOutRequests, null,
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _connection.Transport.ReceiveAsync();
        }

        public void SendRequest<TRequest, TResponse>(
            TRequest request,
            Action<TResponse> onResponse,
            Action? onFailed = null)
            where TRequest : IRequest<TResponse>
            where TResponse : IResponse, new()
        {
            ushort correlationId = AllocateCorrelationId();

            var pending = new PendingRequest
            {
                ResponseFactory = () => new TResponse(),
                OnResponse = response => onResponse((TResponse)response),
                OnFailed = onFailed,
                DeadlineTick = _timeoutTicks > 0 ? Stopwatch.GetTimestamp() + _timeoutTicks : 0
            };

            _pendingRequests[correlationId] = pending;

            if (!SendPacket(PacketType.Request, request.PacketId, correlationId, request))
            {
                if (_pendingRequests.TryRemove(correlationId, out _))
                    onFailed?.Invoke();
            }
        }

        public void Disconnect()
        {
            _connection.DisconnectAsync();
        }

        private void HandlePacketReceived(ITransport transport, SocketAsyncEventArgs args,
                                          Segment segment)
        {
            try
            {
                var span = segment.Memory.Span;

                if (span.Length < PresentationHeader.Size)
                    return;

                PresentationHeader.Read(span, out PacketType type, out bool encrypted,
                                        out ushort packetId, out ushort correlationId);

                int userDataLength = span.Length - PresentationHeader.Size;
                var userData = span.Slice(PresentationHeader.Size, userDataLength);

                if (encrypted && _encryptor != null)
                    _encryptor.Decrypt(userData, userData);

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
                Debug.WriteLine($"Peer.HandlePacketReceived | Exception: {ex.Message}", "error");
            }
            finally
            {
                segment.Release();
                transport.ReceiveAsync();
            }
        }

        private void HandleResponse(ReadOnlySpan<byte> userData, int length,
                                    ushort correlationId)
        {
            if (!_pendingRequests.TryRemove(correlationId, out var pending))
                return;

            var response = pending.ResponseFactory();
            if (response.Deserialize(userData, length))
                pending.OnResponse(response);
            else
                pending.OnFailed?.Invoke();
        }

        private void HandleRequest(ReadOnlySpan<byte> userData, int length,
                                   ushort packetId, ushort correlationId,
                                   ITransport transport)
        {
            if (!_router.TryGetHandler(packetId, out var invoker))
                return;

            try
            {
                invoker.Invoke(userData, length, correlationId, transport, _encryptor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Peer.HandleRequest | Handler threw for PacketId {packetId}: {ex.Message}",
                    "error");
            }
        }

        private void HandleReceiveFailed(ITransport transport)
        {
            Debug.WriteLine("Peer.HandleReceiveFailed | Receive failed.", "error");
        }

        private void HandleDisconnected()
        {
            foreach (var kvp in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                    pending.OnFailed?.Invoke();
            }

            OnDisconnected?.Invoke();
        }

        private void SweepTimedOutRequests(object? state)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var kvp in _pendingRequests)
            {
                if (kvp.Value.DeadlineTick > 0 && now >= kvp.Value.DeadlineTick)
                {
                    if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                        pending.OnFailed?.Invoke();
                }
            }
        }

        private bool SendPacket(PacketType type, ushort packetId, ushort correlationId,
                                IPacket packet)
        {
            if (!_connection.Transport.TryReserveSendPacket(out var segment))
                return false;

            var span = segment.Memory.Span;

            packet.Serialize(span.Slice(PresentationHeader.Size), out int bytesWritten);

            bool encrypted = false;
            if (_encryptor != null)
            {
                var userData = span.Slice(PresentationHeader.Size, bytesWritten);
                _encryptor.Encrypt(userData, userData);
                encrypted = true;
            }

            PresentationHeader.Write(span, type, encrypted, packetId, correlationId);

            _connection.Transport.SendAsync(segment, PresentationHeader.Size + bytesWritten);
            return true;
        }

        private ushort AllocateCorrelationId()
        {
            ushort id;
            do
            {
                id = (ushort)Interlocked.Increment(ref _nextCorrelationId);
            }
            while (_pendingRequests.ContainsKey(id));
            return id;
        }

        #region IDisposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _timeoutTimer?.Dispose();
                    _timeoutTimer = null;

                    _connection.Transport.OnPacketReceived -= HandlePacketReceived;
                    _connection.Transport.OnPacketReceiveFailed -= HandleReceiveFailed;
                    _connection.OnDisconnected -= HandleDisconnected;

                    foreach (var kvp in _pendingRequests)
                    {
                        if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                            pending.OnFailed?.Invoke();
                    }
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

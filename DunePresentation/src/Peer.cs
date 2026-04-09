using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly TimeSpan _requestTimeout;

        private readonly ConcurrentDictionary<ushort, PendingRequest> _pendingRequests =
            new ConcurrentDictionary<ushort, PendingRequest>();

        private int _nextCorrelationId;

        public bool IsConnected => _connection.IsConnected;

        public event Action? OnDisconnected;

        public Peer(IConnection connection, PacketRouter router, PeerConfiguration config)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _encryptor = config.Encryptor;
            _requestTimeout = config.RequestTimeout;

            _connection.Transport.OnPacketReceived += HandlePacketReceived;
            _connection.Transport.OnPacketReceiveFailed += HandleReceiveFailed;
            _connection.OnDisconnected += HandleDisconnected;

            _connection.Transport.ReceiveAsync();
        }

        public Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
            where TResponse : IResponse, new()
        {
            ushort correlationId = AllocateCorrelationId();

            var tcs = new TaskCompletionSource<IResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var pending = new PendingRequest
            {
                Tcs = tcs,
                ResponseFactory = () => new TResponse()
            };

            _pendingRequests[correlationId] = pending;

            CancellationTokenSource? timeoutCts = null;
            CancellationToken effectiveToken = cancellationToken;

            if (_requestTimeout > TimeSpan.Zero)
            {
                timeoutCts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                timeoutCts.CancelAfter(_requestTimeout);
                effectiveToken = timeoutCts.Token;
            }

            if (effectiveToken.CanBeCanceled)
            {
                pending.CancellationRegistration = effectiveToken.Register(() =>
                {
                    if (_pendingRequests.TryRemove(correlationId, out var removed))
                    {
                        removed.Tcs.TrySetCanceled(cancellationToken.IsCancellationRequested
                            ? cancellationToken
                            : CancellationToken.None);
                    }
                    timeoutCts?.Dispose();
                });
                _pendingRequests[correlationId] = pending;
            }

            if (!SendPacket(PacketType.Request, request.PacketId, correlationId, request))
            {
                if (_pendingRequests.TryRemove(correlationId, out var removed))
                {
                    removed.CancellationRegistration.Dispose();
                    removed.Tcs.TrySetException(
                        new InvalidOperationException("Send buffer exhausted."));
                }
                timeoutCts?.Dispose();
            }

            return CastTask<TResponse>(tcs.Task);
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

            pending.CancellationRegistration.Dispose();

            var response = pending.ResponseFactory();
            if (response.Deserialize(userData, length))
                pending.Tcs.TrySetResult(response);
            else
                pending.Tcs.TrySetException(
                    new InvalidOperationException("Failed to deserialize response."));
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
                {
                    pending.CancellationRegistration.Dispose();
                    pending.Tcs.TrySetCanceled();
                }
            }

            OnDisconnected?.Invoke();
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

        private static async Task<TResponse> CastTask<TResponse>(Task<IResponse> task)
            where TResponse : IResponse
        {
            return (TResponse)await task.ConfigureAwait(false);
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

                    foreach (var kvp in _pendingRequests)
                    {
                        if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                        {
                            pending.CancellationRegistration.Dispose();
                            pending.Tcs.TrySetCanceled();
                        }
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

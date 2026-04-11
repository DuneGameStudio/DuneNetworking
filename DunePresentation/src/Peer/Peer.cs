using System;
using System.Diagnostics;
using System.Net.Sockets;
using DunePresentation.Encryption.Interface;
using DunePresentation.Packet;
using DunePresentation.Packet.Interfaces;
using DunePresentation.Peer.Interfaces;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;
using DuneTransport.Transport.Interface;

namespace DunePresentation.Peer
{
    public class Peer : IPeer
    {
        private readonly IConnection _connection;
        private readonly PacketRegistry _registry;
        private readonly IPacketEncryptor? _encryptor;

        public bool IsConnected => _connection.IsConnected;

        public event Action? OnDisconnected;

        public Peer(IConnection connection, PacketRegistry registry, IPacketEncryptor? encryptor = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _encryptor = encryptor;

            _connection.Transport.OnPacketReceived += OnPacketReceivedHandler;
            _connection.Transport.OnPacketReceiveFailed += OnPacketReceiveFailedHandler;
            _connection.OnDisconnected += OnDisconnectedHandler;

            _connection.Transport.ReceiveAsync();
        }

        public bool Send<T>(T packet) where T : IPacket
        {
            ushort packetId = packet.PacketId;
            IPacketEncryptor? encryptor = _encryptor;

            if (!packet.Serialize(_connection.Transport, (seg, size) =>
            {
                var span = seg.Memory.Span;
                PresentationHeader.Write(span, packetId);

                if (encryptor != null)
                    encryptor.Encrypt(span.Slice(0, size), span);
            }))
                return false;

            packet.OnSend(_connection.Transport);
            return true;
        }

        public void Disconnect()
        {
            _connection.DisconnectAsync();
        }

        private void OnPacketReceivedHandler(ITransport transport, SocketAsyncEventArgs args,
                                             Segment segment)
        {
            bool segmentOwned = true;
            try
            {
                var span = segment.Memory.Span;

                if (span.Length < PresentationHeader.Size)
                    return;

                if (_encryptor != null)
                    _encryptor.Decrypt(span, span);

                PresentationHeader.Read(span, out ushort packetId);

                if (!_registry.TryGetEntry(packetId, out Entry entry))
                    return;

                IPacket packet = entry.Factory();
                packet.segment = segment;
                packet.PacketSize = span.Length;

                segmentOwned = false;
                if (!packet.Deserialize())
                    return;

                entry.Invoke(packet);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Peer.OnPacketReceivedHandler | Exception:\n{ex}", "error");
            }
            finally
            {
                if (segmentOwned)
                    segment.Release();
                transport.ReceiveAsync();
            }
        }

        private void OnPacketReceiveFailedHandler(ITransport transport)
        {
            Debug.WriteLine("Peer.OnPacketReceiveFailedHandler | Receive failed, disconnecting.", "error");
            _connection.DisconnectAsync();
        }

        private void OnDisconnectedHandler()
        {
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
                    _connection.Transport.OnPacketReceived -= OnPacketReceivedHandler;
                    _connection.Transport.OnPacketReceiveFailed -= OnPacketReceiveFailedHandler;
                    _connection.OnDisconnected -= OnDisconnectedHandler;
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

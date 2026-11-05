using System;
using System.Net.Sockets;
using System.Threading;
using DunePresentation.Encryption.Interface;
using DunePresentation.Packet;
using DunePresentation.Peer.Interfaces;
using DuneSession.SocketConnectors;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.Transport;


namespace DunePresentation.Peer
{
    /// <summary>
    /// Client-side peer factory — connects to a remote server and produces <see cref="IPeer"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a <see cref="IClientConnector"/> with packet registry and optional encryption.
    /// On successful connection, creates a <see cref="Peer"/> and fires <see cref="OnPeerConnected"/>.
    /// </para>
    /// <para>
    /// Uses an encryptor factory pattern: the factory is invoked per-connection, allowing
    /// each peer to have independent encryption state.
    /// </para>
    /// </remarks>
    public sealed class PeerClient : IPeerClient
    {
        private readonly PacketRegistry _packetRegistry;
        /// <summary>Factory to create per-connection encryptors (nullable).</summary>
        private readonly Func<IPacketEncryptor>? _encryptorFactory;
        private readonly IClientConnector _clientConnector;

        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public bool IsConnected => _clientConnector.IsConnected;

        public event Action<IPeer>? OnPeerConnected;
        public event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Creates a new peer client.
        /// </summary>
        /// <param name="packetRegistry">Registry for incoming packet handler lookup.</param>
        /// <param name="encryptorFactory">Optional factory invoked per-connection to create encryptors.</param>
        /// <param name="client">Optional connector (creates a <see cref="ClientConnector"/> if null). A supplied connector governs its own pool configuration.</param>
        /// <param name="options">Optional segment pool sizing applied to the internally-created connector (ignored when <paramref name="client"/> is supplied).</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="packetRegistry"/> is null.</exception>
        public PeerClient(PacketRegistry packetRegistry, Func<IPacketEncryptor>? encryptorFactory = null, IClientConnector? client = null, TransportOptions? options = null)
        {
            _packetRegistry = packetRegistry ?? throw new ArgumentNullException(nameof(packetRegistry));
            _encryptorFactory = encryptorFactory;

            _clientConnector = client ?? new ClientConnector(options ?? new TransportOptions());
            _clientConnector.OnConnected += HandleConnected;
            _clientConnector.OnConnectFailed += HandleConnectFailed;
        }

        /// <inheritdoc />
        public bool ConnectAsync(string address, int port)
            => _clientConnector.ConnectAsync(address, port);

        /// <summary>
        /// Creates a <see cref="Peer"/> for a successful connection and fires <see cref="OnPeerConnected"/>.
        /// A throwing factory or subscriber disposes the peer and connection instead of propagating.
        /// </summary>
        private void HandleConnected(IConnection connection)
        {
            IPeer? peer = null;
            try
            {
                IPacketEncryptor? encryptor = _encryptorFactory?.Invoke();
                peer = new Peer(connection, _packetRegistry, encryptor);
                OnPeerConnected?.Invoke(peer);
            }
            catch
            {
                // A throwing factory or subscriber means the peer was not taken:
                // dispose what was created so nothing live is left behind. The
                // connector below guards its own fire and disposes the connection
                // too; both disposes are idempotent, so the double is a no-op.
                peer?.Dispose();
                connection.Dispose();
            }
        }

        /// <summary>Relays connection failure to subscribers.</summary>
        private void HandleConnectFailed(SocketError error)
        {
            try
            {
                OnConnectFailed?.Invoke(error);
            }
            catch
            {
                // Subscriber throw is absorbed: no connection exists to dispose.
            }
        }

        /// <summary>
        /// Unsubscribes from connector events and releases the underlying client. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _clientConnector.OnConnected -= HandleConnected;
            _clientConnector.OnConnectFailed -= HandleConnectFailed;
            _clientConnector.Dispose();
        }
    }
}

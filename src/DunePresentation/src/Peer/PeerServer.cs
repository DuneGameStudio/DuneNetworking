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
    /// Server-side peer factory — listens for and accepts connections, producing <see cref="IPeer"/> instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a <see cref="IServerConnector"/> with packet registry and optional encryption.
    /// On each accepted connection, creates a <see cref="Peer"/> and fires <see cref="OnPeerConnected"/>.
    /// </para>
    /// <para>
    /// Uses an encryptor factory pattern: the factory is invoked per-connection, allowing
    /// each peer to have independent encryption state.
    /// </para>
    /// </remarks>
    public sealed class PeerServer : IPeerServer
    {
        private readonly PacketRegistry _packetRegistry;
        /// <summary>Factory to create per-connection encryptors (nullable).</summary>
        private readonly Func<IPacketEncryptor>? _encryptorFactory;
        private readonly IServerConnector _serverConnector;

        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public bool IsListening => _serverConnector.IsListening;

        public event Action<IPeer>? OnPeerConnected;
        public event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Creates a new peer server.
        /// </summary>
        /// <param name="packetRegistry">Registry for incoming packet handler lookup.</param>
        /// <param name="encryptorFactory">Optional factory invoked per-connection to create encryptors.</param>
        /// <param name="server">Optional connector (creates a <see cref="ServerConnector"/> if null). A supplied connector governs its own pool configuration.</param>
        /// <param name="options">Optional segment pool sizing applied to the internally-created connector (ignored when <paramref name="server"/> is supplied).</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="packetRegistry"/> is null.</exception>
        public PeerServer(PacketRegistry packetRegistry, Func<IPacketEncryptor>? encryptorFactory = null, IServerConnector? server = null, TransportOptions? options = null)
        {
            _packetRegistry = packetRegistry ?? throw new ArgumentNullException(nameof(packetRegistry));
            _encryptorFactory = encryptorFactory;

            _serverConnector = server ?? new ServerConnector(128, options);
            _serverConnector.OnClientConnected += HandleClientConnected;
            _serverConnector.OnAcceptFailed += HandleAcceptFailed;
        }

        /// <inheritdoc />
        public void StartListening(string address, int port)
            => _serverConnector.StartListening(address, port);

        /// <inheritdoc />
        public void StopListening()
            => _serverConnector.StopListening();

        /// <inheritdoc />
        public void AcceptConnection()
            => _serverConnector.AcceptConnection();

        /// <summary>
        /// Creates a <see cref="Peer"/> for an accepted connection and fires <see cref="OnPeerConnected"/>.
        /// A throwing factory or subscriber disposes the peer and connection instead of propagating.
        /// </summary>
        private void HandleClientConnected(IConnection connection)
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

        /// <summary>Forwards accept failures to <see cref="OnAcceptFailed"/>.</summary>
        private void HandleAcceptFailed(SocketError error)
        {
            try
            {
                OnAcceptFailed?.Invoke(error);
            }
            catch
            {
                // Subscriber throw is absorbed: no connection exists to dispose.
            }
        }

        /// <summary>
        /// Unsubscribes from connector events and releases the underlying server. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _serverConnector.OnClientConnected -= HandleClientConnected;
            _serverConnector.OnAcceptFailed -= HandleAcceptFailed;
            _serverConnector.Dispose();
        }
    }
}
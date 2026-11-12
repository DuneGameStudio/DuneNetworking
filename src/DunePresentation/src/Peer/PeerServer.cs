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
    /// Wraps an <see cref="IServerConnector"/> with the packet registry and optional
    /// encryption. For each accepted connection, a <see cref="Peer"/> is created and
    /// raised through <see cref="OnPeerConnected"/>.
    /// </para>
    /// <para>
    /// <see cref="TryStartListening"/> is a pure pass-through: the return value and out
    /// parameter carry the synchronous answer, and the relayed events carry only the
    /// asynchronous network outcome.
    /// </para>
    /// <para>
    /// The encryptor comes from a factory invoked once per connection, so each peer gets
    /// its own independent encryption state.
    /// </para>
    /// </remarks>
    public sealed class PeerServer : IPeerServer
    {
        private readonly PacketRegistry _packetRegistry;
        /// <summary>Factory that creates one encryptor per connection. Null disables encryption.</summary>
        private readonly Func<IPacketEncryptor>? _encryptorFactory;
        private readonly IServerConnector _serverConnector;

        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public bool IsListening => _serverConnector.IsListening;

        // Subscribers run on socket-completion threads and must not throw:
        // a throw escapes the completion callback and crashes the process.
        public event Action<IPeer>? OnPeerConnected;
        public event Action<SocketError>? OnAcceptFailed;

        /// <summary>
        /// Creates a new peer server.
        /// </summary>
        /// <param name="packetRegistry">Registry for incoming packet handler lookup.</param>
        /// <param name="encryptorFactory">Optional factory invoked per-connection to create encryptors.</param>
        /// <param name="server">Optional connector to use. When null, a <see cref="ServerConnector"/> is created internally with the default listen backlog. A supplied connector owns its own segment pool configuration, so <paramref name="options"/> has no effect on it.</param>
        /// <param name="options">Optional segment pool sizing for the internally-created connector. Ignored when <paramref name="server"/> is supplied.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="packetRegistry"/> is null.</exception>
        public PeerServer(PacketRegistry packetRegistry, Func<IPacketEncryptor>? encryptorFactory = null, IServerConnector? server = null, TransportOptions? options = null)
        {
            _packetRegistry = packetRegistry ?? throw new ArgumentNullException(nameof(packetRegistry));
            _encryptorFactory = encryptorFactory;

            _serverConnector = server ?? new ServerConnector(128, options);
            _serverConnector.OnClientConnected += HandleClientConnected;
            _serverConnector.OnAcceptFailed += RelayAcceptFailed;
        }

        /// <inheritdoc />
        public bool TryStartListening(string address, int port, out SocketError error)
            => _serverConnector.TryStartListening(address, port, out error);

        /// <inheritdoc />
        public void StopListening()
            => _serverConnector.StopListening();

        /// <inheritdoc />
        public void AcceptConnection()
            => _serverConnector.AcceptConnection();

        /// <summary>
        /// Connector event handler: creates a <see cref="Peer"/> for the accepted
        /// connection and raises <see cref="OnPeerConnected"/>. Runs on the
        /// socket-completion thread — a throw from the encryptor factory or a
        /// subscriber escapes the connector's completion callback and crashes the
        /// process, so neither may throw.
        /// </summary>
        private void HandleClientConnected(IConnection connection)
        {
            IPacketEncryptor? encryptor = _encryptorFactory?.Invoke();
            IPeer peer = new Peer(connection, _packetRegistry, encryptor);
            OnPeerConnected?.Invoke(peer);
        }

        /// <summary>
        /// Relays accept failures to <see cref="OnAcceptFailed"/> subscribers. No
        /// containment: a throwing subscriber propagates from the completion callback.
        /// </summary>
        private void RelayAcceptFailed(SocketError error)
            => OnAcceptFailed?.Invoke(error);

        /// <summary>
        /// Unsubscribes from connector events and releases the underlying server. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _serverConnector.OnClientConnected -= HandleClientConnected;
            _serverConnector.OnAcceptFailed -= RelayAcceptFailed;
            _serverConnector.Dispose();
        }
    }
}
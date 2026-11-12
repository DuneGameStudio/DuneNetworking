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
    /// Wraps an <see cref="IClientConnector"/> with the packet registry and optional
    /// encryption. When the connector reports an established connection, a <see cref="Peer"/>
    /// is created for it and raised through <see cref="OnPeerConnected"/>.
    /// </para>
    /// <para>
    /// <see cref="TryConnect"/> is a pure pass-through: the return value and out parameter
    /// carry the synchronous answer, and the relayed events carry only the asynchronous
    /// network outcome.
    /// </para>
    /// <para>
    /// The encryptor comes from a factory invoked once per connection, so each peer gets
    /// its own independent encryption state.
    /// </para>
    /// </remarks>
    public sealed class PeerClient : IPeerClient
    {
        private readonly PacketRegistry _packetRegistry;
        /// <summary>Factory that creates one encryptor per connection. Null disables encryption.</summary>
        private readonly Func<IPacketEncryptor>? _encryptorFactory;
        private readonly IClientConnector _clientConnector;

        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public bool IsConnected => _clientConnector.IsConnected;

        // Subscribers run on socket-completion threads and must not throw:
        // a throw escapes the completion callback and crashes the process.
        public event Action<IPeer>? OnPeerConnected;
        public event Action<SocketError>? OnConnectFailed;

        /// <summary>
        /// Creates a new peer client.
        /// </summary>
        /// <param name="packetRegistry">Registry for incoming packet handler lookup.</param>
        /// <param name="encryptorFactory">Optional factory invoked per-connection to create encryptors.</param>
        /// <param name="client">Optional connector to use. When null, a <see cref="ClientConnector"/> is created internally. A supplied connector owns its own segment pool configuration, so <paramref name="options"/> has no effect on it.</param>
        /// <param name="options">Optional segment pool sizing for the internally-created connector. Ignored when <paramref name="client"/> is supplied.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="packetRegistry"/> is null.</exception>
        public PeerClient(PacketRegistry packetRegistry, Func<IPacketEncryptor>? encryptorFactory = null, IClientConnector? client = null, TransportOptions? options = null)
        {
            _packetRegistry = packetRegistry ?? throw new ArgumentNullException(nameof(packetRegistry));
            _encryptorFactory = encryptorFactory;

            _clientConnector = client ?? new ClientConnector(options ?? new TransportOptions());
            _clientConnector.OnConnected += HandleConnected;
            _clientConnector.OnConnectFailed += RelayConnectFailed;
        }

        /// <inheritdoc />
        public bool TryConnect(string address, int port, out SocketError error)
            => _clientConnector.TryConnect(address, port, out error);

        /// <summary>
        /// Connector event handler: creates a <see cref="Peer"/> for the new connection and
        /// raises <see cref="OnPeerConnected"/>. Runs on the socket-completion thread — a
        /// throw from the encryptor factory or a subscriber escapes the connector's
        /// completion callback and crashes the process, so neither may throw.
        /// </summary>
        private void HandleConnected(IConnection connection)
        {
            IPacketEncryptor? encryptor = _encryptorFactory?.Invoke();
            IPeer peer = new Peer(connection, _packetRegistry, encryptor);
            OnPeerConnected?.Invoke(peer);
        }

        /// <summary>
        /// Relays the connector's connection failures to <see cref="OnConnectFailed"/>
        /// subscribers. No containment: a throwing subscriber propagates from the
        /// completion callback.
        /// </summary>
        private void RelayConnectFailed(SocketError error)
            => OnConnectFailed?.Invoke(error);

        /// <summary>
        /// Unsubscribes from connector events and releases the underlying client. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _clientConnector.OnConnected -= HandleConnected;
            _clientConnector.OnConnectFailed -= RelayConnectFailed;
            _clientConnector.Dispose();
        }
    }
}

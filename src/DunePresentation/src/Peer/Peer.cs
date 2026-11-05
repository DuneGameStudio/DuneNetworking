using System;
using System.Threading;
using DunePresentation.Encryption.Interface;
using DunePresentation.Packet;
using DunePresentation.Packet.Interfaces;
using DunePresentation.Peer.Interfaces;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;
using DuneTransport.BufferManager.Interface;


namespace DunePresentation.Peer
{
    /// <summary>
    /// Connected peer that can serialize, encrypt, send, decrypt, and deserialize packets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps an <see cref="IConnection"/> with packet registry lookup and optional encryption.
    /// Provides the main send/receive interface for application-level packet I/O.
    /// </para>
    /// </remarks>
    public class Peer : IPeer
    {
        private readonly IConnection _connection;
        private readonly PacketRegistry _packetRegistry;
        private readonly IPacketEncryptor? _encryptor;

        /// <summary>Disposed</summary>
        private int _disposed;

        /// <inheritdoc />
        public IConnection Connection => _connection;

        /// <inheritdoc />
        public bool IsConnected => _connection.IsConnected;

        /// <summary>
        /// Creates a new peer for the given connection.
        /// </summary>
        /// <param name="connection">An active connection to wrap.</param>
        /// <param name="packetRegistry">Registry for incoming packet handler lookup.</param>
        /// <param name="encryptor">Optional encryptor for bidirectional encryption. Null disables encryption.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> or <paramref name="packetRegistry"/> is null.</exception>
        public Peer(IConnection connection, PacketRegistry packetRegistry, IPacketEncryptor? encryptor = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _packetRegistry = packetRegistry ?? throw new ArgumentNullException(nameof(packetRegistry));
            _encryptor = encryptor;
        }

        /// <inheritdoc />
        public (Segment? Segment, PacketError Error) SerializeAndEncrypt<T>(T packet) where T : IPacket
        {
            ushort packetId = packet.PacketId;
            IPacketEncryptor? encryptor = _encryptor;

            // Phase 1: reserve + serialize. OnSerialize is contracted not to throw; the enum
            // disambiguates pool-exhaustion from serialize-failure so the return value carries
            // the correct reason (null segment + Error on failure).
            SerializeResult result = packet.Serialize(_connection.Transport);
            if (result != SerializeResult.Ok)
            {
                return (null, result == SerializeResult.PoolExhausted
                    ? PacketError.PoolExhausted
                    : PacketError.SerializationError);
            }

            // Phase 2: write the presentation header + encrypt in place. This is presentation-layer
            // work that previously lived inside ISegmentManager.Serialize via an injected callback;
            // it now runs here, where a throw can be caught and the segment released (closing the
            // SEG-1/SEG-2 leak path). Ownership of the segment is ours until we return it.
            Segment seg = packet.segment;
            Span<byte> span = seg.Memory.Span;
            try
            {
                PresentationHeader.Write(span, packetId);
                if (encryptor != null)
                    encryptor.Encrypt(span.Slice(0, ((IPacket)packet).PacketSize), span);
            }
            catch (Exception)
            {
                seg.Release();
                return (null, PacketError.SerializationError);
            }

            return (seg, PacketError.None);
        }

        /// <inheritdoc />
        public ((IPacket Packet, Action<IPacket> Handler)? Result, PacketError Error) DecryptAndDeserialize(Segment segment)
        {
            var span = segment.Memory.Span;

            // Stage 1: Decrypt
            try
            {
                _encryptor?.Decrypt(span, span);
            }
            catch
            {
                segment.Release();
                return (null, PacketError.DecryptError);
            }

            // Stage 2: Read header
            ushort packetId;
            try
            {
                PresentationHeader.Read(span, out packetId);
            }
            catch
            {
                segment.Release();
                return (null, PacketError.SerializationError);
            }

            // Stage 3: Registry lookup
            if (!_packetRegistry.TryGetEntry(packetId, out Entry entry))
            {
                segment.Release();
                return (null, PacketError.RegistryError);
            }

            // Stage 4: Factory + deserialize (factory exceptions propagate)
            IPacket packet = entry.Factory();
            packet.segment = segment;
            packet.PacketSize = span.Length;

            // packet.Deserialize() internally calls ISegmentManager.Deserialize() which releases the
            // segment on the success/failure return path. Per the ISegmentManager contract,
            // OnDeserialize must not throw; but if an implementor violates that contract, the
            // throw escapes Deserialize before the inner release runs — so guard it here and
            // release the segment ourselves (closing the SEG-3 leak at the nearest site rather
            // than relying on every outer caller being defensive).
            try
            {
                var deserializeResult = packet.Deserialize();
                if (deserializeResult != DeserializeResult.Ok)
                {
                    return (null, PacketError.DeserializeError);
                }
            }
            catch (Exception)
            {
                segment.Release();
                return (null, PacketError.DeserializeError);
            }

            return ((packet, entry.Invoke), PacketError.None);
        }

        /// <inheritdoc />
        public void Send(Segment segment, int packetSize)
        {
            _connection.Transport.SendAsync(segment, packetSize);
        }

        /// <inheritdoc />
        public void DisconnectAsync()
        {
            _connection.DisconnectAsync();
        }

        /// <summary>Releases the underlying connection. Idempotent.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _connection.Dispose();
        }
    }
}

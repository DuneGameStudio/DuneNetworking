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
    /// Wraps an <see cref="IConnection"/> with packet registry lookup and optional
    /// encryption; it is the main send/receive path for application packets.
    /// </para>
    /// <para>
    /// User-supplied code (field serialization, the encryptor) is contracted never to
    /// throw. The pipeline deliberately contains no try/catch around it: a throw from
    /// that code propagates to the caller in the ordinary C# way and leaks the segment
    /// being processed, because nothing in this class releases it on the throw path.
    /// </para>
    /// </remarks>
    public class Peer : IPeer
    {
        private readonly IConnection _connection;
        private readonly PacketRegistry _packetRegistry;
        private readonly IPacketEncryptor? _encryptor;

        /// <summary>Dispose guard: 0 = active, 1 = disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public IConnection Connection => _connection;

        /// <inheritdoc />
        public bool IsConnected => _connection.IsConnected;

        /// <summary>
        /// Creates a new peer for the given connection.
        /// </summary>
        /// <param name="connection">The connection to wrap. Must not be null.</param>
        /// <param name="packetRegistry">The registry used to look up the factory and handler for each incoming packet ID. Must not be null.</param>
        /// <param name="encryptor">Optional encryptor used in both directions. Pass null to disable encryption.</param>
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

            // Step 1: reserve a send segment from the transport, then serialize the
            // packet's fields into it. The field code is contracted never to throw, and
            // the result enum distinguishes pool exhaustion from a field-write failure,
            // so the return tuple can carry the matching reason (null segment + error).
            SerializeResult result = packet.Serialize(_connection.Transport);
            if (result != SerializeResult.Ok)
            {
                return (null, result == SerializeResult.PoolExhausted
                    ? PacketError.PoolExhausted
                    : PacketError.SerializationError);
            }

            // Step 2: write the 2-byte presentation header, then encrypt in place (the
            // encrypted region is a prefix of the same segment, so no second buffer is
            // needed). The encryptor is contracted never to throw: a throw would escape
            // to the caller with the segment still unreleased — a pool leak nothing in
            // this method can undo.
            Segment seg = packet.segment;
            Span<byte> span = seg.Memory.Span;
            PresentationHeader.Write(span, packetId);
            if (encryptor != null)
                encryptor.Encrypt(span.Slice(0, ((IPacket)packet).PacketSize), span);

            return (seg, PacketError.None);
        }

        /// <inheritdoc />
        public ((IPacket Packet, Action<IPacket> Handler)? Result, PacketError Error) DecryptAndDeserialize(Segment segment)
        {
            var span = segment.Memory.Span;

            // Step 1: decrypt in place. The encryptor is contracted never to throw: a
            // throw escapes to the caller with the segment unreleased — a pool leak
            // nothing in this method can undo.
            _encryptor?.Decrypt(span, span);

            // Step 2: read the 2-byte presentation header. The read throws on a segment
            // smaller than the header — a protocol violation, since a well-formed packet
            // always carries its header.
            ushort packetId;

            PresentationHeader.Read(span, out packetId);

            // Step 3: look up the packet ID in the registry. On a miss no later step will
            // consume the segment, so release it here before returning.
            if (!_packetRegistry.TryGetEntry(packetId, out Entry entry))
            {
                segment.Release();
                return (null, PacketError.RegistryError);
            }

            // Step 4: create the packet via the registry's factory, then deserialize its
            // fields. The constructor and the field code are contracted never to throw; a
            // throw escapes to the caller and leaks the segment, because the release inside
            // Deserialize only runs after the user code returns. PacketSize is set to the
            // full segment length (header included); the packet's OnDeserialize subtracts
            // the header size when it slices out the field span.
            IPacket packet = entry.Factory();
            packet.segment = segment;
            packet.PacketSize = span.Length;

            // Deserialize releases the segment after OnDeserialize returns, whether the
            // read succeeded or failed — so on both return paths below the segment is
            // already back in the pool.
            var deserializeResult = packet.Deserialize();
            if (deserializeResult != DeserializeResult.Ok)
            {
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

using System;
using System.Buffers;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Packets
{
    /// <summary>
    ///     Maps packet IDs to factory functions and routes incoming packets
    ///     to the correct IRequestResponse handler.
    ///
    ///     Array-indexed by packet ID for O(1) lookup.
    ///     Factory functions let consumers control allocation (pooling, new, etc.).
    /// </summary>
    public sealed class PacketRegistry
    {
        private readonly Func<IRequestResponse>?[] _factories;

        public PacketRegistry(int capacity = 256)
        {
            _factories = new Func<IRequestResponse>?[capacity];
        }

        /// <summary>
        ///     Registers a factory function for a given packet ID.
        /// </summary>
        public void Register(ushort packetId, Func<IRequestResponse> factory)
        {
            if (packetId >= _factories.Length)
                throw new ArgumentOutOfRangeException(nameof(packetId),
                    $"Packet ID {packetId} exceeds registry capacity {_factories.Length}.");

            _factories[packetId] = factory;
        }

        /// <summary>
        ///     Reads the 2-byte packet ID from the payload, creates the handler
        ///     via the registered factory, deserializes, and executes.
        ///
        ///     Called by the deserialization thread.
        /// </summary>
        public void HandlePacket(ReadOnlySequence<byte> payload, ITransport source)
        {
            var reader = new SequenceReader<byte>(payload);

            if (!reader.TryReadLittleEndian(out short rawId))
                return;

            ushort packetId = (ushort)rawId;

            if (packetId >= _factories.Length || _factories[packetId] == null)
                throw new InvalidOperationException($"No handler registered for packet ID {packetId}.");

            IRequestResponse handler = _factories[packetId]!();
            handler.OnDeserialize(payload.Slice(reader.Position));
            handler.Execute(source);
        }
    }
}

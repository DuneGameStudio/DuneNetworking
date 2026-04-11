using System;
using System.Collections.Generic;
using DunePresentation.Packet.Interfaces;

namespace DunePresentation.Packet
{
    internal readonly struct Entry
    {
        public readonly Func<IPacket> Factory;
        public readonly Action<IPacket> Invoke;

        public Entry(Func<IPacket> factory, Action<IPacket> invoke)
        {
            Factory = factory;
            Invoke = invoke;
        }
    }

    public sealed class PacketRegistry
    {
        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        public void RegisterHandler<T>(ushort packetId, Action<T> handler) where T : IPacket, new()
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_entries.ContainsKey(packetId))
                throw new InvalidOperationException($"PacketId {packetId} is already registered.");

            _entries[packetId] = new Entry(
                factory: () => new T(),
                invoke: packet => handler((T)packet));
        }

        internal bool TryGetEntry(ushort packetId, out Entry entry)
        {
            return _entries.TryGetValue(packetId, out entry);
        }
    }
}

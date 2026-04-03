using System;
using DuneNetworking.Packets;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Threading
{
    /// <summary>
    ///     Owns the parser and deserialization worker threads.
    ///     One instance per game/match. Created at startup, stopped at shutdown.
    /// </summary>
    public class NetworkEngine : IDisposable
    {
        private readonly PacketHandlerWorker handler;
        private readonly PacketDeserializerWorker _deserializer;
        private bool _disposed;

        /// <param name="registry">
        ///     Registry mapping packet IDs to handler factories.
        ///     Used by the deserialization thread to route packets.
        /// </param>
        public NetworkEngine(PacketRegistry registry)
        {
            _deserializer = new PacketDeserializerWorker(registry);

            handler = new PacketHandlerWorker();
            handler.OnPacketExtracted = _deserializer.Enqueue;
        }

        /// <summary>
        ///     Starts both worker threads.
        /// </summary>
        public void Start()
        {
            _deserializer.Start();
            handler.Start();
        }

        /// <summary>
        ///     Stops both worker threads. Blocks until they exit.
        /// </summary>
        public void Stop()
        {
            handler.Stop();
            _deserializer.Stop();
        }

        /// <summary>
        ///     Called by Transport's IOCP completion callback to signal
        ///     that a connection has new data in its ring buffer.
        /// </summary>
        public void SignalDataReceived(ITransport source)
        {
            handler.Signal(source);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}

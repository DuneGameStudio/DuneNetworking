using System;
using System.Collections.Concurrent;
using System.Threading;
using DuneTransport.Packets;

namespace DuneTransport.Threading
{
    /// <summary>
    ///     A single long-lived thread that deserializes packets from all connections.
    ///
    ///     Receives Packet from the parser thread, routes it through the
    ///     PacketRegistry, then releases the ring buffer space. Packets from each
    ///     connection are processed in the order they were parsed (FIFO queue).
    /// </summary>
    public class PacketDeserializerWorker
    {
        private readonly ConcurrentQueue<Packet> _packetQueue;
        private readonly ManualResetEventSlim _wakeSignal;
        private readonly CancellationTokenSource _cts;
        private readonly PacketRegistry _registry;
        private Thread? _thread;

        public PacketDeserializerWorker(PacketRegistry registry)
        {
            _registry = registry;
            _packetQueue = new ConcurrentQueue<Packet>();
            _wakeSignal = new ManualResetEventSlim(false);
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            _thread = new Thread(RunLoop)
            {
                Name = "DuneNet-Deserializer",
                IsBackground = true
            };
            _thread.Start();
        }

        public void Stop()
        {
            _cts.Cancel();
            _wakeSignal.Set();
            _thread?.Join();
        }

        /// <summary>
        ///     Enqueues a parsed packet for deserialization.
        ///     Called by the parser thread.
        /// </summary>
        public void Enqueue(Packet info)
        {
            _packetQueue.Enqueue(info);
            _wakeSignal.Set();
        }

        private void RunLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _wakeSignal.Wait(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _wakeSignal.Reset();

                while (_packetQueue.TryDequeue(out Packet info))
                {
                    ProcessPacket(info);
                }
            }
        }

        private void ProcessPacket(Packet info)
        {
            try
            {
                _registry.HandlePacket(info.Payload, info.Source);
            }
            finally
            {
                // Always release ring buffer space, even if the handler throws.
                info.SourceBuffer.Release(info.TotalFrameSize);
            }
        }
    }
}

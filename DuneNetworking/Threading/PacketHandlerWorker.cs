using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using DuneNetworking.Buffer;
using DuneNetworking.Packets;
using DuneNetworking.Transport.Interface;

namespace DuneNetworking.Threading
{
    /// <summary>
    ///     A single long-lived thread that parses packet frames from all connections.
    ///     
    ///     IOCP callbacks signal this thread via Signal(). The thread wakes, drains all
    ///     signaled connections, extracts complete packet frames from each ring buffer,
    ///     and forwards them to the deserialization thread via _onPacketExtracted.
    ///     
    ///     Parsing is idempotent: duplicate signals for the same connection cause a
    ///     harmless no-op pass where no new packets are found.
    /// </summary>
    public class PacketHandlerWorker
    {
        private readonly ConcurrentQueue<ITransport> _signalQueue;
        private readonly ManualResetEventSlim _wakeSignal;
        private readonly CancellationTokenSource _cts;
        private readonly List<(ReadOnlySequence<byte>, int)> _packetBuffer;
        private Thread? _thread;

        private Action<Packet>? _onPacketExtracted;

        public PacketHandlerWorker()
        {
            _signalQueue = new ConcurrentQueue<ITransport>();
            _wakeSignal = new ManualResetEventSlim(false);
            _cts = new CancellationTokenSource();
            _packetBuffer = new List<(ReadOnlySequence<byte>, int)>();
        }

        /// <summary>
        ///     Sets the callback invoked for each extracted packet.
        ///     Must be set before calling Start().
        /// </summary>
        public Action<Packet>? OnPacketExtracted
        {
            set => _onPacketExtracted = value;
        }

        public void Start()
        {
            if (_onPacketExtracted == null)
                throw new InvalidOperationException("OnPacketExtracted must be set before Start().");

            _thread = new Thread(RunLoop)
            {
                Name = "DuneNet-Parser",
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
        ///     Called by IOCP completion callbacks to signal that a connection
        ///     has new data in its ring buffer.
        /// </summary>
        public void Signal(ITransport source)
        {
            _signalQueue.Enqueue(source);
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

                // Drain all signaled connections
                while (_signalQueue.TryDequeue(out ITransport? source))
                {
                    ProcessBuffer(source);
                }
            }
        }

        private void ProcessBuffer(ITransport source)
        {
            RingBuffer ringBuffer = source.RingBuffer;
            ReadOnlySequence<byte> parsable = ringBuffer.GetParsableSequence();

            if (parsable.Length == 0)
                return;

            ExtractionResult result = BufferHandler.ExtractPackets(parsable, _packetBuffer);

            if (result.Type == ExtractionResultType.Error)
            {
                source.DisconnectAsync();
                return;
            }

            if (result.BytesConsumed > 0)
                ringBuffer.CommitParsed(result.BytesConsumed);

            for (int i = 0; i < _packetBuffer.Count; i++)
            {
                (ReadOnlySequence<byte> Payload, int TotalFrameSize) packet = _packetBuffer[i];

                _onPacketExtracted!(new Packet(
                    payload: packet.Payload,
                    totalFrameSize: packet.TotalFrameSize,
                    sourceBuffer: ringBuffer,
                    source: source));
            }
        }
    }
}
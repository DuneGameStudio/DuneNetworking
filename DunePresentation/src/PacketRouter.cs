using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DunePresentation.Interface;

namespace DunePresentation
{
    internal readonly struct HandlerEntry
    {
        public readonly Func<IRequest> Factory;
        public readonly Action<IRequest, Action<IResponse>> Invoke;

        public HandlerEntry(Func<IRequest> factory, Action<IRequest, Action<IResponse>> invoke)
        {
            Factory = factory;
            Invoke = invoke;
        }
    }

    public sealed class PacketRouter : IPacketRouter, IDisposable
    {
        private readonly Dictionary<ushort, HandlerEntry> _handlers = new Dictionary<ushort, HandlerEntry>();

        private readonly ConcurrentDictionary<ushort, PendingRequest> _pending = new ConcurrentDictionary<ushort, PendingRequest>();

        private int _nextCorrelationId;

        private readonly Timer _timer;

        internal event Action<PendingRequest>? OnRequestTimedOut;

        public PacketRouter()
        {
            _timer = new Timer(Sweep, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void RegisterRequestHandler<TRequest, TResponse>(Action<TRequest, Action<TResponse>> handler) where TRequest : IRequest, new() where TResponse : IResponse
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            ushort packetId = new TRequest().PacketId;

            if (_handlers.ContainsKey(packetId))
                throw new InvalidOperationException($"A handler is already registered for PacketId {packetId}.");

            _handlers[packetId] = new HandlerEntry(
                factory: () => new TRequest(),
                invoke: (request, sendReply) => handler((TRequest)request, response => sendReply(response)));
        }

        internal bool TryGetHandler(ushort packetId, out HandlerEntry entry)
        {
            return _handlers.TryGetValue(packetId, out entry);
        }

        internal ushort Track(PendingRequest request)
        {
            ushort id = AllocateCorrelationId();
            _pending[id] = request;
            return id;
        }

        internal bool TryComplete(ushort correlationId, out PendingRequest request)
        {
            return _pending.TryRemove(correlationId, out request);
        }

        internal List<PendingRequest> DrainPending()
        {
            var drained = new List<PendingRequest>(_pending.Count);
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var pending))
                    drained.Add(pending);
            }
            return drained;
        }

        private ushort AllocateCorrelationId()
        {
            ushort id;
            do
            {
                id = (ushort)Interlocked.Increment(ref _nextCorrelationId);
            }
            while (_pending.ContainsKey(id));
            return id;
        }

        private void Sweep(object? state)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var kvp in _pending)
            {
                if (kvp.Value.DeadlineTick > 0 && now >= kvp.Value.DeadlineTick)
                {
                    if (_pending.TryRemove(kvp.Key, out var pending))
                        OnRequestTimedOut?.Invoke(pending);
                }
            }
        }

        public void Dispose()
        {
            using var waitHandle = new ManualResetEvent(false);
            _timer.Dispose(waitHandle);
            waitHandle.WaitOne();
        }
    }
}

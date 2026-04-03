using System;
using System.Buffers;
using System.Threading;

namespace DuneNetworking.Buffer
{
    /// <summary>
    ///     A single contiguous byte buffer with three indices serving three consumers:
    ///     
    ///     - WriteIndex:   advanced by the IOCP completion callback after each receive.
    ///     - ParseIndex:   advanced by the parser thread after extracting packet frames.
    ///     - ReleaseIndex: advanced by the deserialization thread after consuming packet data.
    ///     
    ///     Data flows:  IOCP writes → Parser reads frames → Deserializer consumes → space freed.
    ///     
    ///     Thread safety is achieved without locks:
    ///     - _writeIndex:   single writer (IOCP), single reader (parser)       → Volatile
    ///     - _parseIndex:   single writer and reader (parser)                  → no sync
    ///     - _releaseIndex: single writer (deserializer), single reader (IOCP) → Volatile
    ///     - _dataLength:   two writers (IOCP increment, deserializer decrement) → Interlocked
    /// </summary>
    public class RingBuffer
    {
        private readonly byte[] _buffer;
        private readonly int _capacity;

        private int _writeIndex;
        private int _parseIndex;
        private int _releaseIndex;
        private int _dataLength;

        private Action? _onSpaceFreed;

        private readonly RingBufferSegment _segmentA = new RingBufferSegment();
        private readonly RingBufferSegment _segmentB = new RingBufferSegment();

        /// <param name="capacity">Buffer size in bytes. Default 256KB.</param>
        public RingBuffer(int capacity = 262144)
        {
            _capacity = capacity;
            _buffer = new byte[capacity];
        }

        /// <summary>
        ///     Registers a callback invoked on every Release call.
        ///     The Transport uses this to resume a suspended receive when space frees up.
        /// </summary>
        public void RegisterOnSpaceFreed(Action callback)
        {
            _onSpaceFreed = callback;
        }

        // ----------------------------------------------------------------
        //  IOCP Callback Side
        // ----------------------------------------------------------------

        /// <summary>
        ///     Returns a contiguous writable region for SocketAsyncEventArgs.SetBuffer.
        ///     Returns empty if the buffer is full.
        ///     
        ///     The returned region extends from the write pointer toward the end of the
        ///     backing array (or toward the release pointer if write has wrapped).
        ///     After wrap-around, the next call returns the region from 0 forward.
        /// </summary>
        public Memory<byte> GetWritableMemory()
        {
            int free = _capacity - Volatile.Read(ref _dataLength);

            if (free <= 0)
                return Memory<byte>.Empty;

            int write = _writeIndex;
            int release = Volatile.Read(ref _releaseIndex);

            int contiguous;

            if (write >= release)
                contiguous = _capacity - write;
            else
                contiguous = release - write;

            if (contiguous > free)
                contiguous = free;

            if (contiguous <= 0)
                return Memory<byte>.Empty;

            return _buffer.AsMemory(write, contiguous);
        }

        /// <summary>
        ///     Advances the write pointer after a successful receive.
        /// </summary>
        public void CommitWrite(int bytesReceived)
        {
            _writeIndex = (_writeIndex + bytesReceived) % _capacity;
            Interlocked.Add(ref _dataLength, bytesReceived);
        }

        // ----------------------------------------------------------------
        //  Parser Thread Side
        // ----------------------------------------------------------------

        /// <summary>
        ///     Returns all received-but-unparsed data as a ReadOnlySequence.
        ///     If the data wraps around the buffer boundary, the sequence contains
        ///     two segments backed by pre-allocated RingBufferSegment instances.
        /// </summary>
        public ReadOnlySequence<byte> GetParsableSequence()
        {
            int parse = _parseIndex;
            int write = Volatile.Read(ref _writeIndex);

            if (parse == write)
                return ReadOnlySequence<byte>.Empty;

            if (parse < write)
            {
                var memory = new ReadOnlyMemory<byte>(_buffer, parse, write - parse);
                return new ReadOnlySequence<byte>(memory);
            }

            // Wrap-around
            int tailLength = _capacity - parse;

            if (write == 0)
            {
                // All parseable data is at the tail, no wrap needed
                var memory = new ReadOnlyMemory<byte>(_buffer, parse, tailLength);
                return new ReadOnlySequence<byte>(memory);
            }

            var regionA = new ReadOnlyMemory<byte>(_buffer, parse, tailLength);
            var regionB = new ReadOnlyMemory<byte>(_buffer, 0, write);

            _segmentA.SetMemory(regionA, 0);
            _segmentA.SetNext(_segmentB);
            _segmentB.SetMemory(regionB, regionA.Length);
            _segmentB.SetNext(null);

            return new ReadOnlySequence<byte>(_segmentA, 0, _segmentB, write);
        }

        /// <summary>
        ///     Advances the parse pointer after extracting complete packet frames.
        /// </summary>
        public void CommitParsed(int bytesConsumed)
        {
            _parseIndex = (_parseIndex + bytesConsumed) % _capacity;
        }

        // ----------------------------------------------------------------
        //  Deserialization Thread Side
        // ----------------------------------------------------------------

        /// <summary>
        ///     Releases consumed bytes, advancing the release pointer and freeing
        ///     buffer space for new receives. Notifies the transport via the
        ///     registered callback so a suspended receive can resume.
        /// </summary>
        public void Release(int bytesConsumed)
        {
            int newRelease = (_releaseIndex + bytesConsumed) % _capacity;
            Volatile.Write(ref _releaseIndex, newRelease);
            Interlocked.Add(ref _dataLength, -bytesConsumed);

            _onSpaceFreed?.Invoke();
        }

        // ----------------------------------------------------------------
        //  Queries
        // ----------------------------------------------------------------

        public int FreeSpace => _capacity - Volatile.Read(ref _dataLength);
        public int DataLength => Volatile.Read(ref _dataLength);
        public int Capacity => _capacity;
    }
}

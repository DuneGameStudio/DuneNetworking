using System;
using System.Buffers;

namespace DuneNetworking.Buffer
{
    /// <summary>
    ///     Concrete ReadOnlySequenceSegment used to represent ring buffer regions
    ///     in a ReadOnlySequence. Two instances are pre-allocated per ReceiveRingBuffer
    ///     and reused on every GetParsableSequence call to avoid allocations.
    /// </summary>
    public sealed class RingBufferSegment : ReadOnlySequenceSegment<byte>
    {
        public void SetMemory(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(RingBufferSegment? next)
        {
            Next = next;
        }
    }
}

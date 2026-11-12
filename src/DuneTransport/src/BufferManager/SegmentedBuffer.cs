using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DuneTransport.BufferManager
{
    /// <summary>
    /// Thread-safe fixed-size memory pool that leases and reclaims <see cref="Segment"/> slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Divides a contiguous byte array into equal-sized slots. Each slot can be
    /// reserved and released independently, and the pool is safe for concurrent
    /// access from multiple threads.
    /// </para>
    /// <para>
    /// Release is idempotent: calling <see cref="ReleaseMemory"/> multiple times on
    /// the same slot index is safe. The transport relies on this property for
    /// race-condition cleanup (racing completions, dispose races).
    /// </para>
    /// </remarks>
    public class SegmentedBuffer
    {
        /// <summary>The backing byte array, divided into equal-sized slots (slot 0 of the array backs segment 1).</summary>
        readonly byte[] data;

        /// <summary>The size of each segment in bytes.</summary>
        public int SegmentSize => segmentSize;

        /// <summary>The total number of segments in this pool.</summary>
        public int SegmentCount => segmentCount;

        /// <summary>The number of currently free (available) segments.</summary>
        public int FreeCount => freeSegments.Count;

        /// <summary>The size of one slot, in bytes (backing array size divided by the segment count).</summary>
        readonly int segmentSize;

        /// <summary>The number of equal-sized slots the backing array is divided into.</summary>
        readonly int segmentCount;

        /// <summary>The queue of free slot indices; reserving dequeues, releasing enqueues.</summary>
        readonly ConcurrentQueue<int> freeSegments;

        /// <summary>Allocation state per segment: 0 = free, 1 = allocated (accessed via <see cref="Interlocked"/>).</summary>
        readonly int[] isAllocated;

        /// <summary>
        /// Creates a segmented buffer pool.
        /// </summary>
        /// <param name="arrayLength">Total size of the backing byte array in bytes; divided evenly into <paramref name="segmentCount"/> slots.</param>
        /// <param name="segmentCount">Number of equal-sized slots to create (1-based segment indices run 1 through this value).</param>
        public SegmentedBuffer(int arrayLength = 8192, int segmentCount = 32)
        {
            if (segmentCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(segmentCount), "Segment count must be positive.");
            if (arrayLength <= 0)
                throw new ArgumentException("Buffer length must be positive.", nameof(arrayLength));
            if (arrayLength < segmentCount)
                throw new ArgumentException("arrayLength must be >= segmentCount.", nameof(arrayLength));

            segmentSize = arrayLength / segmentCount;
            this.segmentCount = segmentCount;

            data = new byte[arrayLength];

            freeSegments = new ConcurrentQueue<int>();
            isAllocated = new int[segmentCount + 1];

            for (int i = 1; i <= segmentCount; i++)
                freeSegments.Enqueue(i);
        }

        /// <summary>
        /// Attempts to reserve a segment from the pool.
        /// </summary>
        /// <param name="segment">
        /// When this method returns, contains the reserved segment if the operation succeeded;
        /// otherwise contains a default <see cref="Segment"/>.
        /// </param>
        /// <returns><c>true</c> if a segment was successfully reserved; otherwise <c>false</c> if the pool is exhausted.</returns>
        /// <remarks>
        /// The returned segment must be released by calling <see cref="Segment.Release"/> when no longer needed.
        /// Failure to release results in a pool leak.
        /// </remarks>
        public bool TryReserveSegment(out Segment segment)
        {
            segment = new Segment();

            if (!freeSegments.TryDequeue(out int segmentIndex))
                return false;

            Interlocked.Exchange(ref isAllocated[segmentIndex], 1);

            int segmentStart = (segmentIndex - 1) * segmentSize;
            segment.SegmentIndex = segmentIndex;
            segment.ReleaseMemoryCallback = ReleaseMemory;
            Memory<byte> full = data.AsMemory(segmentStart, segmentSize);
            segment.Memory = full;
            segment.FullSlot = full;
            return true;
        }

        /// <summary>
        /// Releases a segment back to the pool, making it available for future reservations.
        /// </summary>
        /// <param name="segmentNumber">The 1-based index of the segment to release.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="segmentNumber"/> is outside the valid range (1 to <see cref="SegmentCount"/>).
        /// </exception>
        /// <remarks>
        /// <para>
        /// This operation is idempotent: calling <see cref="ReleaseMemory"/> multiple times
        /// on the same segment is safe. If the segment is already free, the call is ignored.
        /// </para>
        /// <para>
        /// Thread-safe: uses <see cref="Interlocked.Exchange"/> to prevent double-release races.
        /// </para>
        /// </remarks>
        public void ReleaseMemory(int segmentNumber)
        {
            if (segmentNumber < 1 || segmentNumber > segmentCount)
                throw new ArgumentOutOfRangeException(nameof(segmentNumber));

            // Atomic check-and-clear: if already free (0), return immediately.
            // Idempotent: the transport relies on this for racing-completion and dispose cleanup paths.
            if (Interlocked.Exchange(ref isAllocated[segmentNumber], 0) == 0)
                return;

            freeSegments.Enqueue(segmentNumber);
        }

    }
}

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DuneTransport.BufferManager
{
    /// <summary>
    /// Thread-safe fixed-size memory pool that leases and reclaims <see cref="Segment"/> slices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Divides a contiguous byte array into equal-sized segments. Each segment can be
    /// reserved and released independently. The pool is safe for concurrent access from
    /// multiple threads.
    /// </para>
    /// <para>
    /// Release is idempotent: calling <see cref="ReleaseMemory"/> multiple times on the
    /// same segment index is safe. The <c>ITransport</c> interface relies on this property
    /// for race-condition cleanup (handler exceptions, dispose races).
    /// </para>
    /// </remarks>
    public class SegmentedBuffer
    {
        readonly byte[] data;

        /// <summary>The size of each segment in bytes.</summary>
        public int SegmentSize => segmentSize;

        /// <summary>The total number of segments in this pool.</summary>
        public int SegmentCount => segmentCount;

        /// <summary>The number of currently free (available) segments.</summary>
        public int FreeCount => freeSegments.Count;

        readonly int segmentSize;

        readonly int segmentCount;

        readonly ConcurrentQueue<int> freeSegments;

        /// <summary>Allocation state per segment: 0 = free, 1 = allocated (accessed via <see cref="Interlocked"/>)</summary>
        readonly int[] isAllocated;

        /// <summary>
        /// Creates a new segmented buffer pool.
        /// </summary>
        /// <param name="arrayLength">Total size of the backing byte array. Divided evenly into <paramref name="segmentCount"/> segments.</param>
        /// <param name="segmentCount">Number of equal-sized segments to create.</param>
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
            segment.Memory = data.AsMemory(segmentStart, segmentSize);
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
            // Idempotent: Transport relies on this for handler-throw and dispose-race cleanup paths.
            if (Interlocked.Exchange(ref isAllocated[segmentNumber], 0) == 0)
                return;

            freeSegments.Enqueue(segmentNumber);
        }

        /// <summary>
        /// Retrieves a memory view of a specific segment without transferring ownership.
        /// </summary>
        /// <param name="segmentNumber">The 1-based index of the segment.</param>
        /// <param name="length">The desired length of the memory slice (must not exceed <see cref="SegmentSize"/>).</param>
        /// <param name="registeredMemory">
        /// When this method returns, contains the memory slice if the operation succeeded;
        /// otherwise contains a default <see cref="Memory{T}"/>.
        /// </param>
        /// <returns><c>true</c> if the memory slice was retrieved; otherwise <c>false</c> if the segment number or length is invalid.</returns>
        /// <remarks>
        /// <para>
        /// This method does NOT transfer ownership — the segment remains allocated.
        /// Use <see cref="TryReserveSegment(out Segment)"/> for ownership transfer.
        /// </para>
        /// <para>
        /// Typically used by <see cref="Transport.Interface.ITransport"/> to locate a segment's
        /// backing memory for framing operations (writing length headers).
        /// </para>
        /// </remarks>
        public bool GetRegisteredMemory(int segmentNumber, int length, out Memory<byte> registeredMemory)
        {
            registeredMemory = default;

            if (segmentNumber < 1 || segmentNumber > segmentCount)
                return false;

            if (length < 0 || length > segmentSize)
                return false;

            int start = (segmentNumber - 1) * segmentSize;
            registeredMemory = data.AsMemory(start, length);
            return true;
        }
    }
}

using System;

namespace DuneTransport.BufferManager
{
    /// <summary>
    /// Represents a fixed-size memory segment allocated from a <see cref="SegmentedBuffer"/> pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each segment is leased from the pool via <see cref="SegmentedBuffer.TryReserveSegment(out Segment)"/>
    /// and must be returned by calling <see cref="Release"/> when no longer needed.
    /// </para>
    /// <para>
    /// Failure to call <see cref="Release"/> results in a pool leak — eventually the pool
    /// exhausts and <see cref="SegmentedBuffer.TryReserveSegment(out Segment)"/> returns <c>false</c>.
    /// </para>
    /// </remarks>
    public struct Segment
    {
        /// <summary>
        /// Creates a segment with the given index, memory, and release callback.
        /// </summary>
        /// <param name="segmentIndex">The 1-based index within the parent pool.</param>
        /// <param name="memory">The writable memory slice.</param>
        /// <param name="releaseMemoryCallback">Callback invoked on <see cref="Release"/>.</param>
        public Segment(int segmentIndex, Memory<byte> memory, Action<int> releaseMemoryCallback)
        {
            SegmentIndex = segmentIndex;
            Memory = memory;
            ReleaseMemoryCallback = releaseMemoryCallback;
        }

        /// <summary>The 1-based index of this segment within its parent <see cref="SegmentedBuffer"/>.</summary>
        /// <remarks>Set by <see cref="SegmentedBuffer.TryReserveSegment"/> during reservation. Should not be mutated after reservation.</remarks>
        public int SegmentIndex { get; internal set; }

        /// <summary>The writable memory slice assigned to this segment.</summary>
        public Memory<byte> Memory { get; internal set; }

        /// <summary>Callback to release this segment back to its parent pool.</summary>
        public Action<int> ReleaseMemoryCallback { get; internal set; }

        /// <summary>
        /// Returns this segment to its parent <see cref="SegmentedBuffer"/> pool.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Must be called exactly once per successful reservation. The release operation
        /// is idempotent — calling <see cref="Release"/> multiple times on the same segment
        /// is safe and has no additional effect.
        /// </para>
        /// <para>
        /// After calling <see cref="Release"/>, the <see cref="Memory"/> slice becomes invalid
        /// and should not be accessed.
        /// </para>
        /// </remarks>
        public void Release()
        {
            ReleaseMemoryCallback?.Invoke(SegmentIndex);
        }
    }
}
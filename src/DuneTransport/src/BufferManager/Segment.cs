using System;

namespace DuneTransport.BufferManager
{
    /// <summary>
    /// A fixed-size memory slot leased from a <see cref="SegmentedBuffer"/> pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A segment is leased from the pool via
    /// <see cref="SegmentedBuffer.TryReserveSegment(out Segment)"/> and returned by
    /// calling <see cref="Release"/> when no longer needed.
    /// </para>
    /// <para>
    /// A segment that is never released leaks from the pool — eventually the pool
    /// exhausts and <see cref="SegmentedBuffer.TryReserveSegment(out Segment)"/>
    /// returns <c>false</c>.
    /// </para>
    /// </remarks>
    public struct Segment
    {
        /// <summary>
        /// Creates a segment with the given index, memory view, and release callback.
        /// </summary>
        /// <param name="segmentIndex">The 1-based index of the slot within the parent pool.</param>
        /// <param name="memory">The writable memory slice of the slot.</param>
        /// <param name="releaseMemoryCallback">Callback invoked by <see cref="Release"/> to return the slot to the pool.</param>
        public Segment(int segmentIndex, Memory<byte> memory, Action<int> releaseMemoryCallback)
        {
            SegmentIndex = segmentIndex;
            Memory = memory;
            // A hand-constructed segment has no separate whole-slot view: the given
            // view is treated as the whole slot. The pool overrides FullSlot on
            // reservation when the two views differ.
            FullSlot = memory;
            ReleaseMemoryCallback = releaseMemoryCallback;
        }

        /// <summary>The 1-based index of this segment within its parent <see cref="SegmentedBuffer"/>.</summary>
        /// <remarks>Set by <see cref="SegmentedBuffer.TryReserveSegment"/> during reservation. Should not be mutated after reservation.</remarks>
        public int SegmentIndex { get; internal set; }

        /// <summary>The writable memory slice assigned to this segment.</summary>
        public Memory<byte> Memory { get; internal set; }

        /// <summary>The whole-slot view (the slot's full size, before any pre-slice of
        /// <see cref="Memory"/>). Set by the pool at reservation. Internal because it
        /// reaches slot regions that <see cref="Memory"/> deliberately hides (e.g. the
        /// send length-prefix bytes ahead of the pre-sliced view).</summary>
        internal Memory<byte> FullSlot { get; set; }

        /// <summary>Callback to release this segment back to its parent pool.</summary>
        public Action<int> ReleaseMemoryCallback { get; internal set; }

        /// <summary>
        /// Returns this segment to its parent <see cref="SegmentedBuffer"/> pool.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call once per successful reservation. The release is idempotent — a racing
        /// double release is safe and has no additional effect.
        /// </para>
        /// <para>
        /// After the segment is released, its <see cref="Memory"/> slice is invalid and
        /// must not be accessed.
        /// </para>
        /// </remarks>
        public void Release()
        {
            ReleaseMemoryCallback?.Invoke(SegmentIndex);
        }

        /// <summary>
        /// Gets a writable view of the first <paramref name="length"/> bytes of this
        /// segment's slot, starting at the slot's beginning (ahead of any pre-slice of
        /// <see cref="Memory"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Intended for the owning transport's framing work: writing the 2-byte length
        /// prefix into the bytes ahead of the pre-sliced send view. Internal because the
        /// view reaches slot regions that <see cref="Memory"/> deliberately hides.
        /// </para>
        /// <para>
        /// No ownership check is performed: the caller holds the segment token, and the
        /// reservation flow guarantees the slot is allocated while the token is live.
        /// <paramref name="length"/> is range-checked against the slot size; a length
        /// above it returns false (the transport maps that to
        /// <c>TransportError.InvalidSegment</c>).
        /// </para>
        /// </remarks>
        internal bool TryGetMemory(int length, out Memory<byte> memory)
        {
            memory = default;
            if (length < 0 || length > FullSlot.Length)
                return false;
            memory = FullSlot.Slice(0, length);
            return true;
        }
    }
}
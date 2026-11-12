using DuneTransport.BufferManager;

namespace DuneTransport.Transport
{
    /// <summary>
    /// Segment pool sizing options for a <see cref="Transport"/>. Controls the size of
    /// each segment (which caps the maximum packet size) and the number of segments
    /// per pool. Defaults: 256-byte segments, 32 per pool.
    /// </summary>
    public sealed class TransportOptions
    {
        /// <summary>Size of each segment in bytes. Larger values allow larger packets.</summary>
        public int SegmentSize { get; set; } = 256;

        /// <summary>Number of segments in each send and receive pool.</summary>
        public int SegmentCount { get; set; } = 32;

        /// <summary>Creates a segment pool configured with these options (total array size = segment size × segment count).</summary>
        internal SegmentedBuffer CreatePool() => new SegmentedBuffer(SegmentSize * SegmentCount, SegmentCount);
    }
}

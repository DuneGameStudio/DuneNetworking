using System;

namespace DuneNetworking.ByteArrayManager
{
    public struct Segment
    {
        /// <summary>
        ///     The Segment Index Inside the SegmentedBuffer Instance that Produced it.
        /// </summary>
        public int SegmentIndex { get; set; }

        /// <summary>
        ///     Memory Instance of the Segment.
        /// </summary>
        /// <value></value>
        public Memory<byte> Memory { get; set; }

        /// <summary>
        ///     A Callback Registered Inside The Instance of the SegmentedBuffer That Produced The Segment.
        ///     It's To be Used to Release The Segment Freeing it Inside the SegmentedBuffer.
        /// </summary>
        /// <value></value>
        public Action<int> ReleaseMemoryCallback { get; set; }

        /// <summary>
        ///     A Call for the ReleaseMemoryCallback.
        /// </summary>
        public void Release()
        {
            ReleaseMemoryCallback(SegmentIndex);
        }
    }
}
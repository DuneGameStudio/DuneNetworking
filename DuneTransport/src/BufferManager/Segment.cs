using System;

namespace DuneTransport.BufferManager
{
    public struct Segment
    {
        public int SegmentIndex { get; set; }

        public Memory<byte> Memory { get; set; }
        
        public Action<int> ReleaseMemoryCallback { get; set; }

        public void Release()
        {
            ReleaseMemoryCallback(SegmentIndex);
        }
    }
}
using System;

namespace DuneTransport.BufferManager
{
    public class SegmentedBuffer
    {
        byte[] data;

        readonly int segmentSize;
        
        readonly int segmentCount;
        
        int freeFrom;

        int freeUpTo;

        public SegmentedBuffer(int arrayLength = 8192, int segmentCount = 32)
        {
            segmentSize = arrayLength/segmentCount;
            this.segmentCount  = segmentCount;

            data = new byte[arrayLength];

            freeFrom = 1;
            freeUpTo = segmentCount;
        }
        
        public bool TryReserveSegment(out Segment segment)
        {
            segment = new Segment();

            if (freeFrom == 0)
                return false;

            int segmentStart = (freeFrom - 1) * segmentSize;
            segment.SegmentIndex = freeFrom;
            segment.ReleaseMemoryCallback = ReleaseMemory;
            segment.Memory = data.AsMemory(segmentStart, segmentSize);

            if (freeFrom == freeUpTo)
                freeFrom = 0;
            else if (freeFrom == segmentCount)
                freeFrom = 1;
            else
                freeFrom++;

            return true;
        }

        public void ReleaseMemory(int segmentNumber)
        {
            freeUpTo = segmentNumber;

            if (freeFrom == 0)
            {
                freeFrom = segmentNumber;
            }
        }

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
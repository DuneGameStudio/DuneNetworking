using System;

namespace DuneTransport.ByteArrayManager
{
    public class SegmentedBuffer
    {
        /// <summary>
        ///     The byte array that holds all the data.
        /// </summary>
        byte[] data;

        /// <summary>
        ///     The size of each segment in bytes within the segmented buffer.
        /// </summary>
        int segmentSize;

        /// <summary>
        ///     Number of segments in the Byte Array
        /// </summary>
        int segmentCount => data.Length / segmentSize;

        /// <summary>
        ///     Index Of Fist Free Segment.
        /// </summary>
        int freeFrom;

        /// <summary>
        ///     Index Of Last Free Segment.
        /// </summary>
        int freeUpTo;

        public SegmentedBuffer(int arrayLength = 8192, int segmentSize = 256)
        {
            this.segmentSize = segmentSize;

            data = new byte[arrayLength];

            freeFrom = 1;
            freeUpTo = segmentCount;
        }

        /// <summary>
        ///     Creates and Reserves the Next Free Segment.
        /// </summary>
        /// <param name="segment">Segment Instance That Represents The Byte Range That is Reserved.</param>
        /// <param name="size">Size Of The Byte Range Needed, If it's not Specified i.e. still 0 the function uses the SegmentSize</param>
        /// <returns>true if the Reservation operation was Successful and false if it wasn't.</returns>
        public bool ReserveMemory(out Segment segment, int size = 0)
        {
            segment = new Segment();

            if (freeFrom == 0)
            {
                return false;
            }

            var nextSegmentStart = (freeFrom - 1) * segmentSize;

            segment.SegmentIndex = freeFrom;
            segment.ReleaseMemoryCallback = ReleaseMemory;

            if (size == 0)
            {
                segment.Memory = data.AsMemory(nextSegmentStart + 2, segmentSize - 2);
            }
            else
            {
                segment.Memory = data.AsMemory(nextSegmentStart, size);
            }

            if (freeFrom + 1 > segmentCount)
            {
                if (freeUpTo == segmentCount)
                {
                    freeUpTo = 0;
                    freeFrom = 0;
                    return false;
                }
                else if (freeUpTo >= 1)
                {
                    freeFrom = 1;
                    return true;
                }
            }
            else if (freeFrom + 1 > freeUpTo && freeFrom <= freeUpTo)
            {
                freeUpTo = 0;
                freeFrom = 0;
                return false;
            }

            freeFrom++;

            return true;
        }

        /// <summary>
        ///     Releases the Segment.
        /// </summary>
        public void ReleaseMemory(int segmentNumber)
        {
            freeUpTo = segmentNumber;

            if (freeFrom == 0)
            {
                freeFrom = segmentNumber;
            }
        }

        /// <summary>
        ///     Gets the Segment Memory with the specified Length.
        /// </summary>
        /// <param name="segmentNumber">Segment index in the byte array</param>
        /// <param name="length">length of the returned Memory</param>
        /// <returns>Memory</returns>
        public Memory<byte> GetRegisteredMemory(int segmentNumber, int length)
        {
            return data.AsMemory((segmentNumber - 1) * segmentSize, length + 2);
        }
    }
}

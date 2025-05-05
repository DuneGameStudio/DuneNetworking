using System;

namespace FramedNetworkingSolution.ByteArrayManager
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

        /// <summary>
        ///     
        /// </summary>
        /// <param name="arrayLength"></param>
        /// <param name="segmentSize"></param>
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
        /// <param name="segmentNumber"></param>
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

// using System;
//
// namespace FramedNetworkingSolution.ByteArrayManager
// {
//     public class SegmentedBuffer
//     {
//         /// <summary>
//         ///     The byte array that holds all the data.
//         /// </summary>
//         private readonly byte[] data;
//
//         /// <summary>
//         ///     The size of each segment in bytes within the segmented buffer.
//         /// </summary>
//         private readonly int segmentSize;
//
//         /// <summary>
//         ///     Base-2 logarithm of segmentSize, or -1 if not a power of 2.
//         /// </summary>
//         private readonly int log2SegmentSize = -1;
//
//         /// <summary>
//         ///     Number of segments in the Byte Array
//         /// </summary>
//         private readonly int segmentCount;
//
//         /// <summary>
//         ///     1-based Index Of the Next Free Segment to Allocate. 0 indicates buffer is full.
//         /// </summary>
//         private int freeFrom;
//
//         /// <summary>
//         ///     1-based Index Of the segment after the Last Freed Segment. Used to detect buffer full state.
//         /// </summary>
//         private int nextToLastFreed;
//
//         /// <summary>
//         ///     Initializes the segmented buffer.
//         /// </summary>
//         /// <param name="arrayLength">Total size of the buffer array. Will be rounded down to the nearest multiple of segmentSize.</param>
//         /// <param name="segmentSize">Size of each segment. Using a power of 2 enables optimizations.</param>
//         public SegmentedBuffer(int arrayLength = 8192, int segmentSize = 256)
//         {
//             if (segmentSize <= 0) throw new ArgumentOutOfRangeException(nameof(segmentSize), "Segment size must be positive.");
//             if (arrayLength < segmentSize) throw new ArgumentOutOfRangeException(nameof(arrayLength), "Array length must be at least segment size.");
//
//             this.segmentSize = segmentSize;
//             // Ensure arrayLength is a multiple of segmentSize for simplicity
//             segmentCount = arrayLength / segmentSize;
//             data = new byte[segmentCount * segmentSize];
//
//             // Initially, all segments are free.
//             // 'freeFrom' points to the first segment (index 1).
//             // 'nextToLastFreed' points *after* the last segment (index segmentCount + 1), conceptually.
//             // Using modulo arithmetic simplifies wrap-around logic.
//             freeFrom = 1;
//             nextToLastFreed = segmentCount + 1; // Effectively index 0 or segmentCount in modulo terms
//         }
//
//         /// <summary>
//         ///     Calculates the starting byte offset for a given segment index.
//         /// </summary>
//         /// <param name="segmentIndex">1-based segment index.</param>
//         /// <returns>0-based byte offset.</returns>
//         private int GetSegmentOffset(int segmentIndex)
//         {
//             if (log2SegmentSize != -1)
//             {
//                 // Optimized path: Use bitwise shift if segmentSize is a power of 2
//                 return (segmentIndex - 1) << log2SegmentSize;
//             }
//             
//             // Standard path: Use multiplication
//             return (segmentIndex - 1) * segmentSize;
//         }
//
//         /// <summary>
//         ///     Creates and Reserves the Next Free Segment.
//         /// </summary>
//         /// <param name="segment">Segment Instance That Represents The Byte Range That is Reserved.</param>
//         /// <param name="size">
//         ///     Optional specific size for the Memory slice within the segment.
//         ///     If 0 or not specified, a default slice (offset by 2, length segmentSize - 2) is used.
//         ///     If specified, the slice starts at the segment beginning with the given size.
//         ///     The provided size MUST NOT exceed the segmentSize.
//         /// </param>
//         /// <returns>true if the Reservation operation was Successful and false if the buffer is full.</returns>
//         public bool ReserveMemory(out Segment segment, int size = 0)
//         {
//             segment = default; // Use default for struct
//
//             // Check if the buffer is full. This happens when 'freeFrom' catches up to 'nextToLastFreed'.
//             // The modulo operation handles the wrap-around case naturally.
//             if (freeFrom == nextToLastFreed) // Simplified check for full buffer
//             {
//                 return false; // Buffer is full
//             }
//
//             int currentSegmentIndex = freeFrom;
//             int nextSegmentStart = GetSegmentOffset(currentSegmentIndex);
//
//             segment.SegmentIndex = currentSegmentIndex;
//             segment.ReleaseMemoryCallback = ReleaseMemory;
//
//             if (size == 0)
//             {
//                  // Ensure we don't create negative length slice if segmentSize is small
//                 int defaultOffset = 2;
//                 int defaultLength = segmentSize > defaultOffset ? segmentSize - defaultOffset : 0;
//                 segment.Memory = data.AsMemory(nextSegmentStart + defaultOffset, defaultLength);
//             }
//             else
//             {
//                  // Ensure requested size doesn't exceed segment capacity
//                 if ((uint)size > (uint)segmentSize) // Use uint comparison for combined >= 0 && <= segmentSize check
//                 {
//                      // Option 1: Throw exception for invalid size
//                      // throw new ArgumentOutOfRangeException(nameof(size), $"Requested size {size} exceeds segment size {segmentSize}.");
//                      // Option 2: Clamp the size (potential for silent errors)
//                      size = segmentSize;
//                      // Option 3: Return false (treat as allocation failure)
//                      // return false;
//                 }
//                  // Use requested size, starting from the beginning of the segment
//                  segment.Memory = data.AsMemory(nextSegmentStart, size);
//             }
//
//             // Advance freeFrom pointer, wrapping around if necessary
//             freeFrom++;
//             if (freeFrom > segmentCount)
//             {
//                 freeFrom = 1; // Wrap around to the first segment
//             }
//
//             return true; // Allocation succeeded
//         }
//
//         /// <summary>
//         ///     Releases a previously reserved segment, making it available for reuse.
//         /// </summary>
//         /// <param name="segmentIndex">The 1-based index of the segment to release.</param>
//         public void ReleaseMemory(int segmentIndex)
//         {
//             // In this simplified model, releasing just updates the 'nextToLastFreed' marker.
//             // This assumes segments are released roughly in the order they are allocated.
//             // For more complex scenarios (out-of-order release), a free list/queue/bitmap is better.
//
//             // Basic validation
//              if (segmentIndex < 1 || segmentIndex > segmentCount)
//              {
//                   // Handle invalid index - log error? Throw? Ignore?
//                   return;
//              }
//
//             // Update the marker for the next segment after the one just freed.
//             // This relies on the assumption of mostly sequential release.
//              nextToLastFreed = segmentIndex + 1;
//              if (nextToLastFreed > segmentCount)
//              {
//                  nextToLastFreed = 1; // Wrap around
//              }
//
//              // Potential Improvement: If this released segment *was* the one 'freeFrom' was blocked by,
//              // we could potentially signal availability if needed (e.g., for waiting threads).
//              // However, the current design doesn't explicitly support blocking waits.
//         }
//
//
//         /// <summary>
//         ///     Gets a Memory slice corresponding to a previously reserved segment, potentially with a different length.
//         ///     Note: This method bypasses the normal reservation state check. Use with caution.
//         /// </summary>
//         /// <param name="segmentNumber">1-based Segment index in the byte array</param>
//         /// <param name="length">Desired length of the returned Memory slice.</param>
//         /// <returns>Memory slice.</returns>
//         public Memory<byte> GetRegisteredMemory(int segmentNumber, int length)
//         {
//             // Basic validation
//             if (segmentNumber < 1 || segmentNumber > segmentCount)
//             {
//              throw new ArgumentOutOfRangeException(nameof(segmentNumber));
//             }
//             if ((uint)length > (uint)segmentSize) // Allow length 0, disallow > segmentSize
//             {
//              throw new ArgumentOutOfRangeException(nameof(length), $"Requested length {length} exceeds segment size {segmentSize}.");
//             }
//
//             int segmentStart = GetSegmentOffset(segmentNumber);
//
//             // Note: The original added +2 offset here, which seems inconsistent
//             // with ReserveMemory when a specific size is requested.
//             // Assuming the caller wants memory starting from the segment beginning.
//             // If the +2 offset is intentional for header space, it should be consistent.
//             // Reverting to the original logic's offset for consistency:
//             int offset = 2;
//             if (length + offset > segmentSize) {
//              // Avoid going past the segment boundary if length + offset is too large
//              // This condition indicates potential misuse or conflicting logic.
//              // Consider throwing or adjusting length based on intended use.
//              length = segmentSize - offset; // Clamp length?
//             }
//             return data.AsMemory(segmentStart + offset, length);
//         }
//     }
// }
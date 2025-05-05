using System.Diagnostics;
using FramedNetworkingSolution.Transport.Interface;

namespace FramedNetworkingSolution.ByteArrayManager
{
    public interface SegmentManager
    {
        /// <summary>
        ///     Identifier representing a unique ID associated with a specific segment or operation.
        /// </summary>
        /// <value>
        ///     The ID is a 16-bit unsigned integer that can be used to track or differentiate between
        ///     various segments or operations in a networking solution.
        /// </value>
        ushort Id { get; set; }

        /// <summary>
        ///     Represents a segment used for managing a portion of data in the buffer system.
        /// </summary>
        /// <value>
        ///     The segment is a structure that holds information about its memory index and allocated space,
        ///     facilitating operations such as serialization, deserialization, and memory management within the system.
        /// </value>
        Segment segment { get; set; }

        /// <summary>
        ///     The Number of Actually Used Bytes Of The Segment.
        /// </summary>
        /// <value></value>
        public int PacketSize { get; set; }

        /// <summary>
        ///     Meant To be Overridden To Implement Data Deserialization.
        /// </summary>
        void OnDeserialize();

        /// <summary>
        ///     Meant To be Overridden To Implement Data Serialization.
        /// </summary>
        void OnSerialize();

        /// <summary>
        ///     Sends data asynchronously using the provided transport object and releases the associated segment.
        /// </summary>
        /// <param name="transport">The transport object responsible for handling the send operation.</param>
        void OnSend(ITransport transport)
        {
            transport.SendAsync(transport.sendBuffer.GetRegisteredMemory(segment.SegmentIndex, PacketSize));
            segment.Release();
        }

        /// <summary>
        ///     Serializes data into a newly reserved segment within the provided segmented buffer
        ///     and prepares it for further operations.
        /// </summary>
        /// <param name="segmentedBuffer">
        ///     The segmented buffer instance where the new segment will be reserved
        ///     and the data will be serialized into.
        /// </param>
        void Serialize(SegmentedBuffer segmentedBuffer)
        {
            if (segmentedBuffer.ReserveMemory(out Segment newSegment))
            {
                Debug.WriteLine("New Segment");

                segment = newSegment;
            }
            
            OnSerialize();
        }

        /// <summary>
        ///     Calls OnDeserialize and Releases The Segment.
        /// </summary>
        void Deserialize()
        {
            OnDeserialize();
            segment.Release();
        }
    }
}
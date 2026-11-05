using System;

namespace DuneTransport.BufferManager.Interface
{
    /// <summary>
    /// Result of a serialization lifecycle orchestrated by <see cref="ISegmentManager.Serialize"/>.
    /// </summary>
    /// <remarks>
    /// Distinguishes three outcomes that were previously collapsed into a single <c>bool</c>
    /// (<c>true</c>/<c>false</c>), enabling callers to report the correct <see cref="PacketError"/>.
    /// </remarks>
    public enum SerializeResult
    {
        /// <summary>
        /// Serialization succeeded: a segment was reserved and <see cref="ISegmentManager.OnSerialize"/>
        /// returned <c>true</c>. Ownership of <see cref="ISegmentManager.segment"/> has transferred
        /// to the caller.
        /// </summary>
        Ok,

        /// <summary>
        /// No segment was available from the transport's send pool.
        /// <see cref="ISegmentManager.OnSerialize"/> was not called. The segment is not reserved.
        /// </summary>
        PoolExhausted,

        /// <summary>
        /// A segment was reserved but <see cref="ISegmentManager.OnSerialize"/> returned
        /// <c>false</c> (the packet could not be written). The reserved segment was released.
        /// </summary>
        SerializeFailed
    }

    /// <summary>
    /// Result of a deserialization lifecycle orchestrated by <see cref="ISegmentManager.Deserialize"/>.
    /// </summary>
    /// <remarks>
    /// Distinguishes the two outcomes previously collapsed into a single <c>bool</c>.
    /// </remarks>
    public enum DeserializeResult
    {
        /// <summary>
        /// Deserialization succeeded: <see cref="ISegmentManager.OnDeserialize"/> returned
        /// <c>true</c>. The segment was released.
        /// </summary>
        Ok,

        /// <summary>
        /// <see cref="ISegmentManager.OnDeserialize"/> returned <c>false</c> (the packet could not
        /// be read). The segment was released.
        /// </summary>
        DeserializeFailed
    }
}
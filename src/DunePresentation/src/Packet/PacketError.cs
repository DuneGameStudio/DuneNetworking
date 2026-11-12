namespace DunePresentation.Packet
{
    /// <summary>
    /// Reason codes returned by the peer's serialize and deserialize calls when they fail.
    /// </summary>
    public enum PacketError
    {
        /// <summary>No error — the operation succeeded. The value returned in the error member on
        /// success; a result is successful iff its error member equals this value.</summary>
        None,

        /// <summary>The segment pool had no free segment when one was needed during serialization.</summary>
        PoolExhausted,

        /// <summary>The packet's fields could not be written during serialization.</summary>
        SerializationError,

        /// <summary>
        /// Reserved for the decrypt step. Not produced by the current flow: a
        /// decryptor that throws propagates to the caller (it is contracted never
        /// to throw) instead of being mapped to this code.
        /// </summary>
        DecryptError,

        /// <summary>The packet's fields could not be read during deserialization.</summary>
        DeserializeError,

        /// <summary>The packet registry failed to resolve the received packet ID.</summary>
        RegistryError
    }
}

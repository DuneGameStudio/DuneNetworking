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

        /// <summary>Underlying segment pool had no free segment when one was needed during serialization.</summary>
        PoolExhausted,

        /// <summary>Serialization failed (buffer write, field serialization, or encryptor exception during Serialize).</summary>
        SerializationError,

        /// <summary>Decrypt failed (encryptor threw during DecryptAndDeserialize).</summary>
        DecryptError,

        /// <summary>Deserialize failed (packet.ReadFieldsFromBuffer returned false during DecryptAndDeserialize).</summary>
        DeserializeError,

        /// <summary>The packet registry failed to resolve the received packet ID.</summary>
        RegistryError
    }
}

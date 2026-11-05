using System;

namespace DunePresentation.Encryption.Interface
{
    /// <summary>
    /// Encrypts and decrypts packet data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations must support in-place operations where source and destination
    /// spans reference the same memory region. The encryptor is responsible for any
    /// state management (keys, IVs, nonces) required for correct operation.
    /// </para>
    /// <para>
    /// Used by <see cref="Peer"/> during serialization (encrypt) and deserialization (decrypt).
    /// </para>
    /// </remarks>
    public interface IPacketEncryptor
    {
        /// <summary>
        /// Encrypts data from <paramref name="source"/> and writes to <paramref name="destination"/>.
        /// </summary>
        /// <param name="source">The plaintext data.</param>
        /// <param name="destination">The destination buffer (must be at least as large as <paramref name="source"/>).</param>
        void Encrypt(ReadOnlySpan<byte> source, Span<byte> destination);

        /// <summary>
        /// Decrypts data from <paramref name="source"/> and writes to <paramref name="destination"/>.
        /// </summary>
        /// <param name="source">The ciphertext data.</param>
        /// <param name="destination">The destination buffer (must be at least as large as <paramref name="source"/>).</param>
        void Decrypt(ReadOnlySpan<byte> source, Span<byte> destination);
    }
}

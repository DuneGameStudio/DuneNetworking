using System;

namespace DunePresentation.Encryption.Interface
{
    /// <summary>
    /// Encrypts and decrypts packet data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations must support in-place operations, where the source and
    /// destination spans reference the same memory region. The encryptor owns any
    /// state management (keys, initialization vectors, nonces) required for
    /// correct operation.
    /// </para>
    /// <para>
    /// Used by <see cref="Peer"/> during serialization (encrypt) and deserialization
    /// (decrypt). Implementations are contracted never to throw: the peer's pipeline
    /// runs them without containment, so a throw propagates to the caller and leaks
    /// the segment being processed.
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

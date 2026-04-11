using System;

namespace DunePresentation.Encryption.Interface
{
    public interface IPacketEncryptor
    {
        int Encrypt(ReadOnlySpan<byte> source, Span<byte> destination);

        int Decrypt(ReadOnlySpan<byte> source, Span<byte> destination);
    }
}

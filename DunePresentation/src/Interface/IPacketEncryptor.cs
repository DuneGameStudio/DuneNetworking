using System;

namespace DunePresentation.Interface
{
    public interface IPacketEncryptor
    {
        int Encrypt(ReadOnlySpan<byte> source, Span<byte> destination);

        int Decrypt(ReadOnlySpan<byte> source, Span<byte> destination);
    }
}

using System.Linq;
using DunePresentation.Encryption.Interface;
using DemoPackets;
using Xunit;

namespace DunePresentation.Tests
{
    public class EncryptionTests
    {
        public class PassThroughEncryptorTests
        {
            [Fact]
            public void Encrypt_PreservesData()
            {
                var enc = new PassThroughEncryptor();
                var src = new byte[] { 1, 2, 3, 4, 5 };
                var dest = new byte[5];
                enc.Encrypt(src, dest);
                Assert.Equal(src, dest);
            }

            [Fact]
            public void Decrypt_PreservesData()
            {
                var enc = new PassThroughEncryptor();
                var src = new byte[] { 10, 20, 30 };
                var dest = new byte[3];
                enc.Decrypt(src, dest);
                Assert.Equal(src, dest);
            }

            [Fact]
            public void InPlace_EncryptDecrypt_RoundTrip()
            {
                var enc = new PassThroughEncryptor();
                var buf = new byte[] { 1, 2, 3 };
                enc.Encrypt(buf, buf);
                enc.Decrypt(buf, buf);
                Assert.Equal(new byte[] { 1, 2, 3 }, buf);
            }
        }

        public class XorEncryptorTests
        {
            [Fact]
            public void EncryptDecrypt_RoundTrip()
            {
                var enc = new XorEncryptor(42);
                var src = new byte[] { 1, 2, 3, 4, 5 };
                var encrypted = new byte[5];
                enc.Encrypt(src, encrypted);

                // Data should be transformed
                Assert.NotEqual(src[0], encrypted[0]);

                var decrypted = new byte[5];
                enc.Decrypt(encrypted, decrypted);
                Assert.Equal(src, decrypted);
            }

            [Fact]
            public void InPlace_RoundTrip()
            {
                var enc = new XorEncryptor(0xAB);
                var buf = new byte[] { 10, 20, 30, 40, 50 };
                enc.Encrypt(buf, buf);
                enc.Decrypt(buf, buf);
                Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, buf);
            }

            [Fact]
            public void DifferentKey_ProducesDifferentOutput()
            {
                var src = new byte[] { 1, 2, 3 };
                var enc1 = new XorEncryptor(42);
                var enc2 = new XorEncryptor(99);

                var out1 = new byte[3];
                var out2 = new byte[3];
                enc1.Encrypt(src, out1);
                enc2.Encrypt(src, out2);

                Assert.False(out1.SequenceEqual(out2));
            }
        }
    }
}
using System;
using DunePresentation.Interface;

namespace DunePresentation
{
    public struct PeerConfiguration
    {
        public TimeSpan RequestTimeout { get; set; }

        public IPacketEncryptor? Encryptor { get; set; }

        public static PeerConfiguration Default => new PeerConfiguration
        {
            RequestTimeout = TimeSpan.FromSeconds(5)
        };
    }
}

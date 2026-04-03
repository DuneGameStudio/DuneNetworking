using System;
using System.Buffers;
using System.Collections.Generic;

namespace DuneNetworking.Packets
{
    /// <summary>
    ///     Result of a Handler pass.
    /// </summary>
    public enum ExtractionResultType
    {
        /// <summary>Zero or more packets extracted successfully.</summary>
        Ok,

        /// <summary>Length prefix was zero or exceeded maximum. Protocol violation.</summary>
        Error
    }

    public readonly struct ExtractionResult
    {
        public readonly ExtractionResultType Type;
        public readonly int BytesConsumed;

        private ExtractionResult(ExtractionResultType type, int bytesConsumed)
        {
            Type = type;
            BytesConsumed = bytesConsumed;
        }

        public static ExtractionResult Ok(int bytesConsumed) =>
            new ExtractionResult(ExtractionResultType.Ok, bytesConsumed);

        public static ExtractionResult Error() =>
            new ExtractionResult(ExtractionResultType.Error, 0);
    }

    /// <summary>
    ///     Stateless packet Extractor. Extracts length-prefixed frames from a
    ///     ReadOnlySequence without copying any data.
    ///     
    ///     Wire format per frame:
    ///       [2 bytes, little-endian ushort: payload length] [payload bytes]
    /// </summary>
    public static class BufferHandler
    {
        /// <summary>
        ///     Maximum allowed payload size. Frames claiming a larger payload
        ///     are treated as protocol violations.
        /// </summary>
        public const int MaxPayloadSize = 65534;

        /// <summary>
        ///     Extracts all complete packet frames from the readable sequence.
        ///     
        ///     Returns Ok with the total bytes consumed (headers + payloads).
        ///     Returns Error if a length prefix is invalid.
        ///     
        ///     On Ok, output contains zero or more extracted packets whose
        ///     Payload sequences point into the original readable memory.
        /// </summary>
        public static ExtractionResult ExtractPackets(
            ReadOnlySequence<byte> readable,
            List<(ReadOnlySequence<byte>, int)> output)
        {
            output.Clear();

            var reader = new SequenceReader<byte>(readable);
            int totalConsumed = 0;

            while (true)
            {
                // Need at least 2 bytes for the length header
                if (reader.Remaining < 2)
                    break;

                // Read payload length as little-endian ushort
                if (!reader.TryReadLittleEndian(out short rawLength))
                    break;

                ushort payloadLength = (ushort)rawLength;

                // Validate
                if (payloadLength == 0 || payloadLength > MaxPayloadSize)
                    return ExtractionResult.Error();

                // Check if full payload has arrived
                if (reader.Remaining < payloadLength)
                {
                    // Partial payload — rewind past the header we just consumed
                    reader.Rewind(2);
                    break;
                }

                // Slice payload from the original sequence (zero-copy)
                SequencePosition payloadStart = reader.Position;
                ReadOnlySequence<byte> payload = readable.Slice(payloadStart, payloadLength);

                // Advance past payload
                reader.Advance(payloadLength);

                int frameSize = 2 + payloadLength;
                totalConsumed += frameSize;

                output.Add((payload, frameSize));
            }

            return ExtractionResult.Ok(totalConsumed);
        }
    }
}

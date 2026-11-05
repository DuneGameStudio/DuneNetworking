using System;
using DuneTransport.BufferManager;
using DuneTransport.BufferManager.Interface;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;
using Xunit;

namespace DuneTransport.Tests
{
    /// <summary>
    /// Tests for ISegmentManager default interface methods (Serialize/Deserialize).
    /// These cover the 0% coverage gap in ISegmentManager.cs:79-119.
    /// </summary>
    public class ISegmentManagerTests
    {
        // Test implementation of ISegmentManager for testing default methods
        private class TestSegmentManager : ISegmentManager
        {
            public Segment segment { get; set; }
            public int PacketSize { get; set; }
            public bool OnSerializeCalled { get; private set; }
            public bool OnDeserializeCalled { get; private set; }
            public bool OnSerializeResult { get; set; } = true;
            public bool OnDeserializeResult { get; set; } = true;

            public bool OnSerialize()
            {
                OnSerializeCalled = true;
                return OnSerializeResult;
            }

            public bool OnDeserialize()
            {
                OnDeserializeCalled = true;
                return OnDeserializeResult;
            }
        }

        // Minimal transport that can reserve segments
        private class TestTransport : ITransport
        {
            private readonly SegmentedBuffer _buffer = new SegmentedBuffer(1024, 4);
            public bool IsConnected => true;
            public bool IsDisposed => false;
            public bool ReceiveArmed => false;
            public bool SendArmed => false;
            public Segment? LastReservedSegment { get; private set; }

            public event Action<ITransport>? OnPacketSent;
            public event Action<ITransport, Segment, TransportError>? OnPacketSendFailed;
            public event Action<ITransport, Segment>? OnPacketReceived;
            public event Action<ITransport, TransportError>? OnPacketReceiveFailed;

            public void ReceiveAsync() { }
            public void SendAsync(Segment packet, int packetSize) { }

            public bool TryReserveSendPacket(out Segment segment)
            {
                if (_buffer.TryReserveSegment(out segment))
                {
                    LastReservedSegment = segment;
                    return true;
                }
                segment = default;
                return false;
            }

            public void Dispose() { }
        }

        [Fact]
        public void ISegmentManager_Serialize_Success_CallsOnSerializeAndReserves()
        {
            // ARRANGE
            var manager = new TestSegmentManager();
            var transport = new TestTransport();

            // ACT - Cast to interface to access default methods
            var result = ((ISegmentManager)manager).Serialize(transport);

            // ASSERT
            Assert.Equal(SerializeResult.Ok, result);
            Assert.True(manager.OnSerializeCalled, "OnSerialize should be called");
            // The segment should be set to the reserved one, ready for the caller to post-process.
            Assert.True(manager.segment.SegmentIndex > 0, "Segment should be reserved and assigned");
            Assert.Equal(transport.LastReservedSegment?.SegmentIndex, manager.segment.SegmentIndex);
        }

        [Fact]
        public void ISegmentManager_Serialize_OnSerializeFails_ReturnsSerializeFailedAndReleasesSegment()
        {
            // ARRANGE
            var manager = new TestSegmentManager { OnSerializeResult = false };
            var transport = new TestTransport();

            // ACT
            var result = ((ISegmentManager)manager).Serialize(transport);

            // ASSERT
            Assert.Equal(SerializeResult.SerializeFailed, result);
            Assert.True(manager.OnSerializeCalled, "OnSerialize should still be called");
            // Segment should be released back to pool (FreeCount back to 4)
            var buf = (SegmentedBuffer)typeof(TestTransport).GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(transport)!;
            Assert.Equal(4, buf.FreeCount);
        }

        [Fact]
        public void ISegmentManager_Serialize_PoolExhausted_ReturnsPoolExhausted()
        {
            // ARRANGE - Exhaust the pool
            var transport = new TestTransport();
            for (int i = 0; i < 4; i++)
                transport.TryReserveSendPacket(out _);

            var manager = new TestSegmentManager();

            // ACT
            var result = ((ISegmentManager)manager).Serialize(transport);

            // ASSERT
            Assert.Equal(SerializeResult.PoolExhausted, result);
            Assert.False(manager.OnSerializeCalled, "OnSerialize should not be called when no segment available");
        }

        [Fact]
        public void ISegmentManager_Serialize_TransportDisposed_ReturnsPoolExhausted()
        {
            // ARRANGE
            var manager = new TestSegmentManager();
            var transport = new DisposedTransport();

            // ACT
            var result = ((ISegmentManager)manager).Serialize(transport);

            // ASSERT
            Assert.Equal(SerializeResult.PoolExhausted, result);
        }

        [Fact]
        public void ISegmentManager_Deserialize_Success_CallsOnDeserializeAndReleases()
        {
            // ARRANGE
            var buffer = new SegmentedBuffer(1024, 4);
            buffer.TryReserveSegment(out var segment);
            // Simulate 10-byte packet by creating a new segment with sliced memory
            segment = new Segment(segment.SegmentIndex, segment.Memory.Slice(0, 10), segment.ReleaseMemoryCallback);

            var manager = new TestSegmentManager
            {
                segment = segment,
                PacketSize = 10
            };

            Segment? beforeCallbackSeg = null;
            int beforeCallbackSize = -1;

            // ACT
            var result = ((ISegmentManager)manager).Deserialize((seg, size) =>
            {
                beforeCallbackSeg = seg;
                beforeCallbackSize = size;
            });

            // ASSERT
            Assert.Equal(DeserializeResult.Ok, result);
            Assert.True(manager.OnDeserializeCalled, "OnDeserialize should be called");
            Assert.Equal(segment.SegmentIndex, beforeCallbackSeg?.SegmentIndex);
            Assert.Equal(10, beforeCallbackSize);
            // Segment should be released back to pool
            Assert.Equal(4, buffer.FreeCount);
        }

        [Fact]
        public void ISegmentManager_Deserialize_OnDeserializeFails_ReturnsDeserializeFailedAndReleasesSegment()
        {
            // ARRANGE
            var buffer = new SegmentedBuffer(1024, 4);
            buffer.TryReserveSegment(out var segment);
            segment = new Segment(segment.SegmentIndex, segment.Memory.Slice(0, 10), segment.ReleaseMemoryCallback);

            var manager = new TestSegmentManager
            {
                segment = segment,
                PacketSize = 10,
                OnDeserializeResult = false // Simulate failure
            };

            // ACT
            var result = ((ISegmentManager)manager).Deserialize((_, _) => { });

            // ASSERT
            Assert.Equal(DeserializeResult.DeserializeFailed, result);
            Assert.True(manager.OnDeserializeCalled, "OnDeserialize should be called");
            // Segment MUST be released even on failure
            Assert.Equal(4, buffer.FreeCount);
        }

        [Fact]
        public void ISegmentManager_Deserialize_CallbackReceivesSegmentAndSize()
        {
            // ARRANGE
            var buffer = new SegmentedBuffer(1024, 4);
            buffer.TryReserveSegment(out var segment);
            segment = new Segment(segment.SegmentIndex, segment.Memory.Slice(0, 42), segment.ReleaseMemoryCallback);

            var manager = new TestSegmentManager
            {
                segment = segment,
                PacketSize = 42
            };

            // ACT
            bool callbackInvoked = false;
            ((ISegmentManager)manager).Deserialize((seg, size) =>
            {
                callbackInvoked = true;
                Assert.Equal(segment.SegmentIndex, seg.SegmentIndex);
                Assert.Equal(42, size);
            });

            // ASSERT
            Assert.True(callbackInvoked, "BeforeDeserialize callback should be invoked");
        }

        private class DisposedTransport : ITransport
        {
            public bool IsConnected => false;
            public bool IsDisposed => true;
            public bool ReceiveArmed => false;
            public bool SendArmed => false;

            public event Action<ITransport>? OnPacketSent;
            public event Action<ITransport, Segment, TransportError>? OnPacketSendFailed;
            public event Action<ITransport, Segment>? OnPacketReceived;
            public event Action<ITransport, TransportError>? OnPacketReceiveFailed;

            public void ReceiveAsync() { }
            public void SendAsync(Segment packet, int packetSize) { }
            public bool TryReserveSendPacket(out Segment segment)
            {
                segment = default;
                return false;
            }
            public void Dispose() { }
        }
    }
}
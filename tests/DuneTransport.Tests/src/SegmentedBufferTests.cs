using System;
using System.Threading;
using System.Threading.Tasks;
using DuneTransport.BufferManager;
using Xunit;

namespace DuneTransport.Tests
{
    public class SegmentedBufferTests
    {
        [Fact]
        public void SegmentedBuffer_Construction_DefaultValues()
        {
            var buf = new SegmentedBuffer();
            Assert.Equal(8192 / 32, buf.SegmentSize);
            Assert.Equal(32, buf.SegmentCount);
            Assert.Equal(32, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_Construction_ZeroSegmentCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedBuffer(1024, 0));
        }

        [Fact]
        public void SegmentedBuffer_Construction_NegativeArrayLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => new SegmentedBuffer(-100, 4));
        }

        [Fact]
        public void SegmentedBuffer_Construction_ArrayLengthLessThanCount_Throws()
        {
            Assert.Throws<ArgumentException>(() => new SegmentedBuffer(2, 4));
        }

        [Fact]
        public void SegmentedBuffer_Construction_CustomValues()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.Equal(256, buf.SegmentSize);
            Assert.Equal(4, buf.SegmentCount);
            Assert.Equal(4, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_TryReserveSegment_Success()
        {
            var buf = new SegmentedBuffer(1024, 4);
            bool result = buf.TryReserveSegment(out var segment);

            Assert.True(result);
            Assert.True(segment.SegmentIndex >= 1);
            Assert.True(segment.SegmentIndex <= 4);
            Assert.Equal(256, segment.Memory.Length);
            Assert.Equal(3, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_TryReserveSegment_AllSegments()
        {
            var buf = new SegmentedBuffer(1024, 4);
            var segments = new Segment[4];

            for (int i = 0; i < 4; i++)
            {
                Assert.True(buf.TryReserveSegment(out segments[i]));
                Assert.Equal(3 - i, buf.FreeCount);
            }
            Assert.Equal(0, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_TryReserveSegment_PoolExhausted()
        {
            var buf = new SegmentedBuffer(1024, 4);

            for (int i = 0; i < 4; i++)
                buf.TryReserveSegment(out _);

            Assert.False(buf.TryReserveSegment(out var segment));
            Assert.Equal(0, segment.SegmentIndex);
        }

        [Fact]
        public void SegmentedBuffer_ReleaseMemory_NormalRelease()
        {
            var buf = new SegmentedBuffer(1024, 4);
            buf.TryReserveSegment(out var segment);
            Assert.Equal(3, buf.FreeCount);

            buf.ReleaseMemory(segment.SegmentIndex);
            Assert.Equal(4, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_ReleaseMemory_Idempotent()
        {
            var buf = new SegmentedBuffer(1024, 4);
            buf.TryReserveSegment(out var segment);
            buf.ReleaseMemory(segment.SegmentIndex);
            Assert.Equal(4, buf.FreeCount);

            // Second release should not double-enqueue
            buf.ReleaseMemory(segment.SegmentIndex);
            Assert.Equal(4, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_ReleaseMemory_OutOfRange_Zero()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => buf.ReleaseMemory(0));
        }

        [Fact]
        public void SegmentedBuffer_ReleaseMemory_OutOfRange_NPlus1()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => buf.ReleaseMemory(5));
        }

        [Fact]
        public void SegmentedBuffer_ReleaseMemory_ViaSegmentCallback()
        {
            var buf = new SegmentedBuffer(1024, 4);
            buf.TryReserveSegment(out var segment);
            Assert.Equal(3, buf.FreeCount);

            segment.Release();
            Assert.Equal(4, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_GetRegisteredMemory_ValidSegment()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.True(buf.GetRegisteredMemory(1, 100, out var mem));
            Assert.Equal(100, mem.Length);
        }

        [Fact]
        public void SegmentedBuffer_GetRegisteredMemory_FullSegmentSize()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.True(buf.GetRegisteredMemory(2, 256, out var mem));
            Assert.Equal(256, mem.Length);
        }

        [Fact]
        public void SegmentedBuffer_GetRegisteredMemory_OutOfRange()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.False(buf.GetRegisteredMemory(0, 100, out _));
            Assert.False(buf.GetRegisteredMemory(5, 100, out _));
        }

        [Fact]
        public void SegmentedBuffer_GetRegisteredMemory_ExceedsSegmentSize()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.False(buf.GetRegisteredMemory(1, 257, out _));
        }

        [Fact]
        public void SegmentedBuffer_GetRegisteredMemory_ZeroLength()
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.True(buf.GetRegisteredMemory(1, 0, out var mem));
            Assert.Equal(0, mem.Length);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-100)]
        public void SegmentedBuffer_GetRegisteredMemory_NegativeLength(int length)
        {
            var buf = new SegmentedBuffer(1024, 4);
            Assert.False(buf.GetRegisteredMemory(1, length, out _));
        }

        [Fact]
        public void SegmentedBuffer_ReleaseAndReserve_Cycle()
        {
            var buf = new SegmentedBuffer(1024, 4);
            buf.TryReserveSegment(out var seg1);
            int idx = seg1.SegmentIndex;
            buf.ReleaseMemory(idx);
            Assert.Equal(4, buf.FreeCount);

            Assert.True(buf.TryReserveSegment(out var seg2));
            Assert.Equal(3, buf.FreeCount);
        }

        [Fact]
        public void SegmentedBuffer_ConcurrentReserveRelease_NoDoubleAllocate()
        {
            var buf = new SegmentedBuffer(8192, 32);
            int numThreads = 8;
            int opsPerThread = 100;
            var barrier = new Barrier(numThreads);

            Parallel.For(0, numThreads, _ =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    if (buf.TryReserveSegment(out var seg))
                    {
                        buf.ReleaseMemory(seg.SegmentIndex);
                    }
                }
            });

            // All segments should be free after balanced reserve/release
            Assert.Equal(32, buf.FreeCount);
        }

        /// <summary>
        /// Verifies that concurrent release of the same segment from multiple threads
        /// is handled correctly (idempotent release via Interlocked.Exchange).
        /// </summary>
        [Fact]
        public async Task SegmentedBuffer_ConcurrentDoubleRelease_SameSegment()
        {
            var buf = new SegmentedBuffer(1024, 32);
            Assert.True(buf.TryReserveSegment(out var seg));

            int numThreads = 8;
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();

            for (int i = 0; i < numThreads; i++)
            {
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try { buf.ReleaseMemory(seg.SegmentIndex); } catch { }
                }));
            }

            try { await System.Threading.Tasks.Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

            // Segment should be released exactly once (idempotent)
            Assert.Equal(32, buf.FreeCount);
        }
    }
}
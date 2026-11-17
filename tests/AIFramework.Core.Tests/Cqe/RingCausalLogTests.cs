using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Core.Cqe;
using Xunit;
using Xunit.Abstractions;

namespace AIFramework.Core.Tests.Cqe
{
    /// <summary>
    /// <see cref="RingCausalLog"/> 与 <see cref="CausalTagRegistry"/> 的行为契约：
    /// 环形覆盖后的逻辑下标语义、序号单调性、构造参数校验、Tag 映射以及热路径零分配。
    /// </summary>
    public sealed class RingCausalLogTests
    {
        private readonly ITestOutputHelper _output;

        public RingCausalLogTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RingBuffer_OverwritesOldestAndKeepsLogicalIndexOfNewest()
        {
            RingCausalLog log = new RingCausalLog(4);

            for (int i = 0; i < 10; i++)
            {
                log.Record(100 + i, 1, CausalOutcome.Success);
            }

            // Count 是累计写入数，可以超过容量。
            Assert.Equal(10, log.Count);
            Assert.Equal(4, log.Capacity);

            // [0] 永远是当前最旧记录（序号 7），[Capacity-1] 是最新记录（序号 10）。
            Assert.Equal(7L, log[0].SequenceId);
            Assert.Equal(10L, log[3].SequenceId);
            // 第 n 次写入（n 从 1 起）的 messageTagId = 100 + (n - 1)，因此最旧一条是 106、最新一条是 109。
            Assert.Equal(106, log[0].MessageTagId);
            Assert.Equal(109, log[3].MessageTagId);

            // 序号在有效区间内严格单调递增。
            for (int i = 1; i < log.Capacity; i++)
            {
                Assert.True(log[i].SequenceId > log[i - 1].SequenceId);
            }

            // 有效范围是 [0, min(Count, Capacity))，两端越界都必须抛。
            Assert.Throws<ArgumentOutOfRangeException>(() => Read(log, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => Read(log, -1));
        }

        [Fact]
        public void Clear_ResetsCountersButKeepsSequenceIdMonotonic()
        {
            RingCausalLog log = new RingCausalLog(4);
            log.Record(1, 1, CausalOutcome.Success);
            log.Record(2, 1, CausalOutcome.Success);
            Assert.Equal(2L, log[1].SequenceId);

            log.Clear();

            Assert.Equal(0, log.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => Read(log, 0));

            log.Record(3, 1, CausalOutcome.Success);
            Assert.Equal(3L, log[0].SequenceId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void Constructor_RejectsNonPositiveCapacity(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RingCausalLog(capacity));
        }

        [Fact]
        public void TagRegistry_ReturnsSameIdForSameNameAndResolvesBothWays()
        {
            CausalTagRegistry registry = new CausalTagRegistry();

            int first = registry.Register("Cqe.Send");
            int second = registry.Register("Cqe.Send");
            int other = registry.Register("Cqe.Query");

            Assert.Equal(first, second);
            Assert.NotEqual(first, other);
            Assert.True(first > 0);

            Assert.Equal("Cqe.Send", registry.Resolve(first));
            Assert.Equal("Cqe.Query", registry.Resolve(other));

            // 0 与非已注册区间一律返回 null。
            Assert.Null(registry.Resolve(0));
            Assert.Null(registry.Resolve(-5));
            Assert.Null(registry.Resolve(other + 1));

            Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        }

        [Fact]
        public void Record_AndIndexerTraversal_WarmedUp_AllocateNothingOnHeap()
        {
            RingCausalLog log = new RingCausalLog(128);

            // 预热：填满一轮并回绕，让 Record 的覆盖分支与索引器分支都跑过一次。
            for (int i = 0; i < 256; i++)
            {
                log.Record(i, 1, CausalOutcome.Success);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                log.Record(i, 2, CausalOutcome.Dropped);
            }

            long sequenceSum = 0L;
            for (int i = 0; i < log.Capacity; i++)
            {
                sequenceSum += log[i].SequenceId;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            _output.WriteLine("RingCausalLog.Record x1000 + full indexer walk allocated bytes = " + allocated);

            Assert.Equal(0L, allocated);
            Assert.True(sequenceSum > 0L);
            Assert.Equal(1256, log.Count);
            Assert.Equal(128, log.Capacity);
        }

        /// <summary>把索引器读取包一层，方便在 <see cref="Assert.Throws{T}"/> 的 lambda 里取值。</summary>
        private static CausalRecord Read(RingCausalLog log, int index)
        {
            return log[index];
        }
    }
}

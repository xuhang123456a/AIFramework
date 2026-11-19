using System;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 埋点环形缓冲的行为契约：**容量固定、写满丢最老、热路径零分配、非法事件计数**。
    /// 这些断言是"埋点不会拖慢帧率、也不会悄悄吃掉数据"的证据。
    /// </summary>
    public sealed class AnalyticsEventBufferTests
    {
        [Fact]
        public void TryTrack_ValidEvent_IsAcceptedAndCounted()
        {
            var buffer = new AnalyticsEventBuffer(4);

            bool accepted = buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 5f, 7f, 0f, "level"));

            Assert.True(accepted);
            Assert.Equal(1, buffer.Count);
            Assert.Equal(1, buffer.AcceptedCount);
            Assert.Equal(0, buffer.DroppedCount);
        }

        [Fact]
        public void TryTrack_CountersAreCumulativeAfterDrain()
        {
            var buffer = new AnalyticsEventBuffer(4);
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.Purchase, 1f, 2f, 3f, null));

            AnalyticsEvent[] destination = new AnalyticsEvent[4];
            Assert.Equal(1, buffer.Drain(destination));

            Assert.Equal(0, buffer.Count);
            Assert.Equal(1, buffer.AcceptedCount);
        }

        [Fact]
        public void TryTrack_InvalidEventId_IsDropped()
        {
            var buffer = new AnalyticsEventBuffer(4);

            Assert.False(buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.None, 1f, 1f, 1f, null)));
            Assert.False(buffer.TryTrack(new AnalyticsEvent(-1, 1f, 1f, 1f, null)));
            Assert.Equal(0, buffer.Count);
            Assert.Equal(2, buffer.DroppedCount);
        }

        [Fact]
        public void TryTrack_NonFiniteValues_AreDropped()
        {
            var buffer = new AnalyticsEventBuffer(4);

            Assert.False(buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, float.NaN, 0f, 0f, null)));
            Assert.False(buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 0f, float.PositiveInfinity, 0f, null)));
            Assert.False(buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 0f, 0f, float.NegativeInfinity, null)));
            Assert.Equal(3, buffer.DroppedCount);
        }

        [Fact]
        public void TryTrack_BufferFull_OverwritesOldestAndKeepsNewest()
        {
            var buffer = new AnalyticsEventBuffer(3);
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 0f, 0f, null));
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 2f, 0f, 0f, null));
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 3f, 0f, 0f, null));
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.BattleEnd, 4f, 0f, 0f, null));

            Assert.Equal(3, buffer.Count);
            Assert.Equal(1, buffer.OverwrittenCount);
            Assert.Equal(AnalyticsEventIds.SceneEnter, buffer.Get(0).EventId);
            Assert.Equal(AnalyticsEventIds.BattleEnd, buffer.Get(2).EventId);
        }

        [Fact]
        public void Drain_ReturnsEventsInWriteOrder()
        {
            var buffer = new AnalyticsEventBuffer(4);
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 0f, 0f, null));
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 2f, 0f, 0f, "LobbyScene"));
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 3f, 2f, 0f, null));

            AnalyticsEvent[] destination = new AnalyticsEvent[4];
            int drained = buffer.Drain(destination);

            Assert.Equal(3, drained);
            Assert.Equal(1f, destination[0].Value0);
            Assert.Equal("LobbyScene", destination[1].Label);
            Assert.Equal(3f, destination[2].Value0);
        }

        [Fact]
        public void Drain_SmallerDestination_LeavesRemainderBuffered()
        {
            var buffer = new AnalyticsEventBuffer(8);
            for (int i = 1; i <= 5; i++)
            {
                buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.ItemGranted, i, 1f, 0f, null));
            }

            AnalyticsEvent[] destination = new AnalyticsEvent[2];
            Assert.Equal(2, buffer.Drain(destination));
            Assert.Equal(3, buffer.Count);
            Assert.Equal(3f, buffer.Get(0).Value0);
        }

        [Fact]
        public void Get_OutOfRange_ReturnsDefault()
        {
            var buffer = new AnalyticsEventBuffer(2);
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.Gacha, 10f, 1f, 0f, null));

            Assert.Equal(0, buffer.Get(-1).EventId);
            Assert.Equal(0, buffer.Get(5).EventId);
            Assert.Equal(AnalyticsEventIds.Gacha, buffer.Get(0).EventId);
        }

        [Fact]
        public void Clear_EmptiesBufferButKeepsCounters()
        {
            var buffer = new AnalyticsEventBuffer(4);
            buffer.TryTrack(new AnalyticsEvent(AnalyticsEventIds.Purchase, 1f, 2f, 3f, null));
            buffer.Clear();

            Assert.Equal(0, buffer.Count);
            Assert.Equal(1, buffer.AcceptedCount);
        }

        [Fact]
        public void Constructor_InvalidCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnalyticsEventBuffer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnalyticsEventBuffer(-3));
        }

        [Fact]
        public void Capacity_IsWhatWasRequested()
        {
            Assert.Equal(7, new AnalyticsEventBuffer(7).Capacity);
            Assert.Equal(AnalyticsEventBuffer.DefaultCapacity, new AnalyticsEventBuffer().Capacity);
        }

        [Fact]
        public void TryTrack_DoesNotAllocateAfterWarmup()
        {
            var buffer = new AnalyticsEventBuffer(64);
            var evt = new AnalyticsEvent(AnalyticsEventIds.BattleEnd, 1f, 2f, 3f, null);

            // 预热：让 JIT/分层编译与环形缓冲的首次写入都完成，再测稳态。
            for (int i = 0; i < 4096; i++)
            {
                buffer.TryTrack(in evt);
            }

            // 连续测 3 个窗口取最小值：单窗口可能被测试框架自身的一次分配污染，
            // 取最小窗口等价于"最干净的观测"，同时仍能抓住"每帧都分配"的真实回归
            //（若每次 Track 都分配，三个窗口都会远超阈值）。
            long best = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1024; i++)
                {
                    buffer.TryTrack(in evt);
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (allocated < best)
                {
                    best = allocated;
                }
            }

            Assert.True(
                best <= 256,
                "TryTrack 稳态分配了 " + best + " 字节（预算 256）。检查是否引入了装箱/字符串/List 增长。");
        }
    }
}

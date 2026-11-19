using System.Collections.Generic;
using System.IO;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// 控制台埋点实现的行为契约：可用性短路、环形缓冲、冲刷成可读行、事件名映射。
    /// 它是"埋点代码不炸业务"的最小证据（假后端也能跑完整链路）。
    /// </summary>
    public sealed class ConsoleAnalyticsServiceTests
    {
        [Fact]
        public void IsAvailable_DefaultTrue()
        {
            var service = new ConsoleAnalyticsService(new StringWriter());
            Assert.True(service.IsAvailable);
        }

        [Fact]
        public void Unavailable_TrackIsIgnoredAndCountersStayZero()
        {
            var service = new ConsoleAnalyticsService(new StringWriter(), available: false);

            service.Track(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 1f, 2f, 3f, null));

            Assert.False(service.IsAvailable);
            Assert.Equal(0, service.AcceptedCount);
            Assert.Equal(0, service.BufferedCount);
        }

        [Fact]
        public void Track_ValidEvent_BuffersAndCounts()
        {
            var service = new ConsoleAnalyticsService(new StringWriter());

            service.Track(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 12f, 0f, 0f, "LobbyScene"));

            Assert.Equal(1, service.AcceptedCount);
            Assert.Equal(1, service.BufferedCount);
        }

        [Fact]
        public void Track_InvalidEvent_IsDropped()
        {
            var service = new ConsoleAnalyticsService(new StringWriter());

            service.Track(new AnalyticsEvent(AnalyticsEventIds.None, 0f, 0f, 0f, null));

            Assert.Equal(0, service.AcceptedCount);
            Assert.Equal(1, service.DroppedCount);
        }

        [Fact]
        public void Flush_WritesOneReadableLinePerEvent()
        {
            var writer = new StringWriter();
            var service = new ConsoleAnalyticsService(writer);

            service.Track(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 2f, 7f, 0f, null));
            service.Track(new AnalyticsEvent(AnalyticsEventIds.BattleEnd, 1f, 42.5f, 0f, "Arena"));
            service.Flush();

            string output = writer.ToString();
            Assert.Contains("[Track] battle_start", output);
            Assert.Contains("v0=2", output);
            Assert.Contains("[Track] battle_end", output);
            Assert.Contains("label=Arena", output);
            Assert.Equal(0, service.BufferedCount);
        }

        [Fact]
        public void Flush_OnEmptyBuffer_WritesNothing()
        {
            var writer = new StringWriter();
            var service = new ConsoleAnalyticsService(writer);

            service.Flush();

            Assert.Equal(string.Empty, writer.ToString());
        }

        [Fact]
        public void Flush_IsIdempotent()
        {
            var writer = new StringWriter();
            var service = new ConsoleAnalyticsService(writer);
            service.Track(new AnalyticsEvent(AnalyticsEventIds.Purchase, 3f, 100f, 5f, null));

            service.Flush();
            string first = writer.ToString();
            service.Flush();

            Assert.Equal(first, writer.ToString());
        }

        [Fact]
        public void BufferCapacity_SpillsOldestAndReportsOverwritten()
        {
            var writer = new StringWriter();
            var service = new ConsoleAnalyticsService(writer, capacity: 2);

            service.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 0f, 0f, null));
            service.Track(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 2f, 0f, 0f, null));
            service.Track(new AnalyticsEvent(AnalyticsEventIds.BattleStart, 3f, 0f, 0f, null));

            Assert.Equal(2, service.BufferedCount);
            Assert.Equal(1, service.OverwrittenCount);
            Assert.Equal(1, service.DroppedCount);
        }

        [Fact]
        public void DrainFlushed_ReturnsAndClearsLines()
        {
            var service = new ConsoleAnalyticsService(new StringWriter());
            service.Track(new AnalyticsEvent(AnalyticsEventIds.Gacha, 10f, 2f, 0f, null));
            service.Flush();

            IReadOnlyList<string> lines = service.DrainFlushed();
            Assert.Single(lines);

            Assert.Empty(service.DrainFlushed());
        }

        [Theory]
        [InlineData(AnalyticsEventIds.AppLaunch, "app_launch")]
        [InlineData(AnalyticsEventIds.SceneEnter, "scene_enter")]
        [InlineData(AnalyticsEventIds.BattleStart, "battle_start")]
        [InlineData(AnalyticsEventIds.BattleEnd, "battle_end")]
        [InlineData(AnalyticsEventIds.ItemGranted, "item_granted")]
        [InlineData(AnalyticsEventIds.Purchase, "purchase")]
        [InlineData(AnalyticsEventIds.Gacha, "gacha")]
        [InlineData(AnalyticsEventIds.AdCompleted, "ad_completed")]
        [InlineData(AnalyticsEventIds.IapPurchased, "iap_purchased")]
        public void NameOf_KnownEventIds_ReturnsStableNames(int eventId, string expected)
        {
            Assert.Equal(expected, ConsoleAnalyticsService.NameOf(eventId));
        }

        [Fact]
        public void NameOf_UnknownEventId_FallsBackToHash()
        {
            Assert.Equal("#7777", ConsoleAnalyticsService.NameOf(7777));
        }

        [Fact]
        public void EventIds_AreUniqueAndPositive()
        {
            int[] ids =
            {
                AnalyticsEventIds.AppLaunch,
                AnalyticsEventIds.SceneEnter,
                AnalyticsEventIds.BattleStart,
                AnalyticsEventIds.BattleEnd,
                AnalyticsEventIds.ItemGranted,
                AnalyticsEventIds.Purchase,
                AnalyticsEventIds.Gacha,
                AnalyticsEventIds.AdCompleted,
                AnalyticsEventIds.IapPurchased,
            };

            var seen = new HashSet<int>();
            foreach (int id in ids)
            {
                Assert.True(id > AnalyticsEventIds.None);
                Assert.True(seen.Add(id), "事件号重复：" + id);
            }
        }
    }
}

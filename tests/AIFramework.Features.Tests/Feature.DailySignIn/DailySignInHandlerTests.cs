using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.DailySignIn
{
    /// <summary>
    /// 签到业务规则验收（<c>Doc/02 §六</c>）：真实 <c>CqeBus</c> + 桩端口，
    /// 断言只落在"切片发出了什么消息"与"状态快照"两处。
    /// </summary>
    public sealed class DailySignInHandlerTests
    {
        /// <summary>夹具起点：2020-03-01 12:00 UTC（天序号 60，取正午以验证 <c>.Date</c> 截断）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2020, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ClaimOnFirstDay_GrantsFirstRowAndPublishesOneEvent()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);

            harness.SendClaim();

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(FakeTableLoader.DayItemId(1), grant.ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(1), grant.Count);

            DailySignInClaimedEvent claimed = Assert.Single(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(harness.Handler.TodayDayIndex, claimed.DayIndex);
            Assert.Equal(FakeTableLoader.DayItemId(1), claimed.ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(1), claimed.ItemCount);

            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[0]);
            Assert.False(harness.Handler.Snapshot.ClaimedDays[1]);
            Assert.Equal(harness.Handler.TodayDayIndex, harness.Handler.Snapshot.LastClaimDayIndex);
        }

        [Fact]
        public void SecondClaimOnSameDay_IsRejectedWithoutNewMessage()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);

            harness.SendClaim();
            harness.SendClaim();

            // 第二条命令确实上了总线，但被业务规则静默拒绝。
            Assert.Equal(2, harness.Bus.CountSent<ClaimDailySignInCommand>());
            Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[0]);
        }

        [Fact]
        public void ClaimAfterAdvancingOneDay_GrantsSecondRow()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            int firstDayIndex = harness.Handler.TodayDayIndex;

            harness.SendClaim();
            harness.Time.AdvanceDays(1);
            harness.SendClaim();

            List<GrantItemCommand> grants = harness.Bus.SentOf<GrantItemCommand>();
            Assert.Equal(2, grants.Count);
            Assert.Equal(FakeTableLoader.DayItemId(1), grants[0].ItemId);
            Assert.Equal(FakeTableLoader.DayItemId(2), grants[1].ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(2), grants[1].Count);

            List<DailySignInClaimedEvent> events = harness.Bus.PublishedOf<DailySignInClaimedEvent>();
            Assert.Equal(2, events.Count);
            Assert.Equal(firstDayIndex + 1, events[1].DayIndex);

            Assert.Equal(2, harness.Handler.Snapshot.ClaimedCount);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[1]);
            Assert.Equal(firstDayIndex + 1, harness.Handler.TodayDayIndex);
            Assert.Equal(firstDayIndex, harness.Handler.Snapshot.CycleStartDayIndex);
        }

        [Fact]
        public void ClaimAcrossMidnight_UnlocksNextDayEvenBeforeFullDayElapsed()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            int firstDayIndex = harness.Handler.TodayDayIndex;

            harness.SendClaim();

            // 从 12:00 推到 23:59：还是同一天，必须继续拒绝。
            harness.Time.AdvanceSeconds((11d * 3600d) + (59d * 60d));
            harness.SendClaim();

            Assert.Equal(firstDayIndex, harness.Handler.TodayDayIndex);
            Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);

            // 再跨过 0 点：跨天判据是 UTC 日切（.Date），不是"满 24 小时"。
            harness.Time.AdvanceSeconds(60d);
            harness.SendClaim();

            List<GrantItemCommand> grants = harness.Bus.SentOf<GrantItemCommand>();
            Assert.Equal(2, grants.Count);
            Assert.Equal(FakeTableLoader.DayItemId(2), grants[1].ItemId);
            Assert.Equal(2, harness.Handler.Snapshot.ClaimedCount);
        }

        [Fact]
        public void SevenConsecutiveClaims_FillCycleAndEighthIsRejected()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);

            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                if (dayOffset > 0)
                {
                    harness.Time.AdvanceDays(1);
                }

                harness.SendClaim();
            }

            List<GrantItemCommand> grants = harness.Bus.SentOf<GrantItemCommand>();
            Assert.Equal(7, grants.Count);
            Assert.Equal(FakeTableLoader.DayItemId(7), grants[6].ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(7), grants[6].Count);
            Assert.Equal(7, harness.Handler.Snapshot.ClaimedCount);

            // 第 8 次：同周期内已领满，必须拒绝且不再发奖、不再广播。
            harness.SendClaim();

            Assert.Equal(8, harness.Bus.CountSent<ClaimDailySignInCommand>());
            Assert.Equal(7, harness.Bus.SentOf<GrantItemCommand>().Count);
            Assert.Equal(7, harness.Bus.PublishedOf<DailySignInClaimedEvent>().Count);
            Assert.Equal(7, harness.Handler.Snapshot.ClaimedCount);
        }

        [Fact]
        public void RetroactiveForFutureDay_IsRejectedSoCycleCannotBeFilledAhead()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Inventory.SetCount(SignInHarness.RetroactiveCardItemId, 6);
            int cycleStart = harness.CycleStartDayIndex;

            harness.SendClaim();

            // 本周期第 2..7 格都落在未来：补签必须拒绝，否则可以把整个周期提前"预支"掉。
            // （满勤封顶 `ClaimedCount >= 7` 因此变成防御性分支：正常路径下 7 天领满的次日即跨周期，
            //   只有数据被外部写坏时才可能触达，这里不再伪造场景硬测。）
            for (int slotOffset = 1; slotOffset < 7; slotOffset++)
            {
                harness.SendRetroactive(cycleStart + slotOffset);
            }

            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
            Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(6, harness.Inventory.GetCount(SignInHarness.RetroactiveCardItemId));
        }

        [Fact]
        public void ClaimAfterCycleRollover_RestartsFromFirstRow()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            int firstCycleStart = harness.CycleStartDayIndex;

            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                if (dayOffset > 0)
                {
                    harness.Time.AdvanceDays(1);
                }

                harness.SendClaim();
            }

            harness.Time.AdvanceDays(1);
            harness.SendClaim();

            List<GrantItemCommand> grants = harness.Bus.SentOf<GrantItemCommand>();
            Assert.Equal(8, grants.Count);
            Assert.Equal(FakeTableLoader.DayItemId(1), grants[7].ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(1), grants[7].Count);

            Assert.Equal(firstCycleStart + 7, harness.Handler.Snapshot.CycleStartDayIndex);
            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[0]);
            Assert.False(harness.Handler.Snapshot.ClaimedDays[1]);
        }

        [Fact]
        public void RetroactiveWithoutCard_IsRejectedWithoutAnyMessage()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Inventory.SetCount(SignInHarness.RetroactiveCardItemId, 0);
            int today = harness.Handler.TodayDayIndex;

            harness.SendRetroactive(today);

            Assert.Equal(1, harness.Bus.CountQueried<InventoryQueryRequest>());
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(0, harness.Handler.Snapshot.ClaimedCount);
            Assert.All(harness.Handler.Snapshot.ClaimedDays, claimed => Assert.False(claimed));
        }

        [Fact]
        public void RetroactiveWithCard_ConsumesOneAndGrantsTargetSlot()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Inventory.SetCount(SignInHarness.RetroactiveCardItemId, 1);
            int today = harness.Handler.TodayDayIndex;

            harness.SendRetroactive(today);

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(SignInHarness.RetroactiveCardItemId, consume.ItemId);
            Assert.Equal(1, consume.Count);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(FakeTableLoader.DayItemId(1), grant.ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(1), grant.Count);

            DailySignInClaimedEvent claimed = Assert.Single(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(today, claimed.DayIndex);
            Assert.Equal(FakeTableLoader.DayItemId(1), claimed.ItemId);

            // 顺序契约：先扣补签卡，再发奖，最后广播事实。
            Assert.Equal(3, harness.Bus.SentCommands.Count);
            Assert.IsType<RetroactiveSignInCommand>(harness.Bus.SentCommands[0]);
            Assert.IsType<ConsumeItemCommand>(harness.Bus.SentCommands[1]);
            Assert.IsType<GrantItemCommand>(harness.Bus.SentCommands[2]);

            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[0]);
        }

        [Fact]
        public void RetroactiveOutsideCurrentCycle_IsRejectedWithoutAnyMessage()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Inventory.SetCount(SignInHarness.RetroactiveCardItemId, 3);
            int cycleStart = harness.CycleStartDayIndex;

            harness.SendRetroactive(cycleStart - 1);
            harness.SendRetroactive(cycleStart + 7);

            // 结构/周期非法时直接拒绝，连背包都不查——所以这里必须是 0 次查询。
            Assert.Equal(0, harness.Bus.CountQueried<InventoryQueryRequest>());
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(0, harness.Handler.Snapshot.ClaimedCount);
            Assert.All(harness.Handler.Snapshot.ClaimedDays, claimed => Assert.False(claimed));
        }
    }
}

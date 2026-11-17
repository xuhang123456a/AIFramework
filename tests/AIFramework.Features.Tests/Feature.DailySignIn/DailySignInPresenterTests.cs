using System;
using AIFramework.Contracts.Meta;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.DailySignIn
{
    /// <summary>
    /// Presenter 验收：7 格展示数据由处理器状态推导，点击只转发成 CQE 命令，不在这里算奖励。
    /// </summary>
    public sealed class DailySignInPresenterTests
    {
        /// <summary>夹具起点：2020-03-01 12:00 UTC。</summary>
        private static readonly DateTime StartUtc = new DateTime(2020, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Refresh_RendersSevenSlotsWithFirstClaimable()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);

            harness.Presenter.Refresh();

            Assert.Equal(1, harness.View.RenderCount);
            SignInSlotViewData[] slots = harness.View.LastSlots;
            Assert.Equal(FakeTableLoader.DayCount, slots.Length);

            Assert.Equal(SignInSlotState.Claimable, slots[0].State);
            for (int i = 1; i < slots.Length; i++)
            {
                Assert.Equal(SignInSlotState.Locked, slots[i].State);
            }

            // 格子回传的是周期内相对序号 1..7，奖励内容来自子表第 n 行。
            Assert.Equal(1, slots[0].DayIndex);
            Assert.Equal(7, slots[6].DayIndex);
            for (int i = 0; i < slots.Length; i++)
            {
                int day = i + 1;
                Assert.Equal(day, slots[i].DayIndex);
                Assert.Equal(FakeTableLoader.DayItemId(day), slots[i].ItemId);
                Assert.Equal(FakeTableLoader.DayItemCount(day), slots[i].ItemCount);
            }
        }

        [Fact]
        public void Refresh_AfterClaimAndDayAdvance_MarksClaimedAndNextClaimable()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Presenter.Refresh();

            harness.View.RaiseClaimClicked();

            // 领完 Presenter 自己会刷新一次：第 1 格变 Claimed。
            Assert.Equal(2, harness.View.RenderCount);
            Assert.Equal(SignInSlotState.Claimed, harness.View.LastSlots[0].State);
            // 第 2 格要等当天过去（absoluteDay == today）才可领，此刻仍是 Locked。
            Assert.Equal(SignInSlotState.Locked, harness.View.LastSlots[1].State);

            harness.Time.AdvanceDays(1);
            harness.Presenter.Refresh();

            Assert.Equal(3, harness.View.RenderCount);
            Assert.Equal(SignInSlotState.Claimed, harness.View.LastSlots[0].State);
            Assert.Equal(SignInSlotState.Claimable, harness.View.LastSlots[1].State);
            Assert.Equal(SignInSlotState.Locked, harness.View.LastSlots[2].State);
        }

        [Fact]
        public void ClaimClicked_SendsClaimCommandAndCompletesReward()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Presenter.Refresh();

            harness.View.RaiseClaimClicked();

            Assert.Single(harness.Bus.SentOf<ClaimDailySignInCommand>());
            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(FakeTableLoader.DayItemId(1), grant.ItemId);
            Assert.Single(harness.Bus.PublishedOf<DailySignInClaimedEvent>());
            Assert.Equal(1, harness.Handler.Snapshot.ClaimedCount);
        }

        [Fact]
        public void RetroactiveClicked_ForwardsAbsoluteDayConvertedFromSlot()
        {
            SignInHarness harness = SignInHarness.Create(StartUtc);
            harness.Inventory.SetCount(SignInHarness.RetroactiveCardItemId, 1);
            int cycleStart = harness.CycleStartDayIndex;

            // 补签只允许"过去或当天"：先推进 2 天，让第 3 格正好是今天。
            harness.Time.AdvanceDays(2);
            harness.Presenter.Refresh();

            // 视图按契约回传格子序号 3（1..7），Presenter 负责换算成绝对天序号。
            harness.View.RaiseRetroactiveClicked(3);

            RetroactiveSignInCommand command = Assert.Single(harness.Bus.SentOf<RetroactiveSignInCommand>());
            Assert.Equal(cycleStart + 2, command.DayIndex);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(FakeTableLoader.DayItemId(3), grant.ItemId);
            Assert.True(harness.Handler.Snapshot.ClaimedDays[2]);
            Assert.Equal(SignInSlotState.Claimed, harness.View.LastSlots[2].State);
        }
    }
}

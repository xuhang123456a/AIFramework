using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.SeasonPass;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.SeasonPass
{
    /// <summary>
    /// 通行证切片业务规则验收：真实 <c>CqeBus</c> + 可快进假时间源 + 本目录私有桩，
    /// 断言只落在"切片发出了什么消息（顺序/字段）"与"查询快照"两处。
    /// </summary>
    public sealed class SeasonPassHandlerTests
    {
        /// <summary>发奖原因标签（与切片内常量一致，写在断言里防止被无声改动）。</summary>
        private const int ClaimReasonTagId = 6501;

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            SeasonPassTableStub loader = SeasonPassTableStub.WithStandardRows();
            var time = new FakeTimeProvider(SeasonPassHarness.DefaultNow);

            Assert.Throws<ArgumentNullException>(
                () => new SeasonPassHandler(null!, time, loader, 0L));
        }

        [Fact]
        public void Constructor_NullTime_Throws()
        {
            SeasonPassTableStub loader = SeasonPassTableStub.WithStandardRows();

            Assert.Throws<ArgumentNullException>(
                () => new SeasonPassHandler(new CqeBus(), null!, loader, 0L));
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            var time = new FakeTimeProvider(SeasonPassHarness.DefaultNow);

            Assert.Throws<ArgumentNullException>(
                () => new SeasonPassHandler(new CqeBus(), time, null!, 0L));
        }

        [Fact]
        public void Constructor_MissingTable_ThrowsInvalidOperationException()
        {
            var time = new FakeTimeProvider(SeasonPassHarness.DefaultNow);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new SeasonPassHandler(new CqeBus(), time, SeasonPassTableStub.WithoutTable(), 0L));

            Assert.Contains(SeasonPassTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void InitialSnapshot_HasLevelOneAndNothingClaimed()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            SeasonPassSnapshot snapshot = harness.Query();

            Assert.Equal(1, snapshot.Level);
            Assert.Equal(0, snapshot.CurrentXp);
            Assert.Equal(100, snapshot.NextLevelXp);
            Assert.False(snapshot.IsMaxLevel);
            Assert.False(snapshot.PaidUnlocked);
            Assert.Equal(5, snapshot.Items.Count);
            Assert.Equal(1, snapshot.ClaimableCount);
            Assert.Equal((long)SeasonPassHarness.DefaultSeasonSeconds, snapshot.RemainingSeconds);
            Assert.False(snapshot.IsExpired);

            SeasonRewardState first = snapshot.Items[0];
            Assert.Equal(1, first.Level);
            Assert.Equal(0, first.RequiredXp);
            Assert.Equal(7501, first.FreeItemId);
            Assert.Equal(10, first.FreeCount);
            Assert.Equal(7502, first.PaidItemId);
            Assert.Equal(1, first.PaidCount);
            Assert.False(first.FreeClaimed);
            Assert.False(first.PaidClaimed);
            Assert.True(first.Unlocked);

            SeasonRewardState last = snapshot.Items[4];
            Assert.Equal(5, last.Level);
            Assert.Equal(700, last.RequiredXp);
            Assert.False(last.Unlocked);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void AddXp_NonPositiveAmount_IsIgnored(int amount)
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(amount);

            Assert.Empty(harness.Messages);
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.Level);
            Assert.Equal(0, snapshot.CurrentXp);
        }

        [Fact]
        public void AddXp_BelowThreshold_AccumulatesWithoutLevelUp()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(99);

            Assert.Empty(harness.Messages);
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.Level);
            Assert.Equal(99, snapshot.CurrentXp);
            Assert.Equal(100, snapshot.NextLevelXp);
        }

        [Fact]
        public void AddXp_ReachesThreshold_LevelsUpAndPublishesOneEvent()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(100);

            // 升级只广播事实，不发奖：奖励必须显式领取。
            Assert.Single(harness.Messages);
            SeasonLevelUpEvent levelUp = Assert.IsType<SeasonLevelUpEvent>(harness.Messages[0]);
            Assert.Equal(2, levelUp.Level);
            Assert.Equal(0, harness.GrantCount());

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(2, snapshot.Level);
            Assert.Equal(100, snapshot.CurrentXp);
            Assert.Equal(250, snapshot.NextLevelXp);
            Assert.False(snapshot.IsMaxLevel);
        }

        [Fact]
        public void AddXp_CrossesMultipleLevels_PublishesEventsInAscendingOrder()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            // 0 → 450 一次性跨 2/3/4 级（门槛 100/250/450）。
            harness.AddXp(450);

            List<SeasonLevelUpEvent> levelUps = harness.MessagesOf<SeasonLevelUpEvent>();
            Assert.Equal(3, levelUps.Count);
            Assert.Equal(2, levelUps[0].Level);
            Assert.Equal(3, levelUps[1].Level);
            Assert.Equal(4, levelUps[2].Level);
            Assert.Equal(3, harness.Messages.Count);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(4, snapshot.Level);
            Assert.Equal(450, snapshot.CurrentXp);
            Assert.Equal(700, snapshot.NextLevelXp);
        }

        [Fact]
        public void AddXp_ToMaxLevel_EmitsEveryCrossedLevelOnce()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(700);

            List<SeasonLevelUpEvent> levelUps = harness.MessagesOf<SeasonLevelUpEvent>();
            Assert.Equal(4, levelUps.Count);
            Assert.Equal(2, levelUps[0].Level);
            Assert.Equal(5, levelUps[3].Level);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(5, snapshot.Level);
            Assert.True(snapshot.IsMaxLevel);
            Assert.Equal(700, snapshot.CurrentXp);
            // 满级约定：NextLevelXp 退化为等于 CurrentXp。
            Assert.Equal(snapshot.CurrentXp, snapshot.NextLevelXp);
        }

        [Fact]
        public void AddXp_AfterMaxLevel_KeepsAccumulatingWithoutNewEvents()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            harness.AddXp(700);
            int eventsAfterMax = harness.LevelUpCount();

            harness.AddXp(500);

            Assert.Equal(eventsAfterMax, harness.LevelUpCount());
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(5, snapshot.Level);
            Assert.Equal(1200, snapshot.CurrentXp);
            Assert.True(snapshot.IsMaxLevel);
            Assert.Equal(1200, snapshot.NextLevelXp);
        }

        [Fact]
        public void AddXp_HugeAmount_SaturatesWithoutOverflow()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(int.MaxValue);
            harness.AddXp(int.MaxValue);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.Equal(int.MaxValue, snapshot.CurrentXp);
            Assert.Equal(5, snapshot.Level);
            Assert.Equal(4, harness.LevelUpCount());
        }

        [Fact]
        public void ClaimFree_UnlockedLevel_GrantsItemThenPublishesClaimedEvent()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(1, paidTrack: false);

            // 顺序契约：先发奖指令，再广播领取事件。
            Assert.Equal(2, harness.Messages.Count);

            GrantItemCommand grant = Assert.IsType<GrantItemCommand>(harness.Messages[0]);
            Assert.Equal(7501, grant.ItemId);
            Assert.Equal(10, grant.Count);
            Assert.Equal(ClaimReasonTagId, grant.ReasonTagId);

            SeasonRewardClaimedEvent claimed = Assert.IsType<SeasonRewardClaimedEvent>(harness.Messages[1]);
            Assert.Equal(1, claimed.Level);
            Assert.False(claimed.PaidTrack);
            Assert.Equal(7501, claimed.ItemId);
            Assert.Equal(10, claimed.Count);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.True(snapshot.Items[0].FreeClaimed);
            Assert.False(snapshot.Items[0].PaidClaimed);
            Assert.Equal(0, snapshot.ClaimableCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(6)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void ClaimFree_InvalidLevel_IsIgnored(int level)
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(level, paidTrack: false);

            Assert.Empty(harness.Messages);
            Assert.Equal(1, harness.Query().ClaimableCount);
        }

        [Fact]
        public void ClaimFree_LevelAboveCurrent_IsIgnored()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(2, paidTrack: false);

            Assert.Empty(harness.Messages);
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.False(snapshot.Items[1].FreeClaimed);
            Assert.Equal(1, snapshot.ClaimableCount);
        }

        [Fact]
        public void ClaimFree_Twice_OnlyGrantsOnce()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(1, paidTrack: false);
            harness.Claim(1, paidTrack: false);

            Assert.Equal(2, harness.Messages.Count);
            Assert.Equal(1, harness.GrantCount());
            Assert.Equal(1, harness.ClaimedEventCount());
        }

        [Fact]
        public void ClaimPaid_WhenTrackLocked_IsIgnored()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(1, paidTrack: true);

            Assert.Empty(harness.Messages);
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.False(snapshot.PaidUnlocked);
            Assert.False(snapshot.Items[0].PaidClaimed);
        }

        [Fact]
        public void UnlockPaid_ThenClaimPaid_GrantsPaidReward()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.UnlockPaid();

            // 解锁只改状态：不广播事件、不发奖。
            Assert.Empty(harness.Messages);
            Assert.True(harness.Query().PaidUnlocked);

            harness.Claim(1, paidTrack: true);

            Assert.Equal(2, harness.Messages.Count);
            GrantItemCommand grant = Assert.IsType<GrantItemCommand>(harness.Messages[0]);
            Assert.Equal(7502, grant.ItemId);
            Assert.Equal(1, grant.Count);
            Assert.Equal(ClaimReasonTagId, grant.ReasonTagId);

            SeasonRewardClaimedEvent claimed = Assert.IsType<SeasonRewardClaimedEvent>(harness.Messages[1]);
            Assert.True(claimed.PaidTrack);
            Assert.Equal(7502, claimed.ItemId);
            Assert.True(harness.Query().Items[0].PaidClaimed);
        }

        [Fact]
        public void UnlockPaid_IsIdempotent()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.UnlockPaid();
            harness.UnlockPaid();

            Assert.Empty(harness.Messages);
            Assert.True(harness.Query().PaidUnlocked);

            harness.Claim(1, paidTrack: true);
            harness.Claim(1, paidTrack: true);

            Assert.Equal(2, harness.Messages.Count);
            Assert.Equal(1, harness.GrantCount());
        }

        [Fact]
        public void ClaimPaid_LevelWithoutPaidReward_IsIgnored()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            harness.UnlockPaid();
            harness.AddXp(100);

            harness.Claim(2, paidTrack: true);

            // 2 级付费列整对为 0：无奖可领，不是错误，也不能发 0 个道具。
            Assert.Equal(0, harness.GrantCount());
            Assert.Equal(0, harness.ClaimedEventCount());
            Assert.False(harness.Query().Items[1].PaidClaimed);
        }

        [Fact]
        public void ClaimableCount_TracksUnlockedLevelsAndClaims()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            Assert.Equal(1, harness.Query().ClaimableCount);

            // 解锁付费轨：1 级多一条付费可领（2 级没有付费奖励）。
            harness.UnlockPaid();
            Assert.Equal(2, harness.Query().ClaimableCount);

            // 升到 3 级：免费 1/2/3 三条 + 付费 1/3 两条 = 5。
            harness.AddXp(250);
            Assert.Equal(5, harness.Query().ClaimableCount);

            harness.Claim(1, paidTrack: false);
            Assert.Equal(4, harness.Query().ClaimableCount);

            harness.Claim(1, paidTrack: true);
            Assert.Equal(3, harness.Query().ClaimableCount);

            harness.Claim(2, paidTrack: false);
            Assert.Equal(2, harness.Query().ClaimableCount);

            harness.Claim(3, paidTrack: true);
            Assert.Equal(1, harness.Query().ClaimableCount);

            harness.Claim(3, paidTrack: false);
            Assert.Equal(0, harness.Query().ClaimableCount);
        }

        [Fact]
        public void ClaimableCount_ExcludesPaidRewardsBeforeTrackUnlock()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            harness.AddXp(700);

            // 5 级全到：免费 5 条；付费轨未解锁，1/3/5 级的付费奖励不算可领。
            Assert.Equal(5, harness.Query().ClaimableCount);

            harness.UnlockPaid();
            Assert.Equal(8, harness.Query().ClaimableCount);
        }

        [Fact]
        public void Expired_AtExactEndBoundary_IsExpiredWithZeroRemaining()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(
                SeasonPassTableStub.WithStandardRows(),
                SeasonPassHarness.DefaultNow,
                SeasonPassHarness.DefaultNow);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsExpired);
            Assert.Equal(0L, snapshot.RemainingSeconds);
            Assert.Equal(0, snapshot.ClaimableCount);
        }

        [Fact]
        public void Expired_ClaimsAreRejected()
        {
            SeasonPassHarness harness = SeasonPassHarness.CreateExpired(SeasonPassTableStub.WithStandardRows());
            harness.UnlockPaid();

            harness.Claim(1, paidTrack: false);
            harness.Claim(1, paidTrack: true);

            Assert.Empty(harness.Messages);
            SeasonPassSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsExpired);
            Assert.Equal(0L, snapshot.RemainingSeconds);
            Assert.Equal(0, snapshot.ClaimableCount);
            Assert.False(snapshot.Items[0].FreeClaimed);
            Assert.False(snapshot.Items[0].PaidClaimed);
        }

        [Fact]
        public void Expired_ExperienceStillAccumulatesButCannotBeClaimed()
        {
            // 过期只冻结领取：经验累计仍写（快照保留真实值），避免把未定义规则藏进实现。
            SeasonPassHarness harness = SeasonPassHarness.CreateExpired(SeasonPassTableStub.WithStandardRows());

            harness.AddXp(100);

            SeasonPassSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsExpired);
            Assert.Equal(100, snapshot.CurrentXp);
            Assert.Equal(2, snapshot.Level);
            Assert.Equal(1, harness.LevelUpCount());

            harness.Claim(1, paidTrack: false);
            Assert.Equal(0, harness.GrantCount());
            Assert.Equal(0, harness.ClaimedEventCount());
        }

        [Fact]
        public void RemainingSeconds_DecreasesAsTimeAdvances()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            Assert.Equal(604800L, harness.Query().RemainingSeconds);

            harness.Time.AdvanceSeconds(3600);
            SeasonPassSnapshot afterHour = harness.Query();
            Assert.False(afterHour.IsExpired);
            Assert.Equal(601200L, afterHour.RemainingSeconds);

            harness.Time.AdvanceSeconds(601200);
            SeasonPassSnapshot atEnd = harness.Query();
            Assert.True(atEnd.IsExpired);
            Assert.Equal(0L, atEnd.RemainingSeconds);
        }

        [Fact]
        public void ClaimAtMaxLevel_FreeTrack_Works()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            harness.AddXp(700);

            harness.Claim(5, paidTrack: false);

            Assert.Equal(1, harness.GrantCount());
            GrantItemCommand grant = harness.MessagesOf<GrantItemCommand>()[0];
            Assert.Equal(7501, grant.ItemId);
            Assert.Equal(50, grant.Count);
            Assert.True(harness.Query().Items[4].FreeClaimed);
        }

        [Fact]
        public void Snapshot_AfterProgressAndClaims_CarriesAllFields()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());
            harness.UnlockPaid();
            harness.AddXp(250);
            harness.Claim(1, paidTrack: false);
            harness.Claim(3, paidTrack: true);

            SeasonPassSnapshot snapshot = harness.Query();

            Assert.Equal(3, snapshot.Level);
            Assert.Equal(250, snapshot.CurrentXp);
            Assert.Equal(450, snapshot.NextLevelXp);
            Assert.False(snapshot.IsMaxLevel);
            Assert.True(snapshot.PaidUnlocked);
            Assert.False(snapshot.IsExpired);
            Assert.Equal(5, snapshot.Items.Count);
            Assert.Equal(3, snapshot.ClaimableCount);

            SeasonRewardState level1 = snapshot.Items[0];
            Assert.Equal(1, level1.Level);
            Assert.Equal(0, level1.RequiredXp);
            Assert.Equal(7501, level1.FreeItemId);
            Assert.Equal(10, level1.FreeCount);
            Assert.Equal(7502, level1.PaidItemId);
            Assert.Equal(1, level1.PaidCount);
            Assert.True(level1.FreeClaimed);
            Assert.False(level1.PaidClaimed);
            Assert.True(level1.Unlocked);

            SeasonRewardState level2 = snapshot.Items[1];
            Assert.Equal(100, level2.RequiredXp);
            Assert.Equal(0, level2.PaidItemId);
            Assert.False(level2.FreeClaimed);
            Assert.True(level2.Unlocked);

            SeasonRewardState level3 = snapshot.Items[2];
            Assert.Equal(250, level3.RequiredXp);
            Assert.False(level3.FreeClaimed);
            Assert.True(level3.PaidClaimed);
            Assert.True(level3.Unlocked);

            Assert.False(snapshot.Items[3].Unlocked);
            Assert.False(snapshot.Items[4].Unlocked);
        }

        [Fact]
        public void EmptyTable_QueryDegradesGracefullyAndCommandsAreNoOps()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithRows());

            SeasonPassSnapshot initial = harness.Query();
            Assert.Equal(0, initial.Level);
            Assert.Equal(0, initial.CurrentXp);
            Assert.Equal(0, initial.NextLevelXp);
            Assert.True(initial.IsMaxLevel);
            Assert.False(initial.PaidUnlocked);
            Assert.Empty(initial.Items);
            Assert.Equal(0, initial.ClaimableCount);
            Assert.Equal(604800L, initial.RemainingSeconds);
            Assert.False(initial.IsExpired);

            harness.AddXp(500);
            harness.Claim(1, paidTrack: false);
            harness.Claim(1, paidTrack: true);
            harness.UnlockPaid();

            Assert.Empty(harness.Messages);
            Assert.Equal(0, harness.Query().CurrentXp);
            Assert.True(harness.Query().PaidUnlocked);
        }

        [Fact]
        public void Register_RegistersThreeCommandsAndSnapshotQuery()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            // 1 个发奖桩 + 切片自己的 3 条命令 = 4；查询处理器来自切片；两个事件订阅。
            Assert.Equal(4, harness.Bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, harness.Bus.RegisteredQueryHandlerCount);
            Assert.Equal(2, harness.Bus.SubscriberCount);

            // 经真实总线走一遍完整链路，证明注册确实生效。
            harness.AddXp(100);
            harness.Claim(2, paidTrack: false);
            Assert.Equal(1, harness.GrantCount());
            Assert.Equal(2, harness.Query().Level);
        }

        [Fact]
        public void Claim_EmitsOnlyGrantAndClaimedEvent_NoOtherResponsibilities()
        {
            SeasonPassHarness harness = SeasonPassHarness.Create(SeasonPassTableStub.WithStandardRows());

            harness.Claim(1, paidTrack: false);

            Assert.Equal(2, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<SeasonRewardClaimedEvent>());
            Assert.Empty(harness.MessagesOf<ConsumeItemCommand>());
            Assert.Empty(harness.MessagesOf<SeasonLevelUpEvent>());
        }
    }
}

using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Features.Achievement;
using Xunit;

namespace AIFramework.Features.Tests.Achievement
{
    /// <summary>
    /// 成就切片业务规则验收：真实 <c>CqeBus</c> + 本目录私有桩，
    /// 断言只落在"切片发出了什么消息（顺序/字段）"与"查询快照"两处。
    /// </summary>
    public sealed class AchievementHandlerTests
    {
        /// <summary>发奖原因标签（与切片内常量一致，写在断言里防止被无声改动）。</summary>
        private const int UnlockReasonTagId = 6401;

        [Fact]
        public void Record_BelowTarget_DoesNotUnlockOrEmitAnything()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50)));

            harness.Record(AchievementMetrics.Kill, 9);

            Assert.Empty(harness.Messages);
            AchievementSnapshot snapshot = harness.Query();
            Assert.Equal(9, snapshot.Items[0].Current);
            Assert.False(snapshot.Items[0].IsUnlocked);
            Assert.Equal(0, snapshot.UnlockedCount);
        }

        [Fact]
        public void Record_ReachesTarget_GrantsItemThenPublishesUnlockEvent()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            harness.Record(AchievementMetrics.Kill, 1);

            // 顺序契约：先发奖指令，再广播解锁事件。
            Assert.Equal(2, harness.Messages.Count);

            GrantItemCommand grant = Assert.IsType<GrantItemCommand>(harness.Messages[0]);
            Assert.Equal(3001, grant.ItemId);
            Assert.Equal(10, grant.Count);
            Assert.Equal(UnlockReasonTagId, grant.ReasonTagId);

            AchievementUnlockedEvent unlocked = Assert.IsType<AchievementUnlockedEvent>(harness.Messages[1]);
            Assert.Equal(1001, unlocked.AchievementId);
            Assert.Equal("首杀", unlocked.Name);
            Assert.Equal(3001, unlocked.RewardItemId);
            Assert.Equal(10, unlocked.RewardCount);

            AchievementSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.UnlockedCount);
            Assert.True(snapshot.Items[0].IsUnlocked);
            Assert.Equal(1, snapshot.Items[0].Current);
        }

        [Fact]
        public void Record_CrossesLadder_UnlocksAllCrossedTiersInAscendingIdOrder()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10),
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50),
                new AchievementRowSpec(1003, "千锤百炼", AchievementMetrics.Kill, 100, 3002, 1)));

            // 一次上报跨越 1/10/100 三档：3 个成就全部解锁，每个固定 2 条消息。
            harness.Record(AchievementMetrics.Kill, 100);

            Assert.Equal(6, harness.Messages.Count);

            List<AchievementUnlockedEvent> unlocked = harness.MessagesOf<AchievementUnlockedEvent>();
            Assert.Equal(3, unlocked.Count);
            Assert.Equal(1001, unlocked[0].AchievementId);
            Assert.Equal(1002, unlocked[1].AchievementId);
            Assert.Equal(1003, unlocked[2].AchievementId);

            List<GrantItemCommand> grants = harness.MessagesOf<GrantItemCommand>();
            Assert.Equal(3, grants.Count);
            Assert.Equal(3001, grants[0].ItemId);
            Assert.Equal(3001, grants[1].ItemId);
            Assert.Equal(3002, grants[2].ItemId);

            // 交错顺序：grant1001 → event1001 → grant1002 → event1002 → grant1003 → event1003。
            Assert.IsType<GrantItemCommand>(harness.Messages[0]);
            Assert.IsType<AchievementUnlockedEvent>(harness.Messages[1]);
            Assert.IsType<GrantItemCommand>(harness.Messages[2]);
            Assert.IsType<AchievementUnlockedEvent>(harness.Messages[3]);
            Assert.IsType<GrantItemCommand>(harness.Messages[4]);
            Assert.IsType<AchievementUnlockedEvent>(harness.Messages[5]);

            Assert.Equal(3, harness.Query().UnlockedCount);
        }

        [Fact]
        public void Record_SameAchievementTwice_UnlocksOnlyOnce()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            harness.Record(AchievementMetrics.Kill, 1);
            harness.Record(AchievementMetrics.Kill, 1);

            Assert.Equal(2, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
            Assert.Equal(1, harness.Query().UnlockedCount);
        }

        [Fact]
        public void Record_AfterPartialLadder_UnlocksOnlyTheHigherTier()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10),
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50)));

            harness.Record(AchievementMetrics.Kill, 1);
            Assert.Equal(2, harness.Messages.Count);

            harness.Record(AchievementMetrics.Kill, 9);

            Assert.Equal(4, harness.Messages.Count);
            List<AchievementUnlockedEvent> unlocked = harness.MessagesOf<AchievementUnlockedEvent>();
            Assert.Equal(2, unlocked.Count);
            Assert.Equal(1002, unlocked[1].AchievementId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void Record_NonPositiveAmount_IsIgnored(int amount)
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            harness.Record(AchievementMetrics.Kill, amount);

            Assert.Empty(harness.Messages);
            Assert.Equal(0, harness.Query().Items[0].Current);
            Assert.Equal(0, harness.Query().UnlockedCount);
        }

        [Theory]
        [InlineData(AchievementMetrics.Count)]
        [InlineData(-1)]
        [InlineData(99)]
        public void Record_UnknownMetric_IsIgnored(int metric)
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            harness.Record(metric, 100);

            Assert.Empty(harness.Messages);
            Assert.Equal(0, harness.Query().Items[0].Current);
            Assert.Equal(0, harness.Query().UnlockedCount);
        }

        [Fact]
        public void Record_AccumulatesAcrossCommandsBeforeUnlocking()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50)));

            harness.Record(AchievementMetrics.Kill, 4);
            Assert.Empty(harness.Messages);

            harness.Record(AchievementMetrics.Kill, 6);

            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
            Assert.Equal(10, harness.Query().Items[0].Current);
        }

        [Fact]
        public void Record_HugeAmount_SaturatesWithoutOverflowOrDuplicateUnlock()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1003, "千锤百炼", AchievementMetrics.Kill, 100, 3002, 1)));

            harness.Record(AchievementMetrics.Kill, int.MaxValue);
            harness.Record(AchievementMetrics.Kill, int.MaxValue);

            // 饱和累加：不回绕成负数，也不会因为"再次跨过 target"而重复解锁。
            Assert.Equal(2, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
            Assert.Equal(100, harness.Query().Items[0].Current);
        }

        [Fact]
        public void Record_DifferentMetrics_AccumulateIndependently()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10),
                new AchievementRowSpec(5001, "小有积蓄", AchievementMetrics.Gold, 1000, 3401, 5)));

            harness.Record(AchievementMetrics.Kill, 1);
            harness.Record(AchievementMetrics.Gold, 999);

            Assert.Equal(2, harness.Messages.Count);
            AchievementSnapshot snapshot = harness.Query();
            Assert.True(snapshot.Items[0].IsUnlocked);
            Assert.Equal(1, snapshot.Items[0].Current);
            Assert.False(snapshot.Items[1].IsUnlocked);
            Assert.Equal(999, snapshot.Items[1].Current);
            Assert.Equal(1, snapshot.UnlockedCount);

            harness.Record(AchievementMetrics.Gold, 1);

            Assert.Equal(4, harness.Messages.Count);
            Assert.Equal(2, harness.Query().UnlockedCount);
        }

        [Fact]
        public void Query_BeforeAnyRecord_ReturnsAllLockedRows()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(2001, "初出茅庐", AchievementMetrics.Clear, 1, 3101, 5),
                new AchievementRowSpec(3001, "欧皇启程", AchievementMetrics.Gacha, 1, 3201, 1)));

            AchievementSnapshot snapshot = harness.Query();

            Assert.Equal(2, snapshot.Total);
            Assert.Equal(0, snapshot.UnlockedCount);
            Assert.Equal(2, snapshot.Items.Count);
            Assert.Equal(2001, snapshot.Items[0].AchievementId);
            Assert.Equal("初出茅庐", snapshot.Items[0].Name);
            Assert.Equal(AchievementMetrics.Clear, snapshot.Items[0].Metric);
            Assert.Equal(0, snapshot.Items[0].Current);
            Assert.Equal(1, snapshot.Items[0].Target);
            Assert.False(snapshot.Items[0].IsUnlocked);
            Assert.Equal(3001, snapshot.Items[1].AchievementId);
        }

        [Fact]
        public void Query_AfterUnlock_TruncatesCurrentToTargetAndCarriesAllFields()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50)));

            harness.Record(AchievementMetrics.Kill, 25);

            AchievementSnapshot snapshot = harness.Query();
            AchievementProgress progress = snapshot.Items[0];

            // 内部累计 25、展示 Current 截断到 target 10；截断只影响展示，不影响解锁判定。
            Assert.Equal(1, snapshot.Total);
            Assert.Equal(1, snapshot.UnlockedCount);
            Assert.Equal(1002, progress.AchievementId);
            Assert.Equal("百战小将", progress.Name);
            Assert.Equal(AchievementMetrics.Kill, progress.Metric);
            Assert.Equal(10, progress.Current);
            Assert.Equal(10, progress.Target);
            Assert.True(progress.IsUnlocked);
        }

        [Fact]
        public void Query_UnlockedCountTracksLadderProgression()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10),
                new AchievementRowSpec(1002, "百战小将", AchievementMetrics.Kill, 10, 3001, 50),
                new AchievementRowSpec(1003, "千锤百炼", AchievementMetrics.Kill, 100, 3002, 1)));

            harness.Record(AchievementMetrics.Kill, 10);
            Assert.Equal(2, harness.Query().UnlockedCount);

            harness.Record(AchievementMetrics.Kill, 90);
            Assert.Equal(3, harness.Query().UnlockedCount);
        }

        [Fact]
        public void EmptyTable_QueryReturnsEmptySnapshotAndRecordIsNoOp()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows());

            harness.Record(AchievementMetrics.Kill, 100);

            Assert.Empty(harness.Messages);
            AchievementSnapshot snapshot = harness.Query();
            Assert.Equal(0, snapshot.Total);
            Assert.Equal(0, snapshot.UnlockedCount);
            Assert.Empty(snapshot.Items);
        }

        [Fact]
        public void MissingTable_HandlerConstructorThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new AchievementHandler(new AIFramework.Core.Cqe.CqeBus(), AchievementTableStub.WithoutTable()));

            Assert.Contains(AchievementTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Register_RegistersRecordCommandAndSnapshotQuery()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            // 1 个发奖桩 + 切片自己的上报命令 = 2；查询处理器来自切片；事件订阅只有解锁事件桩。
            Assert.Equal(2, harness.Bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, harness.Bus.RegisteredQueryHandlerCount);
            Assert.Equal(1, harness.Bus.SubscriberCount);

            // 经真实总线走一遍完整链路，证明注册确实生效。
            harness.Record(AchievementMetrics.Kill, 1);
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
            Assert.Equal(1, harness.Query().UnlockedCount);
        }

        [Fact]
        public void Unlock_EmitsOnlyGrantAndUnlockEvent_NoOtherResponsibilities()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 10)));

            harness.Record(AchievementMetrics.Kill, 1);

            // 只发 1 条发奖指令 + 1 条解锁事件：不碰红点、不碰背包扣减、不广播额外进度事件。
            Assert.Equal(2, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
            Assert.Empty(harness.MessagesOf<ConsumeItemCommand>());
        }

        [Fact]
        public void Record_ZeroRewardAchievement_StillUnlocksWithoutRewardAmount()
        {
            AchievementHarness harness = AchievementHarness.Create(AchievementTableStub.WithRows(
                new AchievementRowSpec(4001, "签到七日", AchievementMetrics.SignIn, 7, 0, 0)));

            harness.Record(AchievementMetrics.SignIn, 7);

            GrantItemCommand grant = Assert.IsType<GrantItemCommand>(harness.Messages[0]);
            Assert.Equal(0, grant.ItemId);
            Assert.Equal(0, grant.Count);
            Assert.Single(harness.MessagesOf<AchievementUnlockedEvent>());
        }
    }
}

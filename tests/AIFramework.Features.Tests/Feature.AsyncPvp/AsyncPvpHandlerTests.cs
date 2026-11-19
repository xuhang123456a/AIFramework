using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.AsyncPvp;
using Xunit;

namespace AIFramework.Features.Tests.AsyncPvp
{
    /// <summary>
    /// 异步 PVP（T2-14）验收：真实 <c>CqeBus</c> + 本目录私有桩。
    /// 断言只落在"结算结果 / 段位连胜状态 / 实际发出的指令与事件"三处，
    /// 不触碰实时内核，也不联网。
    /// </summary>
    public sealed class AsyncPvpHandlerTests
    {
        // ---------- 得分公式与上限 ----------

        [Fact]
        public void ScoreFormula_AddsSeedModulo100ToPowerTimesTen()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 10, defenderPower: 20, randomSeed: 55);

            // 进攻 10*10+55=155；防守 20*10+55=255 → 分低者负。
            Assert.Equal(155, result.AttackerScore);
            Assert.Equal(255, result.DefenderScore);
            Assert.False(result.AttackerWon);
        }

        [Fact]
        public void HigherScore_WinsTheDuel()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 7);

            // 307 vs 207：分高者胜，与战力差无关。
            Assert.Equal(307, result.AttackerScore);
            Assert.Equal(207, result.DefenderScore);
            Assert.True(result.AttackerWon);
        }

        [Fact]
        public void AttackerScore_IsCappedAt9999()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 1000, defenderPower: 10, randomSeed: 40);

            // 1000*10+40 超出上限，必须夹到 9999；防守方 10*10+40=140 不夹。
            Assert.Equal(9999, result.AttackerScore);
            Assert.Equal(140, result.DefenderScore);
            Assert.True(result.AttackerWon);
        }

        [Fact]
        public void DefenderScore_IsCappedAt9999Too()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 999, defenderPower: 1000, randomSeed: 9);

            // 双方都被夹到 9999：此时按 power 决胜，999 < 1000 → 进攻方负。
            Assert.Equal(9999, result.AttackerScore);
            Assert.Equal(9999, result.DefenderScore);
            Assert.False(result.AttackerWon);
        }

        // ---------- 胜负三种情形 ----------

        [Fact]
        public void TieScore_HigherPowerWins()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 999, defenderPower: 1000, randomSeed: 9);

            Assert.Equal(result.AttackerScore, result.DefenderScore);
            Assert.True(1000 > 999);
            Assert.False(result.AttackerWon);
        }

        [Fact]
        public void TieScoreAndEqualPower_AttackerWinsByDocumentedRule()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 1000, defenderPower: 1000, randomSeed: 3);

            // 完全平手（分数与战力都相同）：口径固定为"平局进攻方略优"。
            Assert.Equal(9999, result.AttackerScore);
            Assert.Equal(9999, result.DefenderScore);
            Assert.True(result.AttackerWon);
        }

        // ---------- 段位与连胜 ----------

        [Fact]
        public void Win_IncreasesRatingBy20()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 7);

            Assert.Equal(20, result.RatingDelta);
            Assert.Equal(1020, harness.Handler.Rating);
            Assert.Equal(1, harness.Handler.Wins);
        }

        [Fact]
        public void Loss_DecreasesRatingBy15()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0);

            Assert.Equal(-15, result.RatingDelta);
            Assert.Equal(985, harness.Handler.Rating);
            Assert.Equal(1, harness.Handler.Losses);
        }

        [Fact]
        public void ThreeConsecutiveWins_GiveRatingBonusFromTheThirdWin()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();
            int[] deltas = new int[3];

            for (int i = 0; i < deltas.Length; i++)
            {
                deltas[i] = harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0).RatingDelta;
            }

            // 第 1、2 胜 +20；第 3 胜（结算后连胜=3）起 +30。
            Assert.Equal(new[] { 20, 20, 30 }, deltas);
            Assert.Equal(1070, harness.Handler.Rating);
            Assert.Equal(3, harness.Handler.Streak);
            Assert.Equal(3, harness.Handler.Wins);
        }

        [Fact]
        public void FourConsecutiveWins_KeepTheStreakBonus()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();
            int[] deltas = new int[4];

            for (int i = 0; i < deltas.Length; i++)
            {
                deltas[i] = harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0).RatingDelta;
            }

            Assert.Equal(new[] { 20, 20, 30, 30 }, deltas);
            Assert.Equal(1100, harness.Handler.Rating);
            Assert.Equal(4, harness.Handler.Streak);
        }

        [Fact]
        public void ThreeConsecutiveLosses_GiveRatingPenaltyFromTheThirdLoss()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();
            int[] deltas = new int[3];

            for (int i = 0; i < deltas.Length; i++)
            {
                deltas[i] = harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0).RatingDelta;
            }

            Assert.Equal(new[] { -15, -15, -20 }, deltas);
            Assert.Equal(950, harness.Handler.Rating);
            Assert.Equal(-3, harness.Handler.Streak);
            Assert.Equal(3, harness.Handler.Losses);
        }

        [Fact]
        public void RatingIsFlooredAtZero_AndDeltaReportsActualChange()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create(initialRating: 10);

            AsyncDuelResult firstLoss = harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0);
            Assert.Equal(0, harness.Handler.Rating);
            Assert.Equal(-10, firstLoss.RatingDelta);

            // 已经贴底：再输一场实际变化是 0，而不是 -15。
            AsyncDuelResult secondLoss = harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0);
            Assert.Equal(0, harness.Handler.Rating);
            Assert.Equal(0, secondLoss.RatingDelta);
        }

        [Fact]
        public void WinThenLoss_ResetsStreakToNegativeOneAndCountsBoth()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            Assert.Equal(1, harness.Handler.Streak);

            harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0);

            // 胜 1 后负 1：连胜清零直接转 -1，而不是 0 或 -6。
            Assert.Equal(-1, harness.Handler.Streak);
            Assert.Equal(1, harness.Handler.Wins);
            Assert.Equal(1, harness.Handler.Losses);
            Assert.Equal(1005, harness.Handler.Rating);
        }

        // ---------- 发奖 ----------

        [Fact]
        public void Win_Grants20GoldWithDuelReasonTag()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 7);

            GrantItemCommand grant = Assert.Single(harness.Rewards.Grants);
            Assert.Equal(MetaItemIds.Gold, grant.ItemId);
            Assert.Equal(20, grant.Count);
            Assert.Equal(6801, grant.ReasonTagId);
        }

        [Fact]
        public void Loss_GrantsNothing()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: 10, defenderPower: 60, randomSeed: 0);

            Assert.Empty(harness.Rewards.Grants);
        }

        [Fact]
        public void ThirdWin_GrantsRetroactiveCardAtStreakMilestone()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            Assert.Equal(2, harness.Rewards.Grants.Count);

            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);

            // 第 3 胜：先金币、再里程碑补签卡。
            Assert.Equal(4, harness.Rewards.Grants.Count);
            GrantItemCommand card = harness.Rewards.Grants[3];
            Assert.Equal(MetaItemIds.RetroactiveCard, card.ItemId);
            Assert.Equal(1, card.Count);
            Assert.Equal(6802, card.ReasonTagId);
        }

        [Fact]
        public void SixthWin_GrantsSecondMilestoneCard()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            for (int i = 0; i < 6; i++)
            {
                harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            }

            // 6 场胜利 = 6 条金币 + 2 条里程碑补签卡（连胜 3、6）。
            Assert.Equal(8, harness.Rewards.Grants.Count);
            Assert.Equal(MetaItemIds.RetroactiveCard, harness.Rewards.Grants[3].ItemId);
            Assert.Equal(MetaItemIds.RetroactiveCard, harness.Rewards.Grants[7].ItemId);
            Assert.Equal(1160, harness.Handler.Rating);
        }

        // ---------- 事件与状态 ----------

        [Fact]
        public void Duel_PublishesResolvedEvent_AndUpdatesLastResultAndCount()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            AsyncDuelResult result = harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 7, defenderId: 777);

            AsyncDuelResolvedEvent resolved = Assert.Single(harness.Bus.PublishedOf<AsyncDuelResolvedEvent>());
            Assert.Equal(result.AttackerId, resolved.Result.AttackerId);
            Assert.Equal(result.DefenderId, resolved.Result.DefenderId);
            Assert.Equal(result.AttackerScore, resolved.Result.AttackerScore);
            Assert.Equal(result.DefenderScore, resolved.Result.DefenderScore);
            Assert.Equal(result.AttackerWon, resolved.Result.AttackerWon);
            Assert.Equal(result.RatingDelta, resolved.Result.RatingDelta);

            Assert.Equal(1, result.AttackerId);
            Assert.Equal(777, result.DefenderId);
            Assert.Equal(1, harness.Handler.DuelCount);
            Assert.Equal(result.AttackerScore, harness.Handler.LastResult.AttackerScore);
        }

        [Fact]
        public void RejectedDuel_DoesNotPublishEventOrTouchState()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: -1, defenderPower: 10, randomSeed: 0);

            Assert.Empty(harness.Bus.PublishedOf<AsyncDuelResolvedEvent>());
            Assert.Empty(harness.Rewards.Grants);
            Assert.Equal(0, harness.Handler.DuelCount);
            Assert.Equal(1000, harness.Handler.Rating);
            Assert.Equal(0, harness.Handler.Wins);
            Assert.Equal(0, harness.Handler.Losses);
            Assert.Equal(0, harness.Handler.Streak);
            Assert.Equal(0, harness.Handler.LastResult.AttackerId);

            // 命令本身上了总线（这是调用方发的），但切片没有任何输出。
            Assert.Single(harness.Bus.SentCommands);
        }

        // ---------- 非法输入 ----------

        [Theory]
        [InlineData(-1)]
        [InlineData(1001)]
        public void InvalidAttackerPower_IsSilentlyRejected(int attackerPower)
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower, defenderPower: 10, randomSeed: 0);

            Assert.Empty(harness.Bus.PublishedOf<AsyncDuelResolvedEvent>());
            Assert.Empty(harness.Rewards.Grants);
            Assert.Equal(0, harness.Handler.DuelCount);
            Assert.Equal(1000, harness.Handler.Rating);
            Assert.Equal(0, harness.Handler.Streak);
        }

        [Fact]
        public void InvalidDefenderPower_IsSilentlyRejected()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Duel(attackerPower: 100, defenderPower: -1, randomSeed: 0);

            Assert.Empty(harness.Bus.PublishedOf<AsyncDuelResolvedEvent>());
            Assert.Empty(harness.Rewards.Grants);
            Assert.Equal(0, harness.Handler.DuelCount);
            Assert.Equal(1000, harness.Handler.Rating);
            Assert.Equal(0, harness.Handler.LastResult.AttackerId);
        }

        // ---------- 对手池确定性 ----------

        [Fact]
        public void OpponentsQuery_ReturnsThreeDeterministicOfflineOpponents()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            PvpOpponentList list = harness.Handler.Handle(new PvpOpponentsQueryRequest());

            Assert.Equal(3, list.Opponents.Length);
            AssertOpponent(list.Opponents[0], playerId: 100, power: 50, randomSeed: 20261120, layoutId: 0);
            AssertOpponent(list.Opponents[1], playerId: 101, power: 57, randomSeed: 20261133, layoutId: 1);
            AssertOpponent(list.Opponents[2], playerId: 102, power: 64, randomSeed: 20261146, layoutId: 2);
        }

        [Fact]
        public void OpponentsQuery_ChangesWithDuelCount_AndIsReproducible()
        {
            AsyncPvpHarness first = AsyncPvpHarness.Create();
            PvpOpponentList beforeAnyDuel = first.Handler.Handle(new PvpOpponentsQueryRequest());

            first.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            PvpOpponentList afterOneDuel = first.Handler.Handle(new PvpOpponentsQueryRequest());

            first.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            PvpOpponentList afterTwoDuels = first.Handler.Handle(new PvpOpponentsQueryRequest());

            // 场次推进换一批对手（槽位整体平移），而不是永远同一批。
            Assert.NotEqual(beforeAnyDuel.Opponents[0].PlayerId, afterOneDuel.Opponents[0].PlayerId);
            Assert.NotEqual(afterOneDuel.Opponents[0].PlayerId, afterTwoDuels.Opponents[0].PlayerId);
            Assert.NotEqual(beforeAnyDuel.Opponents[1].RandomSeed, afterOneDuel.Opponents[1].RandomSeed);

            // 同一进度、同一顺序重放：内容逐字段一致。
            AsyncPvpHarness second = AsyncPvpHarness.Create();
            AssertSameOpponents(beforeAnyDuel, second.Handler.Handle(new PvpOpponentsQueryRequest()));

            second.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);
            AssertSameOpponents(afterOneDuel, second.Handler.Handle(new PvpOpponentsQueryRequest()));
        }

        // ---------- 快照隔离 ----------

        [Fact]
        public void ProfileQuery_ReturnsSnapshotThatDoesNotTrackLaterChanges()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            PvpProfileSnapshot initial = harness.Handler.Handle(new PvpProfileQueryRequest());
            Assert.Equal(1, initial.PlayerId);
            Assert.Equal(1000, initial.Rating);
            Assert.Equal(0, initial.Wins);
            Assert.Equal(0, initial.Losses);
            Assert.Equal(0, initial.Streak);

            harness.Duel(attackerPower: 30, defenderPower: 20, randomSeed: 0);

            PvpProfileSnapshot current = harness.Handler.Handle(new PvpProfileQueryRequest());
            Assert.Equal(1020, current.Rating);
            Assert.Equal(1, current.Wins);
            Assert.Equal(1, current.Streak);

            // 旧快照是独立对象，不被后续状态变更回写。
            Assert.Equal(1000, initial.Rating);
            Assert.Equal(0, initial.Wins);
        }

        [Fact]
        public void OpponentsQuery_ReturnedArrayMutation_DoesNotAffectNextQuery()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            PvpOpponentList first = harness.Handler.Handle(new PvpOpponentsQueryRequest());
            DefenseSnapshot original = first.Opponents[0];

            // 模拟 UI 侧误改快照数组：切片内部状态不得被污染。
            first.Opponents[0] = new DefenseSnapshot(9999, 9999, 9999, 9999);

            PvpOpponentList second = harness.Handler.Handle(new PvpOpponentsQueryRequest());
            AssertOpponent(second.Opponents[0], original.PlayerId, original.Power, original.RandomSeed, original.LayoutId);
        }

        [Fact]
        public void OpponentListConstructor_CopiesIncomingArray()
        {
            DefenseSnapshot[] raw = new[] { new DefenseSnapshot(7, 70, 700, 1) };

            PvpOpponentList snapshot = new PvpOpponentList(raw);
            raw[0] = new DefenseSnapshot(8, 80, 800, 2);

            Assert.Equal(7, snapshot.Opponents[0].PlayerId);
            Assert.Equal(70, snapshot.Opponents[0].Power);
        }

        [Fact]
        public void Queries_AreReadOnly_AndDoNotMoveDuelState()
        {
            AsyncPvpHarness harness = AsyncPvpHarness.Create();

            harness.Handler.Handle(new PvpProfileQueryRequest());
            harness.Handler.Handle(new PvpOpponentsQueryRequest());

            Assert.Equal(0, harness.Handler.DuelCount);
            Assert.Equal(1000, harness.Handler.Rating);
            Assert.Empty(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        // ---------- 装配 ----------

        [Fact]
        public void Register_ThenUseThroughRealBus()
        {
            CqeBus bus = new CqeBus();
            AsyncPvpRewardSink rewards = new AsyncPvpRewardSink();
            bus.RegisterCommandHandler<GrantItemCommand>(rewards);

            AsyncPvpHandler handler = new AsyncPvpHandler(bus);
            handler.Register(bus);

            AsyncPvpEventSink events = new AsyncPvpEventSink();
            bus.Subscribe(events);

            bus.Send(new AsyncDuelCommand(new DefenseSnapshot(500, 10, 0, 0), 50));

            Assert.Single(events.Results);
            Assert.Equal(handler.LastResult.AttackerScore, events.Results[0].AttackerScore);
            Assert.Equal(1, handler.DuelCount);
            Assert.Single(rewards.Grants);

            PvpProfileSnapshot profile = bus.Query<PvpProfileQueryRequest, PvpProfileSnapshot>(
                new PvpProfileQueryRequest());
            Assert.Equal(1020, profile.Rating);
            Assert.Equal(1, profile.Wins);

            PvpOpponentList opponents = bus.Query<PvpOpponentsQueryRequest, PvpOpponentList>(
                new PvpOpponentsQueryRequest());
            Assert.Equal(3, opponents.Opponents.Length);
        }

        [Fact]
        public void NullBus_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new AsyncPvpHandler(null!);
                });

            AsyncPvpHandler handler = new AsyncPvpHandler(new AsyncPvpRecordingBus());
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }

        // ---------- 断言辅助 ----------

        private static void AssertOpponent(DefenseSnapshot opponent, int playerId, int power, int randomSeed, int layoutId)
        {
            Assert.Equal(playerId, opponent.PlayerId);
            Assert.Equal(power, opponent.Power);
            Assert.Equal(randomSeed, opponent.RandomSeed);
            Assert.Equal(layoutId, opponent.LayoutId);
        }

        private static void AssertSameOpponents(PvpOpponentList expected, PvpOpponentList actual)
        {
            Assert.Equal(expected.Opponents.Length, actual.Opponents.Length);
            for (int i = 0; i < expected.Opponents.Length; i++)
            {
                AssertOpponent(
                    actual.Opponents[i],
                    expected.Opponents[i].PlayerId,
                    expected.Opponents[i].Power,
                    expected.Opponents[i].RandomSeed,
                    expected.Opponents[i].LayoutId);
            }
        }
    }
}

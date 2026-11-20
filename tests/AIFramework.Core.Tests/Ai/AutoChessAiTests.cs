#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ai;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Ai;
using Xunit;

namespace AIFramework.Core.Tests.Ai
{
    /// <summary>
    /// <see cref="AutoChessAi"/> 的策略契约验收：八条规则（超编卖出 / 凑星 / 补满棋盘 / 升人口 /
    /// 攒钱 / 低血搜牌 / 上场 / 等待）的触发条件与优先级、经济下限的可配置性、同价候选的随机与确定性、
    /// 空棋盘/空商店/默认状态/零金币等退化输入、诊断计数与 Reset，以及"决策不改输入 + 预热后零分配"两条硬约束。
    /// <para>
    /// <b>为什么逐个规则钉优先级</b>：AI 的错误通常不是崩溃，而是"某个分支永远不生效"或"低血还在攒钱"
    /// 这类静默策略错；真机上只能靠手感发现。这里把每条规则的触发边界和"谁压谁"写成断言，
    /// 以后调阈值/加规则时，行为变化会立刻红灯，而不是悄悄改变 AI 的强度。
    /// </para>
    /// <para>
    /// <b>测试口径</b>：全部是纯内存构造的状态快照，不依赖内核、不跑战斗；"棋盘坐标"只用于验证建议落点，
    /// 坐标轴方向与尺寸由用例自己约定（8 列 × 4 行）。
    /// </para>
    /// </summary>
    public sealed class AutoChessAiTests
    {
        /// <summary>默认经济下限，与 <see cref="AutoChessAi"/> 的无参默认值一致。</summary>
        private const int DefaultFloor = 50;

        // ────────────────────────────────────────────────────────────────
        // 构造与诊断计数
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Constructor_NegativeEconomyFloor_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AutoChessAi(null, -1));
        }

        [Fact]
        public void DecisionsMade_CountsEveryCallIncludingWait()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(gold: 100, capacity: 4);

            Assert.Equal(0, ai.DecisionsMade);

            ai.Decide(in state);
            ai.Decide(in state);
            ai.Decide(in state);

            Assert.Equal(3, ai.DecisionsMade);
        }

        [Fact]
        public void Reset_ClearsCounterButKeepsPolicy()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(gold: 100, capacity: 4);

            AutoChessDecision before = ai.Decide(in state);
            ai.Decide(in state);
            ai.Reset();

            Assert.Equal(0, ai.DecisionsMade);

            AutoChessDecision after = ai.Decide(in state);
            Assert.Equal(before.Action, after.Action);
            Assert.Equal(before.Reason, after.Reason);
            Assert.Equal(1, ai.DecisionsMade);
        }

        // ────────────────────────────────────────────────────────────────
        // 优先级（相邻规则谁压谁）
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_UpgradeCopy_BeatsFillBoard()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                shop: new[] { UpgradeCopy(21, 3), NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Buy, decision.Action);
            Assert.Equal(21, decision.TargetUnitId);
            Assert.Equal("upgrade copy", decision.Reason);
            Assert.Equal(-1, decision.GridX);
            Assert.Equal(-1, decision.GridY);
        }

        [Fact]
        public void Decide_UpgradeCopy_BeatsReroll()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 25,
                hp: 10,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0) },
                shop: new[] { UpgradeCopy(21, 2) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Buy, decision.Action);
            Assert.Equal("upgrade copy", decision.Reason);
            Assert.Equal(21, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_LevelUp_BeatsEconomyHoldAndReroll()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                level: 2,
                hp: 10,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.LevelUp, decision.Action);
            Assert.Equal("level up", decision.Reason);
        }

        [Fact]
        public void Decide_EconomyHold_BeatsPlacement()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                hp: 100,
                capacity: 4,
                shop: new[] { OwnedUnit(21) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
        }

        [Fact]
        public void Decide_Reroll_BeatsPlacement()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 25,
                hp: 20,
                capacity: 4,
                shop: new[] { OwnedUnit(21) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Reroll, decision.Action);
            Assert.Equal("desperate reroll", decision.Reason);
        }

        [Fact]
        public void Decide_Placement_BeatsNoop()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                capacity: 4,
                shop: new[] { OwnedUnit(21) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Buy, decision.Action);
            Assert.Equal(21, decision.TargetUnitId);
            Assert.Equal("place unit", decision.Reason);
            Assert.Equal(0, decision.GridX);
            Assert.Equal(0, decision.GridY);
        }

        [Fact]
        public void Decide_OverCapacity_BeatsUpgradeCopy()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 1,
                board: new[] { OnBoard(1, 0, 0, frontline: true), OnBoard(2, 1, 0) },
                shop: new[] { UpgradeCopy(21, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Sell, decision.Action);
            Assert.Equal("over capacity", decision.Reason);
            Assert.Equal(2, decision.TargetUnitId);
        }

        // ────────────────────────────────────────────────────────────────
        // 凑星
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_UpgradeCopy_RequiresAffordableCopy()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 2,
                shop: new[] { UpgradeCopy(21, 3), NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(31, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_UpgradeCopy_PrefersMostExpensiveAffordable()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                shop: new[] { UpgradeCopy(21, 2), UpgradeCopy(22, 4), UpgradeCopy(23, 11) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("upgrade copy", decision.Reason);
            Assert.Equal(22, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_UpgradeCopy_IgnoresCopiesNeededOtherThanOne()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                shop: new[]
                {
                    new AutoChessShopSlot(21, 1, true, 2),
                    new AutoChessShopSlot(22, 1, true, 0),
                    NewUnit(31, 2),
                });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(31, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_UpgradeCopy_NoRandomPicksLowestUnitId()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                shop: new[] { UpgradeCopy(23, 2), UpgradeCopy(22, 2) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(22, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_UpgradeCopy_FallsThroughWhenTooPoorForAny()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 1,
                hp: 100,
                shop: new[] { UpgradeCopy(21, 3) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
        }

        // ────────────────────────────────────────────────────────────────
        // 补满棋盘
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_FillBoard_PicksCheapestAffordable()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 5,
                shop: new[] { NewUnit(31, 5), NewUnit(32, 2), NewUnit(33, 3) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Buy, decision.Action);
            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(32, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_FillBoard_IgnoresOwnedSlots()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                shop: new[] { OwnedUnit(21, 1), NewUnit(31, 3) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(31, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_FillBoard_SuggestsFirstFreeCell()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 4,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) },
                shop: new[] { NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(2, decision.GridX);
            Assert.Equal(0, decision.GridY);
        }

        [Fact]
        public void Decide_FillBoard_WrapsToSecondRow()
        {
            var board = new AutoChessBoardUnit[8];
            for (int i = 0; i < board.Length; i++)
            {
                board[i] = OnBoard(i + 1, i, 0);
            }

            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 9,
                board: board,
                shop: new[] { NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("fill board", decision.Reason);
            Assert.Equal(0, decision.GridX);
            Assert.Equal(1, decision.GridY);
        }

        [Fact]
        public void Decide_EmptyBoard_SuggestsOrigin()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 4,
                shop: new[] { NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(0, decision.GridX);
            Assert.Equal(0, decision.GridY);
        }

        // ────────────────────────────────────────────────────────────────
        // 升人口与经济下限
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_LevelUp_RequiresFullBoard()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                level: 2,
                capacity: 4,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("noop", decision.Reason);
        }

        [Fact]
        public void Decide_LevelUp_RequiresEconomyFloor()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 49,
                level: 2,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
        }

        [Fact]
        public void Decide_LevelUp_StopsAtLevelSix()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 100,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("noop", decision.Reason);
        }

        [Fact]
        public void Decide_DefaultEconomyFloorIsFifty()
        {
            var ai = new AutoChessAi();
            AutoChessState atFloor = State(
                gold: DefaultFloor,
                level: 2,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });
            AutoChessState belowFloor = State(
                gold: DefaultFloor - 1,
                level: 2,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            Assert.Equal(AutoChessAiAction.LevelUp, ai.Decide(in atFloor).Action);
            Assert.Equal("economy hold", ai.Decide(in belowFloor).Reason);
        }

        [Fact]
        public void Decide_EconomyFloorIsConfigurable()
        {
            AutoChessState state = State(
                gold: 0,
                level: 2,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            var strict = new AutoChessAi(null, DefaultFloor);
            var relaxed = new AutoChessAi(null, 0);

            Assert.Equal("economy hold", strict.Decide(in state).Reason);
            Assert.Equal(AutoChessAiAction.LevelUp, relaxed.Decide(in state).Action);
        }

        // ────────────────────────────────────────────────────────────────
        // 低血量搜牌
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_LowHp_RerollsBelowEconomyFloor()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 25,
                hp: 30,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Reroll, decision.Action);
            Assert.Equal("desperate reroll", decision.Reason);
        }

        [Fact]
        public void Decide_LowHp_ThresholdIsInclusive()
        {
            var ai = new AutoChessAi();
            AutoChessState atThreshold = State(
                gold: 25,
                hp: 30,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });
            AutoChessState aboveThreshold = State(
                gold: 25,
                hp: 31,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            Assert.Equal(AutoChessAiAction.Reroll, ai.Decide(in atThreshold).Action);
            Assert.Equal("economy hold", ai.Decide(in aboveThreshold).Reason);
        }

        [Fact]
        public void Decide_LowHp_RequiresTwentyGold()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 19,
                hp: 5,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
        }

        // ────────────────────────────────────────────────────────────────
        // 上场（已拥有但未上棋盘）
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_Placement_DeploysOwnedBenchUnit()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                capacity: 4,
                board: new[] { OnBoard(1, 0, 0) },
                shop: new[] { OwnedUnit(21), OwnedUnit(1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Buy, decision.Action);
            Assert.Equal(21, decision.TargetUnitId);
            Assert.Equal("place unit", decision.Reason);
            Assert.Equal(1, decision.GridX);
            Assert.Equal(0, decision.GridY);
        }

        [Fact]
        public void Decide_Placement_PrefersLowestUnitId()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                capacity: 4,
                shop: new[] { OwnedUnit(29), OwnedUnit(24) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("place unit", decision.Reason);
            Assert.Equal(24, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_Placement_WaitsWhenBoardIsFull()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) },
                shop: new[] { OwnedUnit(21) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("noop", decision.Reason);
        }

        [Fact]
        public void Decide_BenchEntryInBoard_DoesNotCountAsDeployed()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 60,
                capacity: 2,
                board: new[] { new AutoChessBoardUnit(11, 1, 1, -1, -1, false) },
                shop: new[] { OwnedUnit(11) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal("place unit", decision.Reason);
            Assert.Equal(11, decision.TargetUnitId);
            Assert.Equal(0, decision.GridX);
            Assert.Equal(0, decision.GridY);
        }

        // ────────────────────────────────────────────────────────────────
        // 超编卖出
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_OverCapacity_SellsFrontlineLast()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 1,
                board: new[] { OnBoard(1, 0, 0, frontline: true), OnBoard(2, 1, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Sell, decision.Action);
            Assert.Equal(2, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_OverCapacity_PrefersCheapestWithinBand()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 1,
                board: new[]
                {
                    OnBoard(1, 0, 0, cost: 5),
                    OnBoard(2, 1, 0, cost: 2),
                });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Sell, decision.Action);
            Assert.Equal(2, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_OverCapacity_SellsLowerStarFirst()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: 1,
                board: new[]
                {
                    OnBoard(1, 0, 0, cost: 1, star: 2),
                    OnBoard(2, 1, 0, cost: 5, star: 1),
                });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Sell, decision.Action);
            Assert.Equal(2, decision.TargetUnitId);
        }

        // ────────────────────────────────────────────────────────────────
        // 退化输入
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_EmptyShopAndBoard_WaitsWithNoop()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(gold: 100, capacity: 4);

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("noop", decision.Reason);
        }

        [Fact]
        public void Decide_DefaultState_TreatedAsEmptyAndWaits()
        {
            var ai = new AutoChessAi();
            AutoChessState state = default;

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
            Assert.Equal(-1, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_ZeroGold_CannotBuy()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(gold: 0, shop: new[] { NewUnit(31, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
            Assert.Equal("economy hold", decision.Reason);
        }

        [Fact]
        public void Decide_NegativeGold_CannotBuy()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: -5,
                shop: new[] { NewUnit(31, 1), UpgradeCopy(21, 1) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Wait, decision.Action);
        }

        [Fact]
        public void Decide_NegativeCapacity_TreatedAsZero()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 10,
                capacity: -3,
                board: new[] { OnBoard(1, 0, 0) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(AutoChessAiAction.Sell, decision.Action);
            Assert.Equal("over capacity", decision.Reason);
        }

        // ────────────────────────────────────────────────────────────────
        // 随机与确定性
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_NullRandom_TieBreaksByLowestUnitId()
        {
            var ai = new AutoChessAi(null, DefaultFloor);
            AutoChessState state = State(
                gold: 10,
                shop: new[] { NewUnit(32, 2), NewUnit(31, 2) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(31, decision.TargetUnitId);
        }

        [Fact]
        public void Decide_Random_PicksAmongEqualCost()
        {
            AutoChessState state = State(
                gold: 10,
                shop: new[] { NewUnit(31, 2), NewUnit(32, 2) });

            var first = new AutoChessAi(new FixedRandomSource(0));
            var second = new AutoChessAi(new FixedRandomSource(1));

            Assert.Equal(31, first.Decide(in state).TargetUnitId);
            Assert.Equal(32, second.Decide(in state).TargetUnitId);
        }

        [Fact]
        public void Decide_SingleBestCandidate_DoesNotConsumeRandom()
        {
            var random = new CountingRandomSource();
            var ai = new AutoChessAi(random);
            AutoChessState state = State(
                gold: 10,
                shop: new[] { NewUnit(31, 2), NewUnit(32, 5) });

            AutoChessDecision decision = ai.Decide(in state);

            Assert.Equal(31, decision.TargetUnitId);
            Assert.Equal(0, random.Calls);
        }

        [Fact]
        public void Decide_TiedCandidates_ConsumeOneRandomPerDecision()
        {
            var random = new CountingRandomSource();
            var ai = new AutoChessAi(random);
            AutoChessState state = State(
                gold: 10,
                shop: new[] { NewUnit(31, 2), NewUnit(32, 2) });

            ai.Decide(in state);
            ai.Decide(in state);

            Assert.Equal(2, random.Calls);
        }

        [Fact]
        public void Decide_Random_SameSeedProducesSameSequence()
        {
            AutoChessState state = State(
                gold: 40,
                shop: new[] { NewUnit(31, 2), NewUnit(32, 2), NewUnit(33, 2), NewUnit(34, 2) });

            var left = new AutoChessAi(new SeededRandomSource(7));
            var right = new AutoChessAi(new SeededRandomSource(7));

            var seen = new HashSet<int>();
            for (int i = 0; i < 64; i++)
            {
                AutoChessDecision leftDecision = left.Decide(in state);
                AutoChessDecision rightDecision = right.Decide(in state);

                Assert.Equal(leftDecision.Action, rightDecision.Action);
                Assert.Equal(leftDecision.TargetUnitId, rightDecision.TargetUnitId);
                Assert.Equal(leftDecision.Reason, rightDecision.Reason);
                seen.Add(leftDecision.TargetUnitId);
            }

            Assert.Equal(64, left.DecisionsMade);
            Assert.Equal(64, right.DecisionsMade);
            Assert.True(seen.Count > 1, "同一随机流下应能挑到不同候选，否则随机分支没生效。");
        }

        // ────────────────────────────────────────────────────────────────
        // 稳定性：不改输入、状态字段不参与、零分配
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_DoesNotMutateState()
        {
            var board = new[] { OnBoard(1, 0, 0, cost: 2), OnBoard(2, 1, 0, cost: 3, star: 2) };
            var shop = new[] { NewUnit(31, 2), UpgradeCopy(32, 1) };
            AutoChessState state = State(gold: 10, capacity: 3, board: board, shop: shop);

            var boardBefore = (AutoChessBoardUnit[])board.Clone();
            var shopBefore = (AutoChessShopSlot[])shop.Clone();

            var ai = new AutoChessAi();
            for (int i = 0; i < 5; i++)
            {
                ai.Decide(in state);
            }

            Assert.Equal(boardBefore.Length, board.Length);
            for (int i = 0; i < boardBefore.Length; i++)
            {
                Assert.Equal(boardBefore[i].UnitId, board[i].UnitId);
                Assert.Equal(boardBefore[i].Cost, board[i].Cost);
                Assert.Equal(boardBefore[i].Star, board[i].Star);
                Assert.Equal(boardBefore[i].GridX, board[i].GridX);
                Assert.Equal(boardBefore[i].GridY, board[i].GridY);
                Assert.Equal(boardBefore[i].IsFrontline, board[i].IsFrontline);
            }

            Assert.Equal(shopBefore.Length, shop.Length);
            for (int i = 0; i < shopBefore.Length; i++)
            {
                Assert.Equal(shopBefore[i].UnitId, shop[i].UnitId);
                Assert.Equal(shopBefore[i].Cost, shop[i].Cost);
                Assert.Equal(shopBefore[i].IsOwned, shop[i].IsOwned);
                Assert.Equal(shopBefore[i].CopiesNeededToUpgrade, shop[i].CopiesNeededToUpgrade);
            }
        }

        [Fact]
        public void Decide_RoundAndStreakDoNotAffectPolicy()
        {
            var ai = new AutoChessAi();
            AutoChessState baseline = State(
                gold: 25,
                hp: 20,
                round: 1,
                streak: 0,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });
            AutoChessState variant = State(
                gold: 25,
                hp: 20,
                round: 17,
                streak: -6,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });

            AutoChessDecision baselineDecision = ai.Decide(in baseline);
            AutoChessDecision variantDecision = ai.Decide(in variant);

            Assert.Equal(baselineDecision.Action, variantDecision.Action);
            Assert.Equal(baselineDecision.TargetUnitId, variantDecision.TargetUnitId);
            Assert.Equal(baselineDecision.Reason, variantDecision.Reason);
        }

        [Fact]
        public void Decide_NonUnitActions_HaveMinusOneTargets()
        {
            var ai = new AutoChessAi();
            AutoChessState levelUp = State(
                gold: 60,
                level: 2,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });
            AutoChessState reroll = State(
                gold: 25,
                hp: 20,
                level: 6,
                capacity: 2,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) });
            AutoChessState noop = State(gold: 100, capacity: 4);

            AutoChessDecision[] decisions =
            {
                ai.Decide(in levelUp),
                ai.Decide(in reroll),
                ai.Decide(in noop),
            };

            for (int i = 0; i < decisions.Length; i++)
            {
                Assert.Equal(-1, decisions[i].TargetUnitId);
                Assert.Equal(-1, decisions[i].GridX);
                Assert.Equal(-1, decisions[i].GridY);
                Assert.False(string.IsNullOrEmpty(decisions[i].Reason));
            }
        }

        [Fact]
        public void Decide_AfterWarmup_DoesNotAllocate()
        {
            var ai = new AutoChessAi();
            AutoChessState state = State(
                gold: 40,
                capacity: 4,
                board: new[] { OnBoard(1, 0, 0), OnBoard(2, 1, 0) },
                shop: new[] { NewUnit(31, 2), NewUnit(32, 3), UpgradeCopy(33, 1) });

            for (int i = 0; i < 200; i++)
            {
                ai.Decide(in state);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                ai.Decide(in state);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        // ────────────────────────────────────────────────────────────────
        // 构造辅助
        // ────────────────────────────────────────────────────────────────

        private static AutoChessState State(
            int gold = 0,
            int level = 1,
            int round = 1,
            int hp = 100,
            int capacity = 4,
            AutoChessBoardUnit[]? board = null,
            AutoChessShopSlot[]? shop = null,
            int streak = 0)
        {
            return new AutoChessState(
                gold,
                level,
                round,
                hp,
                capacity,
                board ?? Array.Empty<AutoChessBoardUnit>(),
                shop ?? Array.Empty<AutoChessShopSlot>(),
                streak);
        }

        private static AutoChessBoardUnit OnBoard(
            int unitId,
            int gridX,
            int gridY,
            int cost = 1,
            int star = 1,
            bool frontline = false)
        {
            return new AutoChessBoardUnit(unitId, cost, star, gridX, gridY, frontline);
        }

        private static AutoChessShopSlot NewUnit(int unitId, int cost)
        {
            return new AutoChessShopSlot(unitId, cost, false, 2);
        }

        private static AutoChessShopSlot OwnedUnit(int unitId, int cost = 3)
        {
            return new AutoChessShopSlot(unitId, cost, true, 2);
        }

        private static AutoChessShopSlot UpgradeCopy(int unitId, int cost)
        {
            return new AutoChessShopSlot(unitId, cost, true, 1);
        }

        /// <summary>固定偏移的随机源：<c>NextInt</c> 恒返回 <c>min + (offset % span)</c>，用于断言"随机分支真的被走到"。</summary>
        private sealed class FixedRandomSource : IRandomSource
        {
            private readonly int _offset;

            internal FixedRandomSource(int offset)
            {
                _offset = offset;
            }

            public int NextInt(int minInclusive, int maxExclusive)
            {
                int span = maxExclusive - minInclusive;
                if (span <= 0)
                {
                    return minInclusive;
                }

                return minInclusive + (_offset % span);
            }
        }

        /// <summary>计数随机源：恒返回区间下界，只关心"策略有没有消耗随机数"。</summary>
        private sealed class CountingRandomSource : IRandomSource
        {
            internal int Calls { get; private set; }

            public int NextInt(int minInclusive, int maxExclusive)
            {
                Calls++;
                return minInclusive;
            }
        }

        /// <summary>线性同余随机源：同一 seed 必然产出同一串序列，用于"同种子同结果"的断言。</summary>
        private sealed class SeededRandomSource : IRandomSource
        {
            private uint _state;

            internal SeededRandomSource(int seed)
            {
                _state = unchecked((uint)seed);
            }

            public int NextInt(int minInclusive, int maxExclusive)
            {
                int span = maxExclusive - minInclusive;
                if (span <= 0)
                {
                    return minInclusive;
                }

                _state = unchecked((_state * 1664525u) + 1013904223u);
                return minInclusive + (int)(_state % (uint)span);
            }
        }
    }
}

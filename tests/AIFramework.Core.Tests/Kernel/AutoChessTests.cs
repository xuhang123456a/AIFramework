using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.AutoChess;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="AutoChessWorld"/> 的自走棋契约：初始布阵与九字段读数、放置/挪位/出售的准入与拒绝
    /// （越界、跨半场、被占、敌方单位、未放置）、战斗结算的确定性、基地血量与对局收口、
    /// <c>EndRound</c> 清场、<c>Step</c> 计数、结算门禁与双条战利品，以及输入/战斗模拟的零分配。
    /// <para>
    /// 用例全部走公开 API（<see cref="IAutoChessReadView"/> / <see cref="IAutoChessInput"/>），不反射内核内部数组；
    /// 坐标一律是"x 向右、y 向上、玩家半场 y=0..3"的棋盘坐标。
    /// </para>
    /// <para>
    /// <b>为什么敢写死血量与位置</b>：自走棋的核心验收点就是"确定性"——同种子同布局必须逐位复现。
    /// 把战斗中期的血量、位置钉成断言值，等于把这条口径焊死；任何改动走位顺序/伤害结算的提交都会立刻红灯，
    /// 而不是悄悄改变战斗结果。
    /// </para>
    /// </summary>
    public sealed class AutoChessTests
    {
        private const int BoardWidth = 8;

        private const int BoardHeight = 8;

        private const int PlayerRowCount = 4;

        private const int DefaultSeed = 20261120;

        private const int InitialBaseHp = 100;

        private const int BaseHpPerRound = 10;

        private const int RoundsToKillBase = InitialBaseHp / BaseHpPerRound;

        private const int PlayerUnitHp = 100;

        private const int PlayerUnitAttack = 20;

        private const int EnemyUnitHp = 80;

        private const int EnemyUnitAttack = 12;

        private const int PlayerUnitId = 1;

        private const int SecondPlayerUnitId = 2;

        private const int ThirdPlayerUnitId = 3;

        private const int EnemyUnitId = 11;

        // ── 初始布阵与读数 ──────────────────────────────────────────────

        [Fact]
        public void Constructor_PreparesEmptyBattlefieldWithFullBaseHp()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.Equal(BoardWidth, world.Width);
            Assert.Equal(BoardHeight, world.Height);
            Assert.Equal(0, world.RoundIndex);
            Assert.Equal(InitialBaseHp, world.PlayerHp);
            Assert.Equal(InitialBaseHp, world.EnemyHp);
            Assert.Equal(3, world.PlayerUnitCount);
            Assert.Equal(3, world.EnemyUnitCount);
            Assert.Equal(0, world.Wins);
            Assert.Equal(0, world.Losses);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.False(world.IsBattleResolved);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void Constructor_PutsPlayerUnitsOnBenchAndEnemiesOnOpponentHalf()
        {
            AutoChessWorld world = CreateWorld(3);

            for (int i = 0; i < 3; i++)
            {
                ChessUnitView unit = world.GetUnit(i);
                Assert.Equal(PlayerUnitId + i, unit.UnitId);
                Assert.Equal(0, unit.Owner);
                Assert.Equal(-1, unit.X);
                Assert.Equal(-1, unit.Y);
                Assert.Equal(PlayerUnitHp, unit.Hp);
                Assert.Equal(PlayerUnitHp, unit.MaxHp);
                Assert.Equal(PlayerUnitAttack, unit.Attack);
                Assert.Equal(1, unit.Range);
                Assert.True(unit.Alive);
            }

            for (int j = 0; j < 3; j++)
            {
                ChessUnitView unit = world.GetUnit(3 + j);
                Assert.Equal(EnemyUnitId + j, unit.UnitId);
                Assert.Equal(1, unit.Owner);
                Assert.Equal(j, unit.X);
                Assert.Equal(PlayerRowCount, unit.Y);
                Assert.Equal(EnemyUnitHp, unit.Hp);
                Assert.Equal(EnemyUnitHp, unit.MaxHp);
                Assert.Equal(EnemyUnitAttack, unit.Attack);
                Assert.Equal(1, unit.Range);
                Assert.True(unit.Alive);
            }
        }

        [Fact]
        public void Constructor_ClampsUnitCountToOneToThree()
        {
            Assert.Equal(4, CreateWorld(1).PlayerUnitCount + CreateWorld(1).EnemyUnitCount);
            Assert.Equal(5, CreateWorld(2).PlayerUnitCount + CreateWorld(2).EnemyUnitCount);
            Assert.Equal(6, CreateWorld(3).PlayerUnitCount + CreateWorld(3).EnemyUnitCount);

            // 越界输入夹到 1..3：0 → 1 个玩家单位，99 → 3 个，不抛异常也不给空阵容。
            Assert.Equal(1, CreateWorld(0).PlayerUnitCount);
            Assert.Equal(3, CreateWorld(99).PlayerUnitCount);
            Assert.Equal(PlayerUnitId, CreateWorld(0).GetUnit(0).UnitId);
            Assert.Equal(ThirdPlayerUnitId, CreateWorld(99).GetUnit(2).UnitId);
        }

        [Fact]
        public void GetUnit_OutOfRange_Throws()
        {
            AutoChessWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetUnit(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetUnit(world.PlayerUnitCount + world.EnemyUnitCount));
        }

        [Fact]
        public void UnitAt_ReportsOnlyDeployedUnits()
        {
            AutoChessWorld world = CreateWorld(3);

            // 开局玩家全在备战席：己方半场没有任何占位。
            Assert.Equal(-1, world.UnitAt(0, 0));
            Assert.Equal(-1, world.UnitAt(7, PlayerRowCount - 1));

            // 敌方三个单位生成在敌方半场头三格。
            Assert.Equal(3, world.UnitAt(0, PlayerRowCount));
            Assert.Equal(4, world.UnitAt(1, PlayerRowCount));
            Assert.Equal(5, world.UnitAt(2, PlayerRowCount));
            Assert.Equal(-1, world.UnitAt(3, PlayerRowCount));

            // 越界一律 -1。
            Assert.Equal(-1, world.UnitAt(-1, 0));
            Assert.Equal(-1, world.UnitAt(BoardWidth, 0));
            Assert.Equal(-1, world.UnitAt(0, BoardHeight));
        }

        // ── 放置 ────────────────────────────────────────────────────────

        [Fact]
        public void TryPlace_UsesFirstFreeCellOnPlayerHalf()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.True(world.TryPlace(PlayerUnitId));
            Assert.Equal(0, world.UnitAt(0, 0));

            Assert.True(world.TryPlace(SecondPlayerUnitId));
            Assert.Equal(1, world.UnitAt(1, 0));

            Assert.True(world.TryPlace(ThirdPlayerUnitId));
            Assert.Equal(2, world.UnitAt(2, 0));

            ChessUnitView placed = world.GetUnit(2);
            Assert.Equal(2, placed.X);
            Assert.Equal(0, placed.Y);
            Assert.Equal(3, world.PlayerUnitCount);
        }

        [Fact]
        public void TryPlace_AlreadyOnBoard_IsRejected()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.True(world.TryPlace(PlayerUnitId));
            Assert.False(world.TryPlace(PlayerUnitId));
            Assert.Equal(0, world.UnitAt(0, 0));
            Assert.Equal(-1, world.UnitAt(1, 0));

            // 换一个空位再放第二个，第一个的位置不受影响。
            Assert.True(world.TryPlace(SecondPlayerUnitId));
            Assert.False(world.TryPlace(SecondPlayerUnitId));
            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(1, world.GetUnit(1).X);
        }

        [Fact]
        public void TryPlace_RejectsUnknownAndEnemyUnits()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.False(world.TryPlace(999));
            Assert.False(world.TryPlace(EnemyUnitId));
            Assert.Equal(3, world.EnemyUnitCount);
            Assert.Equal(PlayerRowCount, world.GetUnit(3).Y);
            Assert.Equal(-1, world.UnitAt(0, 0));
        }

        // ── 挪位 ────────────────────────────────────────────────────────

        [Fact]
        public void TryMoveUnit_MovesWithinPlayerHalf()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));

            Assert.True(world.TryMoveUnit(PlayerUnitId, 3, 3));
            Assert.Equal(-1, world.UnitAt(0, 0));
            Assert.Equal(0, world.UnitAt(3, 3));

            ChessUnitView moved = world.GetUnit(0);
            Assert.Equal(3, moved.X);
            Assert.Equal(3, moved.Y);
        }

        [Fact]
        public void TryMoveUnit_RejectsEnemyHalfAndOutOfBounds()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));

            // 敌方半场（y >= 4）不是布阵区，即使格子空着也不行。
            Assert.False(world.TryMoveUnit(PlayerUnitId, 0, PlayerRowCount));
            Assert.False(world.TryMoveUnit(PlayerUnitId, 5, BoardHeight - 1));

            Assert.False(world.TryMoveUnit(PlayerUnitId, -1, 0));
            Assert.False(world.TryMoveUnit(PlayerUnitId, BoardWidth, 0));
            Assert.False(world.TryMoveUnit(PlayerUnitId, 0, -1));

            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);
        }

        [Fact]
        public void TryMoveUnit_RejectsOccupiedCell()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));
            Assert.True(world.TryPlace(SecondPlayerUnitId));

            // 目标格站着另一个己方单位。
            Assert.False(world.TryMoveUnit(PlayerUnitId, 1, 0));

            // 目标格就是自己脚下，同样算"被占"。
            Assert.False(world.TryMoveUnit(PlayerUnitId, 0, 0));

            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(1, world.GetUnit(1).X);
        }

        [Fact]
        public void TryMoveUnit_RejectsBenchUnit()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));

            // 未放置（还在备战席）的单位不能挪位：目标格空着也不行。
            Assert.False(world.TryMoveUnit(ThirdPlayerUnitId, 5, 2));
            Assert.Equal(-1, world.GetUnit(2).X);

            // 放上场之后同一个调用就成功了 —— 拒绝点确实是"未放置"。
            Assert.True(world.TryPlace(ThirdPlayerUnitId));
            Assert.True(world.TryMoveUnit(ThirdPlayerUnitId, 5, 2));
            Assert.Equal(5, world.GetUnit(2).X);
        }

        [Fact]
        public void TryMoveUnit_RejectsEnemyUnit()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.False(world.TryMoveUnit(EnemyUnitId, 0, 0));
            Assert.False(world.TryMoveUnit(EnemyUnitId, 3, 5));
            Assert.Equal(0, world.GetUnit(3).X);
            Assert.Equal(PlayerRowCount, world.GetUnit(3).Y);
        }

        // ── 出售 ────────────────────────────────────────────────────────

        [Fact]
        public void TrySell_RemovesUnitFromArmyAndFreesCell()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(SecondPlayerUnitId));
            Assert.Equal(1, world.UnitAt(1, 0));

            Assert.True(world.TrySell(SecondPlayerUnitId));
            Assert.Equal(2, world.PlayerUnitCount);
            Assert.Equal(-1, world.UnitAt(1, 0));

            // 槽位压缩：原 1 号位的 3 号单位前移到 1 号位，1 号单位不动。
            Assert.Equal(PlayerUnitId, world.GetUnit(0).UnitId);
            Assert.Equal(ThirdPlayerUnitId, world.GetUnit(1).UnitId);

            // 卖掉的东西不能二次出售，也不能再放置。
            Assert.False(world.TrySell(SecondPlayerUnitId));
            Assert.False(world.TryPlace(SecondPlayerUnitId));
            Assert.Equal(2, world.PlayerUnitCount);
        }

        [Fact]
        public void TrySell_RejectsUnknownEnemyAndDeadUnits()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.False(world.TrySell(999));
            Assert.False(world.TrySell(EnemyUnitId));
            Assert.Equal(3, world.EnemyUnitCount);

            // 三连卖空：全体出局后玩家槽位归零。
            Assert.True(world.TrySell(PlayerUnitId));
            Assert.True(world.TrySell(SecondPlayerUnitId));
            Assert.True(world.TrySell(ThirdPlayerUnitId));
            Assert.Equal(0, world.PlayerUnitCount);
            Assert.False(world.TrySell(PlayerUnitId));
        }

        // ── 战斗结算 ────────────────────────────────────────────────────

        [Fact]
        public void StartBattle_WithFullFormation_WinsAndDropsEnemyBaseHp()
        {
            AutoChessWorld world = CreateWorld(3);
            PlaceAll(world, 3);

            world.StartBattle();

            Assert.True(world.IsBattleResolved);
            Assert.Equal(1, world.Wins);
            Assert.Equal(0, world.Losses);
            Assert.Equal(InitialBaseHp, world.PlayerHp);
            Assert.Equal(InitialBaseHp - BaseHpPerRound, world.EnemyHp);

            // 3v3 正面对撞：玩家 20 攻击 × 4 击打空 80 血的敌人，己方各挨 4 下 12 点伤害。
            Assert.Equal(0, CountAlive(world, 1));
            Assert.Equal(3, CountAlive(world, 0));
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(PlayerUnitHp - (4 * EnemyUnitAttack), world.GetUnit(i).Hp);
            }

            // 阵亡单位保留槽位与坐标（尸体留到 EndRound 清场）。
            Assert.Equal(3, world.EnemyUnitCount);
            Assert.False(world.GetUnit(3).Alive);
        }

        [Fact]
        public void StartBattle_WithoutDeployedUnits_LosesBaseHp()
        {
            AutoChessWorld world = CreateWorld(3);

            world.StartBattle();

            Assert.True(world.IsBattleResolved);
            Assert.Equal(0, world.Wins);
            Assert.Equal(1, world.Losses);
            Assert.Equal(InitialBaseHp - BaseHpPerRound, world.PlayerHp);
            Assert.Equal(InitialBaseHp, world.EnemyHp);

            // 没上场的敌人毫发无损，站位也没变。
            Assert.Equal(3, CountAlive(world, 1));
            for (int j = 0; j < 3; j++)
            {
                ChessUnitView enemy = world.GetUnit(3 + j);
                Assert.Equal(EnemyUnitHp, enemy.Hp);
                Assert.Equal(j, enemy.X);
                Assert.Equal(PlayerRowCount, enemy.Y);
            }

            // 备战席上的单位没参战，所以仍然活着：输的是这一轮的阵容，不是整支队伍。
            Assert.Equal(3, CountAlive(world, 0));
        }

        [Fact]
        public void StartBattle_IsIdempotentUntilEndRound()
        {
            AutoChessWorld world = CreateWorld(3);
            PlaceAll(world, 3);

            world.StartBattle();
            int enemyHp = world.EnemyHp;
            int wins = world.Wins;

            world.StartBattle();
            world.StartBattle();

            Assert.Equal(wins, world.Wins);
            Assert.Equal(enemyHp, world.EnemyHp);
        }

        [Fact]
        public void StartBattle_SameSeedAndLayout_ProducesIdenticalState()
        {
            AutoChessWorld first = CreateWorld(3);
            AutoChessWorld second = CreateWorld(3);

            // 非对称布阵：把两个单位挪到"两轴都要缩短"的格子上，逼随机走位真的参与决策。
            foreach (AutoChessWorld world in new[] { first, second })
            {
                PlaceAll(world, 3);
                Assert.True(world.TryMoveUnit(PlayerUnitId, 5, 3));
                Assert.True(world.TryMoveUnit(SecondPlayerUnitId, 3, 2));
            }

            first.StartBattle();
            second.StartBattle();

            Assert.Equal(first.PlayerHp, second.PlayerHp);
            Assert.Equal(first.EnemyHp, second.EnemyHp);
            Assert.Equal(first.Wins, second.Wins);
            Assert.Equal(first.Losses, second.Losses);
            Assert.Equal(first.PlayerUnitCount, second.PlayerUnitCount);
            Assert.Equal(first.EnemyUnitCount, second.EnemyUnitCount);

            int slots = first.PlayerUnitCount + first.EnemyUnitCount;
            Assert.Equal(slots, second.PlayerUnitCount + second.EnemyUnitCount);
            for (int i = 0; i < slots; i++)
            {
                ChessUnitView a = first.GetUnit(i);
                ChessUnitView b = second.GetUnit(i);

                Assert.Equal(a.UnitId, b.UnitId);
                Assert.Equal(a.Owner, b.Owner);
                Assert.Equal(a.X, b.X);
                Assert.Equal(a.Y, b.Y);
                Assert.Equal(a.Hp, b.Hp);
                Assert.Equal(a.Alive, b.Alive);
            }
        }

        [Fact]
        public void StartBattle_SingleUnitAgainstThree_Loses()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));

            world.StartBattle();

            // 100 血 / 20 攻的孤军，换不掉 240 点总血量的三只敌人。
            Assert.Equal(0, world.Wins);
            Assert.Equal(1, world.Losses);
            Assert.Equal(InitialBaseHp - BaseHpPerRound, world.PlayerHp);
            Assert.Equal(0, CountAlive(world, 0));
            Assert.True(CountAlive(world, 1) > 0);
        }

        // ── 回合推进 ────────────────────────────────────────────────────

        [Fact]
        public void EndRound_ResetsBattlefieldAndAdvancesRound()
        {
            AutoChessWorld world = CreateWorld(3);
            PlaceAll(world, 3);

            world.StartBattle();
            world.EndRound();

            Assert.Equal(1, world.RoundIndex);
            Assert.False(world.IsBattleResolved);

            // 玩家半场清空、存活单位治疗满血回备战席。
            Assert.Equal(-1, world.UnitAt(0, 0));
            Assert.Equal(-1, world.UnitAt(1, 0));
            Assert.Equal(-1, world.UnitAt(2, 0));
            for (int i = 0; i < 3; i++)
            {
                ChessUnitView unit = world.GetUnit(i);
                Assert.Equal(-1, unit.X);
                Assert.Equal(-1, unit.Y);
                Assert.Equal(PlayerUnitHp, unit.Hp);
                Assert.True(unit.Alive);
            }

            // 敌方重新生成 3 个单位，满血站在敌方半场头三格。
            Assert.Equal(3, world.EnemyUnitCount);
            Assert.Equal(3, CountAlive(world, 1));
            for (int j = 0; j < 3; j++)
            {
                ChessUnitView enemy = world.GetUnit(3 + j);
                Assert.Equal(EnemyUnitId + j, enemy.UnitId);
                Assert.Equal(j, enemy.X);
                Assert.Equal(PlayerRowCount, enemy.Y);
                Assert.Equal(EnemyUnitHp, enemy.Hp);
                Assert.True(enemy.Alive);
                Assert.Equal(3 + j, world.UnitAt(j, PlayerRowCount));
            }
        }

        [Fact]
        public void EndRound_BeforeBattleResolved_IsNoOp()
        {
            AutoChessWorld world = CreateWorld(3);
            PlaceAll(world, 3);

            world.EndRound();

            Assert.Equal(0, world.RoundIndex);
            Assert.Equal(3, world.PlayerUnitCount);
            Assert.Equal(3, world.EnemyUnitCount);
            Assert.Equal(0, world.UnitAt(0, 0));
            Assert.False(world.IsBattleResolved);
        }

        [Fact]
        public void PlayerBaseHpZero_EndsRunAsLoss()
        {
            AutoChessWorld world = CreateWorld(3);

            // 每轮都不布阵：连输 10 轮把 100 点基地血量打空。
            for (int round = 0; round < RoundsToKillBase; round++)
            {
                world.StartBattle();
                world.EndRound();
            }

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.PlayerHp);
            Assert.Equal(0, world.Wins);
            Assert.Equal(RoundsToKillBase, world.Losses);
            Assert.Equal(RoundsToKillBase, world.RoundIndex);

            GameplayResult result = world.CollectResult();
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);

            // 备战席单位从未参战，因此仍然留在队伍里。
            Assert.Equal(3, result.RemainingUnits);
        }

        [Fact]
        public void EnemyBaseHpZero_EndsRunAsVictoryWithLoot()
        {
            AutoChessWorld world = CreateWorld(3);

            // 每轮都满编上场：连赢 10 轮把敌方基地打空。
            for (int round = 0; round < RoundsToKillBase; round++)
            {
                PlaceAll(world, 3);
                world.StartBattle();
                world.EndRound();
            }

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.EnemyHp);
            Assert.Equal(RoundsToKillBase, world.Wins);
            Assert.Equal(0, world.Losses);

            GameplayResult result = world.CollectResult();
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(3, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(2, result.Loots.Count);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Equal(1001, result.Loots[1].ItemId);
            Assert.Equal(20, result.Loots[1].Count);
        }

        // ── 结算门禁 ────────────────────────────────────────────────────

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            AutoChessWorld world = CreateWorld(3);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            AutoChessWorld world = CreateWorld(3);

            for (int round = 0; round < RoundsToKillBase; round++)
            {
                world.StartBattle();
                world.EndRound();
            }

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void InputsAfterFinish_AreRejected()
        {
            AutoChessWorld world = CreateWorld(3);

            for (int round = 0; round < RoundsToKillBase; round++)
            {
                world.StartBattle();
                world.EndRound();
            }

            Assert.True(world.IsFinished);

            int roundIndex = world.RoundIndex;
            int playerHp = world.PlayerHp;
            int losses = world.Losses;

            Assert.False(world.TryPlace(PlayerUnitId));
            Assert.False(world.TryMoveUnit(PlayerUnitId, 0, 0));
            Assert.False(world.TrySell(PlayerUnitId));

            world.StartBattle();
            world.EndRound();

            Assert.Equal(roundIndex, world.RoundIndex);
            Assert.Equal(playerHp, world.PlayerHp);
            Assert.Equal(losses, world.Losses);
        }

        // ── Step ────────────────────────────────────────────────────────

        [Fact]
        public void Step_CountsStepsOnly_AndStopsAfterFinish()
        {
            AutoChessWorld world = CreateWorld(3);

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(5, world.ProcessedSteps);

            // Step 不参与战斗推演：开战后单位不会因为 Step 而移动。
            PlaceAll(world, 3);
            world.Step(1f / 60f);
            Assert.Equal(6, world.ProcessedSteps);
            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);

            for (int round = 0; round < RoundsToKillBase; round++)
            {
                world.StartBattle();
                world.EndRound();
            }

            Assert.True(world.IsFinished);
            world.Step(1f / 60f);
            Assert.Equal(6, world.ProcessedSteps);
            Assert.Equal(6, world.CollectResult().StepCount);
        }

        // ── 零分配 ──────────────────────────────────────────────────────

        [Fact]
        public void UnitAtAndTryMoveUnit_AfterWarmup_DoNotAllocate()
        {
            AutoChessWorld world = CreateWorld(3);
            Assert.True(world.TryPlace(PlayerUnitId));

            // 预热：让 JIT/内联与首次调用路径的分配发生在测量之前。
            for (int i = 0; i < 200; i++)
            {
                world.UnitAt(0, 0);
                world.TryMoveUnit(PlayerUnitId, 0, 1);
                world.TryMoveUnit(PlayerUnitId, 0, 0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.UnitAt(0, 0);
                world.TryMoveUnit(PlayerUnitId, 0, 1);
                world.TryMoveUnit(PlayerUnitId, 0, 0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void StartBattleAndEndRound_AfterWarmup_DoNotAllocate()
        {
            AutoChessWorld world = CreateWorld(3);

            // 预热 3 轮完整流程（含战斗中期的走位与伤害结算）。
            for (int round = 0; round < 3; round++)
            {
                PlaceAll(world, 3);
                world.StartBattle();
                world.EndRound();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            PlaceAll(world, 3);
            world.StartBattle();
            world.EndRound();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0L, allocated);
        }

        // ── 辅助 ────────────────────────────────────────────────────────

        private static AutoChessWorld CreateWorld(int unitCount)
        {
            return new AutoChessWorld(new GameplayInitData(1, DefaultSeed, unitCount));
        }

        /// <summary>把 1..<paramref name="count"/> 号单位依次放上己方半场（落点由内核选第一个空格）。</summary>
        private static void PlaceAll(AutoChessWorld world, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.True(world.TryPlace(PlayerUnitId + i));
            }
        }

        /// <summary>数某阵营当前存活（血量 &gt; 0）的槽位——尸体仍占槽位，不能用 UnitCount 代替。</summary>
        private static int CountAlive(AutoChessWorld world, int owner)
        {
            int slots = world.PlayerUnitCount + world.EnemyUnitCount;
            int alive = 0;

            for (int i = 0; i < slots; i++)
            {
                ChessUnitView unit = world.GetUnit(i);
                if (unit.Owner == owner && unit.Alive)
                {
                    alive++;
                }
            }

            return alive;
        }

        /// <summary>当前玩家槽位数：测试里只作自检，避免把计数上限写死。</summary>
        private static int PlayerUnitCountOf(AutoChessWorld world)
        {
            return world.PlayerUnitCount;
        }
    }
}

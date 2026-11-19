using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.IdleRpg;
using Xunit;

namespace AIFramework.Features.Tests.IdleRpg
{
    /// <summary>
    /// 挂机 RPG 玩法内核验收：真实 <c>CqeBus</c>（经 <c>RecordingBus</c> 记录）+ 本切片自带的桩端口。
    /// 断言只落在"切片发出了什么消息"、"处理器只读属性"、"快照"与"金币到账"四处；
    /// 战斗推进一律靠 <see cref="IdleBattleTickCommand"/> 显式驱动，不读时间、不等待。
    /// </summary>
    public sealed class IdleRpgHandlerTests
    {
        // ---------- 初始状态与数值公式 ----------

        [Fact]
        public void NewHandler_StartsBattlingFirstStageWithLevelOneHero()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            Assert.Equal(1, harness.Handler.CurrentStage);
            Assert.Equal(1, harness.Handler.HeroLevel);
            Assert.Equal(IdleRpgHarness.BasePower, harness.Handler.HeroPower);
            Assert.Equal(1, harness.Handler.GoldPerSecond);
            Assert.Equal(0, harness.Handler.BattleTickCount);
            Assert.Equal(0, harness.Handler.StageClearCount);
            Assert.Equal(0, harness.Handler.DamageTaken);
            Assert.Equal(0, harness.Gold);

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(1, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(30, snapshot.EnemyHp);
            Assert.Equal(30, snapshot.EnemyMaxHp);
            Assert.Equal(0, snapshot.BattleTick);

            // 构造期只读表：不发指令、不发事件。
            Assert.Empty(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        [Fact]
        public void HeroPowerAndGoldPerSecond_FollowLevelFormula()
        {
            IdleRpgHarness levelOne = IdleRpgHarness.CreateWithDefaultRows();
            Assert.Equal(10 + ((1 - 1) * 6), levelOne.Handler.HeroPower);
            Assert.Equal(1, levelOne.Handler.GoldPerSecond);

            IdleRpgHarness levelThree = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows()),
                3);
            Assert.Equal(3, levelThree.Handler.HeroLevel);
            Assert.Equal(10 + ((3 - 1) * 6), levelThree.Handler.HeroPower);
            Assert.Equal(3, levelThree.Handler.GoldPerSecond);

            // 非法初始等级夹到 1：power 公式不会出现负数基数。
            IdleRpgHarness clamped = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows()),
                0);
            Assert.Equal(1, clamped.Handler.HeroLevel);
            Assert.Equal(IdleRpgHarness.BasePower, clamped.Handler.HeroPower);
            Assert.Equal(1, clamped.Handler.GoldPerSecond);
        }

        // ---------- 战斗 tick ----------

        [Fact]
        public void SingleTick_DamagesEnemyByHeroPowerWithoutGrantingAnything()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            harness.Tick(1);

            Assert.Equal(20, harness.Snapshot().EnemyHp);
            Assert.Equal(1, harness.Handler.BattleTickCount);
            Assert.Equal(0, harness.Handler.StageClearCount);
            Assert.Equal(0, harness.Gold);

            // 只有玩家自己发的命令，没有任何发奖指令。
            Assert.Single(harness.Bus.SentOf<IdleBattleTickCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
        }

        [Fact]
        public void EnemyCounterAttacks_EveryThirdTick_AndOnlyAccumulatesDamage()
        {
            // 100 点血 + 每 tick 10 点伤害：第 3、6 个 tick 敌人还活着，会反击。
            IdleRpgHarness harness = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(new IdleStageRow(1, 100, 7, 10, 0)));

            harness.Tick(3);

            Assert.Equal(70, harness.Snapshot().EnemyHp);
            Assert.Equal(3, harness.Handler.BattleTickCount);
            Assert.Equal(7, harness.Handler.DamageTaken);

            harness.Tick(3);

            Assert.Equal(40, harness.Snapshot().EnemyHp);
            Assert.Equal(6, harness.Handler.BattleTickCount);
            Assert.Equal(14, harness.Handler.DamageTaken);

            // 反击只累计，不换来任何"死亡/失败"状态：英雄等级、经验、关卡都没动。
            Assert.Equal(1, harness.Handler.HeroLevel);
            Assert.Equal(0, harness.Snapshot().Hero.Exp);
            Assert.True(harness.Snapshot().InBattle);
        }

        [Fact]
        public void KillingBlowOnCounterTick_DoesNotCountEnemyCounterAttack()
        {
            IdleRpgHarness harness = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(new IdleStageRow(1, 100, 7, 10, 0)));

            harness.Tick(9);
            Assert.Equal(10, harness.Snapshot().EnemyHp);
            Assert.Equal(21, harness.Handler.DamageTaken);

            // 第 10 tick 打死敌人：英雄先出手，所以这一 tick 不再有反击。
            harness.Tick(1);

            Assert.Equal(1, harness.Handler.StageClearCount);
            Assert.Equal(21, harness.Handler.DamageTaken);
            Assert.False(harness.Snapshot().InBattle);
        }

        // ---------- 通关结算 ----------

        [Fact]
        public void ClearStage_GrantsGoldWithFixedReasonTagAndAutoEntersNextStage()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            harness.Tick(3);

            Assert.Equal(1, harness.Handler.StageClearCount);
            Assert.Equal(20, harness.Gold);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(MetaItemIds.Gold, grant.ItemId);
            Assert.Equal(20, grant.Count);
            Assert.Equal(IdleRpgHarness.GoldReasonTagId, grant.ReasonTagId);

            // 顺序契约：命令先进来，发奖随后。
            Assert.Equal(2, harness.Bus.SentCommands.Count);
            Assert.IsType<IdleBattleTickCommand>(harness.Bus.SentCommands[0]);
            Assert.IsType<GrantItemCommand>(harness.Bus.SentCommands[1]);

            // 通关经验 = 10 × 关卡号，最高关卡取最大值。
            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(10, snapshot.Hero.Exp);
            Assert.Equal(1, snapshot.Hero.HighestStage);
            Assert.Equal(1, snapshot.Hero.Level);

            // 自动进入下一关：血量回满、战斗 tick 归零。
            Assert.Equal(2, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(60, snapshot.EnemyHp);
            Assert.Equal(60, snapshot.EnemyMaxHp);
            Assert.Equal(0, snapshot.BattleTick);
        }

        [Fact]
        public void SingleTickCommand_CanClearTwoStagesAndContinueIntoTheThird()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            // 3 tick 打完第 1 关，6 tick 打完第 2 关，第 10 tick 落在第 3 关上。
            harness.Tick(10);

            Assert.Equal(2, harness.Handler.StageClearCount);
            Assert.Equal(60, harness.Gold);
            Assert.Equal(2, harness.GoldGrantCount);
            Assert.Equal(4, harness.Handler.DamageTaken);

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(30, snapshot.Hero.Exp);
            Assert.Equal(2, snapshot.Hero.HighestStage);
            Assert.Equal(3, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(110, snapshot.EnemyHp);
            Assert.Equal(120, snapshot.EnemyMaxHp);
            Assert.Equal(1, snapshot.BattleTick);
        }

        [Fact]
        public void ClearingLastStage_StopsBattleAndFurtherTicksAreInert()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            // 5 关共需 3 + 6 + 12 + 24 + 48 = 93 tick，10 次命令足够打完（再多就是空转）。
            for (int i = 0; i < 10; i++)
            {
                harness.Tick(10);
            }

            Assert.Equal(5, harness.Handler.StageClearCount);
            Assert.Equal(620, harness.Gold);
            Assert.Equal(5, harness.GoldGrantCount);
            Assert.Equal(265, harness.Handler.DamageTaken);

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(150, snapshot.Hero.Exp);
            Assert.Equal(5, snapshot.Hero.HighestStage);
            Assert.Equal(5, snapshot.CurrentStage);
            Assert.False(snapshot.InBattle);
            Assert.Equal(0, snapshot.EnemyHp);
            Assert.Equal(480, snapshot.EnemyMaxHp);
            Assert.Equal(0, snapshot.BattleTick);
            Assert.Equal(0, harness.Handler.BattleTickCount);

            int commandsAfterClear = harness.Bus.SentCommands.Count;
            harness.Tick(10);

            // 战斗已结束：再 tick 不发任何指令，状态一动也不动。
            Assert.Equal(commandsAfterClear + 1, harness.Bus.SentCommands.Count);
            Assert.Equal(5, harness.Handler.StageClearCount);
            Assert.Equal(620, harness.Gold);
            Assert.Equal(265, harness.Handler.DamageTaken);
        }

        // ---------- AdvanceStage ----------

        [Fact]
        public void AdvanceStage_OnFreshSession_EntersNextStageWithoutAnyReward()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            harness.Advance();

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(2, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(60, snapshot.EnemyHp);
            Assert.Equal(0, snapshot.BattleTick);

            // 跳关不是通关：不发金币、不给经验、不计通关数。
            Assert.Equal(0, harness.Gold);
            Assert.Equal(0, harness.Handler.StageClearCount);
            Assert.Equal(0, snapshot.Hero.Exp);
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
        }

        [Fact]
        public void AdvanceStage_WhenCurrentStageIsStillBeingFought_IsSilentlyRejected()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            harness.Advance();
            harness.Tick(1);

            int sentBefore = harness.Bus.SentCommands.Count;
            harness.Advance();

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(2, snapshot.CurrentStage);
            Assert.Equal(50, snapshot.EnemyHp);
            Assert.Equal(1, snapshot.BattleTick);
            Assert.Equal(sentBefore + 1, harness.Bus.SentCommands.Count);
            Assert.Equal(0, harness.Gold);
        }

        [Fact]
        public void AdvanceStage_AfterClearingLastStage_IsSilentlyRejected()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();
            for (int i = 0; i < 10; i++)
            {
                harness.Tick(10);
            }

            int sentBefore = harness.Bus.SentCommands.Count;
            harness.Advance();

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(5, snapshot.CurrentStage);
            Assert.False(snapshot.InBattle);
            Assert.Equal(sentBefore + 1, harness.Bus.SentCommands.Count);
            Assert.Equal(5, harness.Handler.StageClearCount);
        }

        [Fact]
        public void AdvanceStage_WhenNextStageIsMissingFromTable_IsSilentlyRejected()
        {
            IdleRpgHarness harness = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(new IdleStageRow(1, 30, 2, 20, 10)));

            harness.Advance();

            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(1, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(30, snapshot.EnemyHp);
            Assert.Equal(0, snapshot.BattleTick);
            Assert.Equal(0, harness.Gold);
        }

        // ---------- LevelUpHero ----------

        [Fact]
        public void LevelUpHero_WithoutEnoughExp_IsSilentlyRejected()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            // 1 级升级需要 100 经验，刚开打只有 0。
            harness.LevelUp();
            Assert.Equal(1, harness.Handler.HeroLevel);
            Assert.Equal(0, harness.Snapshot().Hero.Exp);

            // 通关第 1 关只给 10 经验，仍然不够。
            harness.Tick(3);
            harness.LevelUp();
            Assert.Equal(1, harness.Handler.HeroLevel);
            Assert.Equal(10, harness.Snapshot().Hero.Exp);
            Assert.Equal(IdleRpgHarness.BasePower, harness.Handler.HeroPower);
        }

        [Fact]
        public void LevelUpHero_WithExactlyEnoughExp_LevelsUpAndConsumesPreLevelCost()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            // 连打前 4 关：3 + 6 + 12 + 24 = 45 tick，经验 10 + 20 + 30 + 40 = 100。
            for (int i = 0; i < 4; i++)
            {
                harness.Tick(10);
            }

            harness.Tick(5);
            Assert.Equal(100, harness.Snapshot().Hero.Exp);

            harness.LevelUp();

            Assert.Equal(2, harness.Handler.HeroLevel);
            Assert.Equal(0, harness.Snapshot().Hero.Exp);
            Assert.Equal(10 + ((2 - 1) * 6), harness.Handler.HeroPower);
            Assert.Equal(2, harness.Handler.GoldPerSecond);

            // 2 级再升级要 200 经验：现在只有 0，必须再被拒绝（且经验不会变成负数）。
            harness.LevelUp();
            Assert.Equal(2, harness.Handler.HeroLevel);
            Assert.Equal(0, harness.Snapshot().Hero.Exp);
        }

        [Fact]
        public void LevelUpHero_RaisesAtMostOneLevelPerCommand_AndCostScalesWithLevel()
        {
            // 8 个 1 tick 就能打死的关卡：通关经验 10+20+…+80 = 360。
            IdleRpgHarness harness = IdleRpgHarness.Create(
                IdleRpgLoaderStub.WithRows(BuildOneShotRows(8)));

            harness.Tick(10);
            Assert.Equal(8, harness.Handler.StageClearCount);
            Assert.Equal(360, harness.Snapshot().Hero.Exp);

            // 一次命令只升 1 级：1 级 → 2 级花掉 100，剩 260。
            harness.LevelUp();
            Assert.Equal(2, harness.Handler.HeroLevel);
            Assert.Equal(260, harness.Snapshot().Hero.Exp);

            // 2 级 → 3 级花掉 200，剩 60。
            harness.LevelUp();
            Assert.Equal(3, harness.Handler.HeroLevel);
            Assert.Equal(60, harness.Snapshot().Hero.Exp);
            Assert.Equal(10 + ((3 - 1) * 6), harness.Handler.HeroPower);
            Assert.Equal(3, harness.Handler.GoldPerSecond);

            // 3 级要 300，只剩 60：拒绝且不动状态。
            harness.LevelUp();
            Assert.Equal(3, harness.Handler.HeroLevel);
            Assert.Equal(60, harness.Snapshot().Hero.Exp);
        }

        // ---------- tick 数夹取 ----------

        [Fact]
        public void TickCommand_ClampsTicksToOneThroughTen()
        {
            Assert.Equal(1, new IdleBattleTickCommand(0).Ticks);
            Assert.Equal(1, new IdleBattleTickCommand(-7).Ticks);
            Assert.Equal(1, new IdleBattleTickCommand(int.MinValue).Ticks);
            Assert.Equal(1, new IdleBattleTickCommand(1).Ticks);
            Assert.Equal(7, new IdleBattleTickCommand(7).Ticks);
            Assert.Equal(10, new IdleBattleTickCommand(10).Ticks);
            Assert.Equal(10, new IdleBattleTickCommand(11).Ticks);
            Assert.Equal(10, new IdleBattleTickCommand(int.MaxValue).Ticks);
        }

        [Fact]
        public void TickCommand_DefaultStruct_StillExecutesExactlyOneTick()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            // 绕过构造函数（Ticks = 0）的退化消息：处理器必须自己再夹一次。
            IdleBattleTickCommand degenerate = default;
            harness.Handler.Handle(in degenerate);

            Assert.Equal(1, harness.Handler.BattleTickCount);
            Assert.Equal(20, harness.Snapshot().EnemyHp);
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
        }

        [Fact]
        public void TickCommand_WithHugeTickCount_ExecutesAtMostTenTicks()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();

            harness.Tick(1000);

            Assert.Equal(10, Assert.Single(harness.Bus.SentOf<IdleBattleTickCommand>()).Ticks);

            // 与"恰好 10 tick"完全一致：3 tick 过第 1 关、6 tick 过第 2 关、第 10 tick 落在第 3 关。
            IdleRpgSnapshot snapshot = harness.Snapshot();
            Assert.Equal(2, harness.Handler.StageClearCount);
            Assert.Equal(3, snapshot.CurrentStage);
            Assert.Equal(110, snapshot.EnemyHp);
            Assert.Equal(1, snapshot.BattleTick);
        }

        // ---------- 快照 ----------

        [Fact]
        public void Snapshot_ReportsEveryFieldFromCurrentState()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();
            harness.Tick(3);
            harness.Tick(1);

            IdleRpgSnapshot snapshot = harness.Snapshot();

            Assert.Equal(1, snapshot.Hero.Level);
            Assert.Equal(10, snapshot.Hero.Exp);
            Assert.Equal(10, snapshot.Hero.Power);
            Assert.Equal(1, snapshot.Hero.GoldPerSecond);
            Assert.Equal(1, snapshot.Hero.HighestStage);
            Assert.Equal(2, snapshot.CurrentStage);
            Assert.True(snapshot.InBattle);
            Assert.Equal(50, snapshot.EnemyHp);
            Assert.Equal(60, snapshot.EnemyMaxHp);
            Assert.Equal(1, snapshot.BattleTick);
        }

        [Fact]
        public void SnapshotQuery_HasNoSideEffects()
        {
            IdleRpgHarness harness = IdleRpgHarness.CreateWithDefaultRows();
            harness.Tick(2);

            int sentBefore = harness.Bus.SentCommands.Count;
            IdleRpgSnapshot first = harness.Snapshot();
            IdleRpgSnapshot second = harness.Snapshot();

            Assert.Equal(10, first.EnemyHp);
            Assert.Equal(10, second.EnemyHp);
            Assert.Equal(2, second.BattleTick);
            Assert.Equal(sentBefore, harness.Bus.SentCommands.Count);
            Assert.Equal(2, harness.Bus.CountQueried<IdleRpgSnapshotQueryRequest>());
        }

        // ---------- 空表退化 ----------

        [Fact]
        public void EmptyStageTable_DegeneratesToIdleWithoutAnyBattle()
        {
            IdleRpgHarness harness = IdleRpgHarness.Create(IdleRpgLoaderStub.WithRows());

            IdleRpgSnapshot initial = harness.Snapshot();
            Assert.Equal(1, initial.CurrentStage);
            Assert.False(initial.InBattle);
            Assert.Equal(0, initial.EnemyHp);
            Assert.Equal(0, initial.EnemyMaxHp);
            Assert.Equal(0, initial.BattleTick);
            Assert.Equal(1, harness.Handler.HeroLevel);
            Assert.Equal(0, harness.Handler.StageClearCount);

            harness.Tick(10);
            harness.Advance();
            harness.LevelUp();

            IdleRpgSnapshot after = harness.Snapshot();
            Assert.Equal(1, after.CurrentStage);
            Assert.False(after.InBattle);
            Assert.Equal(0, after.EnemyHp);
            Assert.Equal(0, harness.Handler.StageClearCount);
            Assert.Equal(0, harness.Handler.BattleTickCount);
            Assert.Equal(0, harness.Gold);

            // 三条命令都只是经过总线，切片一次发奖都没发。
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
        }

        // ---------- 装配与真实总线 ----------

        [Fact]
        public void Register_OnRealCqeBus_EnablesCommandsAndSnapshotQuery()
        {
            CqeBus bus = new CqeBus();
            IdleRpgWalletStub wallet = new IdleRpgWalletStub();
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);

            IdleRpgHandler handler = new IdleRpgHandler(
                bus,
                IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows()));
            handler.Register(bus);

            IdleBattleTickCommand tick = new IdleBattleTickCommand(3);
            bus.Send(in tick);

            IdleRpgSnapshotQueryRequest request = default;
            IdleRpgSnapshot snapshot = bus.Query<IdleRpgSnapshotQueryRequest, IdleRpgSnapshot>(in request);

            Assert.Equal(2, snapshot.CurrentStage);
            Assert.Equal(60, snapshot.EnemyHp);
            Assert.Equal(1, handler.StageClearCount);
            Assert.Equal(20, wallet.GetCount(MetaItemIds.Gold));

            // 未注册的命令类型在真实总线上依旧抛异常：证明 Register 只注册了本切片的三条命令。
            Assert.Throws<InvalidOperationException>(() =>
            {
                ConsumeItemCommand notMine = new ConsumeItemCommand(MetaItemIds.Gold, 1, 0);
                bus.Send(in notMine);
            });
        }

        [Fact]
        public void NullArguments_AndBadLoader_ThrowTheDocumentedExceptions()
        {
            IBinaryTableLoader loader = IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows());

            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = new IdleRpgHandler(null!, loader);
            });

            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = new IdleRpgHandler(new CqeBus(), null!);
            });

            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = new IdleRpgTables(null!);
            });

            Assert.Throws<ArgumentNullException>(() =>
            {
                IdleRpgHandler handler = new IdleRpgHandler(new CqeBus(), loader);
                handler.Register(null!);
            });
        }

        /// <summary>造 <paramref name="count"/> 个"英雄 1 tick 就能打死、且不发金币"的关卡。</summary>
        private static IdleStageRow[] BuildOneShotRows(int count)
        {
            IdleStageRow[] rows = new IdleStageRow[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = new IdleStageRow(i + 1, IdleRpgHarness.BasePower, 0, 0, 0);
            }

            return rows;
        }
    }
}

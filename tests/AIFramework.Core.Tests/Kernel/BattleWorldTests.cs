using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Core.Cqe;
using AIFramework.Kernel.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="BattleWorld"/> 的强实时契约：固定 6 阶段推进、打断仲裁（严格大于）、
    /// 阶段 4 的反射仲裁、结算门禁与胜负口径，以及预热后 1000 帧的严格零分配。
    /// </summary>
    public sealed class BattleWorldTests
    {
        private const float StepSeconds = 1f / 60f;

        private const int WarmupFrames = 60;

        private readonly ITestOutputHelper _output;

        public BattleWorldTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ── 用例 ────────────────────────────────────────────────────────

        [Fact]
        public void Step_AdvancesFrameIdAndTurnsIntoNoOpAfterFinish()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle hero = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 1, 10, GameplayTagMask.Empty);
            UnitHandle victim = world.SpawnUnit(new Vec2(1f, 0f), 0.5f, 1, 1, GameplayTagMask.Empty);

            Assert.Equal(0, world.FrameId);
            Assert.False(world.IsFinished);

            world.Step(StepSeconds);
            Assert.Equal(1, world.FrameId);
            Assert.False(world.IsFinished);

            // 阶段 4 结算致死伤害后，阶段 6 仍然推进帧号，随后 IsFinished 才为真。
            world.Submit(new DamageIntent(hero, victim, 5, 0));
            world.Step(StepSeconds);

            Assert.Equal(2, world.FrameId);
            Assert.True(world.IsFinished);

            // 结束后再 Step 是空操作，不推进帧号。
            world.Step(StepSeconds);
            world.Step(StepSeconds);
            Assert.Equal(2, world.FrameId);
        }

        [Fact]
        public void Step_RunsSkillPhaseBeforeArbitrationAndBeforeFrameIncrement()
        {
            OrderProbePhase phase = new OrderProbePhase();
            BattleWorld world = CreateWorld(1, new ISkillInteractionPhase[] { phase });
            UnitHandle unit = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 1, 10, GameplayTagMask.Empty);
            phase.Mover = unit;

            world.Step(StepSeconds);

            Assert.Equal(1, phase.ExecuteCount);
            // 阶段 3 读到的帧号是阶段 6 递增前的值。
            Assert.Equal(0, phase.LastFrameId);
            Assert.Equal(unit, phase.LastOwner);

            // 阶段 3 提交的位移在阶段 5 已落位，说明阶段顺序是 3 → 4 → 5 → 6。
            Assert.True(world.TryGetUnit(unit, out UnitReadView view));
            Assert.Equal(1f, view.Position.X);
            Assert.Equal(0f, view.Position.Y);
            Assert.Equal(1, world.FrameId);
        }

        [Theory]
        [InlineData(0, 1, true)]
        [InlineData(1, 3, true)]
        [InlineData(1, 2, true)]
        [InlineData(1, 1, false)]
        [InlineData(5, 3, false)]
        [InlineData(5, 5, false)]
        [InlineData(5, 6, true)]
        [InlineData(-1, 0, true)]
        public void ShouldInterrupt_IsStrictlyGreaterThanArmor(int armorLevel, int breakPower, bool expected)
        {
            Assert.Equal(expected, SkillInterruptResolver.ShouldInterrupt(armorLevel, breakPower));
        }

        [Fact]
        public void ProjectileHit_WithBreakPowerAboveArmor_IncrementsInterruptedCount()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle shooter = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 99, 100, GameplayTagMask.Empty);
            UnitHandle target = world.SpawnUnit(new Vec2(5f, 0f), 0.5f, 1, 100, GameplayTagMask.Empty);
            ProjectileHandle projectile = world.SpawnProjectile(
                shooter, new Vec2(4f, 0f), new Vec2(1f, 0f), 3, GameplayTagMask.Empty);

            Assert.True(projectile.IsValid);
            Assert.Equal(1, world.ProjectileCount);

            // dt=1：飞行物从 (4,0) 移到 (5,0) 正中目标；命中伤害 3 同时也是打断力 3 > 霸体 1。
            world.Step(1f);

            Assert.Equal(1, world.InterruptedCount);
            Assert.Equal(0, world.ProjectileCount);
            Assert.True(world.TryGetUnit(target, out UnitReadView view));
            Assert.Equal(97, view.Hp);
        }

        [Fact]
        public void ProjectileHit_WithBreakPowerEqualToArmor_DoesNotInterrupt()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle shooter = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 99, 100, GameplayTagMask.Empty);
            UnitHandle target = world.SpawnUnit(new Vec2(5f, 0f), 0.5f, 5, 100, GameplayTagMask.Empty);
            world.SpawnProjectile(shooter, new Vec2(4f, 0f), new Vec2(1f, 0f), 5, GameplayTagMask.Empty);

            world.Step(1f);

            // 打断力 == 霸体 不打断，但伤害照常结算。
            Assert.Equal(0, world.InterruptedCount);
            Assert.True(world.TryGetUnit(target, out UnitReadView view));
            Assert.Equal(95, view.Hp);
        }

        [Fact]
        public void DamageIntent_WithBreakPowerEqualToArmor_DoesNotInterrupt()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle source = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 99, 100, GameplayTagMask.Empty);
            UnitHandle target = world.SpawnUnit(new Vec2(1f, 0f), 0.5f, 5, 100, GameplayTagMask.Empty);

            world.Submit(new DamageIntent(source, target, 4, 5));
            world.Step(StepSeconds);

            Assert.Equal(0, world.InterruptedCount);
            Assert.True(world.TryGetUnit(target, out UnitReadView view));
            Assert.Equal(96, view.Hp);

            world.Submit(new DamageIntent(source, target, 4, 6));
            world.Step(StepSeconds);

            Assert.Equal(1, world.InterruptedCount);
            Assert.True(world.TryGetUnit(target, out view));
            Assert.Equal(92, view.Hp);
        }

        [Fact]
        public void ReflectArbitration_ReassignsOwnerReversesVelocityAndRecordsTag()
        {
            ReflectProbePhase phase = new ReflectProbePhase();
            BattleWorld world = CreateWorld(2, new ISkillInteractionPhase[] { phase });

            UnitHandle hero = world.SpawnUnit(
                new Vec2(0f, 0f),
                0.5f,
                1,
                100,
                GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive));
            UnitHandle dummy = world.SpawnUnit(new Vec2(6f, 6f), 0.5f, 1, 1, GameplayTagMask.Empty);

            // 飞行物朝主角飞来：位置 (0,-3)，速度 (0,+3)。
            ProjectileHandle projectile = world.SpawnProjectile(
                dummy, new Vec2(0f, -3f), new Vec2(0f, 3f), 5, GameplayTagMask.Empty);
            phase.Projectile = projectile;

            world.Step(StepSeconds);

            // 阶段 3 交给阶段 4 仲裁：归属改为主角、速度取反、打上已反射标签。
            Assert.Equal(1, phase.ExecuteCount);
            Assert.Equal(0, phase.LastFrameId);
            Assert.Equal(hero, phase.LastOwner);

            Assert.Equal(1, world.ReflectedCount);
            Assert.True(world.TryGetProjectile(projectile, out ProjectileReadView view));
            Assert.Equal(hero, view.Owner);
            Assert.Equal(0f, view.Velocity.X);
            Assert.Equal(-3f, view.Velocity.Y);
            Assert.True(view.Tags.Has(GameplayTags.ProjReflected));

            // 二次反射必须被 ProjReflected 标签挡住。
            world.Step(StepSeconds);
            Assert.Equal(1, world.ReflectedCount);

            // 结束对局后，结算标签里必须留下"本局发生过护盾反射"。
            world.Submit(new DamageIntent(hero, dummy, 5, 0));
            world.Step(StepSeconds);
            Assert.True(world.IsFinished);

            GameplayResult result = world.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Contains(GameplayTags.EventShieldReflected, result.TriggeredTags);
            Assert.Single(result.Loots);
            Assert.Equal(2001, result.Loots[0].ItemId);
        }

        [Fact]
        public void ReflectArbitration_RejectsProjectileTaggedUnreflectable()
        {
            ReflectProbePhase phase = new ReflectProbePhase();
            BattleWorld world = CreateWorld(2, new ISkillInteractionPhase[] { phase });

            UnitHandle hero = world.SpawnUnit(
                new Vec2(0f, 0f),
                0.5f,
                1,
                100,
                GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive));
            UnitHandle dummy = world.SpawnUnit(new Vec2(6f, 6f), 0.5f, 1, 1, GameplayTagMask.Empty);

            // 带"不可反弹"标签的飞行物：切片（这里是探针阶段）仍然提交了反射意图。
            ProjectileHandle projectile = world.SpawnProjectile(
                dummy,
                new Vec2(0f, -3f),
                new Vec2(0f, 3f),
                5,
                GameplayTagMask.Empty.Add(GameplayTags.ProjUnreflectable));
            phase.Projectile = projectile;

            world.Step(StepSeconds);

            // 内核兜底拒绝：不换归属、不取反速度、不打"已反射"标签、计数不变。
            Assert.Equal(1, phase.ExecuteCount);
            Assert.Equal(0, world.ReflectedCount);
            Assert.True(world.TryGetProjectile(projectile, out ProjectileReadView view));
            Assert.Equal(dummy, view.Owner);
            Assert.Equal(0f, view.Velocity.X);
            Assert.Equal(3f, view.Velocity.Y);
            Assert.False(view.Tags.Has(GameplayTags.ProjReflected));
        }

        [Fact]
        public void Step_1000FramesAfterWarmup_AllocatesNothing()
        {
            // 总线带 1 个命令处理器 + 1 个事件订阅者：BattleWorld 帧内不得触碰 CQE，计数必须保持 0。
            CqeBus bus = new CqeBus();
            CountingCommandHandler commandHandler = new CountingCommandHandler();
            CountingEventHandler eventHandler = new CountingEventHandler();
            bus.RegisterCommandHandler(commandHandler);
            bus.Subscribe(eventHandler);
            Assert.Equal(1, bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, bus.SubscriberCount);

            // MaxFrames = 1200 > 预热 60 + 测量 1000，因此单个会话就能跑满 1000 个真实管线帧，
            // 不会被上限截断成 no-op（截断的话"0 分配"会变成假绿）。
            BattleWorld world = CreateIdleWorld();

            for (int i = 0; i < WarmupFrames; i++)
            {
                world.Step(StepSeconds);
            }

            const int MeasuredFrames = 1000;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredFrames; i++)
            {
                world.Step(StepSeconds);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            _output.WriteLine(
                "BattleWorld: " + MeasuredFrames + " pipeline frames after " + WarmupFrames +
                " warmup frames allocated bytes = " + allocated);

            Assert.Equal(0L, allocated);
            Assert.Equal(WarmupFrames + MeasuredFrames, world.FrameId);
            Assert.False(world.IsFinished);

            Assert.Equal(0, commandHandler.CallCount);
            Assert.Equal(0, eventHandler.CallCount);

            // 证明"0 次调用"不是接线没接上：手动发一条就能命中同一个处理器。
            bus.Send(new ProbeCommand(1));
            Assert.Equal(1, commandHandler.CallCount);
        }

        [Fact]
        public void CollectResult_RequiresFinishedAndSingleCall()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle hero = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 1, 10, GameplayTagMask.Empty);
            UnitHandle victim = world.SpawnUnit(new Vec2(1f, 0f), 0.5f, 1, 1, GameplayTagMask.Empty);

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            world.Submit(new DamageIntent(hero, victim, 5, 0));
            world.Step(StepSeconds);
            Assert.True(world.IsFinished);

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(GameplayKernelType.Realtime, result.KernelType);
            Assert.Equal(1, result.StepCount);
            Assert.Equal(1, result.RemainingUnits);
            Assert.Single(result.Loots);
            Assert.Equal(2001, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_ReportsDefeat_WhenFirstSpawnedUnitDies()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle hero = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 1, 1, GameplayTagMask.Empty);
            UnitHandle enemy = world.SpawnUnit(new Vec2(1f, 0f), 0.5f, 1, 10, GameplayTagMask.Empty);

            world.Submit(new DamageIntent(enemy, hero, 10, 0));
            world.Step(StepSeconds);

            Assert.True(world.IsFinished);

            GameplayResult result = world.CollectResult();

            // 主角阵亡 → 判负；场上仍活着的敌人计入 RemainingUnits，但一件战利品都不发。
            Assert.False(result.IsVictory);
            Assert.Equal(1, result.RemainingUnits);
            Assert.Empty(result.Loots);
        }

        // ── 测试内阶段与假总线处理器 ─────────────────────────────────────

        /// <summary>记录阶段 3 看到的帧号与 owner，并提交一条位移意图。</summary>
        private sealed class OrderProbePhase : ISkillInteractionPhase
        {
            public UnitHandle Mover;

            public int ExecuteCount;

            public int LastFrameId = -1;

            public UnitHandle LastOwner;

            public void Execute(in PhaseContext context, IIntentWriter writer)
            {
                ExecuteCount++;
                LastFrameId = context.FrameId;
                LastOwner = context.Owner.Handle;
                writer.Submit(new MoveIntent(Mover, new Vec2(1f, 0f)));
            }
        }

        /// <summary>用 context 里的护盾 owner 提交反射意图，模拟业务切片的真实用法。</summary>
        private sealed class ReflectProbePhase : ISkillInteractionPhase
        {
            public ProjectileHandle Projectile;

            public int ExecuteCount;

            public int LastFrameId = -1;

            public UnitHandle LastOwner;

            public void Execute(in PhaseContext context, IIntentWriter writer)
            {
                ExecuteCount++;
                LastFrameId = context.FrameId;
                LastOwner = context.Owner.Handle;
                writer.Submit(new ReflectProjectileIntent(context.Owner.Handle, Projectile));
            }
        }

        private readonly struct ProbeCommand : ICommand
        {
            public ProbeCommand(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private readonly struct ProbeEvent : IEvent
        {
            public ProbeEvent(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private sealed class CountingCommandHandler : ICommandHandler<ProbeCommand>
        {
            public int CallCount;

            public void Handle(in ProbeCommand command)
            {
                CallCount++;
            }
        }

        private sealed class CountingEventHandler : IEventHandler<ProbeEvent>
        {
            public int CallCount;

            public void Handle(in ProbeEvent evt)
            {
                CallCount++;
            }
        }

        // ── 辅助方法 ────────────────────────────────────────────────────

        /// <summary>构造世界；<c>null</c> 表示走内核内置的线性空间查询兜底实现（构造期一次分配）。</summary>
        private static BattleWorld CreateWorld(int declaredUnitCount, ISkillInteractionPhase[] phases)
        {
            GameplayInitData init = new GameplayInitData(1, 20241118, declaredUnitCount);
            return new BattleWorld(in init, null!, phases);
        }

        /// <summary>构造一个永不结束的静态战场：2 个高血量单位 + 1 个远离单位匀速飞行的飞行物。</summary>
        private static BattleWorld CreateIdleWorld()
        {
            BattleWorld world = CreateWorld(2, Array.Empty<ISkillInteractionPhase>());
            UnitHandle owner = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 10, 1000000, GameplayTagMask.Empty);
            world.SpawnUnit(new Vec2(20f, 0f), 0.5f, 10, 1000000, GameplayTagMask.Empty);
            world.SpawnProjectile(owner, new Vec2(100f, 100f), new Vec2(1f, 0f), 1, GameplayTagMask.Empty);
            return world;
        }
    }
}

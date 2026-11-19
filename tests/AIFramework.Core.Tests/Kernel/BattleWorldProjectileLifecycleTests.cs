using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Numerics;
using AIFramework.Core.Tests.Replay;
using AIFramework.Kernel.Realtime;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// 飞行物生命周期：出界必须回收槽位。
    /// <para>
    /// <b>为什么值得单独钉死</b>：契约里没有 despawn 通道，内核原先只在命中时回收。
    /// 弹幕类玩法（竞技场/弹幕场景）每帧持续生成飞行物，跑满 1200 帧后会把
    /// <c>unitCapacity*2</c> 的槽位打满，新弹幕静默生成失败却没有任何报错——
    /// 表现为"后半段突然没有弹幕了"，是最难查的一类隐性故障。
    /// </para>
    /// </summary>
    public sealed class BattleWorldProjectileLifecycleTests
    {
        [Fact]
        public void Projectile_FlyingOutOfBounds_IsRecycled()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 2);
            var spatial = new MockSpatialQuery();
            var world = new BattleWorld(in init, spatial, Array.Empty<ISkillInteractionPhase>());
            spatial.Bind(world);

            UnitHandle shooter = world.SpawnUnit(Vec2.Zero, 0.5f, 0, 100, GameplayTagMask.Empty);
            UnitHandle target = world.SpawnUnit(new Vec2(0f, 3f), 0.5f, 0, 100, GameplayTagMask.Empty);

            // 朝 -X 方向飞：不会撞到任何单位，只能靠出界回收。
            world.SpawnProjectile(shooter, new Vec2(0f, 0f), new Vec2(-30f, 0f), 10, GameplayTagMask.Empty);
            Assert.Equal(1, world.ProjectileCount);

            // 40 米 / 30 米每秒 ≈ 1.34 秒 → 100 帧足够飞出去。
            for (int i = 0; i < 100; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(0, world.ProjectileCount);
        }

        [Fact]
        public void Projectile_InsideBounds_StaysAlive()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 2);
            var spatial = new MockSpatialQuery();
            var world = new BattleWorld(in init, spatial, Array.Empty<ISkillInteractionPhase>());
            spatial.Bind(world);

            UnitHandle shooter = world.SpawnUnit(Vec2.Zero, 0.5f, 0, 100, GameplayTagMask.Empty);
            world.SpawnUnit(new Vec2(0f, 3f), 0.5f, 0, 100, GameplayTagMask.Empty);

            // 慢速朝 +X 飞：20 帧只走 ~1 米，远未出界。
            world.SpawnProjectile(shooter, new Vec2(0f, 0f), new Vec2(3f, 0f), 10, GameplayTagMask.Empty);

            for (int i = 0; i < 20; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(1, world.ProjectileCount);
        }

        [Fact]
        public void Projectile_RecycledSlot_CanBeReused()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 1);
            var spatial = new MockSpatialQuery();
            var world = new BattleWorld(in init, spatial, Array.Empty<ISkillInteractionPhase>());
            spatial.Bind(world);

            UnitHandle shooter = world.SpawnUnit(Vec2.Zero, 0.5f, 0, 100, GameplayTagMask.Empty);

            // 容量是 unitCapacity*2 = 2；连续发射并让它们出界回收，槽位应能复用。
            for (int round = 0; round < 4; round++)
            {
                ProjectileHandle handle = world.SpawnProjectile(
                    shooter, new Vec2(0f, 0f), new Vec2(-30f, 0f), 10, GameplayTagMask.Empty);
                Assert.True(handle.IsValid, "第 " + round + " 轮生成失败：槽位没有被回收复用。");

                for (int i = 0; i < 100; i++)
                {
                    world.Step(1f / 60f);
                }

                Assert.Equal(0, world.ProjectileCount);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Ports;
using AIFramework.Kernel.Realtime;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// Realtime 内核在**真 Unity 运行时**的分配门禁：预热后连续 600 帧，托管堆增量必须可忽略。
    /// <para>
    /// 纯 .NET 侧已有"1000 帧 0 分配"断言（`tests/AIFramework.Core.Tests`），但那只证明同一套逻辑在
    /// .NET 10 上的行为。Unity 的 Mono 运行时、GC 与 JIT 情况不同，这里补一条"引擎内真机"的门禁。
    /// </para>
    /// <para>
    /// <b>为什么断言是"≤ 阈值"而不是严格 0</b>：PlayMode 测试框架本身、断言失败信息、日志都可能在同一线程
    /// 分配（尤其测试结束阶段），严格 0 会变成 CI 抖动源。这里用 1 KB 作为"框架噪声上界"，
    /// 内核真实分配若发生（每帧几十字节 × 600 帧也要 10 KB 以上）会远超阈值而被抓住。
    /// </para>
    /// </summary>
    public sealed class RealtimeAllocationTests
    {
        private const int WarmupSteps = 200;
        private const int MeasuredSteps = 600;
        private const long FrameworkNoiseBudgetBytes = 1024;

        [UnityTest]
        public IEnumerator BattleWorld_Step_AfterWarmup_AllocatesAlmostNothing()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 3);
            var world = new BattleWorld(in init, new EmptySpatialQuery(), Array.Empty<ISkillInteractionPhase>());

            GameplayTagMask shieldTags = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            UnitHandle protagonist = world.SpawnUnit(new Vec2(0f, 0f), 1f, 3, 100, shieldTags);
            world.SpawnUnit(new Vec2(0f, 3f), 1f, 0, 40, GameplayTagMask.Empty);
            world.SpawnProjectile(protagonist, new Vec2(0f, 1.4f), new Vec2(0f, -6f), 10, GameplayTagMask.Empty);

            for (int i = 0; i < WarmupSteps; i++)
            {
                world.Step(1f / 60f);
            }

            // 预热后再起一局，确保测的是稳态。
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredSteps; i++)
            {
                world.Step(1f / 60f);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.LessOrEqual(
                allocated,
                FrameworkNoiseBudgetBytes,
                "Realtime 内核在 Unity 里连续 " + MeasuredSteps + " 帧分配了 " + allocated
                + " 字节（预算 " + FrameworkNoiseBudgetBytes + "）。检查是否有每帧 new/装箱/字符串拼接。");

            yield return null;
        }

        [UnityTest]
        public IEnumerator BattleWorld_SpawnAndStep_KeepsStableSlots()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 7, unitCount: 2);
            var world = new BattleWorld(in init, new EmptySpatialQuery(), Array.Empty<ISkillInteractionPhase>());

            UnitHandle shooter = world.SpawnUnit(new Vec2(0f, 0f), 0.5f, 0, 100, GameplayTagMask.Empty);
            world.SpawnUnit(new Vec2(0f, 4f), 0.5f, 0, 100, GameplayTagMask.Empty);

            // 连续发射并让它们出界：槽位应被回收复用，UnitCount 不变、ProjectileCount 不会无限增长。
            for (int i = 0; i < 20; i++)
            {
                ProjectileHandle handle = world.SpawnProjectile(
                    shooter, new Vec2(0f, 0f), new Vec2(-30f, 0f), 5, GameplayTagMask.Empty);
                Assert.IsTrue(handle.IsValid, "第 " + i + " 发弹幕生成失败：槽位没有被回收。");
            }

            for (int i = 0; i < 120; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.AreEqual(2, world.UnitCount, "单位数不应变化。");
            Assert.LessOrEqual(world.ProjectileCount, 2, "出界弹幕应已被回收。");

            yield return null;
        }

        /// <summary>永久空的查询端口（本档只测分配与槽位，不需要真实命中）。</summary>
        private sealed class EmptySpatialQuery : ISpatialQuery
        {
            public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
            {
                return 0;
            }

            public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
            {
                return 0;
            }
        }
    }
}

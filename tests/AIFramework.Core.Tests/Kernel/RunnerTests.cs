using System;
using System.Collections.Generic;
using System.Reflection;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Runner;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="RunnerWorld"/> 的跑酷契约：初始读数、自动前进与速度提升、距离计分、跳跃抛物线、
    /// 下蹲时长与自动站起、Low/High 两类障碍的碰撞与规避、障碍生成与回收、同种子可复现、结算门禁与战利品，
    /// 以及"预热后推进 / 输入 / 重开"的零分配基线。
    /// <para>
    /// <b>为什么跳跃用例要扫描起跳时机而不是写死帧号</b>：规则给的 6.5 m/s 初速与 20 m/s² 重力，
    /// 解析顶点只有 1.056 米，而 Low 的判定线是 1.0 米——越过 1.0 米的滞空时间约 0.15 秒，
    /// 而 <c>|x| ≤ 0.6</c> 的碰撞窗在 9~10 m/s 下也正好约 0.13 秒级。两者几乎等宽，
    /// 因此"能躲开"是**存在性**结论：本文件逐帧扫描出手窗口证明它确实可达，而不是假定某一帧一定安全。
    /// </para>
    /// <para>
    /// <b>布场方式（白盒夹具）</b>：契约没有"把距离推到 50 米"这类入口（也不该有），
    /// 而"速度提升边界""胜利线 500 分"必须从精确距离出发，因此用反射写 <c>_distance</c> 一个字段
    /// （<see cref="SetDistance"/>）；内核字段一旦改名，夹具会立刻失败而不是静默跳过。
    /// 其余用例全部只走公开 API。
    /// </para>
    /// </summary>
    public sealed class RunnerTests
    {
        /// <summary>主种子（只用于与种子无关的用例；障碍种类由种子决定，需要特定种类时另找种子）。</summary>
        private const int Seed = 20261120;

        /// <summary>一帧 = 1/60 秒（与真实帧率一致；跑酷的跳跃/碰撞窗口都是按这个量级定的）。</summary>
        private const double OneFrame = 1.0 / 60.0;

        /// <summary>推演/循环的安全上限，防止行为回归时测试挂死。</summary>
        private const int LoopGuard = 4000;

        // ── 初始状态 ────────────────────────────────────────────────────

        [Fact]
        public void Constructor_InitialViewIsEmpty()
        {
            RunnerWorld world = CreateWorld();

            Assert.Equal(0f, world.Distance);
            Assert.Equal(0, world.Score);
            Assert.Equal(0f, world.PlayerY);
            Assert.Equal(0f, world.PlayerX);
            Assert.False(world.IsSliding);
            Assert.False(world.IsGameOver);
            Assert.False(world.IsFinished);
            Assert.Equal(0, world.ObstacleCount);
            Assert.Equal(8f, world.Speed);
            Assert.Equal(0, world.BestScore);
            Assert.Equal(0, world.ProcessedSteps);
        }

        [Fact]
        public void GetObstacle_OutOfRange_ReturnsInvalidView()
        {
            RunnerWorld world = CreateWorld();
            TickFrames(world, 90);

            Assert.Equal(1, world.ObstacleCount);

            // 0 永远不是合法障碍编号，因此越界查询可以被清楚地区分出来。
            RunnerObstacleView before = world.GetObstacle(-1);
            RunnerObstacleView after = world.GetObstacle(world.ObstacleCount);
            Assert.Equal(0, before.ObstacleId);
            Assert.Equal(0, after.ObstacleId);
            Assert.Equal(0f, after.X);
            Assert.False(after.Hit);
            Assert.True(world.GetObstacle(0).ObstacleId >= 1);
        }

        // ── 自动前进 / 速度提升 / 距离计分 ──────────────────────────────

        [Fact]
        public void Tick_AdvancesDistanceAndScore()
        {
            RunnerWorld world = CreateWorld();
            Assert.Equal(8f, world.Speed);

            world.Tick(0.25);
            Assert.Equal(2f, world.Distance);
            Assert.Equal(20, world.Score);
            Assert.InRange(world.Speed, 8.07f, 8.09f);

            // 第二帧起速度已经随距离抬了一点（8.08 m/s），因此分数仍按整米取整。
            world.Tick(0.25);
            Assert.InRange(world.Distance, 4.01f, 4.03f);
            Assert.Equal(40, world.Score);
            Assert.InRange(world.Speed, 8.16f, 8.17f);

            // 分数是 floor(Distance) * 10：距离 4.02 米仍是 40 分。
            Assert.Equal(40, (int)world.Distance * 10);
        }

        [Fact]
        public void Speed_RampsLinearlyAndCapsAtTwelve()
        {
            RunnerWorld world = CreateWorld();

            Assert.Equal(8f, world.Speed);

            SetDistance(world, 25f);
            Assert.Equal(9f, world.Speed);

            SetDistance(world, 50f);
            Assert.Equal(10f, world.Speed);

            SetDistance(world, 100f);
            Assert.Equal(12f, world.Speed);

            // 封顶后不再上涨。
            SetDistance(world, 250f);
            Assert.Equal(12f, world.Speed);
        }

        [Fact]
        public void Tick_AtHigherSpeed_AddsMoreDistancePerSecond()
        {
            RunnerWorld world = CreateWorld();
            SetDistance(world, 100f);

            // 100 米处的速度是 12 m/s：0.25 秒应前进 3 米，而不是起步时的 2 米。
            world.Tick(0.25);
            Assert.Equal(103f, world.Distance);
            Assert.Equal(1030, world.Score);
        }

        [Fact]
        public void Obstacles_SpawnOncePerIntervalAheadOfPlayer()
        {
            RunnerWorld world = CreateWorld();

            // 间隔 1.4 秒：1.333 秒时还没有障碍。
            TickFrames(world, 80);
            Assert.Equal(0, world.ObstacleCount);

            int frames = 80;
            while (world.ObstacleCount == 0 && frames < 200)
            {
                world.Tick(OneFrame);
                frames++;
            }

            Assert.InRange(frames, 81, 88);
            Assert.Equal(1, world.ObstacleCount);
            Assert.Equal(1, world.GetObstacle(0).ObstacleId);

            // 出生点就在"当帧距离 + 20 米"，出生帧内不再被后移。
            Assert.Equal(world.Distance + 20f, world.GetObstacle(0).X);
        }

        // ── 跳跃 ────────────────────────────────────────────────────────

        [Fact]
        public void Jump_RisesThenFallsBackToGround()
        {
            RunnerWorld world = CreateWorld();
            double distanceAtJump = world.Distance;

            Assert.True(world.TryJump());

            float apex = 0f;
            int frames = 0;
            do
            {
                world.Tick(OneFrame);
                frames++;
                if (world.PlayerY > apex)
                {
                    apex = world.PlayerY;
                }
            }
            while (world.PlayerY > 0f && frames < 200);

            // 解析顶点 6.5² / (2 × 20) = 1.056 米，滞空 0.65 秒（约 39 帧）。
            Assert.InRange(frames, 35, 45);
            Assert.InRange(apex, 1.0f, 1.12f);
            Assert.Equal(0f, world.PlayerY);
            Assert.True(world.Distance > (float)distanceAtJump);
        }

        [Fact]
        public void TryJump_WhileAirborne_IsRejected()
        {
            RunnerWorld world = CreateWorld();

            // 起跳当帧还没有离地高度，但已经在空中：二段跳必须被拒（否则垂直速度会被反复覆盖）。
            Assert.True(world.TryJump());
            Assert.False(world.TryJump());

            world.Tick(OneFrame);
            Assert.True(world.PlayerY > 0f);
            Assert.False(world.TryJump());
        }

        // ── 下蹲 ────────────────────────────────────────────────────────

        [Fact]
        public void Slide_StaysForDurationThenStandsUp()
        {
            RunnerWorld world = CreateWorld();

            Assert.True(world.TrySlide(0.5));
            Assert.True(world.IsSliding);

            TickFrames(world, 24);
            Assert.True(world.IsSliding);

            TickFrames(world, 12);
            Assert.False(world.IsSliding);
        }

        [Fact]
        public void TrySlide_WhileAirborne_IsRejected()
        {
            RunnerWorld world = CreateWorld();
            Assert.True(world.TryJump());

            Assert.False(world.TrySlide(0.5));
            Assert.False(world.IsSliding);

            // 落地后可以正常下蹲。
            TickFrames(world, 45);
            Assert.Equal(0f, world.PlayerY);
            Assert.True(world.TrySlide(0.5));
            Assert.True(world.IsSliding);
        }

        [Fact]
        public void TrySlide_InvalidDuration_ClampsToMinimum()
        {
            RunnerWorld world = CreateWorld();

            // 0 秒、负数与超过 3 秒的请求一律按 0.2 秒处理。
            Assert.True(world.TrySlide(0.0));
            TickFrames(world, 13);
            Assert.False(world.IsSliding);

            Assert.True(world.TrySlide(-5.0));
            TickFrames(world, 13);
            Assert.False(world.IsSliding);

            Assert.True(world.TrySlide(9.0));
            TickFrames(world, 13);
            Assert.False(world.IsSliding);

            // 边界值 3 秒本身不夹取。
            Assert.True(world.TrySlide(3.0));
            TickFrames(world, 30);
            Assert.True(world.IsSliding);
        }

        // ── 障碍碰撞 ────────────────────────────────────────────────────

        [Fact]
        public void LowObstacle_WithoutJump_KillsRunner()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.Low));

            TickUntilGameOver(world);

            Assert.Equal(0f, world.PlayerY);
            RunnerObstacleView killer = world.GetObstacle(0);
            Assert.Equal(RunnerObstacleKind.Low, killer.Kind);
            Assert.True(killer.Hit);
            Assert.InRange(killer.X, -0.6f, 0.6f);

            // 不操作时跑不到 50 米（420 分左右），因此这一局是失败局。
            Assert.InRange(world.Score, 0, 499);
        }

        [Fact]
        public void HighObstacle_WithoutSlide_KillsRunner()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.High));

            TickUntilGameOver(world);

            Assert.False(world.IsSliding);
            RunnerObstacleView killer = world.GetObstacle(0);
            Assert.Equal(RunnerObstacleKind.High, killer.Kind);
            Assert.True(killer.Hit);
            Assert.InRange(killer.X, -0.6f, 0.6f);
        }

        [Fact]
        public void HighObstacle_SlidingDodges()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.High));

            int firstId = 0;
            bool slid = false;
            int guard = 0;

            while (!world.IsGameOver && guard++ < LoopGuard)
            {
                if (world.ObstacleCount > 0)
                {
                    RunnerObstacleView head = world.GetObstacle(0);
                    if (firstId == 0)
                    {
                        firstId = head.ObstacleId;
                    }

                    // 障碍进入 4 米内就开蹲：1.5 秒足够它从 4 米跑到身后并回收。
                    if (!slid && head.Kind == RunnerObstacleKind.High && head.X > 0f && head.X < 4f)
                    {
                        Assert.True(world.TrySlide(1.5));
                        slid = true;
                    }

                    if (!ContainsObstacle(world, firstId))
                    {
                        break;
                    }
                }

                world.Tick(OneFrame);
            }

            Assert.True(slid);
            Assert.False(world.IsGameOver);
            Assert.False(ContainsObstacle(world, firstId));
        }

        [Fact]
        public void LowObstacle_JumpTimingScan_FindsAtLeastOneDodge()
        {
            int seed = FindSeedByFirstKind(RunnerObstacleKind.Low);
            int survivors = 0;
            int firstSurvivorOffset = -1;

            // 出手窗口只有一帧两级（见类注释）：逐帧扫描 2.5~5.5 秒的起跳时机，断言"存在能躲开的时机"。
            for (int offset = 150; offset <= 330; offset++)
            {
                RunnerWorld world = CreateWorld(seed);
                for (int i = 0; i < offset && !world.IsGameOver; i++)
                {
                    world.Tick(OneFrame);
                }

                if (world.IsGameOver || !world.TryJump())
                {
                    continue;
                }

                int firstId = world.ObstacleCount > 0 ? world.GetObstacle(0).ObstacleId : 0;
                int guard = 0;
                while (!world.IsGameOver && guard++ < 600)
                {
                    world.Tick(OneFrame);
                    if (firstId != 0 && !ContainsObstacle(world, firstId))
                    {
                        break;
                    }
                }

                if (!world.IsGameOver)
                {
                    survivors++;
                    if (firstSurvivorOffset < 0)
                    {
                        firstSurvivorOffset = offset;
                    }
                }
            }

            Assert.True(survivors > 0, "在 150..330 帧的起跳时机里没有任何一帧能躲开 Low 障碍。");
            Assert.InRange(firstSurvivorOffset, 150, 330);
        }

        // ── 生成 / 回收 / 可复现 ────────────────────────────────────────

        [Fact]
        public void Obstacle_PassingPlayer_IsRecycledAndCountDrops()
        {
            // 前两个障碍都是 High，玩家可以只靠下蹲安全穿过第一个，从而观察到"越过玩家后被回收"。
            int seed = FindSeedByFirstTwoKinds(RunnerObstacleKind.High, RunnerObstacleKind.High);
            RunnerWorld world = CreateWorld(seed);

            var slidIds = new List<int>();
            int firstId = 0;
            int maxCount = 0;
            int guard = 0;

            while (!world.IsGameOver && guard++ < LoopGuard)
            {
                if (world.ObstacleCount > maxCount)
                {
                    maxCount = world.ObstacleCount;
                }

                for (int i = 0; i < world.ObstacleCount; i++)
                {
                    RunnerObstacleView obstacle = world.GetObstacle(i);
                    if (firstId == 0)
                    {
                        firstId = obstacle.ObstacleId;
                    }

                    if (obstacle.Kind == RunnerObstacleKind.High
                        && obstacle.X > 0f
                        && obstacle.X < 4f
                        && !slidIds.Contains(obstacle.ObstacleId))
                    {
                        slidIds.Add(obstacle.ObstacleId);
                        world.TrySlide(1.5);
                    }
                }

                world.Tick(OneFrame);

                if (firstId != 0 && !ContainsObstacle(world, firstId))
                {
                    break;
                }
            }

            Assert.False(world.IsGameOver);
            Assert.True(slidIds.Count >= 1);
            Assert.False(ContainsObstacle(world, firstId));
            Assert.True(maxCount >= 2);
            Assert.True(world.ObstacleCount < maxCount);

            // 索引按生成顺序排列：剩下的障碍编号一定比被回收的那个大。
            for (int i = 0; i < world.ObstacleCount; i++)
            {
                Assert.True(world.GetObstacle(i).ObstacleId > firstId);
            }
        }

        [Fact]
        public void SameSeed_ReplaysIdenticalObstacleSequence()
        {
            int[] first = RecordFirstKinds(Seed, 3);
            int[] second = RecordFirstKinds(Seed, 3);

            Assert.Equal(first, second);
        }

        [Fact]
        public void DifferentSeed_ProducesDifferentObstacleSequence()
        {
            int[] lowFirst = RecordFirstKinds(FindSeedByFirstKind(RunnerObstacleKind.Low), 3);
            int[] highFirst = RecordFirstKinds(FindSeedByFirstKind(RunnerObstacleKind.High), 3);

            // 种子只决定"种类序列"，第一个障碍就已经不同，因此整串必然不同。
            Assert.Equal((int)RunnerObstacleKind.Low, lowFirst[0]);
            Assert.Equal((int)RunnerObstacleKind.High, highFirst[0]);
            Assert.NotEqual(lowFirst[0], highFirst[0]);
        }

        // ── 结束语义 / 重开 / 结算 ──────────────────────────────────────

        [Fact]
        public void GameOver_FreezesTickAndInputs()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.Low));
            TickUntilGameOver(world);

            float distance = world.Distance;
            int score = world.Score;
            int steps = world.ProcessedSteps;
            int count = world.ObstacleCount;
            float headX = world.GetObstacle(0).X;

            world.Tick(5.0);
            world.Step(5f);

            Assert.False(world.TryJump());
            Assert.False(world.TrySlide(1.0));

            Assert.Equal(distance, world.Distance);
            Assert.Equal(score, world.Score);
            Assert.Equal(steps, world.ProcessedSteps);
            Assert.Equal(count, world.ObstacleCount);
            Assert.Equal(headX, world.GetObstacle(0).X);
            Assert.Equal(0f, world.PlayerY);
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void Restart_ResetsRunButKeepsBestScore()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.Low));
            TickUntilGameOver(world);

            int best = world.Score;
            Assert.Equal(best, world.BestScore);

            world.Restart();

            Assert.Equal(0f, world.Distance);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ObstacleCount);
            Assert.Equal(0f, world.PlayerY);
            Assert.False(world.IsSliding);
            Assert.False(world.IsGameOver);
            Assert.False(world.IsFinished);

            // 重开不清零"本会话最好"，也不清零会话步数。
            Assert.Equal(best, world.BestScore);

            world.Tick(0.25);
            Assert.Equal(2f, world.Distance);
            Assert.Equal(20, world.Score);
            Assert.Equal(best, world.BestScore);
        }

        [Fact]
        public void CollectResult_ThrowsBeforeGameOver()
        {
            RunnerWorld world = CreateWorld();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            world.Tick(0.25);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_ThrowsOnSecondCall()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.Low));
            TickUntilGameOver(world);

            GameplayResult result = world.CollectResult();
            Assert.NotNull(result);
            Assert.True(world.IsFinished);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_Defeat_ReportsTurnWithoutLoot()
        {
            RunnerWorld world = CreateWorld(FindSeedByFirstKind(RunnerObstacleKind.Low));

            int guard = 0;
            while (!world.IsFinished && guard++ < LoopGuard)
            {
                world.Step(1f / 60f);
            }

            Assert.True(world.IsGameOver);
            Assert.InRange(world.Score, 0, 499);

            GameplayResult result = world.CollectResult();

            // 跑酷结算按 Turn 口径，不新增枚举值。
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Equal(world.ProcessedSteps, result.StepCount);
            Assert.Equal((int)world.Distance, result.RemainingUnits);
            Assert.True(result.StepCount > 0);
        }

        [Fact]
        public void CollectResult_Victory_AtFiftyMetersWithLoot()
        {
            RunnerWorld world = CreateWorld(Seed);

            // 胜利线 = 500 分 = 50 米；推演本身不可能手动跑到这么远，因此白盒把距离推到 50 米。
            SetDistance(world, 50f);
            Assert.Equal(500, world.Score);

            TickUntilGameOver(world);

            GameplayResult result = world.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.InRange(world.Score, 500, int.MaxValue);

            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(1003, loot.ItemId);
            Assert.Equal(1, loot.Count);
            Assert.Equal((int)world.Distance, result.RemainingUnits);
        }

        // ── 步进 / 非法帧长 / 零分配 ────────────────────────────────────

        [Fact]
        public void Step_AdvancesProcessedSteps()
        {
            RunnerWorld world = CreateWorld();

            world.Step(0.25f);
            world.Step(0.25f);
            world.Step(0.25f);

            Assert.Equal(3, world.ProcessedSteps);
            Assert.InRange(world.Distance, 6.05f, 6.07f);
            Assert.Equal(60, world.Score);
        }

        [Fact]
        public void Tick_IgnoresNonPositiveAndNaNDelta()
        {
            RunnerWorld world = CreateWorld();

            world.Tick(0.0);
            world.Tick(-1.0);
            world.Tick(double.NaN);

            Assert.Equal(0f, world.Distance);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ObstacleCount);
            Assert.Equal(0f, world.PlayerY);

            // 合法帧长仍然照常推进。
            world.Tick(0.25);
            Assert.Equal(2f, world.Distance);
        }

        [Fact]
        public void Tick_AfterWarmup_DoesNotAllocate()
        {
            RunnerWorld world = CreateWorld(Seed);
            TickFrames(world, 200);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.Tick(OneFrame);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void InputsAndRestart_AfterWarmup_DoNotAllocate()
        {
            RunnerWorld world = CreateWorld(Seed);

            for (int i = 0; i < 200; i++)
            {
                world.Tick(OneFrame);
                if ((i & 7) == 0)
                {
                    world.TryJump();
                    world.TrySlide(0.4);
                }

                if ((i & 63) == 63)
                {
                    world.Restart();
                }
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.TryJump();
                world.TrySlide(0.4);
                world.Tick(OneFrame);
                if ((i & 63) == 63)
                {
                    world.Restart();
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        // ── 夹具 ────────────────────────────────────────────────────────

        private static RunnerWorld CreateWorld()
        {
            return CreateWorld(Seed);
        }

        private static RunnerWorld CreateWorld(int seed)
        {
            return new RunnerWorld(new GameplayInitData(1, seed, 0));
        }

        private static void TickFrames(RunnerWorld world, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                world.Tick(OneFrame);
            }
        }

        private static void TickUntilGameOver(RunnerWorld world)
        {
            int guard = 0;
            while (!world.IsGameOver && guard++ < LoopGuard)
            {
                world.Tick(OneFrame);
            }

            Assert.True(world.IsGameOver, "在 " + LoopGuard + " 帧内没有撞上任何障碍。");
        }

        private static bool ContainsObstacle(RunnerWorld world, int obstacleId)
        {
            for (int i = 0; i < world.ObstacleCount; i++)
            {
                if (world.GetObstacle(i).ObstacleId == obstacleId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>找"第一个障碍就是 <paramref name="kind"/>"的种子（种类序列完全由种子决定）。</summary>
        private static int FindSeedByFirstKind(RunnerObstacleKind kind)
        {
            for (int seed = 1; seed <= 2000; seed++)
            {
                RunnerWorld world = CreateWorld(seed);
                int guard = 0;
                while (world.ObstacleCount == 0 && !world.IsGameOver && guard++ < 600)
                {
                    world.Tick(OneFrame);
                }

                if (world.ObstacleCount > 0 && world.GetObstacle(0).Kind == kind)
                {
                    return seed;
                }
            }

            throw new InvalidOperationException("在 1..2000 的种子里找不到首障碍为 " + kind + " 的序列。");
        }

        /// <summary>找"前两个障碍依次为 <paramref name="first"/>、<paramref name="second"/>"的种子。</summary>
        private static int FindSeedByFirstTwoKinds(RunnerObstacleKind first, RunnerObstacleKind second)
        {
            for (int seed = 1; seed <= 2000; seed++)
            {
                RunnerWorld world = CreateWorld(seed);
                bool matched = false;

                for (int i = 0; i < 220 && !world.IsGameOver; i++)
                {
                    world.Tick(OneFrame);

                    if (world.ObstacleCount >= 1 && world.GetObstacle(0).Kind != first)
                    {
                        break;
                    }

                    if (world.ObstacleCount >= 2)
                    {
                        matched = world.GetObstacle(world.ObstacleCount - 1).Kind == second;
                        break;
                    }
                }

                if (matched)
                {
                    return seed;
                }
            }

            throw new InvalidOperationException("在 1..2000 的种子里找不到前两个障碍依次为 " + first + "/" + second + " 的序列。");
        }

        /// <summary>记录前 <paramref name="count"/> 个障碍的种类（测试侧独立采样，不读内核私有状态）。</summary>
        private static int[] RecordFirstKinds(int seed, int count)
        {
            RunnerWorld world = CreateWorld(seed);
            int[] kinds = new int[count];
            int seen = 0;
            int lastId = 0;

            for (int i = 0; i < 400 && seen < count && !world.IsGameOver; i++)
            {
                world.Tick(OneFrame);

                if (world.ObstacleCount == 0)
                {
                    continue;
                }

                RunnerObstacleView newest = world.GetObstacle(world.ObstacleCount - 1);
                if (newest.ObstacleId == lastId)
                {
                    continue;
                }

                lastId = newest.ObstacleId;
                kinds[seen] = (int)newest.Kind;
                seen++;
            }

            Assert.Equal(count, seen);
            return kinds;
        }

        /// <summary>
        /// 白盒夹具：把"已跑距离"直接写到 <c>_distance</c>，用于验证速度提升边界与 500 分胜利线。
        /// 契约刻意不提供这类入口（正常玩法只有 <see cref="RunnerWorld.Tick"/> 一种推进方式）。
        /// </summary>
        private static void SetDistance(RunnerWorld world, float meters)
        {
            FieldInfo field = typeof(RunnerWorld).GetField("_distance", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("RunnerWorld 缺少 _distance 字段：内核字段改名后本夹具必须同步。");

            Assert.Equal(typeof(float), field.FieldType);
            field.SetValue(world, meters);
        }
    }
}

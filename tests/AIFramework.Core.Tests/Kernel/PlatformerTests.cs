using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Platformer;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="PlatformerWorld"/> 的平台跳跃契约：3 关初始化与读数、左右移动与边界、
    /// 跳跃高度曲线与二段跳拒绝、单向平台（从下穿过 / 从上方落住）、掉出世界判负、
    /// 收集品与去重计分、终点（落地才算）与胜负结算、<c>Restart</c>、<c>Step</c> 计数、
    /// <c>LevelId</c> 夹取，以及"预热后 1000 次 Tick 零分配"。
    /// <para>
    /// <b>为什么不硬编码平台坐标</b>：关卡数据后续要换成配置表，硬编码 <c>(6, 1.2)</c> 这类坐标会把测试
    /// 变成"抄关卡表"。本文件一律从 <see cref="IPlatformerReadView.GetPlatform"/> 读出真实平台，
    /// 再按"相对这块平台的几何关系"构造场景——于是断言的是<b>规则</b>（单向、贴顶、落地），而不是某块平台的数值。
    /// </para>
    /// <para>
    /// <b>帧步长</b>：一律 1/60 秒（小于 0.05 秒的子步上限，因此一帧就是一个物理子步），
    /// 抛物线解析值：顶点 <c>8² / (2·22) = 1.4545</c> 米、滞空 <c>2·8 / 22 = 0.727</c> 秒（约 44 帧）。
    /// </para>
    /// </summary>
    public sealed class PlatformerTests
    {
        /// <summary>测试帧步长（秒）：1/60 小于内核 0.05s 的子步上限。</summary>
        private const float Frame = 1f / 60f;

        /// <summary>契约锁死的玩家 AABB 宽度（米），只用于"站在平台中段"这类几何辅助。</summary>
        private const float PlayerWidth = 0.8f;

        /// <summary>契约锁死的重力（米/秒²），用于解析顶点断言。</summary>
        private const float Gravity = 22f;

        /// <summary>契约锁死的起跳初速（米/秒）。</summary>
        private const float JumpSpeed = 8f;

        /// <summary>契约锁死的水平速度（米/秒）。</summary>
        private const float MoveSpeed = 6f;

        /// <summary>推演循环的安全上限，防止行为回归时测试挂死。</summary>
        private const int LoopGuard = 2000;

        // ── 3 关初始化与读数 ──────────────────────────────────────────

        [Fact]
        public void Constructor_LevelOne_IsAFlatGroundWithFiveFloatingPlatforms()
        {
            PlatformerWorld world = CreateWorld(1);

            Assert.Equal(1, world.LevelId);
            Assert.Equal(6, world.PlatformCount);
            Assert.Equal(6, world.CollectibleCount);
            Assert.Equal(40f, WorldWidth(world));
            Assert.Equal(40f - PlayerWidth, world.GoalX);

            // 出生即站立：脚底贴出生平台顶面，速度与分数全零。
            Assert.Equal(1f, world.PlayerX);
            Assert.Equal(GroundTop(world), world.PlayerY);
            Assert.Equal(0f, world.PlayerVelocityY);
            Assert.True(world.IsOnGround);
            Assert.False(world.IsFinished);
            Assert.False(world.IsVictory);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.CollectedCount);
            Assert.Equal(0, world.ProcessedSteps);

            // 第 1 关地面横贯全图（没有坑）：地面平台右沿就是世界宽度。
            PlatformerView ground = world.GetPlatform(0);
            Assert.Equal(0f, ground.X);
            Assert.Equal(0f, ground.Y);
            Assert.Equal(WorldWidth(world), ground.X + ground.Width);

            int floating = 0;
            for (int i = 0; i < world.PlatformCount; i++)
            {
                if (world.GetPlatform(i).Y > 0f)
                {
                    floating++;
                }
            }

            Assert.Equal(5, floating);
        }

        [Fact]
        public void Constructor_LevelTwo_HasThreePitsAndSevenPlatforms()
        {
            PlatformerWorld world = CreateWorld(2);

            Assert.Equal(2, world.LevelId);
            Assert.Equal(7, world.PlatformCount);
            Assert.Equal(7, world.CollectibleCount);
            Assert.Equal(48f, WorldWidth(world));
            Assert.Equal(48f - PlayerWidth, world.GoalX);
            Assert.Equal(3, CountGroundPits(world));
        }

        [Fact]
        public void Constructor_LevelThree_UsesEightPlatformsAndCollectibles()
        {
            PlatformerWorld world = CreateWorld(3);

            Assert.Equal(3, world.LevelId);
            Assert.Equal(8, world.PlatformCount);
            Assert.Equal(8, world.CollectibleCount);
            Assert.Equal(56f, WorldWidth(world));
            Assert.Equal(56f - PlayerWidth, world.GoalX);
            Assert.Equal(3, CountGroundPits(world));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(-3)]
        [InlineData(int.MaxValue)]
        public void Constructor_UnknownLevelId_ClampsToOne(int levelId)
        {
            PlatformerWorld world = CreateWorld(levelId);

            Assert.Equal(1, world.LevelId);
            Assert.Equal(6, world.PlatformCount);
            Assert.Equal(40f, WorldWidth(world));
        }

        [Fact]
        public void Constructor_PlatformsAreInsideWorldAndHavePositiveSize()
        {
            PlatformerWorld world = CreateWorld(3);
            float width = WorldWidth(world);

            for (int i = 0; i < world.PlatformCount; i++)
            {
                PlatformerView platform = world.GetPlatform(i);

                Assert.Equal(i + 1, platform.PlatformId);
                Assert.True(platform.Width > 0f, "平台宽度必须为正");
                Assert.True(platform.Height > 0f, "平台高度必须为正");
                Assert.InRange(platform.X, 0f, width);
                Assert.InRange(platform.Y, 0f, 100f);
                Assert.InRange(platform.X + platform.Width, platform.X, width);
            }
        }

        [Fact]
        public void Constructor_CollectiblesAreInsideWorldAndStartUncollected()
        {
            PlatformerWorld world = CreateWorld(3);
            float width = WorldWidth(world);

            for (int i = 0; i < world.CollectibleCount; i++)
            {
                CollectibleView collectible = world.GetCollectible(i);

                Assert.Equal(i + 1, collectible.CollectibleId);
                Assert.False(collectible.Collected);
                Assert.InRange(collectible.X, 0f, width);
                Assert.InRange(collectible.Y, 0f, 100f);
            }
        }

        [Fact]
        public void GetPlatform_OutOfRange_Throws()
        {
            PlatformerWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPlatform(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPlatform(world.PlatformCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPlatform(int.MaxValue));
        }

        [Fact]
        public void GetCollectible_OutOfRange_Throws_AndCollectedFlagsArePerIndex()
        {
            PlatformerWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetCollectible(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetCollectible(world.CollectibleCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetCollectible(int.MaxValue));

            // 越界抛异常而不是返回 default：0 号收集品是合法数据，不能被"越界"混为一谈。
            Assert.Equal(1, world.GetCollectible(0).CollectibleId);
        }

        // ── 左右移动与边界 ────────────────────────────────────────────

        [Fact]
        public void SetMove_MovesRight_AndStopsWhenReleased()
        {
            PlatformerWorld world = CreateWorld(1);
            float start = world.PlayerX;

            world.SetMove(1f);
            Advance(world, 30);

            Assert.InRange(world.PlayerX, start + (MoveSpeed * 0.5f) - 0.01f, start + (MoveSpeed * 0.5f) + 0.01f);
            Assert.Equal(GroundTop(world), world.PlayerY);
            Assert.True(world.IsOnGround);

            float released = world.PlayerX;
            world.SetMove(0f);
            Advance(world, 30);

            Assert.Equal(released, world.PlayerX);
        }

        [Fact]
        public void SetMove_ClampsOutOfRangeAndTreatsNaNAsReleased()
        {
            PlatformerWorld world = CreateWorld(1);
            float start = world.PlayerX;

            // 满舵 +5 与 +1 等价：0.5s 只走 3 米（不是 15 米）。
            world.SetMove(5f);
            Advance(world, 30);
            Assert.InRange(world.PlayerX, start + 3f - 0.01f, start + 3f + 0.01f);

            // 满舵 -5 与 -1 等价：再走 0.5s 回到起点。
            world.SetMove(-5f);
            Advance(world, 30);
            Assert.InRange(world.PlayerX, start - 0.01f, start + 0.01f);

            // NaN 当松手：位置不能变成 NaN。
            world.SetMove(float.NaN);
            Advance(world, 30);
            Assert.Equal(start, world.PlayerX);
            Assert.False(float.IsNaN(world.PlayerX));
        }

        [Fact]
        public void SetMove_Left_StopsAtLeftWall()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(-1f);
            Advance(world, 30);
            Assert.Equal(0f, world.PlayerX);

            // 继续按住也不能穿到墙外，且站地状态不受影响。
            Advance(world, 60);
            Assert.Equal(0f, world.PlayerX);
            Assert.True(world.IsOnGround);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void SetMove_Right_StopsAtRightWallWhichIsTheGoalLine()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(1f);
            int frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsFinished);
            Assert.Equal(WorldWidth(world) - PlayerWidth, world.PlayerX);
            Assert.Equal(world.GoalX, world.PlayerX);

            // 结束后输入与推进都是空操作：位置不再变化，也不会二次计分。
            world.SetMove(-1f);
            world.Tick(Frame);
            Assert.Equal(world.GoalX, world.PlayerX);
        }

        [Fact]
        public void SetMove_AfterFinish_IsIgnored()
        {
            PlatformerWorld world = CreateWorld(2);

            // 走进第一个坑，判负。
            world.SetMove(1f);
            int frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsFinished);
            float x = world.PlayerX;
            float y = world.PlayerY;

            world.SetMove(-1f);
            world.Tick(Frame);

            Assert.Equal(x, world.PlayerX);
            Assert.Equal(y, world.PlayerY);
            Assert.False(world.TryJump());
        }

        // ── 跳跃 ──────────────────────────────────────────────────────

        [Fact]
        public void TryJump_OnGround_StartsJumpImmediately()
        {
            PlatformerWorld world = CreateWorld(1);
            float groundTop = GroundTop(world);

            Assert.True(world.TryJump());

            // 起跳当帧就离地，防止"起跳瞬间还能再跳一次"。
            Assert.False(world.IsOnGround);
            Assert.Equal(JumpSpeed, world.PlayerVelocityY);

            world.Tick(Frame);

            Assert.True(world.PlayerY > groundTop);
            Assert.True(world.PlayerVelocityY < JumpSpeed);
            Assert.True(world.PlayerVelocityY > 0f);
        }

        [Fact]
        public void TryJump_InAir_IsRejectedAndKeepsState()
        {
            PlatformerWorld world = CreateWorld(1);

            Assert.True(world.TryJump());
            world.Tick(Frame);

            float velocityBefore = world.PlayerVelocityY;
            float yBefore = world.PlayerY;

            Assert.False(world.TryJump());
            Assert.Equal(velocityBefore, world.PlayerVelocityY);
            Assert.Equal(yBefore, world.PlayerY);
            Assert.False(world.IsOnGround);
        }

        [Fact]
        public void TryJump_AfterLanding_IsAllowedAgain()
        {
            PlatformerWorld world = CreateWorld(1);
            float groundTop = GroundTop(world);

            Assert.True(world.TryJump());

            int frames = RunUntil(world, () => world.IsOnGround, 120);
            Assert.True(world.IsOnGround);
            Assert.InRange(frames, 42, 46);
            Assert.Equal(groundTop, world.PlayerY);

            Assert.True(world.TryJump());
            Assert.False(world.IsOnGround);
        }

        [Fact]
        public void Jump_ReachesAnalyticApexAndLandsBackOnGround()
        {
            PlatformerWorld world = CreateWorld(1);
            float groundTop = GroundTop(world);

            Assert.True(world.TryJump());

            float maxY = world.PlayerY;
            int frames = 0;
            while (!world.IsOnGround && frames < 120)
            {
                world.Tick(Frame);
                frames++;
                if (world.PlayerY > maxY)
                {
                    maxY = world.PlayerY;
                }
            }

            Assert.True(world.IsOnGround);
            Assert.Equal(0f, world.PlayerVelocityY);

            // 解析顶点 = 8² / (2·22) = 1.4545 米；采样点比连续顶点低不到一个帧步长的位移。
            float apex = (JumpSpeed * JumpSpeed) / (2f * Gravity);
            Assert.InRange(maxY - groundTop, apex - 0.01f, apex + 0.01f);
            Assert.InRange(frames, 42, 46);

            // 落回原平台顶面，不是掉到地面以下。
            Assert.Equal(groundTop, world.PlayerY);
        }

        [Fact]
        public void Jump_FromBelow_PassesThroughPlatformAbove()
        {
            PlatformerWorld world = CreateWorld(1);
            PlatformerView platform = MoveUnderFirstFloatingPlatform(world);
            float top = platform.Y + platform.Height;

            Assert.True(world.TryJump());

            bool sawAboveWhileRising = false;
            int frames = 0;
            while (!world.IsOnGround && frames < 120)
            {
                float previousY = world.PlayerY;
                world.Tick(Frame);
                frames++;

                if (!sawAboveWhileRising
                    && !world.IsOnGround
                    && world.PlayerY > top
                    && world.PlayerVelocityY > 0f)
                {
                    sawAboveWhileRising = true;

                    // 必须是"穿过去"，不能是被平台挡住后瞬移到顶面。
                    Assert.True(previousY < top, "从下方起跳时玩家不能被平台顶面挡住");
                }
            }

            Assert.True(sawAboveWhileRising, "从下方起跳必须能穿过单向平台");
            Assert.True(world.IsOnGround);
        }

        [Fact]
        public void Jump_FallingOntoPlatform_LandsExactlyOnItsTop()
        {
            PlatformerWorld world = CreateWorld(1);
            PlatformerView platform = MoveUnderFirstFloatingPlatform(world);
            float top = platform.Y + platform.Height;

            Assert.True(world.TryJump());

            int frames = 0;
            while (!world.IsOnGround && frames < 120)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsOnGround);
            Assert.Equal(top, world.PlayerY, 4);
            Assert.Equal(0f, world.PlayerVelocityY);
            Assert.True(frames > 20, "落地必须发生在抛物线下降段，而不是起跳当帧");
        }

        [Fact]
        public void WalkThroughBeneathFloatingPlatform_IsNotBlocked()
        {
            PlatformerWorld world = CreateWorld(1);
            PlatformerView platform = FirstFloatingPlatform(world);
            float groundTop = GroundTop(world);

            // 地面高度走到浮台右沿之外：玩家 AABB 会与浮台矩形竖直相交（脚底 1 < 顶面 2.2，头顶 2.6 > 底面 1.2），
            // 单向平台在左右方向不阻挡，因此不该被挡在浮台左沿，也不该被"抬"到顶面上。
            MoveRightToX(world, platform.X + platform.Width + 0.2f, LoopGuard);

            Assert.True(world.PlayerX > platform.X + platform.Width);
            Assert.Equal(groundTop, world.PlayerY);
            Assert.True(world.IsOnGround);
        }

        [Fact]
        public void WalkOffPlatform_FallsAndLandsOnGroundBelow()
        {
            PlatformerWorld world = CreateWorld(1);
            PlatformerView platform = MoveUnderFirstFloatingPlatform(world);
            float groundTop = GroundTop(world);

            Assert.True(world.TryJump());
            RunUntil(world, () => world.IsOnGround, 120);
            Assert.Equal(platform.Y + platform.Height, world.PlayerY, 4);

            // 走到浮台右沿之外：当帧失去支撑，转为自由下落。
            world.SetMove(1f);
            int frames = 0;
            while (world.IsOnGround && frames < 600)
            {
                world.Tick(Frame);
                frames++;
            }

            world.SetMove(0f);

            Assert.False(world.IsOnGround);
            Assert.True(world.PlayerX > platform.X + platform.Width);

            RunUntil(world, () => world.IsOnGround, 120);

            Assert.True(world.IsOnGround);
            Assert.Equal(groundTop, world.PlayerY);
        }

        [Fact]
        public void Tick_LargeDelta_SubstepsPreventTunnelingThroughGround()
        {
            PlatformerWorld world = CreateWorld(1);
            PlatformerView platform = MoveUnderFirstFloatingPlatform(world);
            float groundTop = GroundTop(world);

            Assert.True(world.TryJump());
            RunUntil(world, () => world.IsOnGround, 120);
            Assert.Equal(platform.Y + platform.Height, world.PlayerY, 4);

            // 走出浮台，然后一次性推 0.5 秒：不切子步会直接穿到地面以下（y ≈ -0.55），
            // 切了 0.05 秒子步就会在顶面 y = 1 上被接住。
            world.SetMove(1f);
            int frames = 0;
            while (world.IsOnGround && frames < 600)
            {
                world.Tick(Frame);
                frames++;
            }

            world.SetMove(0f);
            Assert.False(world.IsOnGround);

            world.Tick(0.5);

            Assert.True(world.IsOnGround);
            Assert.Equal(groundTop, world.PlayerY);
            Assert.False(world.IsFinished);
        }

        // ── 掉出世界 ──────────────────────────────────────────────────

        [Fact]
        public void WalkIntoGap_FallsOutOfWorld_AndFinishesAsDefeat()
        {
            PlatformerWorld world = CreateWorld(2);

            world.SetMove(1f);
            int frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsFinished);
            Assert.False(world.IsVictory);
            Assert.True(world.PlayerY < -10f);
            Assert.NotEqual(world.GoalX, world.PlayerX);
        }

        [Fact]
        public void FallOut_CollectResult_IsDefeatWithoutLoot()
        {
            PlatformerWorld world = CreateWorld(2);
            world.SetMove(1f);
            while (!world.IsFinished)
            {
                world.Tick(Frame);
            }

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(world.Score, result.RemainingUnits);
        }

        // ── 收集品 ────────────────────────────────────────────────────

        [Fact]
        public void Collect_OverlappingCollectible_ScoresTenAndMarksIt()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(1f);
            int frames = 0;
            while (world.Score == 0 && frames < 600 && !world.IsFinished)
            {
                world.Tick(Frame);
                frames++;
            }

            world.SetMove(0f);

            Assert.True(frames > 0);
            Assert.Equal(10, world.Score);
            Assert.Equal(1, world.CollectedCount);
            Assert.True(world.GetCollectible(0).Collected);

            // 其余收集品不能被"顺带"收掉（它们都在空中，必须跳上去）。
            for (int i = 1; i < world.CollectibleCount; i++)
            {
                Assert.False(world.GetCollectible(i).Collected);
            }
        }

        [Fact]
        public void Collect_SameCollectibleTwice_DoesNotScoreTwice()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(1f);
            while (world.Score == 0)
            {
                world.Tick(Frame);
            }

            int score = world.Score;
            int collected = world.CollectedCount;
            Assert.Equal(10, score);
            Assert.Equal(1, collected);

            // 原地折返，两次经过同一颗收集品。
            world.SetMove(-1f);
            Advance(world, 30);
            world.SetMove(1f);
            Advance(world, 60);
            world.SetMove(0f);

            Assert.Equal(score, world.Score);
            Assert.Equal(collected, world.CollectedCount);
            Assert.True(world.GetCollectible(0).Collected);
        }

        // ── 终点与胜负 ────────────────────────────────────────────────

        [Fact]
        public void Goal_OnGround_FinishesAsVictory_WithLoot()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(1f);
            int steps = 0;
            while (!world.IsFinished && steps < LoopGuard)
            {
                world.Step(Frame);
                steps++;
            }

            Assert.True(world.IsFinished);
            Assert.True(world.IsVictory);
            Assert.Equal(world.GoalX, world.PlayerX);
            Assert.True(world.IsOnGround);
            Assert.Equal(1, world.CollectedCount);
            Assert.Equal(10, world.Score);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Single(result.Loots);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(world.Score, result.RemainingUnits);
            Assert.Equal(world.ProcessedSteps, result.StepCount);
        }

        [Fact]
        public void Goal_CrossedWhileAirborne_DoesNotWinUntilLanding()
        {
            PlatformerWorld world = CreateWorld(1);
            float goalX = world.GoalX;

            MoveRightToX(world, goalX - 0.8f, LoopGuard);
            Assert.False(world.IsFinished);

            // 保持按住右键再起跳：空中继续向右，才会出现"人在终点线上方但还没落地"的帧。
            world.SetMove(1f);
            Assert.True(world.TryJump());

            bool sawAirborneBeyondGoal = false;
            int frames = 0;
            while (!world.IsOnGround && frames < 120)
            {
                world.Tick(Frame);
                frames++;

                if (!world.IsOnGround && world.PlayerX >= goalX)
                {
                    sawAirborneBeyondGoal = true;
                    Assert.False(world.IsFinished, "空中越过终点线不得判胜");
                    Assert.False(world.IsVictory);
                }
            }

            Assert.True(sawAirborneBeyondGoal, "起跳点必须让玩家在空中越过终点线");
            Assert.True(world.IsOnGround);
            Assert.True(world.IsFinished);
            Assert.True(world.IsVictory);
        }

        // ── 结算门禁与二次结算 ────────────────────────────────────────

        [Fact]
        public void CollectResult_BeforeFinished_Throws()
        {
            PlatformerWorld world = CreateWorld(1);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            world.Step(Frame);
            world.Step(Frame);
            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            PlatformerWorld world = CreateWorld(1);
            world.SetMove(1f);
            while (!world.IsFinished)
            {
                world.Tick(Frame);
            }

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.True(world.IsFinished);

            // 所有权已交还：Restart 不得把会话"复活"。
            float x = world.PlayerX;
            world.Restart();

            Assert.True(world.IsFinished);
            Assert.Equal(x, world.PlayerX);
        }

        // ── Restart / Step / 非法帧长 ─────────────────────────────────

        [Fact]
        public void Restart_ResetsRunButKeepsProcessedSteps()
        {
            PlatformerWorld world = CreateWorld(1);

            world.SetMove(1f);
            while (world.Score == 0)
            {
                world.Step(Frame);
            }

            world.SetMove(0f);
            int stepsBefore = world.ProcessedSteps;
            Assert.True(stepsBefore > 0);
            Assert.Equal(10, world.Score);

            world.Restart();

            Assert.Equal(1f, world.PlayerX);
            Assert.Equal(GroundTop(world), world.PlayerY);
            Assert.Equal(0f, world.PlayerVelocityY);
            Assert.True(world.IsOnGround);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.CollectedCount);
            Assert.False(world.IsFinished);
            Assert.False(world.IsVictory);
            Assert.Equal(stepsBefore, world.ProcessedSteps);

            for (int i = 0; i < world.CollectibleCount; i++)
            {
                Assert.False(world.GetCollectible(i).Collected);
            }
        }

        [Fact]
        public void Restart_AfterDefeat_AllowsReplay()
        {
            PlatformerWorld world = CreateWorld(2);

            world.SetMove(1f);
            int frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsFinished);
            Assert.False(world.IsVictory);
            int steps = world.ProcessedSteps;

            world.Restart();

            Assert.False(world.IsFinished);
            Assert.False(world.IsVictory);
            Assert.Equal(1f, world.PlayerX);
            Assert.Equal(GroundTop(world), world.PlayerY);
            Assert.True(world.IsOnGround);
            Assert.Equal(steps, world.ProcessedSteps);

            // 重开后可再次开跑，并且第二次仍然会掉进同一个坑（关卡数据没变）。
            world.SetMove(1f);
            frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(Frame);
                frames++;
            }

            Assert.True(world.IsFinished);
            Assert.False(world.IsVictory);
        }

        [Fact]
        public void Step_IncrementsProcessedSteps_AndStopsAfterFinish()
        {
            PlatformerWorld world = CreateWorld(1);

            world.Step(Frame);
            Assert.Equal(1, world.ProcessedSteps);

            world.Tick(Frame);
            Assert.Equal(1, world.ProcessedSteps);

            world.Step(Frame);
            Assert.Equal(2, world.ProcessedSteps);

            world.SetMove(1f);
            while (!world.IsFinished)
            {
                world.Step(Frame);
            }

            int stepsAtEnd = world.ProcessedSteps;
            Assert.True(stepsAtEnd > 2);
            Assert.Equal(stepsAtEnd, world.CollectResult().StepCount);

            world.Step(Frame);
            Assert.Equal(stepsAtEnd, world.ProcessedSteps);
        }

        [Fact]
        public void Tick_IgnoresNonPositiveAndNonFiniteDelta()
        {
            PlatformerWorld world = CreateWorld(1);
            float x = world.PlayerX;
            float y = world.PlayerY;

            world.Tick(0.0);
            world.Tick(-1.0);
            world.Tick(double.NaN);
            world.Tick(double.PositiveInfinity);
            world.Tick(double.NegativeInfinity);

            Assert.Equal(x, world.PlayerX);
            Assert.Equal(y, world.PlayerY);
            Assert.Equal(0f, world.PlayerVelocityY);
            Assert.True(world.IsOnGround);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.False(world.IsFinished);
        }

        // ── 零分配基线 ────────────────────────────────────────────────

        [Fact]
        public void Tick_AfterWarmup_DoesNotAllocate()
        {
            PlatformerWorld world = CreateWorld(1);

            for (int i = 0; i < 200; i++)
            {
                DriveOneFrame(world, i);
            }

            float xBeforeMeasure = world.PlayerX;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                DriveOneFrame(world, i);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);

            // 测量区间必须真的在推进物理，否则"零分配"会退化成空跑。
            Assert.True(Math.Abs(world.PlayerX - xBeforeMeasure) > 0.01f, "测量区间内玩家必须真的移动过");
            Assert.False(world.IsFinished);
        }

        /// <summary>零分配测量用的一帧驱动：左右来回 + 周期性起跳，全程不申请托管堆内存。</summary>
        private static void DriveOneFrame(PlatformerWorld world, int frameIndex)
        {
            if ((frameIndex % 12) == 0)
            {
                world.SetMove((frameIndex % 24) == 0 ? 1f : -1f);
            }

            if ((frameIndex % 40) == 0)
            {
                world.TryJump();
            }

            world.Tick(1.0 / 240.0);
        }

        // ── 夹具与小工具 ──────────────────────────────────────────────

        private static PlatformerWorld CreateWorld(int levelId)
        {
            return new PlatformerWorld(new GameplayInitData(levelId, 20261120, 0));
        }

        /// <summary>出生平台顶面（米）：第 0 块平台是出生平台，三关都是 <c>0 + 1 = 1</c>。</summary>
        private static float GroundTop(PlatformerWorld world)
        {
            PlatformerView ground = world.GetPlatform(0);
            return ground.Y + ground.Height;
        }

        /// <summary>世界宽度（米）：所有平台右沿的最大值（地面平台横贯全图，因此等于世界宽度）。</summary>
        private static float WorldWidth(PlatformerWorld world)
        {
            float maxRight = 0f;
            for (int i = 0; i < world.PlatformCount; i++)
            {
                PlatformerView platform = world.GetPlatform(i);
                float right = platform.X + platform.Width;
                if (right > maxRight)
                {
                    maxRight = right;
                }
            }

            return maxRight;
        }

        /// <summary>数地面（<c>Y == 0</c>）平台之间的坑：相邻地面平台间距 &gt; 2 米算一个坑。</summary>
        private static int CountGroundPits(PlatformerWorld world)
        {
            int pits = 0;
            float previousRight = -1f;

            for (int i = 0; i < world.PlatformCount; i++)
            {
                PlatformerView platform = world.GetPlatform(i);
                if (platform.Y > 0f)
                {
                    continue;
                }

                if (previousRight >= 0f && platform.X - previousRight > 2f)
                {
                    pits++;
                }

                previousRight = platform.X + platform.Width;
            }

            return pits;
        }

        /// <summary>按 1/60 秒推进指定帧数（不改变输入意图）。</summary>
        private static void Advance(PlatformerWorld world, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                world.Tick(Frame);
            }
        }

        /// <summary>推进到条件成立，返回消耗的帧数（带安全上限）。</summary>
        private static int RunUntil(PlatformerWorld world, Func<bool> condition, int maxFrames)
        {
            int frames = 0;
            while (!condition() && frames < maxFrames)
            {
                world.Tick(Frame);
                frames++;
            }

            return frames;
        }

        /// <summary>按住右键走到指定横坐标（到位后松手）。</summary>
        private static void MoveRightToX(PlatformerWorld world, float targetX, int maxFrames)
        {
            world.SetMove(1f);
            int frames = 0;
            while (world.PlayerX < targetX && frames < maxFrames && !world.IsFinished)
            {
                world.Tick(Frame);
                frames++;
            }

            world.SetMove(0f);

            Assert.True(world.PlayerX >= targetX, "玩家没有走到目标横坐标（关卡或移动速度被改坏了）");
        }

        /// <summary>
        /// 把玩家水平挪到第 1 关第一块浮台的中段下方，返回该浮台。
        /// <para>平台坐标一律从视图读，测试不复制关卡表。</para>
        /// </summary>
        private static PlatformerView MoveUnderFirstFloatingPlatform(PlatformerWorld world)
        {
            PlatformerView platform = FirstFloatingPlatform(world);

            MoveRightToX(world, platform.X + (platform.Width * 0.5f), LoopGuard);
            Assert.True(
                world.PlayerX < platform.X + platform.Width
                    && world.PlayerX + PlayerWidth > platform.X,
                "玩家必须站在浮台正下方");

            return platform;
        }

        /// <summary>第 1 关第一块浮台（<c>Y &gt; 0</c> 的第一块平台），并断言它在一跳可及的高度内。</summary>
        private static PlatformerView FirstFloatingPlatform(PlatformerWorld world)
        {
            for (int i = 0; i < world.PlatformCount; i++)
            {
                PlatformerView candidate = world.GetPlatform(i);
                if (candidate.Y <= 0f)
                {
                    continue;
                }

                Assert.True(
                    candidate.Y + candidate.Height < GroundTop(world) + 1.4f,
                    "第一块浮台必须在一跳（≈1.45 米）可及的范围内");

                return candidate;
            }

            Assert.Fail("第 1 关没有找到浮台。");
            return default;
        }
    }
}

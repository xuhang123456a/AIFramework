using AIFramework.Contracts.Gameplay;
using AIFramework.Kernel.Turn;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="TurnWorld"/> 的回合推演契约：步数上限、全灭判胜、结算单次性、动作队列溢出。
    /// 其中"步数耗尽但敌人未全灭"在当前实现里不可达：Phase 1 的伤害速率是 1 点/步、每个敌人 2 点血，
    /// 故 2N 步必然全灭，而 <c>MaxSteps = 3N</c> 的超时分支是留给未来"敌人带护甲/会闪避"的安全网，
    /// 不作为缺陷修复（改伤害速率或血量会让它重新可达，届时下面的用例会先变红提醒同步口径）。
    /// </summary>
    public sealed class TurnWorldTests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(-3, 1)]
        [InlineData(1, 3)]
        [InlineData(2, 6)]
        [InlineData(4, 12)]
        public void MaxSteps_IsThreeTimesUnitCountWithFloorOfOne(int unitCount, int expectedMaxSteps)
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 1, unitCount));

            Assert.Equal(expectedMaxSteps, world.MaxSteps);
        }

        [Fact]
        public void Step_UntilEveryEnemyDies_ReportsVictoryWithLoot()
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 4242, 3));
            Assert.Equal(3, world.AliveCount);

            int guard = 0;
            while (!world.IsFinished && guard <= world.MaxSteps)
            {
                world.Step(1f);
                guard++;
            }

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.AliveCount);

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(0, result.RemainingUnits);
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.Equal(6, result.StepCount);
            Assert.True(result.StepCount <= world.MaxSteps);
            Assert.Single(result.Loots);
            Assert.Equal(1001, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Empty(result.TriggeredTags);
        }

        [Fact]
        public void StepExhaustion_WithoutFullAnnihilation_IsUnreachable()
        {
            // 真实实现里每步只要还有活敌就必然结算 1 点伤害，总血量 2N 一定先于 3N 步耗尽，
            // 因此"步数耗尽但未全灭（IsVictory=false）"这条分支走不到。这里把它钉成事实。
            for (int unitCount = 1; unitCount <= 4; unitCount++)
            {
                TurnWorld world = new TurnWorld(new GameplayInitData(1, 7, unitCount));

                int guard = 0;
                while (!world.IsFinished && guard <= world.MaxSteps + 1)
                {
                    world.Step(1f);
                    guard++;
                }

                Assert.True(world.IsFinished);
                Assert.Equal(0, world.AliveCount);
                Assert.Equal(unitCount * 2, world.ProcessedSteps);
                Assert.True(world.ProcessedSteps < world.MaxSteps);

                GameplayResult result = world.CollectResult();
                Assert.True(result.IsVictory);
                Assert.Equal(0, result.RemainingUnits);
            }
        }

        [Fact]
        public void Step_WithQueuedActions_HonorsRequestedTargetIndex()
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 99, 3));

            world.Enqueue(new TurnAction(1));
            world.Enqueue(new TurnAction(1));
            world.Enqueue(new TurnAction(1));

            world.Step(1f);
            world.Step(1f);
            world.Step(1f);

            // 前两刀打 1 号并击杀，第三刀的目标已阵亡 → 回退打首个存活。
            // 若忽略队列目标（永远打首个存活），三刀会全落在 0 号身上，存活数仍是 3。
            Assert.Equal(2, world.AliveCount);
            Assert.Equal(3, world.ProcessedSteps);
            Assert.Equal(0, world.DroppedActionCount);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 1, 4));

            Assert.False(world.IsFinished);
            Assert.Throws<System.InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 1, 1));

            while (!world.IsFinished)
            {
                world.Step(1f);
            }

            world.CollectResult();

            Assert.Throws<System.InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void Enqueue_WhenQueueIsFull_IncrementsDroppedCount()
        {
            // MaxSteps=3 的会话：只入队不 Step，队列不会自然消解。
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 1, 1));

            for (int i = 0; i < 64; i++)
            {
                world.Enqueue(new TurnAction(0));
                Assert.Equal(0, world.DroppedActionCount);
            }

            world.Enqueue(new TurnAction(0));
            Assert.Equal(1, world.DroppedActionCount);

            world.Enqueue(new TurnAction(0));
            world.Enqueue(new TurnAction(0));
            Assert.Equal(3, world.DroppedActionCount);

            // 丢弃动作不改变队列里已有的 64 条：逐步推演时仍然每次只结算 1 步。
            world.Step(1f);
            Assert.Equal(1, world.ProcessedSteps);
        }

        [Fact]
        public void ZeroUnits_FinishesImmediatelyWithNoSurvivors()
        {
            TurnWorld world = new TurnWorld(new GameplayInitData(1, 1, 0));

            Assert.Equal(1, world.MaxSteps);
            Assert.True(world.IsFinished);
            Assert.Equal(0, world.AliveCount);

            GameplayResult result = world.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Equal(0, result.StepCount);
        }
    }
}

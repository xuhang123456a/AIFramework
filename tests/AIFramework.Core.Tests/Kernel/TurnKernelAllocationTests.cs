using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Kernel.Turn.CardBattle;
using AIFramework.Kernel.Turn.Match3;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// 回合内核的分配基线：预热后连续推演不应产生托管堆分配。
    /// <para>
    /// <b>为什么回合制也要管分配</b>：回合内核虽然比实时内核宽松，但一局里 <c>Step</c> 会被调用上千次，
    /// 一旦每次分配几十字节，手机端就会周期性 GC 抖动。把这条口径钉死，后续加玩法时不会悄悄引入分配。
    /// </para>
    /// <para>
    /// 断言口径与实时内核一致：预热 200 步（让 JIT/内联/首次分配都过去），再连续 1000 步，
    /// <c>GC.GetAllocatedBytesForCurrentThread()</c> 的增量必须为 0。
    /// </para>
    /// </summary>
    public sealed class TurnKernelAllocationTests
    {
        private const int WarmupSteps = 200;
        private const int MeasuredSteps = 1000;

        [Fact]
        public void CardBattle_Step_AfterWarmup_DoesNotAllocate()
        {
            var world = new CardBattleWorld(new GameplayInitData(1, 20261120, 2));

            for (int i = 0; i < WarmupSteps; i++)
            {
                world.Step(1f / 60f);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredSteps; i++)
            {
                world.Step(1f / 60f);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void CardBattle_TryPlayCard_AfterWarmup_DoesNotAllocate()
        {
            var world = new CardBattleWorld(new GameplayInitData(1, 20261120, 2));

            // 先把第一张能打出的牌打掉，之后每次尝试（含失败分支）都不该分配。
            for (int i = 0; i < world.HandCount; i++)
            {
                if (world.TryPlayCard(i))
                {
                    break;
                }
            }

            for (int i = 0; i < WarmupSteps; i++)
            {
                world.TryPlayCard(0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredSteps; i++)
            {
                world.TryPlayCard(0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void Match3_Step_AfterWarmup_DoesNotAllocate()
        {
            var world = new Match3World(new GameplayInitData(1, 20261120, 0));

            for (int i = 0; i < WarmupSteps; i++)
            {
                world.Step(1f / 60f);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredSteps; i++)
            {
                world.Step(1f / 60f);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void Match3_TrySwap_AfterWarmup_DoesNotAllocate()
        {
            var world = new Match3World(new GameplayInitData(1, 20261120, 0));

            // 预热：把可消的交换先消耗掉，之后连续调用（多为被拒分支）不应分配。
            for (int i = 0; i < 32; i++)
            {
                world.TrySwap(0, 0, 1, 0);
                world.Step(1f / 60f);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredSteps; i++)
            {
                world.TrySwap(0, 0, 1, 0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }
    }
}

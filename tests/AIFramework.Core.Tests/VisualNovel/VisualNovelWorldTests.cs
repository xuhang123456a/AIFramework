using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.VisualNovel;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="VisualNovelWorld"/>（T4-9 视觉小说/文字冒险内核）的契约用例。
    /// <para>
    /// <b>覆盖口径</b>：构造期结构校验（空剧本/重复 id/起点缺失/Choice 缺选项与坏目标/坏 NextNodeId/
    /// 发奖字段不自洽/负条件位）、Line 与 Jump 推进、End 结束与输入幂等、选项越界与条件过滤
    /// （flag 满足/不满足/混排顺序/全隐藏死局）、发奖去重与多笔战利品顺序、<c>CollectResult</c> 的
    /// victory / stepCount / remainingUnits / loots 口径、<c>NodesVisited</c> 计数、
    /// <c>Step</c> 计时、双结局分支、同输入序列确定性、<c>CaptureState</c> / <c>TryRestoreState</c>
    /// 往返与坏状态拒绝，以及"剧本防御性拷贝"。
    /// </para>
    /// <para>
    /// <b>为什么所有期望值都写死</b>：本内核零随机、零时间源，"同一剧本 + 同一输入序列 ⇒ 同一读视图"
    /// 是契约本身。用例直接写死节点 id、访问计数与战利品清单，既不复刻内核内部结构，
    /// 也不靠"拿内核答案验内核"；剧本改动导致这些数字变化时，用例会先炸出来。
    /// </para>
    /// <para>
    /// <b>存档语义边界</b>：快照只承载进度四元组（当前节点 / flags / 访问数 / 结局 id），
    /// 不迁移待发奖台账与发奖去重台账——<see cref="TryRestoreState_FinishedSnapshot_RestoresFinishedStateWithoutReplay"/>
    /// 把这条边界显式钉成用例，避免后续实现"顺手补发"破坏读档幂等。
    /// </para>
    /// </summary>
    public sealed class VisualNovelWorldTests
    {
        /// <summary>测试用关卡 id（只做战绩口径，不参与规则）。</summary>
        private const int TestLevelId = 9001;

        /// <summary>森林分支的发奖物品 id（映射到 flag 位 <c>1 &lt;&lt; 7</c>）。</summary>
        private const int GrantForestItemId = 7;

        // ── 构造期校验 ───────────────────────────────────────────────

        [Fact]
        public void Constructor_NullScript_Throws()
        {
            Assert.Throws<ArgumentException>(() => new VisualNovelWorld(Init(), null!));
        }

        [Fact]
        public void Constructor_EmptyScript_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), Array.Empty<VisualNovelNode>()));
        }

        [Fact]
        public void Constructor_DuplicateNodeId_Throws()
        {
            var script = new[] { Line(7, -1), Line(7, -1) };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("节点 id 重复", error.Message);
            Assert.Contains("7", error.Message);
        }

        [Fact]
        public void Constructor_MissingStartNode_Throws()
        {
            var script = new[] { End(0) };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script, 9));

            Assert.Contains("起始节点 9", error.Message);
        }

        [Fact]
        public void Constructor_ChoiceWithoutChoices_Throws()
        {
            var script = new[]
            {
                Line(0, 1),
                new VisualNovelNode(1, VisualNovelNodeKind.Choice, "主角", "怎么选？", -1, null, 0, 0)
            };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("Choice 节点 1", error.Message);
        }

        [Fact]
        public void Constructor_ChoiceWithEmptyChoices_Throws()
        {
            var script = new[]
            {
                Line(0, 1),
                new VisualNovelNode(
                    1,
                    VisualNovelNodeKind.Choice,
                    "主角",
                    "怎么选？",
                    -1,
                    Array.Empty<VisualNovelChoice>(),
                    0,
                    0)
            };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("Choice 节点 1", error.Message);
        }

        [Fact]
        public void Constructor_ChoiceTargetMissing_Throws()
        {
            var script = new[]
            {
                Line(0, 1),
                Choice(1, Option("去一个不存在的地方", 99)),
                End(2)
            };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("节点 1", error.Message);
            Assert.Contains("目标节点 99", error.Message);
        }

        [Fact]
        public void Constructor_LineNextMissing_Throws()
        {
            var script = new[] { Line(0, 99) };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("NextNodeId=99", error.Message);
        }

        [Fact]
        public void Constructor_JumpNextMissing_Throws()
        {
            var script = new[] { Line(0, 1), Jump(1, 99) };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("节点 1", error.Message);
            Assert.Contains("NextNodeId=99", error.Message);
        }

        [Fact]
        public void Constructor_NegativeNodeId_Throws()
        {
            var script = new[] { Line(-3, -1) };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("必须为非负", error.Message);
            Assert.Contains("-3", error.Message);
        }

        [Fact]
        public void Constructor_GrantWithoutPositiveCount_Throws()
        {
            var script = new[]
            {
                new VisualNovelNode(0, VisualNovelNodeKind.Line, "旁白", "获得物品", -1, null, 5, 0)
            };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("声明了发奖", error.Message);
            Assert.Contains("GrantCount", error.Message);
        }

        [Fact]
        public void Constructor_ChoiceNegativeRequiredItem_Throws()
        {
            var script = new[]
            {
                Line(0, 1),
                Choice(1, Option("坏条件", 2, -1)),
                End(2)
            };

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new VisualNovelWorld(Init(), script));

            Assert.Contains("条件标记不能为负", error.Message);
        }

        [Fact]
        public void Constructor_DoesNotAliasCallerScript()
        {
            VisualNovelChoice[] choices = { Option("去森林", 1) };
            var script = new[]
            {
                Choice(0, choices),
                End(1)
            };

            VisualNovelWorld world = CreateWorld(script);
            Assert.Equal(1, world.ChoiceCount);

            // 构造后篡改调用方数组：内核已做防御性拷贝，已开始的会话不受影响。
            choices[0] = Option("被篡改的选项", 0);
            script[0] = End(0);

            Assert.Equal(1, world.ChoiceCount);
            Assert.Equal("去森林", world.GetChoice(0).Text);
            Assert.True(world.TryChoose(0));
            Assert.Equal(1, world.CurrentNodeId);
            Assert.True(world.IsFinished);
            Assert.Equal(1, world.EndingId);
        }

        // ── 初始读视图 ───────────────────────────────────────────────

        [Fact]
        public void Constructor_EntersStartNodeAndExposesReadView()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.Equal(TestLevelId, world.LevelId);
            Assert.Equal(6, world.NodeCount);
            Assert.Equal(0, world.CurrentNodeId);
            Assert.Equal(VisualNovelNodeKind.Line, world.CurrentKind);
            Assert.Equal("旁白", world.Speaker);
            Assert.Equal("故事开始", world.Text);
            Assert.Equal(0, world.ChoiceCount);
            Assert.False(world.IsFinished);
            Assert.Equal(-1, world.EndingId);
            Assert.Equal(1, world.NodesVisited);
            Assert.Equal(0, world.Flags);
            Assert.Equal(0f, world.ElapsedSeconds);
        }

        // ── 推进与跳转 ───────────────────────────────────────────────

        [Fact]
        public void TryAdvance_LineMovesToNextNode()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.True(world.TryAdvance());

            Assert.Equal(1, world.CurrentNodeId);
            Assert.Equal(VisualNovelNodeKind.Choice, world.CurrentKind);
            Assert.Equal(2, world.NodesVisited);
            Assert.Equal(2, world.ChoiceCount);
        }

        [Fact]
        public void TryAdvance_LineWithoutNext_FinishesWithoutExplicitEnding()
        {
            VisualNovelWorld world = CreateWorld(new[] { Line(0, -1) });

            Assert.False(world.IsFinished);

            Assert.True(world.TryAdvance());

            Assert.True(world.IsFinished);
            Assert.Equal(-1, world.EndingId);
            Assert.Equal(1, world.NodesVisited);
            Assert.False(world.TryAdvance());
        }

        [Fact]
        public void TryAdvance_JumpFollowsNextNode()
        {
            var script = new[] { Line(0, 1), Jump(1, 2), End(2) };
            VisualNovelWorld world = CreateWorld(script);

            Assert.True(world.TryAdvance());
            Assert.Equal(1, world.CurrentNodeId);
            Assert.Equal(VisualNovelNodeKind.Jump, world.CurrentKind);
            Assert.Equal(2, world.NodesVisited);

            Assert.True(world.TryAdvance());
            Assert.Equal(2, world.CurrentNodeId);
            Assert.Equal(VisualNovelNodeKind.End, world.CurrentKind);
            Assert.Equal(3, world.NodesVisited);
            Assert.True(world.IsFinished);
            Assert.Equal(2, world.EndingId);
        }

        [Fact]
        public void TryAdvance_JumpWithoutNext_FinishesWithoutExplicitEnding()
        {
            VisualNovelWorld world = CreateWorld(new[] { Jump(0, -1) });

            Assert.False(world.IsFinished);
            Assert.True(world.TryAdvance());
            Assert.True(world.IsFinished);
            Assert.Equal(-1, world.EndingId);
            Assert.Equal(1, world.NodesVisited);
        }

        [Fact]
        public void TryAdvance_OnChoiceNode_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());

            Assert.False(world.TryAdvance());

            Assert.Equal(1, world.CurrentNodeId);
            Assert.Equal(2, world.NodesVisited);
            Assert.Equal(2, world.ChoiceCount);
        }

        [Fact]
        public void TryAdvance_OnEndNode_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(new[] { End(0) });

            Assert.False(world.TryAdvance());
            Assert.Equal(0, world.CurrentNodeId);
        }

        [Fact]
        public void TryAdvance_AfterFinish_ReturnsFalse()
        {
            VisualNovelWorld world = ReachForestEnding();

            Assert.True(world.IsFinished);
            Assert.False(world.TryAdvance());
            Assert.False(world.TryAdvance());
            Assert.Equal(11, world.CurrentNodeId);
            Assert.Equal(4, world.NodesVisited);
        }

        // ── 选项 ────────────────────────────────────────────────────

        [Fact]
        public void TryChoose_OnLineNode_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.False(world.TryChoose(0));
            Assert.Equal(0, world.CurrentNodeId);
            Assert.Equal(1, world.NodesVisited);
        }

        [Fact]
        public void TryChoose_OutOfRange_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());

            Assert.False(world.TryChoose(-1));
            Assert.False(world.TryChoose(2));
            Assert.False(world.TryChoose(100));

            Assert.Equal(1, world.CurrentNodeId);
            Assert.Equal(2, world.NodesVisited);
        }

        [Fact]
        public void TryChoose_ValidChoiceMovesToTarget()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());

            Assert.True(world.TryChoose(1));

            Assert.Equal(20, world.CurrentNodeId);
            Assert.Equal(VisualNovelNodeKind.Jump, world.CurrentKind);
            Assert.Equal(3, world.NodesVisited);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void TryChoose_OnEndNode_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(new[] { End(0) });

            Assert.False(world.TryChoose(0));
            Assert.Equal(0, world.CurrentNodeId);
        }

        [Fact]
        public void TryChoose_AfterFinish_ReturnsFalse()
        {
            VisualNovelWorld world = ReachForestEnding();

            Assert.False(world.TryChoose(0));
            Assert.Equal(11, world.CurrentNodeId);
        }

        // ── 结束节点 ─────────────────────────────────────────────────

        [Fact]
        public void EndNode_EnterFinishesAndSetsEndingId()
        {
            var script = new[] { Line(0, 1, "旁白", "最后一句"), End(1, "THE END") };
            VisualNovelWorld world = CreateWorld(script);

            Assert.True(world.TryAdvance());

            Assert.True(world.IsFinished);
            Assert.Equal(1, world.EndingId);
            Assert.Equal(VisualNovelNodeKind.End, world.CurrentKind);
            Assert.Equal("THE END", world.Text);
            Assert.Equal(2, world.NodesVisited);
            Assert.False(world.TryAdvance());
            Assert.False(world.TryChoose(0));
        }

        [Fact]
        public void StartNodeIsEnd_FinishesFromConstruction()
        {
            VisualNovelWorld world = CreateWorld(new[] { End(0) });

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.EndingId);
            Assert.Equal(1, world.NodesVisited);
            Assert.Equal(0, world.ChoiceCount);
            Assert.False(world.TryAdvance());
            Assert.False(world.TryChoose(0));

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(1, result.StepCount);
            Assert.Empty(result.Loots);
        }

        // ── 条件选项（演示用 flag 位条件） ─────────────────────────────

        [Fact]
        public void ConditionalChoice_HiddenWhenFlagMissing()
        {
            var script = new[]
            {
                Line(0, 1),
                Choice(1, Option("需要钥匙", 2, 3), Option("直接走", 3)),
                End(2),
                End(3)
            };
            VisualNovelWorld world = CreateWorld(script);

            Assert.Equal(0, world.Flags);
            Assert.True(world.TryAdvance());

            // 条件不满足的选项不出现、不占位：可用序列只有"直接走"。
            Assert.Equal(1, world.ChoiceCount);
            Assert.Equal("直接走", world.GetChoice(0).Text);
            Assert.False(world.TryChoose(1));

            Assert.True(world.TryChoose(0));
            Assert.Equal(3, world.CurrentNodeId);
            Assert.True(world.IsFinished);
            Assert.Equal(3, world.EndingId);
        }

        [Fact]
        public void ConditionalChoice_VisibleWhenGrantSetsFlag()
        {
            var script = new[]
            {
                LineWithGrant(0, 1, 3, 1),
                Choice(1, Option("需要钥匙", 2, 3), Option("直接走", 3)),
                End(2),
                End(3)
            };
            VisualNovelWorld world = CreateWorld(script);

            // 进入发奖节点即置位 1 << (3 & 31)。
            Assert.Equal(1 << 3, world.Flags);
            Assert.True(world.TryAdvance());

            Assert.Equal(2, world.ChoiceCount);
            Assert.Equal("需要钥匙", world.GetChoice(0).Text);
            Assert.Equal("直接走", world.GetChoice(1).Text);

            Assert.True(world.TryChoose(0));
            Assert.True(world.IsFinished);
            Assert.Equal(2, world.EndingId);
        }

        [Fact]
        public void ConditionalChoice_AvailableOrderSkipsHiddenMiddleChoice()
        {
            var script = new[]
            {
                Line(0, 1),
                Choice(1, Option("无条件甲", 2), Option("条件乙", 3, 3), Option("无条件丙", 4)),
                End(2),
                End(3),
                End(4)
            };
            VisualNovelWorld world = CreateWorld(script);
            Assert.True(world.TryAdvance());

            Assert.Equal(2, world.ChoiceCount);
            Assert.Equal("无条件甲", world.GetChoice(0).Text);
            Assert.Equal("无条件丙", world.GetChoice(1).Text);

            Assert.True(world.TryChoose(1));
            Assert.Equal(4, world.CurrentNodeId);
            Assert.Equal(4, world.EndingId);
        }

        [Fact]
        public void ConditionalChoice_AllHidden_ChoiceCountZero()
        {
            var script = new[]
            {
                Choice(0, Option("需要钥匙", 1, 3)),
                End(1)
            };
            VisualNovelWorld world = CreateWorld(script);

            Assert.Equal(VisualNovelNodeKind.Choice, world.CurrentKind);
            Assert.Equal(0, world.ChoiceCount);
            Assert.False(world.TryChoose(0));
            Assert.False(world.TryAdvance());
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void GetChoice_ThrowsOnNonChoiceNode()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetChoice(0));
        }

        [Fact]
        public void GetChoice_ThrowsForOutOfRangeIndex()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetChoice(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetChoice(2));
        }

        // ── 发奖 ────────────────────────────────────────────────────

        [Fact]
        public void GrantNode_SetsFlagAndQueuesLootOnce()
        {
            var script = new[]
            {
                LineWithGrant(0, 1, 3, 1),
                Choice(1, Option("再拿一次", 0), Option("结束", 2)),
                End(2)
            };
            VisualNovelWorld world = CreateWorld(script);

            Assert.Equal(1 << 3, world.Flags);
            Assert.Equal(1, world.NodesVisited);

            // 绕回发奖节点：只加访问计数，不重复发奖、不改变 flags。
            Assert.True(world.TryAdvance());
            Assert.True(world.TryChoose(0));
            Assert.Equal(0, world.CurrentNodeId);
            Assert.Equal(3, world.NodesVisited);
            Assert.Equal(1 << 3, world.Flags);

            Assert.True(world.TryAdvance());
            Assert.True(world.TryChoose(1));
            Assert.True(world.IsFinished);
            Assert.Equal(2, world.EndingId);
            Assert.Equal(5, world.NodesVisited);

            GameplayResult result = world.CollectResult();

            Assert.Equal(5, result.StepCount);
            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(3, loot.ItemId);
            Assert.Equal(1, loot.Count);
        }

        [Fact]
        public void GrantNodes_QueueLootInEntryOrder()
        {
            var script = new[]
            {
                LineWithGrant(0, 1, 100, 1),
                LineWithGrant(1, 2, 200, 3),
                End(2)
            };
            VisualNovelWorld world = CreateWorld(script);

            // 进入 0 号节点只置位 100 & 31 = 4；200 & 31 = 8 要到进入 1 号节点才置位。
            Assert.Equal(1 << 4, world.Flags);

            Assert.True(world.TryAdvance());
            Assert.Equal((1 << 4) | (1 << 8), world.Flags);
            Assert.True(world.TryAdvance());
            Assert.True(world.IsFinished);
            Assert.Equal(3, world.NodesVisited);

            GameplayResult result = world.CollectResult();

            Assert.Equal(2, result.Loots.Count);
            Assert.Equal(100, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Equal(200, result.Loots[1].ItemId);
            Assert.Equal(3, result.Loots[1].Count);
        }

        // ── 结算报告 ─────────────────────────────────────────────────

        [Fact]
        public void CollectResult_ThrowsBeforeFinish()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_ThrowsOnSecondCall()
        {
            VisualNovelWorld world = CreateWorld(new[] { End(0) });

            GameplayResult first = world.CollectResult();
            Assert.True(first.IsVictory);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            // 结算取回后会话仍表现为"已结束"，任何输入都是拒绝。
            Assert.True(world.IsFinished);
            Assert.False(world.TryAdvance());
            Assert.False(world.TryChoose(0));
        }

        [Fact]
        public void CollectResult_ReportsVictoryWithCountsAndLoot()
        {
            VisualNovelWorld world = ReachForestEnding();

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(4, result.StepCount);
            Assert.Equal(4, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);

            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(GrantForestItemId, loot.ItemId);
            Assert.Equal(2, loot.Count);
        }

        [Fact]
        public void CollectResult_ImplicitEnd_ReportsDefeatButPaysLoot()
        {
            VisualNovelWorld world = CreateWorld(new[] { LineWithGrant(0, -1, 5, 1) });

            Assert.False(world.IsFinished);
            Assert.True(world.TryAdvance());
            Assert.True(world.IsFinished);
            Assert.Equal(-1, world.EndingId);

            GameplayResult result = world.CollectResult();

            // 悬空 Line 不是显式结局：victory 取 EndingId >= 0，故为 false；已入袋的战利品照常交还。
            Assert.False(result.IsVictory);
            Assert.Equal(1, result.StepCount);
            Assert.Equal(1, result.RemainingUnits);
            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(5, loot.ItemId);
            Assert.Equal(1, loot.Count);
        }

        // ── 多结局与确定性 ───────────────────────────────────────────

        [Fact]
        public void MultiEndingScript_BranchesReachDifferentEndings()
        {
            var script = CreateBranchingScript();

            VisualNovelWorld forest = ReachForestEnding();
            VisualNovelWorld city = CreateWorld(script);
            Assert.True(city.TryAdvance());
            Assert.True(city.TryChoose(1));
            Assert.True(city.TryAdvance()); // 20 Jump → 21 End

            Assert.True(forest.IsFinished);
            Assert.True(city.IsFinished);
            Assert.Equal(11, forest.EndingId);
            Assert.Equal(21, city.EndingId);
            Assert.NotEqual(forest.EndingId, city.EndingId);
            Assert.Equal(4, city.NodesVisited);

            Assert.True(forest.CollectResult().IsVictory);
            Assert.True(city.CollectResult().IsVictory);
        }

        [Fact]
        public void SameChoiceSequence_IsDeterministic()
        {
            var script = CreateBranchingScript();
            VisualNovelWorld first = CreateWorld(script);
            VisualNovelWorld second = CreateWorld(script);

            Assert.True(first.TryAdvance());
            Assert.True(second.TryAdvance());
            Assert.Equal(first.CurrentNodeId, second.CurrentNodeId);
            Assert.Equal(first.NodesVisited, second.NodesVisited);
            Assert.Equal(first.ChoiceCount, second.ChoiceCount);
            for (int i = 0; i < first.ChoiceCount; i++)
            {
                Assert.Equal(first.GetChoice(i).Text, second.GetChoice(i).Text);
                Assert.Equal(first.GetChoice(i).TargetNodeId, second.GetChoice(i).TargetNodeId);
                Assert.Equal(first.GetChoice(i).RequiredItemId, second.GetChoice(i).RequiredItemId);
            }

            Assert.True(first.TryChoose(0));
            Assert.True(second.TryChoose(0));
            Assert.Equal(first.CurrentNodeId, second.CurrentNodeId);
            Assert.Equal(first.Flags, second.Flags);
            Assert.Equal(first.NodesVisited, second.NodesVisited);

            Assert.True(first.TryAdvance());
            Assert.True(second.TryAdvance());
            Assert.Equal(first.IsFinished, second.IsFinished);
            Assert.Equal(first.EndingId, second.EndingId);
            Assert.Equal(first.NodesVisited, second.NodesVisited);

            GameplayResult firstResult = first.CollectResult();
            GameplayResult secondResult = second.CollectResult();

            Assert.Equal(firstResult.KernelType, secondResult.KernelType);
            Assert.Equal(firstResult.IsVictory, secondResult.IsVictory);
            Assert.Equal(firstResult.StepCount, secondResult.StepCount);
            Assert.Equal(firstResult.RemainingUnits, secondResult.RemainingUnits);
            Assert.Equal(firstResult.Loots.Count, secondResult.Loots.Count);
            for (int i = 0; i < firstResult.Loots.Count; i++)
            {
                Assert.Equal(firstResult.Loots[i].ItemId, secondResult.Loots[i].ItemId);
                Assert.Equal(firstResult.Loots[i].Count, secondResult.Loots[i].Count);
            }
        }

        // ── Step 计时 ────────────────────────────────────────────────

        [Fact]
        public void Step_AccumulatesElapsedSecondsOnly()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            world.Step(0.5f);
            world.Step(0.25f);
            Assert.Equal(0.75f, world.ElapsedSeconds);

            // Step 只喂时间轴，不推进剧情。
            Assert.Equal(0, world.CurrentNodeId);
            Assert.True(world.TryAdvance());
            Assert.Equal(1, world.CurrentNodeId);
            Assert.Equal(0.75f, world.ElapsedSeconds);
        }

        [Fact]
        public void Step_AfterFinish_DoesNotAccumulate()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            world.Step(0.5f);

            Assert.True(world.TryAdvance());
            Assert.True(world.TryChoose(0));
            Assert.True(world.TryAdvance());
            Assert.True(world.IsFinished);

            world.Step(1f);
            Assert.Equal(0.5f, world.ElapsedSeconds);
        }

        // ── 存档快照 ─────────────────────────────────────────────────

        [Fact]
        public void CaptureState_ExportsProgressQuadruple()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());

            VisualNovelSaveState atChoice = world.CaptureState();
            Assert.Equal(1, atChoice.CurrentNodeId);
            Assert.Equal(0, atChoice.Flags);
            Assert.Equal(2, atChoice.NodesVisited);
            Assert.Equal(-1, atChoice.EndingId);

            Assert.True(world.TryChoose(0));
            VisualNovelSaveState afterGrant = world.CaptureState();
            Assert.Equal(10, afterGrant.CurrentNodeId);
            Assert.Equal(1 << GrantForestItemId, afterGrant.Flags);
            Assert.Equal(3, afterGrant.NodesVisited);
            Assert.Equal(-1, afterGrant.EndingId);
        }

        [Fact]
        public void TryRestoreState_RoundTripsIntoFreshSession()
        {
            var script = CreateBranchingScript();
            VisualNovelWorld original = CreateWorld(script);
            Assert.True(original.TryAdvance());
            VisualNovelSaveState snapshot = original.CaptureState();

            VisualNovelWorld loaded = CreateWorld(script);
            Assert.True(loaded.TryRestoreState(snapshot));

            Assert.Equal(original.CurrentNodeId, loaded.CurrentNodeId);
            Assert.Equal(original.CurrentKind, loaded.CurrentKind);
            Assert.Equal(original.Speaker, loaded.Speaker);
            Assert.Equal(original.Text, loaded.Text);
            Assert.Equal(original.ChoiceCount, loaded.ChoiceCount);
            Assert.Equal(original.Flags, loaded.Flags);
            Assert.Equal(original.NodesVisited, loaded.NodesVisited);
            Assert.Equal(original.EndingId, loaded.EndingId);
            Assert.Equal(original.IsFinished, loaded.IsFinished);

            // 读档后继续走：选项仍然有效，且读档点之后的发奖节点照常发放。
            Assert.True(loaded.TryChoose(0));
            Assert.Equal(10, loaded.CurrentNodeId);
            Assert.True(loaded.TryAdvance());
            Assert.True(loaded.IsFinished);
            Assert.Equal(11, loaded.EndingId);

            GameplayResult result = loaded.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Equal(4, result.StepCount);
            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(GrantForestItemId, loot.ItemId);
        }

        [Fact]
        public void TryRestoreState_UnknownNode_ReturnsFalseAndKeepsState()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.False(world.TryRestoreState(new VisualNovelSaveState(999, 0, 1, -1)));

            Assert.Equal(0, world.CurrentNodeId);
            Assert.Equal(1, world.NodesVisited);
            Assert.Equal(-1, world.EndingId);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void TryRestoreState_NegativeNodesVisited_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            Assert.False(world.TryRestoreState(new VisualNovelSaveState(0, 0, -1, -1)));
            Assert.Equal(1, world.NodesVisited);
        }

        [Fact]
        public void TryRestoreState_InconsistentEndingId_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());

            // 结局 id 等于当前节点 id，但当前节点不是 End：坏档。
            Assert.False(world.TryRestoreState(new VisualNovelSaveState(0, 0, 1, 0)));

            // 结局 id 与当前节点 id 不一致：坏档。
            Assert.False(world.TryRestoreState(new VisualNovelSaveState(0, 0, 1, 11)));

            Assert.Equal(0, world.CurrentNodeId);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void TryRestoreState_AfterCollectResult_ReturnsFalse()
        {
            VisualNovelWorld world = CreateWorld(new[] { End(0) });
            world.CollectResult();

            Assert.False(world.TryRestoreState(new VisualNovelSaveState(0, 0, 1, 0)));
        }

        [Fact]
        public void TryRestoreState_FinishedSnapshot_RestoresFinishedStateWithoutReplay()
        {
            var script = CreateBranchingScript();
            VisualNovelWorld finished = ReachForestEnding();
            VisualNovelSaveState snapshot = finished.CaptureState();
            Assert.Equal(11, snapshot.EndingId);
            Assert.Equal(4, snapshot.NodesVisited);

            VisualNovelWorld loaded = CreateWorld(script);
            Assert.True(loaded.TryRestoreState(snapshot));

            Assert.True(loaded.IsFinished);
            Assert.Equal(11, loaded.EndingId);
            Assert.Equal(11, loaded.CurrentNodeId);
            Assert.False(loaded.TryAdvance());
            Assert.False(loaded.TryChoose(0));

            GameplayResult result = loaded.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Equal(snapshot.NodesVisited, result.StepCount);

            // 快照不迁移待发奖台账：独立会话读档后不会重放读档点之前的发奖。
            Assert.Empty(result.Loots);
        }

        // ── 夹具 ────────────────────────────────────────────────────

        /// <summary>
        /// 标准三幕剧本：0 Line → 1 Choice；走"森林"分支 10 发奖 → 11 结局；
        /// 走"城市"分支 20 Jump → 21 结局。节点数 6，两个结局分别 11 / 21。
        /// </summary>
        private static VisualNovelNode[] CreateBranchingScript()
        {
            return new VisualNovelNode[]
            {
                Line(0, 1, "旁白", "故事开始"),
                Choice(1, Option("去森林", 10), Option("回城", 20)),
                LineWithGrant(10, 11, GrantForestItemId, 2),
                End(11, "森林结局"),
                Jump(20, 21),
                End(21, "城市结局")
            };
        }

        /// <summary>把标准剧本走到森林结局（0 → 1 → 10 → 11）。</summary>
        private static VisualNovelWorld ReachForestEnding()
        {
            VisualNovelWorld world = CreateWorld(CreateBranchingScript());
            Assert.True(world.TryAdvance());
            Assert.True(world.TryChoose(0));
            Assert.True(world.TryAdvance());
            return world;
        }

        private static GameplayInitData Init(int levelId = TestLevelId)
        {
            return new GameplayInitData(levelId, 20261120, 0);
        }

        private static VisualNovelWorld CreateWorld(VisualNovelNode[] script, int startNodeId = 0)
        {
            return new VisualNovelWorld(Init(), script, startNodeId);
        }

        private static VisualNovelNode Line(int nodeId, int nextNodeId, string speaker = "旁白", string text = "正文")
        {
            return new VisualNovelNode(
                nodeId, VisualNovelNodeKind.Line, speaker, text, nextNodeId, null, 0, 0);
        }

        private static VisualNovelNode LineWithGrant(int nodeId, int nextNodeId, int grantItemId, int grantCount)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.Line,
                "旁白",
                "获得物品",
                nextNodeId,
                null,
                grantItemId,
                grantCount);
        }

        private static VisualNovelNode Jump(int nodeId, int nextNodeId)
        {
            return new VisualNovelNode(
                nodeId, VisualNovelNodeKind.Jump, string.Empty, string.Empty, nextNodeId, null, 0, 0);
        }

        private static VisualNovelNode End(int nodeId, string text = "结局")
        {
            return new VisualNovelNode(
                nodeId, VisualNovelNodeKind.End, "旁白", text, -1, null, 0, 0);
        }

        private static VisualNovelNode Choice(int nodeId, params VisualNovelChoice[] choices)
        {
            return new VisualNovelNode(
                nodeId, VisualNovelNodeKind.Choice, "主角", "怎么选？", -1, choices, 0, 0);
        }

        private static VisualNovelChoice Option(string text, int targetNodeId, int requiredItemId = 0)
        {
            return new VisualNovelChoice(text, targetNodeId, requiredItemId);
        }
    }
}

#nullable enable
using System;
using AIFramework.Contracts.Ai;
using AIFramework.Core.Ai;
using Xunit;

namespace AIFramework.Core.Tests.Ai
{
    /// <summary>
    /// 行为树解释器的行为契约验收：Selector/Sequence 短路与空子节点语义、Condition 七个条件码、
    /// Action 四个动作码与未知码、嵌套/优先级、构造期校验（节点上限/深度/环/未知引用/未知类型）、
    /// 诊断计数、Reset、重复求值稳定性、零分配基线，以及默认竞技场敌人树的分支顺序。
    /// <para>
    /// <b>为什么这些用例值得单独写</b>：行为树的错误通常不是"崩"，而是"某个分支永远不生效"
    /// 或"低血不撤退"这类静默逻辑错；真机上只能靠观感发现。这里把每个条件码/动作码和短路顺序钉死，
    /// 适配层就只剩"填 Blackboard + 执行动作"这一件必须真机验证的事。
    /// </para>
    /// <para>
    /// <b>测试口径</b>：所有树都是纯内存构造；"条件成功"用"条件节点 + 攻击动作"包成 Selector 观察，
    /// 因为解释器对纯条件树成功只返回 <see cref="BehaviorAction.None"/>（没有动作可执行）。
    /// </para>
    /// </summary>
    public sealed class BehaviorTreeTests
    {
        // ────────────────────────────────────────────────────────────────
        // Selector
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Selector_FirstChildSucceeds_ReturnsFirstAction()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 2),
                ActionNode(1, BehaviorAction.Attack),
                ActionNode(2, BehaviorAction.Wait));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));
            Assert.Equal(2, tree.EvaluationsUsed);
        }

        [Fact]
        public void Selector_FirstChildFails_FallsThroughToSecond()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 2),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysFail),
                ActionNode(2, BehaviorAction.Wait));

            Assert.Equal(BehaviorAction.Wait, Eval(tree, Board()));
            Assert.Equal(3, tree.EvaluationsUsed);
        }

        [Fact]
        public void Selector_AllChildrenFail_ReturnsNone()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 2),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysFail),
                Condition(2, BehaviorTreeLibrary.ConditionAlwaysFail));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
            Assert.Equal(3, tree.EvaluationsUsed);
        }

        [Fact]
        public void Selector_EmptyChildren_ReturnsNone()
        {
            BehaviorTree tree = Compile(0, Selector(0));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
            Assert.Equal(1, tree.EvaluationsUsed);
        }

        [Fact]
        public void Selector_ShortCircuits_DoesNotEvaluateLaterSiblings()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 2, 3),
                ActionNode(1, BehaviorAction.Attack),
                Condition(2, BehaviorTreeLibrary.ConditionAlwaysSucceed),
                ActionNode(3, BehaviorAction.Wait));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));

            // 根 + 第一个动作；后两个兄弟一个都不该被访问。
            Assert.Equal(2, tree.EvaluationsUsed);
        }

        // ────────────────────────────────────────────────────────────────
        // Sequence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Sequence_AllChildrenSucceed_ReturnsLastAction()
        {
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1, 2),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysSucceed),
                ActionNode(2, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));
            Assert.Equal(3, tree.EvaluationsUsed);
        }

        [Fact]
        public void Sequence_MultipleActions_ReturnsLastExecutedAction()
        {
            // 纯决策树一帧只能输出一个动作；Sequence 里多个动作按"最后执行的那个"收敛。
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1, 2),
                ActionNode(1, BehaviorAction.MoveToward),
                ActionNode(2, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));
        }

        [Fact]
        public void Sequence_FirstChildFails_ReturnsNone()
        {
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1, 2),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysFail),
                ActionNode(2, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
        }

        [Fact]
        public void Sequence_ShortCircuitsAfterFailure()
        {
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1, 2, 3),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysFail),
                Condition(2, BehaviorTreeLibrary.ConditionAlwaysSucceed),
                ActionNode(3, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));

            // 根 + 第一个失败条件；后两个兄弟不应被访问。
            Assert.Equal(2, tree.EvaluationsUsed);
        }

        [Fact]
        public void Sequence_EmptyChildren_SucceedsWithoutAction()
        {
            BehaviorTree tree = Compile(0, Sequence(0));

            // 空 Sequence 是"空真"：整体成功，但没有动作可执行。
            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
            Assert.Equal(1, tree.EvaluationsUsed);
        }

        [Fact]
        public void Sequence_SingleChildFailure_ReturnsNone()
        {
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1),
                Condition(1, BehaviorTreeLibrary.ConditionAlwaysFail));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
        }

        // ────────────────────────────────────────────────────────────────
        // Condition 语义
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Condition_HpBelowRetreat_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionHpBelowRetreat,
                    Board(hpPercent: 29, retreatThreshold: 30)));
        }

        [Fact]
        public void Condition_HpAtThreshold_Fails()
        {
            // 语义是严格小于：30 < 30 不成立。
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionHpBelowRetreat,
                    Board(hpPercent: 30, retreatThreshold: 30)));
        }

        [Fact]
        public void Condition_HpZero_SucceedsWhenThresholdPositive()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionHpBelowRetreat,
                    Board(hpPercent: 0, retreatThreshold: 1)));
        }

        [Fact]
        public void Condition_HpHundred_IsNotClampedDown()
        {
            // 黑板不做钳制：100 < 101 仍应成立（边界值由调用方负责，解释器只做比较）。
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionHpBelowRetreat,
                    Board(hpPercent: 100, retreatThreshold: 101)));
        }

        [Fact]
        public void Condition_EnemyInRange_AtOne_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionEnemyInRange,
                    Board(enemyDistance: 1)));
        }

        [Fact]
        public void Condition_EnemyInRange_Outside_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionEnemyInRange,
                    Board(enemyDistance: 2)));
        }

        [Fact]
        public void Condition_EnemyInRange_NegativeDistance_Succeeds()
        {
            // 负距离是异常/近似哨兵值，按"已在射程内"处理，不能因为取整方向漏判。
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionEnemyInRange,
                    Board(enemyDistance: -5)));
        }

        [Fact]
        public void Condition_CooldownReady_True_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionCooldownReady,
                    Board(cooldownReady: true)));
        }

        [Fact]
        public void Condition_CooldownReady_False_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionCooldownReady,
                    Board(cooldownReady: false)));
        }

        [Fact]
        public void Condition_Outnumbered_TwoEnemies_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionOutnumbered,
                    Board(enemyCount: 2)));
        }

        [Fact]
        public void Condition_Outnumbered_OneEnemy_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionOutnumbered,
                    Board(enemyCount: 1)));
        }

        [Fact]
        public void Condition_LateWave_ThirdWave_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionLateWave,
                    Board(waveIndex: 3)));
        }

        [Fact]
        public void Condition_LateWave_SecondWave_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(
                    BehaviorTreeLibrary.ConditionLateWave,
                    Board(waveIndex: 2)));
        }

        [Fact]
        public void Condition_AlwaysSucceed_Succeeds()
        {
            Assert.Equal(
                BehaviorAction.Attack,
                EvaluateCondition(BehaviorTreeLibrary.ConditionAlwaysSucceed, Board()));
        }

        [Fact]
        public void Condition_AlwaysFail_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(BehaviorTreeLibrary.ConditionAlwaysFail, Board()));
        }

        [Fact]
        public void Condition_UnknownCode_Fails()
        {
            Assert.Equal(
                BehaviorAction.None,
                EvaluateCondition(condition: 99, Board()));
        }

        // ────────────────────────────────────────────────────────────────
        // Action 语义
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1, BehaviorAction.MoveToward)]
        [InlineData(2, BehaviorAction.MoveAway)]
        [InlineData(3, BehaviorAction.Attack)]
        [InlineData(4, BehaviorAction.Wait)]
        public void Action_KnownCodes_MapToBehaviorAction(int actionCode, BehaviorAction expected)
        {
            BehaviorTree tree = Compile(0, ActionNode(0, (BehaviorAction)actionCode));

            Assert.Equal(expected, Eval(tree, Board()));
        }

        [Fact]
        public void Action_ZeroCode_IsUnknownAndFails()
        {
            BehaviorTree tree = Compile(0, ActionNode(0, BehaviorAction.None));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
        }

        [Fact]
        public void Action_UnknownCode_Fails()
        {
            BehaviorTree tree = Compile(0, ActionNode(0, (BehaviorAction)99));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
        }

        [Fact]
        public void RootAction_IsReturnedDirectly()
        {
            BehaviorTree tree = Compile(0, ActionNode(0, BehaviorAction.MoveAway));

            Assert.Equal(BehaviorAction.MoveAway, Eval(tree, Board()));
            Assert.Equal(1, tree.EvaluationsUsed);
        }

        [Fact]
        public void RootConditionSuccess_ReturnsNoneBecauseThereIsNoAction()
        {
            BehaviorTree tree = Compile(0, Condition(0, BehaviorTreeLibrary.ConditionAlwaysSucceed));

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
            Assert.Equal(1, tree.EvaluationsUsed);
        }

        // ────────────────────────────────────────────────────────────────
        // 嵌套 / 优先级 / 计数
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NestedTree_SelectorFallsThroughSequenceToAction()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 4),
                Sequence(1, 2, 3),
                Condition(2, BehaviorTreeLibrary.ConditionAlwaysFail),
                ActionNode(3, BehaviorAction.Wait),
                ActionNode(4, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));

            // 根 + 失败序列 + 其条件 + 兜底动作。
            Assert.Equal(4, tree.EvaluationsUsed);
        }

        [Fact]
        public void PriorityMetadata_DoesNotReorderChildren()
        {
            // 第一个子节点优先级标 0，第二个标 999：执行顺序必须仍以 children 数组为准。
            BehaviorTree tree = Compile(
                0,
                new BehaviorNodeConfig(
                    0, BehaviorNodeType.Selector, 0, 0, new[] { 1, 2 }, priority: 0),
                new BehaviorNodeConfig(
                    1, BehaviorNodeType.Action, 0, (int)BehaviorAction.Wait, Array.Empty<int>(), priority: 0),
                new BehaviorNodeConfig(
                    2, BehaviorNodeType.Action, 0, (int)BehaviorAction.Attack, Array.Empty<int>(), priority: 999));

            Assert.Equal(BehaviorAction.Wait, Eval(tree, Board()));
        }

        [Fact]
        public void EvaluationsUsed_CountsEveryVisitedNodeIncludingSharedSubtree()
        {
            BehaviorTree tree = Compile(
                0,
                Selector(0, 1, 2),
                Sequence(1, 3, 3),
                Condition(2, BehaviorTreeLibrary.ConditionAlwaysSucceed),
                ActionNode(3, BehaviorAction.Wait));

            // 根(1) → 序列(1) → 同一个动作叶子被访问两次(2) → 根短路到成功，不再访问兜底节点。
            Assert.Equal(BehaviorAction.Wait, Eval(tree, Board()));
            Assert.Equal(4, tree.EvaluationsUsed);
        }

        // ────────────────────────────────────────────────────────────────
        // 构造校验
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NodeLimit_SixtyFiveNodes_Throws()
        {
            BehaviorNodeConfig[] nodes = BuildWideTree(childCount: 64);

            Assert.Throws<ArgumentException>(() =>
            {
                Compile(0, nodes);
            });
        }

        [Fact]
        public void NodeLimit_SixtyFourNodes_IsAccepted()
        {
            BehaviorNodeConfig[] nodes = BuildWideTree(childCount: 63);

            BehaviorTree tree = Compile(0, nodes);

            Assert.Equal(BehaviorAction.Wait, Eval(tree, Board()));
        }

        [Fact]
        public void DepthLimit_SixteenLevels_IsAccepted()
        {
            BehaviorTree tree = Compile(0, BuildChain(depth: 16));

            Assert.Equal(BehaviorAction.Wait, Eval(tree, Board()));
        }

        [Fact]
        public void DepthLimit_SeventeenLevels_Throws()
        {
            BehaviorNodeConfig[] nodes = BuildChain(depth: 17);

            Assert.Throws<ArgumentException>(() =>
            {
                Compile(0, nodes);
            });
        }

        [Fact]
        public void SelfReference_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(0, Selector(0, 0));
            });
        }

        [Fact]
        public void TwoNodeCycle_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(0, Selector(0, 1), Sequence(1, 2), Selector(2, 0));
            });
        }

        [Fact]
        public void MissingChildId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(0, Selector(0, 9));
            });
        }

        [Fact]
        public void MissingRootId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(42, ActionNode(0, BehaviorAction.Wait));
            });
        }

        [Fact]
        public void UnknownNodeType_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(
                    0,
                    new BehaviorNodeConfig(
                        0, (BehaviorNodeType)200, 0, 0, Array.Empty<int>(), priority: 0));
            });
        }

        [Fact]
        public void DuplicateNodeId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Compile(
                    0,
                    Selector(0, 1, 2),
                    ActionNode(1, BehaviorAction.Wait),
                    ActionNode(1, BehaviorAction.Attack));
            });
        }

        [Fact]
        public void NullNodeArray_Throws()
        {
            var config = new BehaviorTreeConfig(1, null!, 0);

            Assert.Throws<ArgumentException>(() =>
            {
                var tree = new BehaviorTree(in config);
            });
        }

        [Fact]
        public void NullChildrenArray_IsTreatedAsEmpty()
        {
            var node = new BehaviorNodeConfig(
                0, BehaviorNodeType.Selector, 0, 0, null!, priority: 0);
            BehaviorTree tree = Compile(0, node);

            Assert.Equal(BehaviorAction.None, Eval(tree, Board()));
        }

        // ────────────────────────────────────────────────────────────────
        // 诊断 / 稳定性 / 分配
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TreeId_IsExposedFromConfig()
        {
            var config = new BehaviorTreeConfig(777, new[] { ActionNode(0, BehaviorAction.Wait) }, 0);
            var tree = new BehaviorTree(in config);

            Assert.Equal(777, tree.TreeId);
        }

        [Fact]
        public void Reset_ClearsDiagnosticsAndKeepsBehavior()
        {
            BehaviorTree tree = Compile(0, ActionNode(0, BehaviorAction.Attack));

            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));
            Assert.True(tree.EvaluationsUsed > 0);

            tree.Reset();

            Assert.Equal(0, tree.EvaluationsUsed);
            Assert.Equal(BehaviorAction.Attack, Eval(tree, Board()));
        }

        [Fact]
        public void RepeatedEvaluate_IsStable()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();
            Blackboard board = Board(hpPercent: 100, enemyDistance: 4, cooldownReady: false);

            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(BehaviorAction.MoveToward, tree.Evaluate(in board));
                Assert.True(tree.EvaluationsUsed > 0);
            }
        }

        [Fact]
        public void Evaluate_AfterWarmup_DoesNotAllocate()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();
            Blackboard board = Board(hpPercent: 100, enemyDistance: 4, cooldownReady: false);

            for (int i = 0; i < 200; i++)
            {
                tree.Evaluate(in board);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                tree.Evaluate(in board);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        // ────────────────────────────────────────────────────────────────
        // 默认竞技场敌人树
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void ArenaEnemyTree_HasStableIdAndCompiles()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();

            Assert.Equal(BehaviorTreeLibrary.ArenaEnemyTreeId, tree.TreeId);

            // 正常状态走兜底追击分支。
            Assert.Equal(BehaviorAction.MoveToward, Eval(tree, Board()));
        }

        [Fact]
        public void ArenaEnemyTree_LowHp_Retreats()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();

            // 即使贴脸且冷却就绪，撤退分支也排在攻击之前。
            Assert.Equal(
                BehaviorAction.MoveAway,
                Eval(tree, Board(
                    hpPercent: 20,
                    enemyDistance: 1,
                    enemyCount: 3,
                    cooldownReady: true,
                    retreatThreshold: 30)));
        }

        [Fact]
        public void ArenaEnemyTree_InRangeAndReady_Attacks()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();

            Assert.Equal(
                BehaviorAction.Attack,
                Eval(tree, Board(
                    hpPercent: 100,
                    enemyDistance: 1,
                    cooldownReady: true,
                    retreatThreshold: 30)));
        }

        [Fact]
        public void ArenaEnemyTree_InRangeButNotReady_Waits()
        {
            BehaviorTree tree = BehaviorTreeLibrary.CreateArenaEnemyTree();

            Assert.Equal(
                BehaviorAction.Wait,
                Eval(tree, Board(
                    hpPercent: 100,
                    enemyDistance: 1,
                    cooldownReady: false,
                    retreatThreshold: 30)));
        }

        // ────────────────────────────────────────────────────────────────
        // 夹具
        // ────────────────────────────────────────────────────────────────

        private static BehaviorTree Compile(int rootNodeId, params BehaviorNodeConfig[] nodes)
        {
            var config = new BehaviorTreeConfig(1, nodes, rootNodeId);
            return new BehaviorTree(in config);
        }

        /// <summary>把临时黑板交给 <c>in</c> 参数（C# 9 不允许直接对方法返回值写 <c>in</c>）。</summary>
        private static BehaviorAction Eval(BehaviorTree tree, in Blackboard board)
        {
            return tree.Evaluate(in board);
        }

        private static BehaviorAction EvaluateCondition(int condition, in Blackboard board)
        {
            // 条件本身没有动作可执行：必须包进 Sequence 才会在条件成立时真正走到动作叶子。
            BehaviorTree tree = Compile(
                0,
                Sequence(0, 1, 2),
                Condition(1, condition),
                ActionNode(2, BehaviorAction.Attack));

            return tree.Evaluate(in board);
        }

        private static BehaviorNodeConfig Selector(int nodeId, params int[] children)
        {
            return new BehaviorNodeConfig(nodeId, BehaviorNodeType.Selector, 0, 0, children, priority: 0);
        }

        private static BehaviorNodeConfig Sequence(int nodeId, params int[] children)
        {
            return new BehaviorNodeConfig(nodeId, BehaviorNodeType.Sequence, 0, 0, children, priority: 0);
        }

        private static BehaviorNodeConfig Condition(int nodeId, int condition)
        {
            return new BehaviorNodeConfig(
                nodeId, BehaviorNodeType.Condition, condition, 0, Array.Empty<int>(), priority: 0);
        }

        private static BehaviorNodeConfig ActionNode(int nodeId, BehaviorAction action)
        {
            return new BehaviorNodeConfig(
                nodeId, BehaviorNodeType.Action, 0, (int)action, Array.Empty<int>(), priority: 0);
        }

        private static Blackboard Board(
            int hpPercent = 100,
            int enemyDistance = 4,
            int enemyCount = 1,
            bool cooldownReady = false,
            int waveIndex = 1,
            int retreatThreshold = 30)
        {
            return new Blackboard(
                hpPercent, enemyDistance, enemyCount, cooldownReady, waveIndex, retreatThreshold);
        }

        /// <summary>根 + <paramref name="childCount"/> 个动作叶子，节点总数 = childCount + 1。</summary>
        private static BehaviorNodeConfig[] BuildWideTree(int childCount)
        {
            BehaviorNodeConfig[] nodes = new BehaviorNodeConfig[childCount + 1];
            int[] children = new int[childCount];
            for (int i = 0; i < childCount; i++)
            {
                children[i] = i + 1;
            }

            nodes[0] = Selector(0, children);
            for (int i = 1; i <= childCount; i++)
            {
                nodes[i] = ActionNode(i, BehaviorAction.Wait);
            }

            return nodes;
        }

        /// <summary>一条 <paramref name="depth"/> 层深的单链：前 depth-1 个 Sequence，最后一个动作。</summary>
        private static BehaviorNodeConfig[] BuildChain(int depth)
        {
            BehaviorNodeConfig[] nodes = new BehaviorNodeConfig[depth];
            for (int i = 0; i < depth - 1; i++)
            {
                nodes[i] = new BehaviorNodeConfig(
                    i, BehaviorNodeType.Sequence, 0, 0, new[] { i + 1 }, priority: 0);
            }

            nodes[depth - 1] = ActionNode(depth - 1, BehaviorAction.Wait);
            return nodes;
        }
    }
}

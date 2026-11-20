#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ai;

namespace AIFramework.Core.Ai
{
    /// <summary>
    /// 数据驱动战斗行为树解释器（纯逻辑，零引擎依赖）。
    /// <para>
    /// <b>语义</b>：每帧从根节点完整求值一次，只输出本帧应执行的一个 <see cref="BehaviorAction"/>。
    /// Selector 按 children 顺序短路到第一个成功；Sequence 按 children 顺序短路到第一个失败；
    /// Condition/Action 是叶子。整棵树没有跨帧状态，因此<b>不支持 Running</b>——没有状态机可回填，
    /// 求值中一旦遇到 <see cref="BehaviorStatus.Running"/>（未来节点类型可能出现）一律按 Failure 处理。
    /// </para>
    /// <para>
    /// <b>构造期校验</b>：节点数 &lt;= 64、树深 &lt;= 16、节点 ID 唯一且引用存在、根存在、类型合法、
    /// 无环（自引用/多点循环）。任一不满足抛 <see cref="ArgumentException"/>；校验全部在构造期完成，
    /// 运行期不再做防御性判断。
    /// </para>
    /// <para>
    /// <b>零分配</b>：配置在构造期被拍平成四个定长 <c>int[]</c>（childStart/childCount/children/condition/action）
    /// 与一个 <c>byte[]</c>（nodeType）；<see cref="Evaluate"/> 只做数组下标与递归，不 new 数组、不装箱、不拼字符串。
    /// 预热后连续求值的托管堆增量应为 0。
    /// </para>
    /// <para>
    /// <b>线程</b>：构造完成后节点数据只读，单实例可被多处共享读取；但 <see cref="Evaluate"/> 会写
    /// <see cref="EvaluationsUsed"/> 与最近动作两个诊断字段，因此<b>同一实例不支持并发求值</b>。
    /// 需要多线程并行时，每线程各持有独立实例（或只共享构造好的 <see cref="BehaviorTreeConfig"/>）。
    /// </para>
    /// </summary>
    public sealed class BehaviorTree
    {
        /// <summary>单棵树允许的最大节点数。</summary>
        public const int MaxNodes = 64;

        /// <summary>从根到叶允许的最大节点层数（根算第 1 层）。</summary>
        public const int MaxDepth = 16;

        /// <summary>环检测状态：未访问。</summary>
        private const byte StateUnvisited = 0;

        /// <summary>环检测状态：当前递归栈上。</summary>
        private const byte StateVisiting = 1;

        /// <summary>环检测状态：已完成（深度已缓存）。</summary>
        private const byte StateDone = 2;

        private readonly BehaviorNodeType[] _types;

        private readonly int[] _conditions;

        private readonly int[] _actions;

        /// <summary>每个节点在扁平 child 数组里的起始下标。</summary>
        private readonly int[] _childStart;

        /// <summary>每个节点的子节点数量。</summary>
        private readonly int[] _childCount;

        /// <summary>所有节点的子节点下标按节点顺序首尾相接的扁平数组。</summary>
        private readonly int[] _children;

        private readonly int _rootIndex;

        private readonly int _treeId;

        private int _evaluationsUsed;

        private BehaviorAction _action;

        public BehaviorTree(in BehaviorTreeConfig config)
        {
            BehaviorNodeConfig[]? configuredNodes = config.Nodes;
            if (configuredNodes == null)
            {
                throw new ArgumentException("行为树配置不能为 null。", nameof(config));
            }

            int nodeCount = configuredNodes.Length;
            if (nodeCount <= 0)
            {
                throw new ArgumentException("行为树至少需要一个节点。", nameof(config));
            }

            if (nodeCount > MaxNodes)
            {
                throw new ArgumentException(
                    "行为树节点数 " + nodeCount + " 超过上限 " + MaxNodes + "。", nameof(config));
            }

            // ID → 下标：构造期一次性建字典并校验唯一性；求值期只走数组下标，不碰字典。
            var indexById = new Dictionary<int, int>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                int nodeId = configuredNodes[i].NodeId;
                if (indexById.ContainsKey(nodeId))
                {
                    throw new ArgumentException("行为树节点 ID 重复：" + nodeId + "。", nameof(config));
                }

                indexById.Add(nodeId, i);
            }

            if (!indexById.TryGetValue(config.RootNodeId, out _rootIndex))
            {
                throw new ArgumentException(
                    "行为树根节点 ID 不存在：" + config.RootNodeId + "。", nameof(config));
            }

            _treeId = config.TreeId;

            _types = new BehaviorNodeType[nodeCount];
            _conditions = new int[nodeCount];
            _actions = new int[nodeCount];
            _childStart = new int[nodeCount];
            _childCount = new int[nodeCount];

            // 第一遍：校验节点形状与所有子引用，同时统计扁平 child 数组的总长度。
            int totalChildren = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                BehaviorNodeConfig node = configuredNodes[i];
                BehaviorNodeType type = node.Type;
                if (type != BehaviorNodeType.Selector
                    && type != BehaviorNodeType.Sequence
                    && type != BehaviorNodeType.Condition
                    && type != BehaviorNodeType.Action)
                {
                    throw new ArgumentException(
                        "行为树节点 " + node.NodeId + " 的类型非法：" + (byte)type + "。", nameof(config));
                }

                int[]? children = node.Children;
                int childCount = children == null ? 0 : children.Length;
                for (int c = 0; c < childCount; c++)
                {
                    if (!indexById.ContainsKey(children![c]))
                    {
                        throw new ArgumentException(
                            "行为树节点 " + node.NodeId + " 引用了不存在的子节点 ID：" + children[c] + "。",
                            nameof(config));
                    }
                }

                _types[i] = type;
                _conditions[i] = node.Condition;
                _actions[i] = node.Action;
                _childStart[i] = totalChildren;
                _childCount[i] = childCount;
                totalChildren += childCount;
            }

            // 第二遍：把子节点 ID 解析成下标，落到一段连续内存里。
            _children = totalChildren == 0 ? Array.Empty<int>() : new int[totalChildren];
            for (int i = 0; i < nodeCount; i++)
            {
                int[]? children = configuredNodes[i].Children;
                int start = _childStart[i];
                int childCount = _childCount[i];
                for (int c = 0; c < childCount; c++)
                {
                    _children[start + c] = indexById[children![c]];
                }
            }

            // 环检测 + 深度计算。所有节点都遍历一遍：不可达子图里的环同样属于非法配置，
            // 深度也按"任意节点到最深后代"取最大值，避免一段不可达的超深链悄悄留在配置里。
            var states = new byte[nodeCount];
            var depths = new int[nodeCount];
            int maxDepth = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                int depth = MeasureDepth(i, states, depths);
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
            }

            if (maxDepth > MaxDepth)
            {
                throw new ArgumentException(
                    "行为树深度 " + maxDepth + " 超过上限 " + MaxDepth + "。", nameof(config));
            }

            _evaluationsUsed = 0;
            _action = BehaviorAction.None;
        }

        /// <summary>树 ID（来自配置，不参与求值）。</summary>
        public int TreeId
        {
            get { return _treeId; }
        }

        /// <summary>最近一次 <see cref="Evaluate"/> 访问的节点数（含根，重复访问重复计数）。</summary>
        public int EvaluationsUsed
        {
            get { return _evaluationsUsed; }
        }

        /// <summary>
        /// 从根开始求值一次，返回本帧应执行的动作。
        /// 根失败或根成功但树里没有动作叶子时返回 <see cref="BehaviorAction.None"/>。
        /// </summary>
        public BehaviorAction Evaluate(in Blackboard blackboard)
        {
            _evaluationsUsed = 0;
            _action = BehaviorAction.None;

            BehaviorStatus status = EvaluateNode(_rootIndex, in blackboard);
            if (status == BehaviorStatus.Success)
            {
                return _action;
            }

            // 根失败时，即使某个 Sequence 前半段已经"经过"动作叶子，这一帧也不该执行任何动作。
            _action = BehaviorAction.None;
            return BehaviorAction.None;
        }

        /// <summary>
        /// 清空诊断字段。<b>树本身没有跨帧状态</b>，因此这里只把
        /// <see cref="EvaluationsUsed"/> 与最近动作复位，不改变后续求值结果。
        /// </summary>
        public void Reset()
        {
            _evaluationsUsed = 0;
            _action = BehaviorAction.None;
        }

        /// <summary>递归求值一个节点；节点总数 &lt;= 64、深度 &lt;= 16，递归栈深有硬上限。</summary>
        private BehaviorStatus EvaluateNode(int index, in Blackboard blackboard)
        {
            _evaluationsUsed++;

            switch (_types[index])
            {
                case BehaviorNodeType.Condition:
                    return EvaluateCondition(_conditions[index], in blackboard)
                        ? BehaviorStatus.Success
                        : BehaviorStatus.Failure;

                case BehaviorNodeType.Action:
                    if (!TryResolveAction(_actions[index], out BehaviorAction action))
                    {
                        return BehaviorStatus.Failure;
                    }

                    _action = action;
                    return BehaviorStatus.Success;

                case BehaviorNodeType.Selector:
                {
                    int start = _childStart[index];
                    int end = start + _childCount[index];
                    for (int i = start; i < end; i++)
                    {
                        BehaviorStatus status = Normalize(EvaluateNode(_children[i], in blackboard));
                        if (status == BehaviorStatus.Success)
                        {
                            return BehaviorStatus.Success;
                        }
                    }

                    // 空 Selector 没有任何成功分支：按失败处理（与经典 BT 语义一致）。
                    return BehaviorStatus.Failure;
                }

                case BehaviorNodeType.Sequence:
                {
                    int start = _childStart[index];
                    int end = start + _childCount[index];
                    for (int i = start; i < end; i++)
                    {
                        BehaviorStatus status = Normalize(EvaluateNode(_children[i], in blackboard));
                        if (status != BehaviorStatus.Success)
                        {
                            return BehaviorStatus.Failure;
                        }
                    }

                    // 空 Sequence 没有失败分支：按成功处理（与经典 BT 语义一致）。
                    return BehaviorStatus.Success;
                }

                default:
                    // 构造期已拒绝未知类型；这里只是防御性兜底，正常运行不可达。
                    return BehaviorStatus.Failure;
            }
        }

        /// <summary>
        /// Running 不支持：本树是纯决策树，没有"下一帧从暂停节点续跑"的状态机。
        /// 未来若增加状态型节点，这里仍然按 Failure 收口，避免半执行状态悄悄泄漏到下一帧。
        /// </summary>
        private static BehaviorStatus Normalize(BehaviorStatus status)
        {
            return status == BehaviorStatus.Running ? BehaviorStatus.Failure : status;
        }

        /// <summary>
        /// 条件码映射：0=HP&lt;retreatThreshold、1=敌人在射程内、2=冷却就绪、3=敌人数量&gt;=2、
        /// 4=waveIndex&gt;=3、5=永远成功、6=永远失败；未知一律失败。
        /// </summary>
        private static bool EvaluateCondition(int condition, in Blackboard blackboard)
        {
            switch (condition)
            {
                case 0:
                    return blackboard.HpPercent < blackboard.RetreatThreshold;

                case 1:
                    return blackboard.EnemyDistance <= 1;

                case 2:
                    return blackboard.CooldownReady;

                case 3:
                    return blackboard.EnemyCount >= 2;

                case 4:
                    return blackboard.WaveIndex >= 3;

                case 5:
                    return true;

                case 6:
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>动作码映射：1=MoveToward、2=MoveAway、3=Attack、4=Wait；未知返回 false 且动作为 None。</summary>
        private static bool TryResolveAction(int actionCode, out BehaviorAction action)
        {
            switch (actionCode)
            {
                case 1:
                    action = BehaviorAction.MoveToward;
                    return true;

                case 2:
                    action = BehaviorAction.MoveAway;
                    return true;

                case 3:
                    action = BehaviorAction.Attack;
                    return true;

                case 4:
                    action = BehaviorAction.Wait;
                    return true;

                default:
                    action = BehaviorAction.None;
                    return false;
            }
        }

        /// <summary>
        /// 环检测（三色标记）并返回以 <paramref name="index"/> 为根的子树深度。
        /// 深度定义为"从根到最深叶子的节点数"，叶子为 1。
        /// </summary>
        private int MeasureDepth(int index, byte[] states, int[] depths)
        {
            byte state = states[index];
            if (state == StateDone)
            {
                return depths[index];
            }

            if (state == StateVisiting)
            {
                throw new ArgumentException(
                    "行为树配置存在环（节点下标 " + index + " 回到递归栈上的祖先）。", "config");
            }

            states[index] = StateVisiting;

            int maxChildDepth = 0;
            int start = _childStart[index];
            int end = start + _childCount[index];
            for (int i = start; i < end; i++)
            {
                int childDepth = MeasureDepth(_children[i], states, depths);
                if (childDepth > maxChildDepth)
                {
                    maxChildDepth = childDepth;
                }
            }

            int depth = maxChildDepth + 1;
            depths[index] = depth;
            states[index] = StateDone;
            return depth;
        }
    }
}

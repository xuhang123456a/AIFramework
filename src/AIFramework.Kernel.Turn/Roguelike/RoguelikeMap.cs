#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 线性 8 层地图：节点种类由种子生成的"地图流"决定，且强制满足
    /// "第 1 层 Battle、第 8 层 Boss、中段至少 1 个 Rest、至少 1 个 Shop"。
    /// <para>
    /// <b>为什么种类是种子决定的而不是写死的表</b>：肉鸽的"每局不一样"必须可复现——
    /// 同种子必然同地图，异种子高概率不同图；测试据此断言确定性，存档只需存种子。
    /// </para>
    /// <para>
    /// <b>生成算法</b>：中段 6 个槽位各掷一次 0..99——&lt;50 Battle、&lt;70 Elite、&lt;85 Shop、其余 Rest；
    /// 若中段缺 Rest（或 Shop），再从"未被占用为强制槽位"的中段槽位里随机挑一个改写成该种类。
    /// 已有的自然 Rest/Shop 会被标记为强制槽位保护起来，因此第二次强制不会把第一次的保底顶掉。
    /// 常数消耗：最多 6 + 1 + 1 次随机数，序列完全由种子决定。
    /// </para>
    /// <para>
    /// <b>零分配</b>：节点数组与"强制槽位"标记都在构造期一次分配，<see cref="Regenerate"/> 只做原地改写。
    /// </para>
    /// </summary>
    internal sealed class RoguelikeMap
    {
        private readonly NodeState[] _nodes;

        /// <summary>被"保底"规则占用的槽位：后续强制改写不得覆盖。</summary>
        private readonly bool[] _pinnedSlots;

        private readonly RoguelikeRandom _generator;

        internal RoguelikeMap(int seed)
        {
            _nodes = new NodeState[RoguelikeRules.MapNodeCount];
            _pinnedSlots = new bool[RoguelikeRules.MapNodeCount];
            _generator = new RoguelikeRandom(seed);

            Regenerate();
        }

        /// <summary>节点总数（固定 8）。</summary>
        internal int NodeCount
        {
            get { return _nodes.Length; }
        }

        /// <summary>取第 <paramref name="index"/> 个节点的种类。</summary>
        internal RoguelikeNodeKind KindAt(int index)
        {
            return _nodes[index].Kind;
        }

        /// <summary>该节点是否已被进入过。</summary>
        internal bool IsVisited(int index)
        {
            return _nodes[index].Visited;
        }

        /// <summary>标记为已进入。</summary>
        internal void MarkVisited(int index)
        {
            _nodes[index].Visited = true;
        }

        /// <summary>取只读快照；越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        internal RoguelikeNodeView GetNode(int index)
        {
            if (index < 0 || index >= _nodes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "节点 index 越界。");
            }

            return new RoguelikeNodeView(index, index + 1, _nodes[index].Kind, _nodes[index].Visited);
        }

        /// <summary>
        /// 用种子重放整张地图：随机流回到初始状态，种类序列与首次生成完全一致，访问标记清零。
        /// 这是 <see cref="RoguelikeWorld.Restart"/> 的地图侧半边。
        /// </summary>
        internal void Regenerate()
        {
            _generator.Reset();

            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i].Visited = false;
                _pinnedSlots[i] = false;
            }

            _nodes[RoguelikeRules.FirstNodeIndex].Kind = RoguelikeNodeKind.Battle;

            int lastMiddle = RoguelikeRules.BossNodeIndex - 1;
            for (int i = RoguelikeRules.FirstNodeIndex + 1; i <= lastMiddle; i++)
            {
                _nodes[i].Kind = RollKind(_generator);
            }

            _nodes[RoguelikeRules.BossNodeIndex].Kind = RoguelikeNodeKind.Boss;

            EnsureMiddleKind(RoguelikeNodeKind.Rest);
            EnsureMiddleKind(RoguelikeNodeKind.Shop);
        }

        /// <summary>中段必须出现的种类，缺失时随机挑一个未占用槽位改写。</summary>
        private void EnsureMiddleKind(RoguelikeNodeKind required)
        {
            int lastMiddle = RoguelikeRules.BossNodeIndex - 1;

            for (int i = RoguelikeRules.FirstNodeIndex + 1; i <= lastMiddle; i++)
            {
                if (_nodes[i].Kind == required)
                {
                    // 自然命中同样标记为强制槽位：后续强制另一类时不允许把它顶掉。
                    _pinnedSlots[i] = true;
                    return;
                }
            }

            int candidateCount = 0;
            for (int i = RoguelikeRules.FirstNodeIndex + 1; i <= lastMiddle; i++)
            {
                if (!_pinnedSlots[i])
                {
                    candidateCount++;
                }
            }

            if (candidateCount == 0)
            {
                // 6 个中段槽位最多只需保底 2 类，不可能占满；真占满说明常量被改坏。
                throw new InvalidOperationException("地图中段没有可用的保底槽位，MapNodeCount 不变量被破坏。");
            }

            int pick = _generator.NextInt(candidateCount);
            for (int i = RoguelikeRules.FirstNodeIndex + 1; i <= lastMiddle; i++)
            {
                if (_pinnedSlots[i])
                {
                    continue;
                }

                if (pick == 0)
                {
                    _nodes[i].Kind = required;
                    _pinnedSlots[i] = true;
                    return;
                }

                pick--;
            }
        }

        /// <summary>中段掷点：Battle 50% / Elite 20% / Shop 15% / Rest 15%。</summary>
        private static RoguelikeNodeKind RollKind(RoguelikeRandom random)
        {
            int roll = random.NextInt(100);

            if (roll < 50)
            {
                return RoguelikeNodeKind.Battle;
            }

            if (roll < 70)
            {
                return RoguelikeNodeKind.Elite;
            }

            if (roll < 85)
            {
                return RoguelikeNodeKind.Shop;
            }

            return RoguelikeNodeKind.Rest;
        }

        /// <summary>节点内部状态：种类 + 访问标记（可变结构体，只作为数组元素原地改写）。</summary>
        private struct NodeState
        {
            internal RoguelikeNodeKind Kind;

            internal bool Visited;
        }
    }
}

#nullable enable
using System;

namespace AIFramework.Contracts.Ai
{
    /// <summary>
    /// 行为树节点类型。本树是<b>纯决策树</b>：没有状态机、没有 Running 节点，
    /// 每帧从根重新求值一次，只输出本帧应执行的一个动作。
    /// </summary>
    public enum BehaviorNodeType : byte
    {
        /// <summary>选择节点：按 children 顺序尝试，第一个成功即成功；全部失败才失败。</summary>
        Selector = 0,

        /// <summary>序列节点：按 children 顺序执行，全部成功才成功；任一失败即失败。</summary>
        Sequence = 1,

        /// <summary>条件节点（叶子）：只读黑板判定，成功/失败。</summary>
        Condition = 2,

        /// <summary>动作节点（叶子）：映射成 <see cref="BehaviorAction"/>，恒为成功或非法失败。</summary>
        Action = 3,
    }

    /// <summary>
    /// 单个行为树节点的配置（纯数据，构造期校验，运行期只读）。
    /// <para>
    /// <b>为什么用 int 而不是枚举承载 condition/action</b>：配置由表/Luban/JSON 导出，
    /// int 是唯一不会在导出层产生额外映射负担的表示；枚举只负责"节点形状"，
    /// 具体语义由 <c>BehaviorTree</c> 的映射表解释，未知值一律按失败处理（向前兼容）。
    /// </para>
    /// <para>
    /// <b>Priority 只做创作期标注</b>：执行顺序严格以 <see cref="Children"/> 数组顺序为准，
    /// 本字段不会被解释器重排（"优先级"若参与排序，兄弟顺序就会在运行期漂移，配置不可复现）。
    /// </para>
    /// </summary>
    public readonly struct BehaviorNodeConfig
    {
        public BehaviorNodeConfig(
            int nodeId,
            BehaviorNodeType type,
            int condition,
            int action,
            int[] children,
            int priority)
        {
            NodeId = nodeId;
            Type = type;
            Condition = condition;
            Action = action;
            Children = children;
            Priority = priority;
        }

        /// <summary>节点在本棵树内的唯一 ID（子节点按 ID 引用）。</summary>
        public int NodeId { get; }

        /// <summary>节点形状。</summary>
        public BehaviorNodeType Type { get; }

        /// <summary>条件码（仅 <see cref="BehaviorNodeType.Condition"/> 使用，映射见 <c>BehaviorTree</c>）。</summary>
        public int Condition { get; }

        /// <summary>动作码（仅 <see cref="BehaviorNodeType.Action"/> 使用，映射见 <c>BehaviorTree</c>）。</summary>
        public int Action { get; }

        /// <summary>子节点 ID 列表；Selector/Sequence 允许为空，叶子节点应留空。</summary>
        public int[] Children { get; }

        /// <summary>创作期优先级标注；解释器不据此重排，执行顺序只看 <see cref="Children"/>。</summary>
        public int Priority { get; }
    }

    /// <summary>
    /// 一棵行为树的完整配置。<see cref="BehaviorTreeConfig"/> 允许在构造后继续被外部持有，
    /// 但 <c>BehaviorTree</c> 会在构造期做一次快照，之后的修改不会影响已编译的树。
    /// </summary>
    public readonly struct BehaviorTreeConfig
    {
        public BehaviorTreeConfig(int treeId, BehaviorNodeConfig[] nodes, int rootNodeId)
        {
            TreeId = treeId;
            Nodes = nodes;
            RootNodeId = rootNodeId;
        }

        /// <summary>树 ID（诊断/多棵树并存时的标识，不参与求值）。</summary>
        public int TreeId { get; }

        /// <summary>全部节点；节点数上限 64。</summary>
        public BehaviorNodeConfig[] Nodes { get; }

        /// <summary>根节点 ID；必须存在于 <see cref="Nodes"/> 中。</summary>
        public int RootNodeId { get; }
    }

    /// <summary>
    /// 节点求值结果。<b>本树不产生 <see cref="Running"/></b>：
    /// 没有跨帧状态机，Running 只作为契约枚举保留；解释器遇到 Running 时按 Failure 处理。
    /// </summary>
    public enum BehaviorStatus : byte
    {
        Success = 0,
        Failure = 1,
        Running = 2,
    }

    /// <summary>
    /// 一帧只读黑板：解释器只读这些值，不反写、不持有。
    /// <para>
    /// <b>为什么全是整数/布尔</b>：决策层只需要门槛判断，整数比较天然确定、跨平台一致，
    /// 且不会把浮点误差带进"AI 这一帧到底该不该攻击"这种可复现性敏感的判断里。
    /// </para>
    /// </summary>
    public readonly struct Blackboard
    {
        public Blackboard(
            int hpPercent,
            int enemyDistance,
            int enemyCount,
            bool cooldownReady,
            int waveIndex,
            int retreatThreshold)
        {
            HpPercent = hpPercent;
            EnemyDistance = enemyDistance;
            EnemyCount = enemyCount;
            CooldownReady = cooldownReady;
            WaveIndex = waveIndex;
            RetreatThreshold = retreatThreshold;
        }

        /// <summary>自身生命百分比（0..100）；边界值按原样传入，不做钳制。</summary>
        public int HpPercent { get; }

        /// <summary>最近敌人距离（整数化，调用方决定取整口径）；&lt;= 1 视为进入射程。</summary>
        public int EnemyDistance { get; }

        /// <summary>视野内敌人数量。</summary>
        public int EnemyCount { get; }

        /// <summary>攻击冷却是否就绪。</summary>
        public bool CooldownReady { get; }

        /// <summary>当前波次（调用方决定 0 基还是 1 基；条件 4 按 &gt;= 3 判定）。</summary>
        public int WaveIndex { get; }

        /// <summary>撤退血量门槛（条件 0 按严格小于判定）。</summary>
        public int RetreatThreshold { get; }
    }

    /// <summary>本帧要执行的原子动作；树只产出这一个动作，不负责执行。</summary>
    public enum BehaviorAction : byte
    {
        /// <summary>无动作（根失败、纯条件树成功、或未知动作码）。</summary>
        None = 0,

        /// <summary>朝目标移动。</summary>
        MoveToward = 1,

        /// <summary>远离目标。</summary>
        MoveAway = 2,

        /// <summary>攻击。</summary>
        Attack = 3,

        /// <summary>原地等待。</summary>
        Wait = 4,
    }
}

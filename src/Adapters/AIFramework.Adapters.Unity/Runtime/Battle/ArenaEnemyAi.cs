#nullable enable
using System;
using AIFramework.Contracts.Ai;
using AIFramework.Core.Ai;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// Realtime 战斗 AI 的<b>策略桥</b>：把一棵纯逻辑 <see cref="BehaviorTree"/> 包成"喂黑板 → 取动作"的对象，
    /// 供 <see cref="ArenaController"/> 在每帧 Step 之后调用。
    /// <para>
    /// <b>为什么不是 MonoBehaviour</b>：决策规则（条件码/动作码/短路）全部在 Core，适配层只负责
    /// 把内核只读视图翻译成 <see cref="Blackboard"/>。做成普通 C# 类后，决策桥不依赖 GameObject 生命周期，
    /// 也不会被 Unity 的组件序列化/Inspector 改坏；场景控制器只持有它的引用。
    /// </para>
    /// <para>
    /// <b>为什么一个实例可以服务多个敌人</b>：<see cref="BehaviorTree"/> 没有跨帧状态（纯决策树，
    /// 不支持 Running），<see cref="Tick"/> 是"当前黑板 → 本帧动作"的纯函数，只是把诊断字段更新为
    /// 最近一次求值的结果。因此一个实例顺序喂多个敌人的黑板是安全的；若将来树变成有状态，
    /// 这里必须改成每个敌人一份实例。
    /// </para>
    /// <para>
    /// <b>线程</b>：与 ArenaController 同线程（Unity 主线程的逻辑步）；不做锁，也不能跨线程并发 Tick。
    /// </para>
    /// </summary>
    public sealed class ArenaEnemyAi
    {
        private readonly BehaviorTree _tree;

        private BehaviorAction _lastAction;

        private int _lastEvaluationCount;

        /// <summary>用一棵已编译的行为树构造策略桥。</summary>
        /// <param name="tree">行为树；不能为 <c>null</c>（配置错误应在构造树时已经抛过）。</param>
        public ArenaEnemyAi(BehaviorTree tree)
        {
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        }

        /// <summary>最近一次 <see cref="Tick"/> 返回的动作（尚未 Tick 时为 <see cref="BehaviorAction.None"/>）。</summary>
        public BehaviorAction LastAction
        {
            get { return _lastAction; }
        }

        /// <summary>最近一次求值访问的节点数（诊断用，口径与 <see cref="BehaviorTree.EvaluationsUsed"/> 一致）。</summary>
        public int LastEvaluationCount
        {
            get { return _lastEvaluationCount; }
        }

        /// <summary>被包裹的树 ID（诊断用）。</summary>
        public int TreeId
        {
            get { return _tree.TreeId; }
        }

        /// <summary>
        /// 求值一次并返回本帧动作。只读黑板，不执行动作；动作到世界效果的映射由调用方决定。
        /// </summary>
        /// <param name="blackboard">本帧只读黑板；由调用方从内核只读视图近似填充。</param>
        public BehaviorAction Tick(in Blackboard blackboard)
        {
            BehaviorAction action = _tree.Evaluate(in blackboard);
            _lastAction = action;
            _lastEvaluationCount = _tree.EvaluationsUsed;
            return action;
        }
    }
}

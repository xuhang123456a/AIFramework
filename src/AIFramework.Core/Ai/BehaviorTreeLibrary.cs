#nullable enable
using AIFramework.Contracts.Ai;

namespace AIFramework.Core.Ai
{
    /// <summary>
    /// 常用行为树节点的构造库：用代码拼配置，避免业务层直接填 6 个裸参数。
    /// <para>
    /// <b>为什么是工厂而不是节点类继承体系</b>：行为树的形状由数据（<see cref="BehaviorNodeConfig"/>）
    /// 决定，节点行为由解释器按 <see cref="BehaviorNodeType"/> + 条件/动作码分派。若再引入
    /// <c>SelectorNode</c>/<c>ActionNode</c> 这类对象，配置表就得多维护一套类型映射，且每帧求值
    /// 会从"数组下标"退化成"虚调用/装箱"。这里只提供拼装便利，运行期仍然是纯数据。
    /// </para>
    /// <para>
    /// <b>优先级只是标注</b>：<see cref="BehaviorNodeConfig.Priority"/> 不参与求值排序；
    /// Selector/Sequence 严格按传入的 children 顺序执行。创作时把高优先级分支放前面即可。
    /// </para>
    /// </summary>
    public static class BehaviorTreeLibrary
    {
        /// <summary>条件码 0：HP 低于撤退门槛。</summary>
        public const int ConditionHpBelowRetreat = 0;

        /// <summary>条件码 1：敌人在射程内（距离 &lt;= 1）。</summary>
        public const int ConditionEnemyInRange = 1;

        /// <summary>条件码 2：攻击冷却就绪。</summary>
        public const int ConditionCooldownReady = 2;

        /// <summary>条件码 3：敌人数量 &gt;= 2（被包夹/多目标）。</summary>
        public const int ConditionOutnumbered = 3;

        /// <summary>条件码 4：波次 &gt;= 3（后期波）。</summary>
        public const int ConditionLateWave = 4;

        /// <summary>条件码 5：永远成功（用于"兜底分支"）。</summary>
        public const int ConditionAlwaysSucceed = 5;

        /// <summary>条件码 6：永远失败（用于占位/禁用分支）。</summary>
        public const int ConditionAlwaysFail = 6;

        /// <summary>竞技场敌人默认树 ID。</summary>
        public const int ArenaEnemyTreeId = 9001;

        /// <summary>构造一个选择节点（children 顺序即尝试顺序）。</summary>
        public static BehaviorNodeConfig Selector(int nodeId, int priority, params int[] children)
        {
            return new BehaviorNodeConfig(nodeId, BehaviorNodeType.Selector, 0, 0, children, priority);
        }

        /// <summary>构造一个序列节点（children 顺序即执行顺序）。</summary>
        public static BehaviorNodeConfig Sequence(int nodeId, int priority, params int[] children)
        {
            return new BehaviorNodeConfig(nodeId, BehaviorNodeType.Sequence, 0, 0, children, priority);
        }

        /// <summary>构造一个条件叶子。</summary>
        public static BehaviorNodeConfig Condition(int nodeId, int condition, int priority = 0)
        {
            return new BehaviorNodeConfig(
                nodeId, BehaviorNodeType.Condition, condition, 0, System.Array.Empty<int>(), priority);
        }

        /// <summary>构造一个动作叶子。</summary>
        public static BehaviorNodeConfig Action(int nodeId, int action, int priority = 0)
        {
            return new BehaviorNodeConfig(
                nodeId, BehaviorNodeType.Action, 0, action, System.Array.Empty<int>(), priority);
        }

        /// <summary>HP 低于撤退门槛（条件 0）。</summary>
        public static BehaviorNodeConfig HpBelowRetreat(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionHpBelowRetreat, priority);
        }

        /// <summary>敌人在射程内（条件 1）。</summary>
        public static BehaviorNodeConfig EnemyInRange(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionEnemyInRange, priority);
        }

        /// <summary>攻击冷却就绪（条件 2）。</summary>
        public static BehaviorNodeConfig CooldownReady(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionCooldownReady, priority);
        }

        /// <summary>敌人数量 &gt;= 2（条件 3）。</summary>
        public static BehaviorNodeConfig Outnumbered(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionOutnumbered, priority);
        }

        /// <summary>波次 &gt;= 3（条件 4）。</summary>
        public static BehaviorNodeConfig LateWave(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionLateWave, priority);
        }

        /// <summary>永远成功（条件 5）。</summary>
        public static BehaviorNodeConfig AlwaysSucceed(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionAlwaysSucceed, priority);
        }

        /// <summary>永远失败（条件 6）。</summary>
        public static BehaviorNodeConfig AlwaysFail(int nodeId, int priority = 0)
        {
            return Condition(nodeId, ConditionAlwaysFail, priority);
        }

        /// <summary>移动靠近动作（动作码 1）。</summary>
        public static BehaviorNodeConfig MoveToward(int nodeId, int priority = 0)
        {
            return Action(nodeId, (int)BehaviorAction.MoveToward, priority);
        }

        /// <summary>移动远离动作（动作码 2）。</summary>
        public static BehaviorNodeConfig MoveAway(int nodeId, int priority = 0)
        {
            return Action(nodeId, (int)BehaviorAction.MoveAway, priority);
        }

        /// <summary>攻击动作（动作码 3）。</summary>
        public static BehaviorNodeConfig Attack(int nodeId, int priority = 0)
        {
            return Action(nodeId, (int)BehaviorAction.Attack, priority);
        }

        /// <summary>等待动作（动作码 4）。</summary>
        public static BehaviorNodeConfig Wait(int nodeId, int priority = 0)
        {
            return Action(nodeId, (int)BehaviorAction.Wait, priority);
        }

        /// <summary>
        /// 竞技场敌人默认策略（纯数据，可被表驱动配置整体替换）：
        /// <list type="number">
        /// <item>HP 低于撤退门槛 → 远离；</item>
        /// <item>敌人在射程内且冷却就绪 → 攻击；</item>
        /// <item>敌人在射程内但冷却未就绪 → 等待；</item>
        /// <item>其余情况 → 追击。</item>
        /// </list>
        /// 分支顺序就是优先级：撤退 &gt; 攻击 &gt; 等待 &gt; 追击。
        /// </summary>
        public static BehaviorTreeConfig CreateArenaEnemyConfig()
        {
            BehaviorNodeConfig[] nodes =
            {
                // 根：按 100 → 90 → 80 → 10 的创作优先级排列（实际求值只看数组顺序）。
                Selector(0, 0, 1, 4, 8, 11),

                // 1) 低血撤退。
                Sequence(1, 100, 2, 3),
                HpBelowRetreat(2),
                MoveAway(3),

                // 2) 射程内且冷却就绪：攻击。
                Sequence(4, 90, 5, 6, 7),
                EnemyInRange(5),
                CooldownReady(6),
                Attack(7),

                // 3) 射程内但冷却未就绪：等待（避免贴脸空转）。
                Sequence(8, 80, 9, 10),
                EnemyInRange(9),
                Wait(10),

                // 4) 兜底：追击。
                Sequence(11, 10, 12, 13),
                AlwaysSucceed(12),
                MoveToward(13),
            };

            return new BehaviorTreeConfig(ArenaEnemyTreeId, nodes, 0);
        }

        /// <summary>编译竞技场敌人默认策略；等价于 <c>new BehaviorTree(CreateArenaEnemyConfig())</c>。</summary>
        public static BehaviorTree CreateArenaEnemyTree()
        {
            BehaviorTreeConfig config = CreateArenaEnemyConfig();
            return new BehaviorTree(in config);
        }
    }
}

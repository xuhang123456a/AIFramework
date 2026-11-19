using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Core.Meta
{
    /// <summary>
    /// 红点树（主板）。
    /// <para>
    /// 各业务切片只发 <see cref="RedDotDeltaCommand"/>（"+1 有新东西 / -1 看过了"），由本类统一维护
    /// 每个节点的计数；UI 通过 <see cref="RedDotQueryRequest"/> 问"这个节点亮不亮、亮几个"。
    /// 切片之间因此互不可见，也不需要知道树结构，跨模块协作全部收口在 <c>Contracts</c> 的 CQE 契约上。
    /// </para>
    /// <para>
    /// <b>为什么用定长数组</b>：节点 ID 由 <see cref="RedDotNodeIds"/> 约束在 <c>[1, <see cref="MaxNodeId"/>]</c>
    /// 内，直接下标访问即可，<see cref="Add"/> / <see cref="Get"/> / <see cref="Set"/> 全程
    /// 不查字典、不装箱、不分配、不抛异常（正常入参下），也没有任何需要兜底的异常控制流。
    /// </para>
    /// <para>
    /// 线程安全不在 Phase 1 范围内：与 <c>CqeBus</c> 一致，装配期单线程注册、运行期单线程发送。
    /// </para>
    /// </summary>
    public sealed class RedDotTree
        : ICommandHandler<RedDotDeltaCommand>,
          IQueryHandler<RedDotQueryRequest, RedDotSnapshot>
    {
        /// <summary>
        /// 允许的最大节点 ID。取值区间为 <c>[1, MaxNodeId]</c>；越界即视为调用方 bug，
        /// 写路径（<see cref="Add"/> / <see cref="Set"/>）直接抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        public const int MaxNodeId = 1024;

        /// <summary>节点计数槽位；下标即节点 ID，下标 0 永久不用（节点 ID 从 1 开始）。</summary>
        private readonly int[] _values;

        /// <summary>当前值大于 0 的节点个数，随 0 ↔ 非 0 的跃迁增减，查询 <see cref="NodeCount"/> 无需扫数组。</summary>
        private int _nodeCount;

        /// <summary>创建一棵所有节点都为 0（不亮）的空红点树。</summary>
        public RedDotTree()
        {
            _values = new int[MaxNodeId + 1];
            _nodeCount = 0;
        }

        /// <summary>当前有非 0 值的节点数（即亮着的节点个数，不含值为 0 的节点）。</summary>
        public int NodeCount
        {
            get { return _nodeCount; }
        }

        /// <summary>
        /// 把本实例挂到总线上：注册 1 个命令处理器（<see cref="RedDotDeltaCommand"/>）
        /// + 1 个查询处理器（<see cref="RedDotQueryRequest"/> → <see cref="RedDotSnapshot"/>）。
        /// <para>注册期允许分配，不属于热路径；同一总线重复注册会由总线自己抛异常。</para>
        /// </summary>
        /// <param name="bus">装配入口注入的总线实例。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">该总线已注册过同类处理器（重复调用了本方法，或别的实例先占了槽位）。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<RedDotDeltaCommand>(this);
            bus.RegisterQueryHandler<RedDotQueryRequest, RedDotSnapshot>(this);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 语义与 <see cref="Add"/> 完全一致（增量夹到 <c>&gt;= 0</c>）。
        /// 非法 <see cref="RedDotDeltaCommand.NodeId"/> 原样抛 <see cref="ArgumentOutOfRangeException"/>：
        /// 那是切片发出的 bug 指令，静默吞掉只会让红点长期错位，必须让问题在第一次出现时爆出来。
        /// </remarks>
        public void Handle(in RedDotDeltaCommand command)
        {
            Add(command.NodeId, command.Delta);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 查询路径是"全函数"：未知或越界节点一律回 <c>0</c>，绝不抛异常——
        /// UI 每帧都可能问一个当前版本还不存在的节点，读路径不该有异常控制流。
        /// </remarks>
        public RedDotSnapshot Handle(in RedDotQueryRequest query)
        {
            return new RedDotSnapshot(query.NodeId, Get(query.NodeId));
        }

        /// <summary>
        /// 在当前值上叠加 <paramref name="delta"/>（正数点亮、负数熄灭），结果夹到 <c>&gt;= 0</c>。
        /// <para>
        /// 求和走 <c>long</c> 再饱和回 <see cref="int"/>：极端增量（如 <see cref="int.MaxValue"/>）不会回绕成负数
        /// 导致红点凭空熄灭。全程不分配、不抛异常。
        /// </para>
        /// </summary>
        /// <param name="nodeId">节点 ID，取值区间 <c>[1, <see cref="MaxNodeId"/>]</c>。</param>
        /// <param name="delta">增量，可以为负。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 不在 <c>[1, MaxNodeId]</c> 内。</exception>
        public void Add(int nodeId, int delta)
        {
            ValidateNodeId(nodeId);

            long next = (long)_values[nodeId] + delta;
            if (next < 0L)
            {
                next = 0L;
            }
            else if (next > int.MaxValue)
            {
                next = int.MaxValue;
            }

            Apply(nodeId, (int)next);
        }

        /// <summary>读取节点当前值；未知或越界节点返回 <c>0</c>（不抛异常）。</summary>
        /// <param name="nodeId">节点 ID。</param>
        /// <returns>节点值，<c>0</c> 表示不亮。</returns>
        public int Get(int nodeId)
        {
            if (nodeId < 1 || nodeId > MaxNodeId)
            {
                return 0;
            }

            return _values[nodeId];
        }

        /// <summary>直接设值（覆盖而不是叠加），负数夹到 <c>0</c>。</summary>
        /// <param name="nodeId">节点 ID，取值区间 <c>[1, <see cref="MaxNodeId"/>]</c>。</param>
        /// <param name="value">目标值，可以为负（按 <c>0</c> 处理）。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 不在 <c>[1, MaxNodeId]</c> 内。</exception>
        public void Set(int nodeId, int value)
        {
            ValidateNodeId(nodeId);

            Apply(nodeId, value < 0 ? 0 : value);
        }

        /// <summary>
        /// 全清：所有节点归 0、<see cref="NodeCount"/> 归 0（切场景 / 登出 / 热更换账号时用）。
        /// </summary>
        public void Clear()
        {
            Array.Clear(_values, 0, _values.Length);
            _nodeCount = 0;
        }

        /// <summary>
        /// 写入一个已经夹好的值，并只在 "0 ↔ 非 0" 跃迁时维护 <see cref="_nodeCount"/>。
        /// </summary>
        /// <param name="nodeId">已校验过的节点 ID。</param>
        /// <param name="value">已夹到 <c>&gt;= 0</c> 的目标值。</param>
        private void Apply(int nodeId, int value)
        {
            int previous = _values[nodeId];
            if (previous == value)
            {
                return;
            }

            _values[nodeId] = value;

            if (previous == 0)
            {
                _nodeCount++;
            }
            else if (value == 0)
            {
                _nodeCount--;
            }
        }

        /// <summary>写路径的入参校验：只在非法入参时抛，正常调用零开销。</summary>
        /// <param name="nodeId">待校验的节点 ID。</param>
        private static void ValidateNodeId(int nodeId)
        {
            if (nodeId < 1 || nodeId > MaxNodeId)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nodeId),
                    nodeId,
                    "Red dot node id must be within [1, " + MaxNodeId.ToString() + "].");
            }
        }
    }
}

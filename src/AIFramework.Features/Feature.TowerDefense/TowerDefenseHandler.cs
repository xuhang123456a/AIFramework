using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.TowerDefense
{
    /// <summary>
    /// 塔防 Meta 切片：持有 8×8 棋盘状态，判定"能不能建"，并在扣款成功后落子、广播事实。
    /// <para>
    /// <b>它不驱动战斗</b>。战斗是 <c>Kernel.Realtime</c> 的事，本切片只写"塔在哪、是什么"，
    /// 由适配层在布场时把格坐标翻成世界坐标喂给实时内核——两套内核之间只通过
    /// <c>TowerLayoutQueryRequest</c> 这段只读数据交接，谁也不引用谁。
    /// </para>
    /// <para>
    /// <b>为什么所有拒绝都是静默的</b>：建造是玩家高频点击的入口，
    /// "点了没反应"就是 Phase 1 的既定交互（与商店切片一致）；报错事件会迫使 UI 为每个非法点击写提示文案，
    /// 而这些文案没有一条是玩家看得懂的（"格子越界"本身就不该被点出来）。
    /// </para>
    /// </summary>
    public sealed class TowerDefenseHandler : ICommandHandler<BuildTowerCommand>,
                                              IQueryHandler<TowerLayoutQueryRequest, TowerLayoutSnapshot>
    {
        /// <summary>棋盘列数（Phase 1 固定 8×8，与 HUD 的 <c>GridLayoutGroup</c> 约束一致）。</summary>
        public const int Width = 8;

        /// <summary>棋盘行数。</summary>
        public const int Height = 8;

        /// <summary>建造扣款的原因标签（因果日志用；6601 属于塔防建造段）。</summary>
        private const int ConsumeReasonTagId = 6601;

        /// <summary>总线的注入引用；只用于查金币和发扣款/事件，绝不直接改背包。</summary>
        private readonly ICqeBus _bus;

        /// <summary>本切片子表（构造期解析一次，之后只读）。</summary>
        private readonly TowerTables _tables;

        /// <summary>棋盘状态：行优先，下标 = <c>y * Width + x</c>，0 = 空格。</summary>
        private readonly int[] _cells;

        /// <summary>成功建造次数（诊断与测试断言用）。</summary>
        private int _buildCount;

        /// <summary>注入总线与子表加载端口。</summary>
        /// <param name="bus">CQE 总线（查金币、发扣款与事件）。</param>
        /// <param name="loader">子表加载端口，必须能提供 <c>TbTower</c>。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">子表不存在。</exception>
        /// <exception cref="System.IO.InvalidDataException">子表长度/重复 ID/非法数值。</exception>
        public TowerDefenseHandler(ICqeBus bus, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new TowerTables(loader);
            _cells = new int[Width * Height];
            _buildCount = 0;
        }

        /// <summary>成功建造次数。</summary>
        public int BuildCount
        {
            get { return _buildCount; }
        }

        /// <summary>子表行数（HUD 校验"塔表是否可用"时用，不必再查一次表）。</summary>
        public int TowerKindCount
        {
            get { return _tables.Count; }
        }

        /// <summary>
        /// 注册命令与布局查询处理器。<b>构造里不自动注册</b>：装配层的注册顺序就是因果日志的顺序，
        /// 不应该被构造函数偷偷改掉。
        /// </summary>
        /// <param name="bus">目标总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<BuildTowerCommand>(this);
            bus.RegisterQueryHandler<TowerLayoutQueryRequest, TowerLayoutSnapshot>(this);
        }

        /// <summary>
        /// 建造：越界 / 格子已占用 / 未知塔 → 静默拒绝（连金币都不查）；
        /// 金币不足 → 静默拒绝；金币足够 → 严格按"先扣金币、再落子、最后广播"三步走。
        /// </summary>
        /// <param name="command">建造意图。</param>
        public void Handle(in BuildTowerCommand command)
        {
            if (command.X < 0 || command.X >= Width || command.Y < 0 || command.Y >= Height)
            {
                // 越界：坐标本身非法，不查表也不查钱。
                return;
            }

            int index = (command.Y * Width) + command.X;
            if (_cells[index] != 0)
            {
                // 格子已占用：不允许覆盖，也不允许"卖塔"，Phase 1 没有拆除指令。
                return;
            }

            TowerDef def;
            if (!_tables.TryFind(command.TowerId, out def))
            {
                // 未知塔：静默拒绝，不查背包。
                return;
            }

            InventorySnapshot inventory = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold));
            if (inventory.Count < def.Cost)
            {
                // 金币不足：静默拒绝，状态不变。
                return;
            }

            // 顺序契约：先扣款，再落子，两者都成功后才广播事实。
            // 顺序颠倒会让"建了塔没扣钱"的脏状态出现在因果日志里，无法回放定位。
            _bus.Send(new ConsumeItemCommand(MetaItemIds.Gold, def.Cost, ConsumeReasonTagId));
            _cells[index] = def.TowerId;
            _buildCount++;

            _bus.Publish(new TowerBuiltEvent(command.X, command.Y, def.TowerId, def.Cost));
        }

        /// <summary>布局查询：返回当前棋盘的新快照（行优先，0 = 空格）。</summary>
        /// <param name="query">空查询体。</param>
        /// <returns>与内部状态解耦的布局快照。</returns>
        public TowerLayoutSnapshot Handle(in TowerLayoutQueryRequest query)
        {
            return new TowerLayoutSnapshot(Width, Height, _cells);
        }
    }
}

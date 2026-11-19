#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Merge
{
    /// <summary>
    /// 合成棋盘状态机：6×6 网格（行优先一维数组，0 = 空格），每格只存一个物品等级。
    /// <para>
    /// 唯一允许改写棋盘的地方：拖拽合成（<see cref="MergeCellsCommand"/>）与生成器掉落（<see cref="SpawnItemCommand"/>）。
    /// 所有非法请求一律静默拒绝——不改棋盘、不计数、不发指令、不发事件，避免 UI 与因果日志被无效请求污染。
    /// 跨模块交互只经注入的 <see cref="ICqeBus"/>：金币奖励发 <see cref="GrantItemCommand"/>，禁止直接改背包。
    /// </para>
    /// </summary>
    public sealed class MergeHandler : ICommandHandler<MergeCellsCommand>,
                                      ICommandHandler<SpawnItemCommand>,
                                      IQueryHandler<MergeGridQueryRequest, MergeGridSnapshot>
    {
        /// <summary>棋盘宽度（列数）。</summary>
        private const int BoardWidth = 6;

        /// <summary>棋盘高度（行数）。</summary>
        private const int BoardHeight = 6;

        /// <summary>空格等级：0 不占用子表任何一行。</summary>
        private const int EmptyLevel = 0;

        /// <summary>合成金币奖励的原因标签（因果日志用）。</summary>
        private const int MergeGoldReasonTagId = 6701;

        private readonly ICqeBus _bus;
        private readonly MergeTables _tables;

        /// <summary>棋盘状态：下标 = <c>y * BoardWidth + x</c>，值 = 物品等级（0 = 空）。</summary>
        private readonly int[] _levels;

        private int _mergeCount;
        private int _spawnCount;

        public MergeHandler(ICqeBus bus, IBinaryTableLoader loader)
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
            _tables = new MergeTables(loader);

            // 构造时棋盘全空：合成盘的初始状态由进关流程用 SpawnItemCommand 铺，不写死在切片里。
            _levels = new int[BoardWidth * BoardHeight];
            _mergeCount = 0;
            _spawnCount = 0;
        }

        /// <summary>成功合成的累计次数（被拒绝的请求不计数）。</summary>
        public int MergeCount
        {
            get { return _mergeCount; }
        }

        /// <summary>成功生成的累计次数（被拒绝的请求不计数）。</summary>
        public int SpawnCount
        {
            get { return _spawnCount; }
        }

        /// <summary>注册两条命令处理器与棋盘查询（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<MergeCellsCommand>(this);
            bus.RegisterCommandHandler<SpawnItemCommand>(this);
            bus.RegisterQueryHandler<MergeGridQueryRequest, MergeGridSnapshot>(this);
        }

        /// <summary>
        /// 拖拽合成：源格与目标格必须都非空、等级相同、正交相邻，且未达满级。
        /// 合法时源格清空、目标格升级、金币按"合成后等级"查表发放，最后广播合成事实。
        /// </summary>
        public void Handle(in MergeCellsCommand command)
        {
            if (!IsInside(command.FromX, command.FromY) || !IsInside(command.ToX, command.ToY))
            {
                // 越界：连棋盘下标都不合法，直接拒绝。
                return;
            }

            int fromLevel = LevelAt(command.FromX, command.FromY);
            if (fromLevel == EmptyLevel)
            {
                // 源格为空：没有可拖走的物品。
                return;
            }

            if (ManhattanDistance(command.FromX, command.FromY, command.ToX, command.ToY) != 1)
            {
                // 只允许四个正交方向的相邻格：对角、隔格与同一格一律拒绝。
                return;
            }

            // 合成口径：目标格必须已经放着同等级物品（两件同级合成一件更高一级）。
            // 目标格为空（0）或等级不同都无法构成合成链，因此同一个守卫同时覆盖"目标为空"与"等级不同"。
            int toLevel = LevelAt(command.ToX, command.ToY);
            if (toLevel != fromLevel)
            {
                return;
            }

            if (fromLevel >= _tables.MaxLevel)
            {
                // 满级不可再合成：level + 1 没有对应配置行，宁可不合成也不产出表中不存在的等级。
                return;
            }

            int newLevel = fromLevel + 1;
            SetLevel(command.FromX, command.FromY, EmptyLevel);
            SetLevel(command.ToX, command.ToY, newLevel);
            _mergeCount++;

            int goldAwarded = ResolveSellGold(newLevel);
            if (goldAwarded > 0)
            {
                // 先结算金币再广播事实：订阅方收到事件时背包已经落账，读到的状态与事件一致。
                _bus.Send(new GrantItemCommand(MetaItemIds.Gold, goldAwarded, MergeGoldReasonTagId));
            }

            _bus.Publish(new MergePerformedEvent(newLevel, command.ToX, command.ToY, goldAwarded));
        }

        /// <summary>
        /// 生成一件物品：只允许落在空格上，且等级必须在 1..MaxLevel 之内。
        /// 成功时不发事件、不发金币指令，只累计 <see cref="SpawnCount"/>。
        /// </summary>
        public void Handle(in SpawnItemCommand command)
        {
            if (!IsInside(command.X, command.Y))
            {
                return;
            }

            if (command.Level < 1 || command.Level > _tables.MaxLevel)
            {
                // 0 是空格哨兵、超出 MaxLevel 是配置外等级：两者都不允许被生成。
                return;
            }

            if (LevelAt(command.X, command.Y) != EmptyLevel)
            {
                // 格子已占：生成器必须自己先找一个空格，切片不做"覆盖已有物品"。
                return;
            }

            SetLevel(command.X, command.Y, command.Level);
            _spawnCount++;
        }

        /// <summary>棋盘查询：返回当前状态的防御性快照（行优先，0 = 空）。</summary>
        public MergeGridSnapshot Handle(in MergeGridQueryRequest query)
        {
            return new MergeGridSnapshot(BoardWidth, BoardHeight, _levels);
        }

        /// <summary>
        /// 按等级取金币售价：找不到该行返回 0（不打断已完成的合成）。
        /// 子表校验已保证等级连续 1..MaxLevel、合成后等级必然 ≤ MaxLevel，故正常路径不会走到 0 兜底。
        /// </summary>
        private int ResolveSellGold(int level)
        {
            for (int i = 0; i < _tables.Count; i++)
            {
                MergeItemDef def = _tables.Get(i);
                if (def.Level == level)
                {
                    return def.SellGold;
                }
            }

            return 0;
        }

        private static bool IsInside(int x, int y)
        {
            return x >= 0 && x < BoardWidth && y >= 0 && y < BoardHeight;
        }

        private static int ManhattanDistance(int fromX, int fromY, int toX, int toY)
        {
            return Math.Abs(fromX - toX) + Math.Abs(fromY - toY);
        }

        /// <summary>读格子等级；调用方必须先做 <see cref="IsInside"/> 判定。</summary>
        private int LevelAt(int x, int y)
        {
            return _levels[(y * BoardWidth) + x];
        }

        /// <summary>写格子等级；调用方必须先做 <see cref="IsInside"/> 判定。</summary>
        private void SetLevel(int x, int y, int level)
        {
            _levels[(y * BoardWidth) + x] = level;
        }
    }
}

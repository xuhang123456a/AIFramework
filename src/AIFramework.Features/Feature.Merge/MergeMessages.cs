using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Merge
{
    /// <summary>
    /// 拖拽合成：把 <c>(FromX, FromY)</c> 的物品拖到正交相邻的 <c>(ToX, ToY)</c>。
    /// 任一前置条件不满足一律静默拒绝（不抛异常、不发事件、不改棋盘），由调用方自行决定提示。
    /// </summary>
    public readonly struct MergeCellsCommand : ICommand
    {
        public MergeCellsCommand(int fromX, int fromY, int toX, int toY)
        {
            FromX = fromX;
            FromY = fromY;
            ToX = toX;
            ToY = toY;
        }

        /// <summary>源格列下标。</summary>
        public int FromX { get; }

        /// <summary>源格行下标。</summary>
        public int FromY { get; }

        /// <summary>目标格列下标（合成后新等级落在这里）。</summary>
        public int ToX { get; }

        /// <summary>目标格行下标。</summary>
        public int ToY { get; }
    }

    /// <summary>
    /// 合成成功事实。只有棋盘真的完成"两格清空 + 目标升级"后才广播；
    /// 被拒绝的请求不发任何事件，因此订阅方不需要再判一次合法性。
    /// </summary>
    public readonly struct MergePerformedEvent : IEvent
    {
        public MergePerformedEvent(int newLevel, int gridX, int gridY, int goldAwarded)
        {
            NewLevel = newLevel;
            GridX = gridX;
            GridY = gridY;
            GoldAwarded = goldAwarded;
        }

        /// <summary>合成后的等级（= 合成前等级 + 1）。</summary>
        public int NewLevel { get; }

        /// <summary>新物品所在列下标（即命令里的目标列）。</summary>
        public int GridX { get; }

        /// <summary>新物品所在行下标（即命令里的目标行）。</summary>
        public int GridY { get; }

        /// <summary>本次发放的金币数；0 表示该等级没有金币奖励。</summary>
        public int GoldAwarded { get; }
    }

    /// <summary>
    /// 生成一件物品（生成器产出 / 关卡掉落使用）。
    /// 只落格子，不发放金币、不广播事件：掉落播报由生成器切片自己的事件承担。
    /// </summary>
    public readonly struct SpawnItemCommand : ICommand
    {
        public SpawnItemCommand(int x, int y, int level)
        {
            X = x;
            Y = y;
            Level = level;
        }

        /// <summary>目标格列下标。</summary>
        public int X { get; }

        /// <summary>目标格行下标。</summary>
        public int Y { get; }

        /// <summary>物品等级（1..MaxLevel）。</summary>
        public int Level { get; }
    }

    /// <summary>棋盘只读查询端口：UI 一次拿全 6×6 等级布局。</summary>
    public readonly struct MergeGridQueryRequest : IQuery<MergeGridSnapshot>
    {
    }
}

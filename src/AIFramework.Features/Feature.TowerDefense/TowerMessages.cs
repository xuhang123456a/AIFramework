#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.TowerDefense
{
    /// <summary>
    /// 建造命令：在第 (<see cref="X"/>, <see cref="Y"/>) 格建一座 <see cref="TowerId"/> 塔。
    /// <para>
    /// <b>为什么是全量意图而不是"点一下格子"</b>：坐标、塔种、扣款都在切片内判定，
    /// 适配层只翻译"玩家点了哪格 + 当前选的是哪种塔"，不复制任何一条规则。
    /// </para>
    /// </summary>
    public readonly struct BuildTowerCommand : ICommand
    {
        public BuildTowerCommand(int x, int y, int towerId)
        {
            X = x;
            Y = y;
            TowerId = towerId;
        }

        /// <summary>棋盘列坐标（0..7），越界静默拒绝。</summary>
        public int X { get; }

        /// <summary>棋盘行坐标（0..7），越界静默拒绝。</summary>
        public int Y { get; }

        /// <summary>要建的塔 ID；子表里没有该 ID 则静默拒绝。</summary>
        public int TowerId { get; }
    }

    /// <summary>
    /// 建造成功事实。只有"扣款 + 落子"都完成后才广播；被拒绝的请求不发任何事件
    /// （与商店切片一致：静默拒绝，UI 侧不据此做提示）。
    /// </summary>
    public readonly struct TowerBuiltEvent : IEvent
    {
        public TowerBuiltEvent(int x, int y, int towerId, int cost)
        {
            X = x;
            Y = y;
            TowerId = towerId;
            Cost = cost;
        }

        /// <summary>落子列坐标。</summary>
        public int X { get; }

        /// <summary>落子行坐标。</summary>
        public int Y { get; }

        /// <summary>实际建成的塔 ID。</summary>
        public int TowerId { get; }

        /// <summary>实际扣掉的金币数量（等于该塔行的 cost）。</summary>
        public int Cost { get; }
    }

    /// <summary>塔布局只读查询端口：UI 与适配层靠它渲染棋盘，不需要知道状态怎么存的。</summary>
    public interface ITowerLayoutQuery : IQuery<TowerLayoutSnapshot>
    {
    }

    /// <summary>塔布局查询请求体：空结构体，棋盘尺寸由快照自带。</summary>
    public readonly struct TowerLayoutQueryRequest : ITowerLayoutQuery
    {
    }
}

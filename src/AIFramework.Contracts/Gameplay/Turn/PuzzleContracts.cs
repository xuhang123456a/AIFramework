#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>解谜格子类型。</summary>
    public enum PuzzleCell : byte
    {
        /// <summary>虚空：不可站立也不可推入（关卡未铺设的区域）。</summary>
        Empty = 0,

        /// <summary>墙：阻挡玩家与箱子。</summary>
        Wall = 1,

        /// <summary>普通地面：可站立、可放箱子。</summary>
        Floor = 2,

        /// <summary>目标点：可站立；箱子停在其上即算归位。</summary>
        Goal = 3
    }

    /// <summary>
    /// 解谜只读视图：渲染层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// <b>地形与箱子分层</b>：<see cref="GetCell"/> 只返回地形，箱子是否存在一律问 <see cref="HasBox"/>；
    /// 因此箱子压在目标点上时 <c>GetCell</c> 仍是 <see cref="PuzzleCell.Goal"/>，渲染层自行叠两层。
    /// </para>
    /// <para>坐标约定：<c>x</c> 向右 <c>0..Width-1</c>，<c>y</c> 向上 <c>0..Height-1</c>（<c>y = 0</c> 是底行）。</para>
    /// </summary>
    public interface IPuzzleReadView
    {
        /// <summary>关卡宽（列数）。</summary>
        int Width { get; }

        /// <summary>关卡高（行数）。</summary>
        int Height { get; }

        /// <summary>已走步数（只统计成功的移动/推箱，被拒的输入不计数）。</summary>
        int Moves { get; }

        /// <summary>当前停在目标点上的箱子数。</summary>
        int BoxesOnGoal { get; }

        /// <summary>本关箱子总数。</summary>
        int BoxCount { get; }

        /// <summary>是否所有箱子都在目标点上。</summary>
        bool IsSolved { get; }

        /// <summary>玩家所在列。</summary>
        int PlayerX { get; }

        /// <summary>玩家所在行。</summary>
        int PlayerY { get; }

        /// <summary>取 (x, y) 的地形；越界返回 <see cref="PuzzleCell.Wall"/>（调用方不必先判界）。</summary>
        PuzzleCell GetCell(int x, int y);

        /// <summary>(x, y) 是否有箱子；越界返回 false。</summary>
        bool HasBox(int x, int y);
    }

    /// <summary>解谜输入端口：适配层只能通过它提交意图，不能直接改状态。</summary>
    public interface IPuzzleInput
    {
        /// <summary>朝方向推一步：0=上 1=右 2=下 3=左；非法移动返回 false（不消耗步数）。</summary>
        bool TryMove(int direction);

        /// <summary>回到初始局面。</summary>
        void Reset();
    }
}

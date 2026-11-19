#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// 障碍槽位（可变的纯数据结构）。<b>只在内核槽位数组里存在</b>，对外一律投影成
    /// <see cref="RunnerObstacleView"/> 只读快照，渲染层拿不到可写引用。
    /// <para>
    /// <b>为什么用"可变字段 + 定长数组"而不是类</b>：每帧都要给每个障碍改 <see cref="X"/>，
    /// 用数组里的值类型就地改写既不动 GC，也不会产生"渲染层缓存了旧引用"的问题；
    /// 槽位数量上限 6，构造期一次分配，重开只是把 <c>ObstacleCount</c> 归零。
    /// </para>
    /// </summary>
    internal struct RunnerObstacle
    {
        internal RunnerObstacle(int obstacleId, float x, RunnerObstacleKind kind)
        {
            ObstacleId = obstacleId;
            X = x;
            Kind = kind;
            Hit = false;
        }

        /// <summary>本局内自增编号（从 1 开始）。</summary>
        internal int ObstacleId;

        /// <summary>世界横坐标（米）。</summary>
        internal float X;

        /// <summary>障碍类型。</summary>
        internal RunnerObstacleKind Kind;

        /// <summary>是否已撞上玩家（撞上即对局结束，至多一个为 true）。</summary>
        internal bool Hit;

        /// <summary>投影成只读快照（纯值复制，不分配）。</summary>
        internal RunnerObstacleView ToView()
        {
            return new RunnerObstacleView(ObstacleId, X, Kind, Hit);
        }
    }
}

#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 跑酷障碍类型。枚举值同时是<b>存档/回放里的稳定数值</b>（不要重排）。
    /// <para>
    /// <see cref="Low"/> 是贴地障碍（箱子、栏杆），只能<b>跳</b>过去；
    /// <see cref="High"/> 是空中障碍（横杆、吊灯），只能<b>蹲</b>下去。
    /// 两者按同一套 <c>|x| ≤ 0.6</c> 的判定窗触发，区别只在"用哪个玩家姿态解除"。
    /// </para>
    /// </summary>
    public enum RunnerObstacleKind : byte
    {
        /// <summary>地面障碍：玩家 <c>PlayerY &lt; 1.0</c> 时撞上。</summary>
        Low = 0,

        /// <summary>空中障碍：玩家未下蹲（<c>IsSliding == false</c>）时撞上。</summary>
        High = 1
    }

    /// <summary>
    /// 单个障碍的只读快照（不可变值类型：渲染层按帧取一次就能画，不必持有内核引用）。
    /// <para>
    /// <b><see cref="X"/> 是世界横坐标（米）</b>：玩家固定在 <c>x = 0</c>，障碍从右向左相对移动，
    /// 因此 <c>X</c> 递减；<c>X &lt; -2</c> 即被回收，不会再出现在视图里。
    /// </para>
    /// <para>
    /// <b><see cref="ObstacleId"/> 从 1 开始自增</b>（0 永远不是合法障碍），
    /// 所以 <c>default(RunnerObstacleView)</c> 天然可以表示"没有这个障碍"，越界查询不会和真障碍混淆。
    /// </para>
    /// </summary>
    public readonly struct RunnerObstacleView
    {
        public RunnerObstacleView(int obstacleId, float x, RunnerObstacleKind kind, bool hit)
        {
            ObstacleId = obstacleId;
            X = x;
            Kind = kind;
            Hit = hit;
        }

        /// <summary>本局内自增的障碍编号（从 1 开始，重开不回卷）。</summary>
        public int ObstacleId { get; }

        /// <summary>世界横坐标（米）；玩家在 <c>x = 0</c>，障碍从正数一侧向左移动。</summary>
        public float X { get; }

        /// <summary>障碍类型：决定用跳跃还是下蹲解除。</summary>
        public RunnerObstacleKind Kind { get; }

        /// <summary>本障碍是否已经撞到玩家。撞上即对局结束，因此至多一个障碍为 true。</summary>
        public bool Hit { get; }
    }

    /// <summary>
    /// 跑酷只读视图：渲染层每帧只读这里，不碰内核内部槽位数组。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右为正，玩家恒在 <c>x = 0</c>（<see cref="PlayerX"/> 永远返回 0）；
    /// <c>PlayerY</c> 是离地高度，<b>0 表示站在地面</b>，大于 0 表示正在跳跃。
    /// "已跑距离"只增不减，重开归零。
    /// </para>
    /// <para>
    /// <b>为什么障碍要按索引逐条取</b>：同屏最多 6 个，一次 <see cref="GetObstacle"/> 只复制一个
    /// 值类型快照，不产生数组、不产生堆分配，也不存在"整帧快照"被误缓存的问题。
    /// </para>
    /// </summary>
    public interface IRunnerReadView
    {
        /// <summary>已跑距离（米，单调递增；重开归零）。</summary>
        float Distance { get; }

        /// <summary>当前分数（<c>floor(Distance) * 10</c>，整数，不做浮点累加）。</summary>
        int Score { get; }

        /// <summary>玩家离地高度（米）：0 = 地面，&gt; 0 = 跳跃中。</summary>
        float PlayerY { get; }

        /// <summary>是否正在下蹲（到时自动站起）。</summary>
        bool IsSliding { get; }

        /// <summary>是否已撞上障碍（对局结束）。结算是否已取回由 <c>IGameplaySession.IsFinished</c> 表达。</summary>
        bool IsGameOver { get; }

        /// <summary>当前存活的障碍数量（0..6）。</summary>
        int ObstacleCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个障碍的快照；索引按<b>生成顺序</b>排列（0 = 最早生成、最靠近玩家）。
        /// 越界返回 <c>default</c>（<c>ObstacleId == 0</c>，不是合法障碍），调用方不必先判界。
        /// </summary>
        RunnerObstacleView GetObstacle(int index);

        /// <summary>玩家横坐标，恒为 0（保留属性是为了渲染层统一按"玩家也有一对坐标"取值）。</summary>
        float PlayerX { get; }
    }

    /// <summary>
    /// 跑酷输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>所有方法在对局结束后一律返回 false / 空操作，不抛异常。</para>
    /// </summary>
    public interface IRunnerInput
    {
        /// <summary>
        /// 起跳：垂直初速 6.5 m/s，重力 20 m/s²。<b>只有站在地面才能起跳</b>，
        /// 空中（含刚落脚前的一瞬）返回 false；起跳会结束当前下蹲。
        /// </summary>
        bool TryJump();

        /// <summary>
        /// 下蹲：地面才能蹲（空中返回 false），持续 <paramref name="durationSeconds"/> 秒后自动站起。
        /// 时长 <c>&lt;= 0</c>、NaN 或 <c>&gt; 3</c> 秒一律夹到 0.2 秒。
        /// </summary>
        bool TrySlide(double durationSeconds);

        /// <summary>推进世界：自动前进、障碍后移与回收、跳跃/下蹲计时、生成与碰撞判定。</summary>
        void Tick(double deltaSeconds);
    }
}

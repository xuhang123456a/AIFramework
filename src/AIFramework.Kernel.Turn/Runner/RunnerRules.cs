#nullable enable
namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// 跑酷的全部数值口径（常量 + 纯函数）。<b>这里只有公式，没有状态</b>，
    /// 因此 <see cref="RunnerWorld"/> 里不会再散落魔法数字，测试也可以直接引用同一份常量。
    /// <para>
    /// <b>为什么数值集中放一处</b>：跑酷的"速度 / 分数 / 碰撞窗 / 跳跃曲线"是四条会被反复调参的曲线，
    /// 一旦分散到世界类的各个分支里，调一个数就要通读整份实现；集中后改口径只动本文件。
    /// </para>
    /// </summary>
    internal static class RunnerRules
    {
        /// <summary>起步速度（米/秒）。</summary>
        internal const float BaseSpeed = 8f;

        /// <summary>速度上限相对起步的增量（米/秒），即速度封顶 <c>8 + 4 = 12</c>。</summary>
        internal const float MaxSpeedBonus = 4f;

        /// <summary>速度从 <see cref="BaseSpeed"/> 线性升到封顶所需跑过的距离（米）。</summary>
        internal const float SpeedRampDistance = 100f;

        /// <summary>障碍出生点在玩家前方的距离（米）：<c>x = Distance + 20</c>。</summary>
        internal const float SpawnAheadDistance = 20f;

        /// <summary>同屏障碍槽位上限（定长数组长度，构造期一次分配）。</summary>
        internal const int MaxObstacles = 6;

        /// <summary>生成间隔（秒）：每 1.4 秒尝试放一个障碍。</summary>
        internal const double SpawnIntervalSeconds = 1.4;

        /// <summary>越过玩家多远后回收槽位（米）：<c>x &lt; -2</c>。</summary>
        internal const float RecycleBehindDistance = 2f;

        /// <summary>碰撞判定半宽（米）：<c>|x| ≤ 0.6</c> 的窗口内逐帧判定。</summary>
        internal const float CollisionHalfWidth = 0.6f;

        /// <summary>跳过多高的地面障碍算安全（米）：<c>PlayerY ≥ 1.0</c> 即越过 Low。</summary>
        internal const float LowClearHeight = 1f;

        /// <summary>起跳垂直初速（米/秒）。</summary>
        internal const float JumpVelocity = 6.5f;

        /// <summary>重力加速度（米/秒²，向下为正）。</summary>
        internal const float Gravity = 20f;

        /// <summary>下蹲时长的下限/夹取值（秒）。</summary>
        internal const double MinSlideSeconds = 0.2;

        /// <summary>下蹲时长上限（秒）：超过它的请求一律按 <see cref="MinSlideSeconds"/> 处理。</summary>
        internal const double MaxSlideSeconds = 3.0;

        /// <summary>每米分数。</summary>
        internal const int ScorePerMeter = 10;

        /// <summary>胜利线（分）：<c>Score ≥ 500</c>，即跑满 50 米。</summary>
        internal const int VictoryScore = 500;

        /// <summary>Low（地面障碍）的百分比权重，其余归 High。</summary>
        internal const int LowKindPercent = 70;

        /// <summary>胜利战利品模板 ID。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        internal const int VictoryLootCount = 1;

        /// <summary>
        /// 当前速度（米/秒）：<c>8 + min(Distance / 100, 1) * 4</c>，即 0 米 8 m/s 线性升到 100 米 12 m/s 后封顶。
        /// </summary>
        internal static float SpeedAt(float distance)
        {
            float ratio = distance / SpeedRampDistance;
            if (ratio > 1f)
            {
                ratio = 1f;
            }
            else if (ratio < 0f)
            {
                ratio = 0f;
            }

            return BaseSpeed + (ratio * MaxSpeedBonus);
        }

        /// <summary>
        /// 当前分数：<c>floor(Distance) * 10</c>。<b>分数字段从不单独累加</b>，
        /// 每次都由距离现算，因此不会出现"距离停了分数还在涨"的浮点漂移。
        /// </summary>
        internal static int ScoreAt(float distance)
        {
            if (!(distance > 0f))
            {
                return 0;
            }

            int meters = (int)distance;
            return meters * ScorePerMeter;
        }
    }
}

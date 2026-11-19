#nullable enable
namespace AIFramework.Kernel.Turn.Platformer
{
    /// <summary>
    /// 平台跳跃的全部数值口径（常量 + 纯函数）。<b>这里只有公式与常量，没有状态</b>，
    /// 因此 <see cref="PlatformerWorld"/> 里不会散落魔法数字，测试也可以直接引用同一份口径。
    /// <para>
    /// <b>为什么数值集中放一处</b>：重力/跳跃/移动/下落封顶是四条会被反复调参的曲线；
    /// 一旦分散到物理分支里，改一个数就要通读整份实现。集中后调手感只动本文件。
    /// </para>
    /// <para>
    /// <b>几何口径</b>：长度单位一律米，坐标 <c>y</c> 向上为正；玩家 AABB 的锚点是<b>左下角</b>
    /// （<c>PlayerX</c>/<c>PlayerY</c> 就是左沿与脚底），平台顶面是 <c>Y + Height</c>。
    /// </para>
    /// </summary>
    internal static class PlatformerRules
    {
        /// <summary>重力加速度（米/秒²，向下为正）。</summary>
        internal const float Gravity = 22f;

        /// <summary>起跳垂直初速（米/秒）：解析顶点 <c>8² / (2 · 22) = 1.4545</c> 米。</summary>
        internal const float JumpSpeed = 8f;

        /// <summary>水平移动速度（米/秒，按住满舵时的速度）。</summary>
        internal const float MoveSpeed = 6f;

        /// <summary>下落速度上限（米/秒）：垂直速度不会低于 <c>-18</c>。</summary>
        internal const float MaxFallSpeed = 18f;

        /// <summary>玩家 AABB 宽度（米）。</summary>
        internal const float PlayerWidth = 0.8f;

        /// <summary>玩家 AABB 高度（米）。</summary>
        internal const float PlayerHeight = 1.6f;

        /// <summary>
        /// 单个物理子步的上限（秒）：一次 <see cref="PlatformerWorld.Tick(double)"/> 会按它切分，
        /// 避免大步长直接穿墙（下落 18 m/s 时一个子步最多走 0.9 米，小于任何平台间距）。
        /// </summary>
        internal const float MaxSubstepSeconds = 0.05f;

        /// <summary>
        /// 单帧可消费的最大时长（秒）：超过它视为"宿主卡顿/断点"而非真实帧长，只推 2 秒。
        /// <para>没有这条上限时，一个 <c>double.MaxValue</c> 帧长会把子步循环撑成天文数字。</para>
        /// </summary>
        internal const double MaxTickSeconds = 2.0;

        /// <summary>掉出世界的判定线（米）：玩家脚底低于它即判负。</summary>
        internal const float FallOutY = -10f;

        /// <summary>收集品判定盒的半边长（米）：判定盒是 <c>0.5 × 0.5</c>，中心即收集品坐标。</summary>
        internal const float CollectibleHalfExtent = 0.25f;

        /// <summary>垂直方向的落地容差（米）：上一子步的脚底不低于平台顶面减它，才允许落到顶面。</summary>
        internal const float LandingEpsilon = 0.0001f;

        /// <summary>站地判定容差（米）：脚底与平台顶面的高度差在此以内视为"还站在它上面"。</summary>
        internal const float GroundSnapTolerance = 0.001f;

        /// <summary>每个收集品的分数。</summary>
        internal const int ScorePerCollectible = 10;

        /// <summary>胜利战利品模板 ID（与其它回合卡带一致）。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        internal const int VictoryLootCount = 1;

        /// <summary>解析顶点高度（米）：<c>JumpSpeed² / (2 · Gravity)</c>，文档与断言共用。</summary>
        internal static float JumpApexHeight
        {
            get { return (JumpSpeed * JumpSpeed) / (2f * Gravity); }
        }

        /// <summary>
        /// 把移动意图夹到 <c>-1..1</c>：<c>NaN</c> 视作 <c>0</c>（松手），超出量程的按满舵处理。
        /// <para>NaN 必须显式拦掉——它参与任何比较都为 false，会一路污染到 <c>PlayerX</c>。</para>
        /// </summary>
        internal static float ClampMoveDirection(float direction)
        {
            if (float.IsNaN(direction))
            {
                return 0f;
            }

            if (direction > 1f)
            {
                return 1f;
            }

            if (direction < -1f)
            {
                return -1f;
            }

            return direction;
        }

        /// <summary>把玩家左沿夹进世界：左墙是 <c>0</c>，右墙是 <c>worldWidth - PlayerWidth</c>。</summary>
        internal static float ClampPlayerX(float x, float worldWidth)
        {
            float maxX = worldWidth - PlayerWidth;
            if (x < 0f)
            {
                return 0f;
            }

            if (x > maxX)
            {
                return maxX;
            }

            return x;
        }

        /// <summary>水平区间是否相交（半开区间：贴边不算相交，保证"走出平台边缘即失去支撑"）。</summary>
        internal static bool HorizontalOverlap(float left, float width, float otherLeft, float otherWidth)
        {
            return left < otherLeft + otherWidth && left + width > otherLeft;
        }

        /// <summary>
        /// 两个 AABB 是否相交（半开区间，贴边不算）。用于"玩家 AABB × 收集品判定盒"。
        /// </summary>
        internal static bool BoxesOverlap(
            float leftX,
            float leftY,
            float width,
            float height,
            float otherLeftX,
            float otherLeftY,
            float otherWidth,
            float otherHeight)
        {
            return leftX < otherLeftX + otherWidth
                && leftX + width > otherLeftX
                && leftY < otherLeftY + otherHeight
                && leftY + height > otherLeftY;
        }
    }
}

#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// <see cref="RunnerWorld"/> 的输入端口实现（<c>IRunnerInput</c>）：起跳与下蹲。
    /// <para>
    /// <b>输入语义</b>：对局结束后一律返回 false；空中既不能起跳也不能下蹲（"空中二段跳"与"空中下蹲"
    /// 都在这里被拒），下蹲时长按规则 7 夹取——<c>&lt;= 0</c>、NaN、<c>&gt; 3</c> 秒都变成 0.2 秒。
    /// </para>
    /// <para>
    /// <b>起跳会结束下蹲</b>：下蹲是"贴着地面"的姿态，起跳即站起；否则"边蹲边跳"会让 High / Low
    /// 两类障碍同时失效，跑酷就没有判负可能了。这条补充规则在文件头与测试里都写明。
    /// </para>
    /// <para>
    /// <b>为什么单独拆文件</b>：本文件只有"意图 → 状态变化"，状态读取、推进与结算在 <c>RunnerWorld.cs</c>；
    /// 两者都不超过 400 行，评审可以一次读完。拆开但仍是同一个 <c>partial</c> 类型，对外只有一个 <c>RunnerWorld</c>。
    /// </para>
    /// <para>
    /// <b>零分配</b>：这里只有浮点赋值与比较，不申请任何托管堆内存。
    /// </para>
    /// </summary>
    public sealed partial class RunnerWorld
    {
        /// <summary>
        /// 起跳：置垂直速度 6.5 m/s，由 <see cref="Tick(double)"/> 积分成抛物线。
        /// 只有站在地面才能跳（已起跳但尚未推进的那一瞬同样算离地），否则返回 false 且不改任何状态。
        /// </summary>
        public bool TryJump()
        {
            if (IsFinished || IsPlayerAirborne)
            {
                return false;
            }

            _playerVerticalVelocity = RunnerRules.JumpVelocity;
            _isSliding = false;
            _slideRemainingSeconds = 0.0;
            return true;
        }

        /// <summary>
        /// 下蹲：地面才能蹲（空中返回 false），<c>IsSliding</c> 置真并在 <paramref name="durationSeconds"/> 秒后
        /// 自动站起。时长 <c>&lt;= 0</c>、NaN 或 <c>&gt; 3</c> 秒一律夹到 0.2 秒；重复下蹲会刷新计时。
        /// </summary>
        public bool TrySlide(double durationSeconds)
        {
            if (IsFinished || IsPlayerAirborne)
            {
                return false;
            }

            double duration = durationSeconds;
            if (!(duration > 0.0) || duration > RunnerRules.MaxSlideSeconds)
            {
                duration = RunnerRules.MinSlideSeconds;
            }

            _isSliding = true;
            _slideRemainingSeconds = duration;
            return true;
        }
    }
}

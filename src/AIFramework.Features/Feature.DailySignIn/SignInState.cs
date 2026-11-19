#nullable enable
namespace AIFramework.Features.DailySignIn
{
    /// <summary>
    /// 签到切片纯数据状态（无业务方法，可整体序列化）。
    /// 字段语义见 CONTRACT.md 与 <see cref="SignInHandler"/> 注释：
    /// <see cref="CycleStartDayIndex"/> 是当前周期的第 1 天（绝对天序号）；
    /// <see cref="ClaimedDays"/> 下标 0 对应当前周期第 1 格。
    /// </summary>
    public sealed class SignInState
    {
        /// <summary>本切片固定 7 日周期长度。</summary>
        private const int CycleLength = 7;

        public SignInState()
        {
            LastClaimDayIndex = -1;
            ClaimedDays = new bool[CycleLength];
        }

        /// <summary>当前周期第 1 天的绝对天序号（自 2020-01-01 UTC 起算）。</summary>
        public int CycleStartDayIndex { get; internal set; }

        /// <summary>本周期已领天数，取值 0..7。</summary>
        public int ClaimedCount { get; internal set; }

        /// <summary>上次领取的绝对天序号；-1 表示本对象从未领取过。</summary>
        public int LastClaimDayIndex { get; internal set; }

        /// <summary>长度固定为 7 的领取标记，纯数据，测试只读。</summary>
        public bool[] ClaimedDays { get; }
    }
}

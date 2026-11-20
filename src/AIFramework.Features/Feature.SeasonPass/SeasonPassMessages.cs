#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.SeasonPass
{
    /// <summary>
    /// 赛季经验上报命令：把 <see cref="Amount"/> 累加到赛季累计经验上。
    /// <para>
    /// 规则：<c>Amount &lt;= 0</c> 静默忽略（不抛、不广播、不改状态）；
    /// 一次上报跨 N 个等级时按等级升序逐级广播 <see cref="SeasonLevelUpEvent"/>。
    /// </para>
    /// </summary>
    public readonly struct AddSeasonXpCommand : ICommand
    {
        public AddSeasonXpCommand(int amount)
        {
            Amount = amount;
        }

        /// <summary>本次经验增量，必须为正整数才会生效。</summary>
        public int Amount { get; }
    }

    /// <summary>
    /// 领取某等级某轨奖励的命令。
    /// <para>
    /// 命令通道没有返回值：所有拒绝（等级越界、未解锁、重复领取、付费轨未解锁、无付费奖励、赛季过期）
    /// 一律静默忽略——不发指令、不广播、不改状态；拒绝原因由 <c>SeasonPassSnapshot</c> 的字段自解释。
    /// </para>
    /// </summary>
    public readonly struct ClaimSeasonRewardCommand : ICommand
    {
        public ClaimSeasonRewardCommand(int level, bool paidTrack)
        {
            Level = level;
            PaidTrack = paidTrack;
        }

        /// <summary>要领取的等级（配置表 1 基等级）。</summary>
        public int Level { get; }

        /// <summary><c>false</c> 领免费轨，<c>true</c> 领付费轨。</summary>
        public bool PaidTrack { get; }
    }

    /// <summary>
    /// 解锁付费轨命令：由适配层在 IAP 收据校验通过后发出。
    /// <para>
    /// 本切片不做真实付费校验，只把"是否已解锁"落成状态；重复调用幂等（不广播事件、不改其它状态）。
    /// </para>
    /// </summary>
    public readonly struct UnlockPaidTrackCommand : ICommand
    {
    }

    /// <summary>
    /// 升级事实：一次经验上报跨 N 个等级时，按等级升序每级广播一次（<see cref="Level"/> 为新达到的等级）。
    /// <para>本事件只宣告升级，不带奖励：奖励必须由玩家显式领取，避免"自动发奖"绕过领取状态。</para>
    /// </summary>
    public readonly struct SeasonLevelUpEvent : IEvent
    {
        public SeasonLevelUpEvent(int level)
        {
            Level = level;
        }

        /// <summary>新达到的等级。</summary>
        public int Level { get; }
    }

    /// <summary>
    /// 奖励领取事实：同等级同轨只广播一次。
    /// <para>发出顺序固定：先 <c>GrantItemCommand</c> 发奖，再广播本事件。</para>
    /// </summary>
    public readonly struct SeasonRewardClaimedEvent : IEvent
    {
        public SeasonRewardClaimedEvent(int level, bool paidTrack, int itemId, int count)
        {
            Level = level;
            PaidTrack = paidTrack;
            ItemId = itemId;
            Count = count;
        }

        /// <summary>被领取奖励所属等级。</summary>
        public int Level { get; }

        /// <summary><c>false</c> 免费轨，<c>true</c> 付费轨。</summary>
        public bool PaidTrack { get; }

        /// <summary>本次发放的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>本次发放的数量。</summary>
        public int Count { get; }
    }
}

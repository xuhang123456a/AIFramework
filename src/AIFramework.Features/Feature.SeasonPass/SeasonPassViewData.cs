#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.SeasonPass
{
    /// <summary>
    /// 单级通行证展示状态：配置列 + 领取标记 + 等级解锁标记，一次查询内合并完成。
    /// <para>
    /// <see cref="FreeItemId"/>/<see cref="PaidItemId"/> 为 0 表示该轨在该等级没有奖励；
    /// <see cref="Unlocked"/> 只表示等级已到达（与付费轨是否解锁无关，后者看快照的 PaidUnlocked）。
    /// </para>
    /// </summary>
    public readonly struct SeasonRewardState
    {
        public SeasonRewardState(
            int level,
            int requiredXp,
            int freeItemId,
            int freeCount,
            int paidItemId,
            int paidCount,
            bool freeClaimed,
            bool paidClaimed,
            bool unlocked)
        {
            Level = level;
            RequiredXp = requiredXp;
            FreeItemId = freeItemId;
            FreeCount = freeCount;
            PaidItemId = paidItemId;
            PaidCount = paidCount;
            FreeClaimed = freeClaimed;
            PaidClaimed = paidClaimed;
            Unlocked = unlocked;
        }

        /// <summary>等级（1 基，配置表升序）。</summary>
        public int Level { get; }

        /// <summary>达到该等级所需的**累计总经验**（不是升级增量）。</summary>
        public int RequiredXp { get; }

        /// <summary>免费轨道具 ID；0 表示该级免费轨无奖励（正常配置不允许，表校验会挡掉）。</summary>
        public int FreeItemId { get; }

        /// <summary>免费轨发放数量。</summary>
        public int FreeCount { get; }

        /// <summary>付费轨道具 ID；0 表示该等级没有付费奖励。</summary>
        public int PaidItemId { get; }

        /// <summary>付费轨发放数量；无付费奖励时为 0。</summary>
        public int PaidCount { get; }

        /// <summary>免费轨是否已领取（同进程内不会回退）。</summary>
        public bool FreeClaimed { get; }

        /// <summary>付费轨是否已领取（同进程内不会回退）。</summary>
        public bool PaidClaimed { get; }

        /// <summary>当前等级是否已到达该级（等价于 <see cref="Level"/> &lt;= 快照的当前等级）。</summary>
        public bool Unlocked { get; }
    }

    /// <summary>
    /// 通行证全量只读快照：构造后不可变，等级 1 基、按等级升序。
    /// <para>
    /// 退化约定：配置表 0 行时 <see cref="Level"/> 为 0、<see cref="IsMaxLevel"/> 为 <c>true</c>、
    /// <see cref="Items"/> 为空数组，所有命令静默无效——空表是"赛季未配置"，不是崩溃条件。
    /// </para>
    /// <para>
    /// 满级约定：<see cref="CurrentXp"/> 保留真实累计值（满级后经验继续累加），
    /// <see cref="NextLevelXp"/> 退化为等于 <see cref="CurrentXp"/>，不再展示下一档门槛。
    /// </para>
    /// </summary>
    public sealed class SeasonPassSnapshot
    {
        public SeasonPassSnapshot(
            int level,
            int currentXp,
            int nextLevelXp,
            bool isMaxLevel,
            bool paidUnlocked,
            IReadOnlyList<SeasonRewardState> items,
            int claimableCount,
            long remainingSeconds,
            bool isExpired)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            Level = level;
            CurrentXp = currentXp;
            NextLevelXp = nextLevelXp;
            IsMaxLevel = isMaxLevel;
            PaidUnlocked = paidUnlocked;
            Items = items;
            ClaimableCount = claimableCount;
            RemainingSeconds = remainingSeconds;
            IsExpired = isExpired;
        }

        /// <summary>当前等级（1 基）；空表退化为 0。</summary>
        public int Level { get; }

        /// <summary>赛季累计经验（真实值，满级后继续累加）。</summary>
        public int CurrentXp { get; }

        /// <summary>升到下一级所需的累计经验；满级时等于 <see cref="CurrentXp"/>。</summary>
        public int NextLevelXp { get; }

        /// <summary>是否已满级（空表同样视为满级）。</summary>
        public bool IsMaxLevel { get; }

        /// <summary>付费轨是否已解锁（由 <see cref="UnlockPaidTrackCommand"/> 置位）。</summary>
        public bool PaidUnlocked { get; }

        /// <summary>全部等级展示状态，按等级升序；空表为空数组。</summary>
        public IReadOnlyList<SeasonRewardState> Items { get; }

        /// <summary>当前可领奖励条数：已到达等级中未领取的免费轨 + 已解锁付费轨中未领取且有奖励的付费轨；过期或空表恒为 0。</summary>
        public int ClaimableCount { get; }

        /// <summary>赛季剩余秒数（不小于 0，按注入的 <c>ITimeProvider</c> 计算）。</summary>
        public long RemainingSeconds { get; }

        /// <summary>赛季是否已过期；过期后所有领取命令被拒。</summary>
        public bool IsExpired { get; }
    }

    /// <summary>通行证全量查询请求：无参数，返回当前玩家的只读快照。</summary>
    public readonly struct SeasonPassQueryRequest : IQuery<SeasonPassSnapshot>
    {
    }
}

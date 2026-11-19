#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Features.Mail
{
    /// <summary>单封邮件展示数据（过期邮件不下发给视图）。</summary>
    public readonly struct MailItemViewData
    {
        public MailItemViewData(int mailId, bool claimed, bool canClaim, int rewardItemId, int rewardCount)
        {
            MailId = mailId;
            Claimed = claimed;
            CanClaim = canClaim;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        /// <summary>邮件唯一 ID（与子表 <c>mailId</c> 一致，视图回传它来定位操作目标）。</summary>
        public int MailId { get; }

        /// <summary>是否已领取（已领取邮件永不因时间推移变成过期）。</summary>
        public bool Claimed { get; }

        /// <summary>现在是否可领（Presenter 算好：未领取且未过期）。</summary>
        public bool CanClaim { get; }

        /// <summary>附件道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>附件数量。</summary>
        public int RewardCount { get; }
    }


}

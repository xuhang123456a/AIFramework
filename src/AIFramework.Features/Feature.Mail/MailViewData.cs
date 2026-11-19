#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Features.Mail
{
    /// <summary>邮件展示状态。只有 <see cref="Unread"/> 可领取，其余状态领取一律静默拒绝。</summary>
    public enum MailState
    {
        /// <summary>未领取且在有效期内。</summary>
        Unread = 0,

        /// <summary>已领取。</summary>
        Claimed = 1,

        /// <summary>超过有效期（7 天）仍未领取。</summary>
        Expired = 2,
    }

    /// <summary>单封邮件的只读展示数据：UI 只读它，禁止据此反算业务规则。</summary>
    public readonly struct MailViewData
    {
        public MailViewData(int mailId, MailState state, int rewardItemId, int rewardCount)
        {
            MailId = mailId;
            State = state;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        public int MailId { get; }

        /// <summary>查询时刻实时判定的状态（过期不落状态机字段）。</summary>
        public MailState State { get; }

        public int RewardItemId { get; }

        public int RewardCount { get; }
    }

    /// <summary>邮件列表快照：按 <see cref="MailViewData.MailId"/> 升序的只读视图。</summary>
    public sealed class MailListSnapshot
    {
        public MailListSnapshot(IReadOnlyList<MailViewData> mails)
        {
            if (mails == null)
            {
                throw new ArgumentNullException(nameof(mails));
            }

            Mails = mails;
        }

        /// <summary>邮件列表，按 mailId 升序；空邮箱返回空列表而不是 null。</summary>
        public IReadOnlyList<MailViewData> Mails { get; }
    }
}

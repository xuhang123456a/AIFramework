using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Mail
{
    /// <summary>领取指定邮件的附件。合法领取才会发奖并广播，非法一律静默拒绝。</summary>
    public readonly struct ClaimMailCommand : ICommand
    {
        public ClaimMailCommand(int mailId)
        {
            MailId = mailId;
        }

        /// <summary>目标邮件 ID（子表 <c>mailId</c>）。</summary>
        public int MailId { get; }
    }

    /// <summary>删除指定邮件（不领奖）。已领取邮件是否可删不在 Phase 1 范围内，见 CONTRACT.md。</summary>
    public readonly struct DeleteMailCommand : ICommand
    {
        public DeleteMailCommand(int mailId)
        {
            MailId = mailId;
        }

        /// <summary>目标邮件 ID（子表 <c>mailId</c>）。</summary>
        public int MailId { get; }
    }

    /// <summary>邮件领取成功事实：字段为本次实际发放内容，订阅方据此刷新背包 UI。</summary>
    public readonly struct MailClaimedEvent : IEvent
    {
        public MailClaimedEvent(int mailId, int rewardItemId, int rewardCount)
        {
            MailId = mailId;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        public int MailId { get; }

        public int RewardItemId { get; }

        public int RewardCount { get; }
    }

    /// <summary>邮件列表查询端口：UI 只问"我有哪些邮件、什么状态"，不关心子表与有效期规则。</summary>
    public interface IMailListQuery : IQuery<MailListSnapshot>
    {
    }

    /// <summary>邮件列表查询请求体：空结构体，列表内容完全由切片状态决定。</summary>
    public readonly struct MailListQueryRequest : IMailListQuery
    {
    }
}

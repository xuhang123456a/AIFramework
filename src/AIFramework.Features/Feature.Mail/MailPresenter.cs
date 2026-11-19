using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Mail
{
    /// <summary>
    /// 邮件面板的纯逻辑 UI 大脑：把 <see cref="MailHandler"/> 的邮箱快照翻译成视图数据，
    /// 并把视图点击转发成 CQE 命令。过期过滤与"可领"判定只在这里做，
    /// 邮件的真实状态与领取规则仍完全归处理器所有，这里不写任何状态。
    /// </summary>
    public sealed class MailPresenter
    {
        private readonly ICqeBus _bus;
        private readonly IMailView _view;

        public MailPresenter(ICqeBus bus, IMailView view)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            _bus = bus;
            _view = view;
            _view.OnClaimClicked += HandleClaimClicked;
            _view.OnDeleteClicked += HandleDeleteClicked;
        }

        /// <summary>
        /// 查询邮箱快照 → 丢掉 <see cref="MailState.Expired"/> → 交给视图。
        /// 每次允许分配（非帧内路径）。
        /// </summary>
        public void Refresh()
        {
            MailListSnapshot snapshot = _bus.Query<MailListQueryRequest, MailListSnapshot>(new MailListQueryRequest());
            List<MailItemViewData> visible = new List<MailItemViewData>(snapshot.Mails.Count);
            for (int i = 0; i < snapshot.Mails.Count; i++)
            {
                MailViewData mail = snapshot.Mails[i];
                if (mail.State == MailState.Expired)
                {
                    // 过期邮件不下发：视图不需要"已失效但还占一行"的条目，删除入口也随之消失。
                    continue;
                }

                visible.Add(new MailItemViewData(
                    mail.MailId,
                    mail.State == MailState.Claimed,
                    mail.State == MailState.Unread,
                    mail.RewardItemId,
                    mail.RewardCount));
            }

            _view.RenderMails(visible);
        }

        /// <summary>领取指定邮件：只发意图，成败由处理器判定；随后重新查询刷新视图。</summary>
        public void Claim(int mailId)
        {
            _bus.Send(new ClaimMailCommand(mailId));
            Refresh();
        }

        /// <summary>删除指定邮件：只发意图，是否允许删除由处理器判定；随后重新查询刷新视图。</summary>
        public void Delete(int mailId)
        {
            _bus.Send(new DeleteMailCommand(mailId));
            Refresh();
        }

        private void HandleClaimClicked(int mailId)
        {
            Claim(mailId);
        }

        private void HandleDeleteClicked(int mailId)
        {
            Delete(mailId);
        }
    }
}

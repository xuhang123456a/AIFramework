using System;
using System.Collections.Generic;
using AIFramework.Features.Mail;
using Xunit;

namespace AIFramework.Features.Tests.Mail
{
    /// <summary>
    /// 邮件 Presenter 验收：视图只拿"未过期"的展示数据，领取/删除只转发成 CQE 命令，
    /// 状态永远由处理器决定。全程真实 <c>CqeBus</c> + 本切片夹具（<see cref="MailHarness"/>），
    /// 不直接调用处理器、不回写视图数据。
    /// </summary>
    public sealed class MailPresenterTests
    {
        /// <summary>夹具起点：2020-03-01 12:00 UTC（取正午以验证 <c>.Date</c> 截断）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2020, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            FakeMailView view = new FakeMailView();

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () =>
                {
                    _ = new MailPresenter(null!, view);
                });

            Assert.Equal("bus", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullView_Throws()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () =>
                {
                    _ = new MailPresenter(harness.Bus, null!);
                });

            Assert.Equal("view", exception.ParamName);
        }

        [Fact]
        public void Refresh_RendersEveryUnreadMailWithCanClaim()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);

            presenter.Refresh();

            Assert.Equal(1, view.RenderCount);
            Assert.Equal(3, view.LastMails.Length);

            // 查询按 mailId 升序：1001 → 1002 → 1003，与子表物理行序无关。
            Assert.Equal(MailFixture.OldestMailId, view.LastMails[0].MailId);
            Assert.Equal(MailFixture.TodayMailId, view.LastMails[1].MailId);
            Assert.Equal(MailFixture.RecentMailId, view.LastMails[2].MailId);

            for (int i = 0; i < view.LastMails.Length; i++)
            {
                Assert.False(view.LastMails[i].Claimed);
                Assert.True(view.LastMails[i].CanClaim);
            }

            MailItemViewData today = RequireMail(view.LastMails, MailFixture.TodayMailId);
            Assert.Equal(MailFixture.TodayRewardItemId, today.RewardItemId);
            Assert.Equal(MailFixture.TodayRewardCount, today.RewardCount);
        }

        [Fact]
        public void Refresh_AfterExpiryWindow_FiltersExpiredAndKeepsClaimed()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();
            Assert.Equal(3, view.LastMails.Length);

            // 先领掉今天这封：已领取邮件不会随时间推移翻成 Expired。
            presenter.Claim(MailFixture.TodayMailId);

            // 推进 7 天：1001（1 天前发）与 1003（2 天前发）越过有效期，只剩已领取的 1002。
            harness.Time.AdvanceDays(7);
            presenter.Refresh();

            MailItemViewData survivor = Assert.Single(view.LastMails);
            Assert.Equal(MailFixture.TodayMailId, survivor.MailId);
            Assert.True(survivor.Claimed);
            Assert.False(survivor.CanClaim);
        }

        [Fact]
        public void Refresh_WhenEveryMailExpired_RendersEmptyList()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);

            presenter.Refresh();
            Assert.Equal(3, view.LastMails.Length);

            // 一封都没领就推进 7 天：三封全过期，视图必须收到空列表（而不是三条失效行）。
            harness.Time.AdvanceDays(7);
            presenter.Refresh();

            Assert.Equal(2, view.RenderCount);
            Assert.Empty(view.LastMails);
        }

        [Fact]
        public void Claim_UnreadMail_RefreshesToClaimedWithoutCanClaim()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);

            presenter.Claim(MailFixture.TodayMailId);

            // Claim 内部发指令 + 自动 Refresh：视图立刻看到已领取。
            Assert.Equal(1, view.RenderCount);
            Assert.Equal(3, view.LastMails.Length);
            MailItemViewData claimed = RequireMail(view.LastMails, MailFixture.TodayMailId);
            Assert.True(claimed.Claimed);
            Assert.False(claimed.CanClaim);

            // 再显式刷一次也必须保持：状态来自处理器，Presenter 自己不记任何状态。
            presenter.Refresh();
            Assert.Equal(2, view.RenderCount);
            MailItemViewData refreshed = RequireMail(view.LastMails, MailFixture.TodayMailId);
            Assert.True(refreshed.Claimed);
            Assert.False(refreshed.CanClaim);

            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
            Assert.Single(harness.Grants.Items);
            Assert.Single(harness.ClaimedEvents.Items);

            // 其余两封仍是未领取且可领。
            Assert.True(RequireMail(view.LastMails, MailFixture.OldestMailId).CanClaim);
            Assert.True(RequireMail(view.LastMails, MailFixture.RecentMailId).CanClaim);
        }

        [Fact]
        public void Delete_UnreadMail_RemovesItFromViewList()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();

            presenter.Delete(MailFixture.OldestMailId);

            Assert.Equal(2, view.LastMails.Length);
            Assert.Equal(MailFixture.TodayMailId, view.LastMails[0].MailId);
            Assert.Equal(MailFixture.RecentMailId, view.LastMails[1].MailId);

            // 删除不发奖、不发领取事件，邮件确实从处理器信箱里消失。
            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            Assert.Equal(2, harness.QueryList().Mails.Count);
        }

        [Fact]
        public void Claim_UnknownMailId_DoesNotThrowAndKeepsList()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();

            presenter.Claim(424242);

            // 未知 ID 由处理器静默拒绝：视图刷新了一次，内容原样。
            Assert.Equal(2, view.RenderCount);
            Assert.Equal(3, view.LastMails.Length);
            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            for (int i = 0; i < view.LastMails.Length; i++)
            {
                Assert.True(view.LastMails[i].CanClaim);
            }
        }

        [Fact]
        public void Delete_UnknownMailId_DoesNotThrowAndKeepsList()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();

            presenter.Delete(424242);

            Assert.Equal(2, view.RenderCount);
            Assert.Equal(3, view.LastMails.Length);
            Assert.Equal(3, harness.QueryList().Mails.Count);
        }

        [Fact]
        public void ViewClaimClicked_ForwardsMailIdToBusAndRefreshes()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();

            view.RaiseClaimClicked(MailFixture.TodayMailId);

            // 事件回传的是邮件 ID，Presenter 原样转发，不自己编号。
            Assert.Equal(2, view.RenderCount);
            Assert.True(RequireMail(view.LastMails, MailFixture.TodayMailId).Claimed);
            Assert.Single(harness.Grants.Items);
        }

        [Fact]
        public void ViewDeleteClicked_ForwardsMailIdToBusAndRefreshes()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            FakeMailView view = new FakeMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);
            presenter.Refresh();

            view.RaiseDeleteClicked(MailFixture.OldestMailId);

            Assert.Equal(2, view.RenderCount);
            Assert.Equal(2, view.LastMails.Length);
            Assert.DoesNotContain(view.LastMails, mail => mail.MailId == MailFixture.OldestMailId);
        }

        [Fact]
        public void CallsWithoutAnyEventSubscriber_DoNotThrow()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            // 视图把事件实现成空 add/remove，等价于"永远没有订阅者"：
            // Presenter 不得依赖回调存在，直接调 Claim / Delete 也必须正常。
            SilentMailView view = new SilentMailView();
            MailPresenter presenter = new MailPresenter(harness.Bus, view);

            presenter.Refresh();
            presenter.Claim(MailFixture.TodayMailId);
            presenter.Delete(MailFixture.OldestMailId);

            Assert.Equal(3, view.RenderCount);
            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
            Assert.Equal(2, harness.QueryList().Mails.Count);
        }

        /// <summary>按 mailId 取视图行；不存在时直接让用例失败（比断言下标更易读）。</summary>
        private static MailItemViewData RequireMail(MailItemViewData[] mails, int mailId)
        {
            for (int i = 0; i < mails.Length; i++)
            {
                if (mails[i].MailId == mailId)
                {
                    return mails[i];
                }
            }

            throw new InvalidOperationException("邮件不在视图列表中：" + mailId + "。");
        }
    }

    /// <summary>邮件视图假实现：记录每次渲染的列表副本，并提供"人手点击"的触发入口。</summary>
    internal sealed class FakeMailView : IMailView
    {
        public FakeMailView()
        {
            LastMails = Array.Empty<MailItemViewData>();
        }

        /// <inheritdoc />
        public event Action<int>? OnClaimClicked;

        /// <inheritdoc />
        public event Action<int>? OnDeleteClicked;

        /// <summary>渲染次数。</summary>
        public int RenderCount { get; private set; }

        /// <summary>最近一次渲染的邮件列表副本（视图外不可回写）。</summary>
        public MailItemViewData[] LastMails { get; private set; }

        /// <inheritdoc />
        public void RenderMails(IReadOnlyList<MailItemViewData> mails)
        {
            if (mails == null)
            {
                throw new ArgumentNullException(nameof(mails));
            }

            MailItemViewData[] copy = new MailItemViewData[mails.Count];
            for (int i = 0; i < mails.Count; i++)
            {
                copy[i] = mails[i];
            }

            RenderCount++;
            LastMails = copy;
        }

        /// <summary>模拟点击某封邮件的领取按钮。</summary>
        public void RaiseClaimClicked(int mailId)
        {
            Action<int>? handler = OnClaimClicked;
            if (handler != null)
            {
                handler(mailId);
            }
        }

        /// <summary>模拟点击某封邮件的删除按钮。</summary>
        public void RaiseDeleteClicked(int mailId)
        {
            Action<int>? handler = OnDeleteClicked;
            if (handler != null)
            {
                handler(mailId);
            }
        }
    }

    /// <summary>没有任何事件订阅者的视图：事件访问器为空实现，用来验证 Presenter 不依赖回调。</summary>
    internal sealed class SilentMailView : IMailView
    {
        /// <summary>渲染次数。</summary>
        public int RenderCount { get; private set; }

        event Action<int>? IMailView.OnClaimClicked
        {
            add { }
            remove { }
        }

        event Action<int>? IMailView.OnDeleteClicked
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public void RenderMails(IReadOnlyList<MailItemViewData> mails)
        {
            if (mails == null)
            {
                throw new ArgumentNullException(nameof(mails));
            }

            RenderCount++;
        }
    }
}

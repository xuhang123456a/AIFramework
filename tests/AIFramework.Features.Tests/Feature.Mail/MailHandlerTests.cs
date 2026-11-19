using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.Mail;
using Xunit;

namespace AIFramework.Features.Tests.Mail
{
    /// <summary>
    /// 邮件切片业务规则验收：真实 <c>CqeBus</c> 往返 + 假时间源 + 子表字节夹具。
    /// 断言只落在"切片发出了什么消息"与"处理器状态"两处。
    /// </summary>
    public sealed class MailHandlerTests
    {
        /// <summary>夹具起点：2020-03-01 12:00 UTC（取正午以验证 <c>.Date</c> 截断）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2020, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>领取成功的原因标签（契约固定值）。</summary>
        private const int ClaimReasonTagId = 6301;

        [Fact]
        public void Construction_BuildsMailboxWithEveryMailUnread()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            MailListSnapshot list = harness.QueryList();

            Assert.Equal(3, list.Mails.Count);
            for (int i = 0; i < list.Mails.Count; i++)
            {
                Assert.Equal(MailState.Unread, list.Mails[i].State);
            }

            Assert.Equal(MailFixture.OldestMailId, list.Mails[0].MailId);
            Assert.Equal(MailFixture.OldestRewardItemId, list.Mails[0].RewardItemId);
            Assert.Equal(MailFixture.OldestRewardCount, list.Mails[0].RewardCount);
            Assert.Equal(MailFixture.TodayRewardItemId, list.Mails[1].RewardItemId);
            Assert.Equal(MailFixture.TodayRewardCount, list.Mails[1].RewardCount);
            Assert.Equal(MailFixture.RecentRewardItemId, list.Mails[2].RewardItemId);
            Assert.Equal(MailFixture.RecentRewardCount, list.Mails[2].RewardCount);
        }

        [Fact]
        public void Construction_SendsNothingOnBus()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            // 构造期总线可能还没挂上发奖/红点处理器，因此构造期必须零总线调用（红点改由 SeedRedDot 显式点亮）。
            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.RedDots.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            Assert.Empty(harness.Journal);
        }

        [Fact]
        public void Query_OrdersMailsByMailIdAscending_NotByTableRowOrder()
        {
            // 子表物理顺序是 1002 → 1001 → 1003，查询必须输出 1001 → 1002 → 1003。
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            MailListSnapshot list = harness.QueryList();

            Assert.Equal(MailFixture.OldestMailId, list.Mails[0].MailId);
            Assert.Equal(MailFixture.TodayMailId, list.Mails[1].MailId);
            Assert.Equal(MailFixture.RecentMailId, list.Mails[2].MailId);
        }

        [Fact]
        public void SeedRedDot_SendsSingleDeltaEqualToUnreadCount()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SeedRedDot();

            RedDotDeltaCommand delta = Assert.Single(harness.RedDots.Items);
            Assert.Equal(RedDotNodeIds.Mail, delta.NodeId);
            Assert.Equal(3, delta.Delta);
        }

        [Fact]
        public void SeedRedDot_WhenNothingIsUnread_SendsNothing()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            harness.SendClaim(MailFixture.OldestMailId);
            harness.SendClaim(MailFixture.TodayMailId);
            harness.SendClaim(MailFixture.RecentMailId);

            int deltasAfterClaims = harness.RedDots.Count;
            harness.SeedRedDot();

            Assert.Equal(3, deltasAfterClaims);
            Assert.Equal(deltasAfterClaims, harness.RedDots.Count);
            for (int i = 0; i < harness.RedDots.Items.Count; i++)
            {
                Assert.Equal(-1, harness.RedDots.Items[i].Delta);
            }
        }

        [Fact]
        public void SeedRedDot_CountsEveryUnclaimedMail_EvenWhenRowIsAlreadyExpired()
        {
            // 第二行 sendDayOffset = 7：构造当刻就已过期，但它仍是"未领取"，
            // 点亮数必须与后续删除时的 -1 配平（否则红点值会永久跑偏）。
            byte[] payload = new MailPayloadBuilder()
                .Add(MailFixture.OldestMailId, MailFixture.OldestRewardItemId, MailFixture.OldestRewardCount, 0)
                .Add(MailFixture.TodayMailId, MailFixture.TodayRewardItemId, MailFixture.TodayRewardCount, 7)
                .Build();
            FakeMailTableLoader loader = FakeMailTableLoader.WithoutTables().AddTable(MailFixture.TableName, payload);
            MailHarness harness = MailHarness.Create(StartUtc, loader);

            Assert.Equal(MailState.Expired, harness.Handler.GetState(MailFixture.TodayMailId));

            harness.SeedRedDot();

            RedDotDeltaCommand delta = Assert.Single(harness.RedDots.Items);
            Assert.Equal(2, delta.Delta);
        }

        [Fact]
        public void ClaimUnreadMail_GrantsItemPublishesEventAndDecrementsRedDot()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendClaim(MailFixture.TodayMailId);

            GrantItemCommand grant = Assert.Single(harness.Grants.Items);
            Assert.Equal(MailFixture.TodayRewardItemId, grant.ItemId);
            Assert.Equal(MailFixture.TodayRewardCount, grant.Count);
            Assert.Equal(ClaimReasonTagId, grant.ReasonTagId);

            MailClaimedEvent claimed = Assert.Single(harness.ClaimedEvents.Items);
            Assert.Equal(MailFixture.TodayMailId, claimed.MailId);
            Assert.Equal(MailFixture.TodayRewardItemId, claimed.RewardItemId);
            Assert.Equal(MailFixture.TodayRewardCount, claimed.RewardCount);

            RedDotDeltaCommand delta = Assert.Single(harness.RedDots.Items);
            Assert.Equal(RedDotNodeIds.Mail, delta.NodeId);
            Assert.Equal(-1, delta.Delta);

            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
        }

        [Fact]
        public void ClaimUnreadMail_SendsMessagesInContractOrder()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendClaim(MailFixture.TodayMailId);

            // 顺序契约：发奖指令 → 领取事件 → 红点 -1。
            Assert.Equal(3, harness.Journal.Count);
            Assert.IsType<GrantItemCommand>(harness.Journal[0]);
            Assert.IsType<MailClaimedEvent>(harness.Journal[1]);
            Assert.IsType<RedDotDeltaCommand>(harness.Journal[2]);
        }

        [Fact]
        public void ClaimSameMailTwice_IsRejectedSilently()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendClaim(MailFixture.TodayMailId);
            harness.SendClaim(MailFixture.TodayMailId);

            // 第二条命令确实上了总线，但被业务规则静默拒绝。
            Assert.Equal(1, harness.Grants.Count);
            Assert.Equal(1, harness.ClaimedEvents.Count);
            Assert.Equal(1, harness.RedDots.Count);
            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
        }

        [Fact]
        public void ClaimBeforeExpiryBoundary_IsStillAllowed()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            // 有效期 7 天，第 6 天仍在有效期内。
            harness.Time.AdvanceDays(6);

            Assert.Equal(MailState.Unread, harness.Handler.GetState(MailFixture.TodayMailId));

            harness.SendClaim(MailFixture.TodayMailId);

            Assert.Single(harness.Grants.Items);
            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
        }

        [Fact]
        public void ClaimAtExpiryBoundary_IsRejectedAsExpired()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            // 判据是 today - SentDayIndex >= 7，第 7 天当刻即失效。
            harness.Time.AdvanceDays(7);

            Assert.Equal(MailState.Expired, harness.Handler.GetState(MailFixture.TodayMailId));

            harness.SendClaim(MailFixture.TodayMailId);

            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            Assert.Empty(harness.RedDots.Items);
        }

        [Fact]
        public void ClaimAfterEightDays_IsRejectedAndStateIsExpired()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.Time.AdvanceDays(8);

            harness.SendClaim(MailFixture.TodayMailId);

            Assert.Equal(MailState.Expired, harness.Handler.GetState(MailFixture.TodayMailId));
            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            Assert.Empty(harness.RedDots.Items);

            MailListSnapshot list = harness.QueryList();
            for (int i = 0; i < list.Mails.Count; i++)
            {
                Assert.Equal(MailState.Expired, list.Mails[i].State);
            }
        }

        [Fact]
        public void DeleteUnclaimedMail_RemovesItWithoutRewardAndDecrementsRedDot()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendDelete(MailFixture.OldestMailId);

            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);

            RedDotDeltaCommand delta = Assert.Single(harness.RedDots.Items);
            Assert.Equal(RedDotNodeIds.Mail, delta.NodeId);
            Assert.Equal(-1, delta.Delta);

            MailListSnapshot list = harness.QueryList();
            Assert.Equal(2, list.Mails.Count);
            Assert.Equal(MailFixture.TodayMailId, list.Mails[0].MailId);
            Assert.Equal(MailFixture.RecentMailId, list.Mails[1].MailId);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    harness.Handler.GetState(MailFixture.OldestMailId);
                });
        }

        [Fact]
        public void DeleteClaimedMail_IsRejectedWithoutAnyMessage()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);
            harness.SendClaim(MailFixture.TodayMailId);
            int deltasAfterClaim = harness.RedDots.Count;

            harness.SendDelete(MailFixture.TodayMailId);

            // 规则只定义"存在且未领取"的删除路径：已领取邮件不删、不再发红点增量。
            Assert.Equal(deltasAfterClaim, harness.RedDots.Count);
            Assert.Equal(3, harness.QueryList().Mails.Count);
            Assert.Equal(MailState.Claimed, harness.Handler.GetState(MailFixture.TodayMailId));
        }

        [Fact]
        public void ClaimUnknownMailId_IsRejectedWithoutAnyMessage()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendClaim(424242);

            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.ClaimedEvents.Items);
            Assert.Empty(harness.RedDots.Items);
            Assert.Equal(3, harness.QueryList().Mails.Count);
        }

        [Fact]
        public void DeleteUnknownMailId_IsRejectedWithoutAnyMessage()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            harness.SendDelete(424242);

            Assert.Empty(harness.Grants.Items);
            Assert.Empty(harness.RedDots.Items);
            Assert.Equal(3, harness.QueryList().Mails.Count);
        }

        [Fact]
        public void GetState_UnknownMailId_ThrowsArgumentOutOfRange()
        {
            MailHarness harness = MailHarness.CreateWithDefaultTable(StartUtc);

            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    harness.Handler.GetState(424242);
                });

            Assert.Contains("424242", exception.Message);
        }

        [Fact]
        public void Tables_ExposePhysicalRowsAndRejectOutOfRangeIndex()
        {
            FakeMailTableLoader loader = FakeMailTableLoader.WithDefaultMailTable();

            MailTables tables = new MailTables(loader);

            Assert.Equal(3, tables.Count);
            Assert.Equal(MailFixture.TodayMailId, tables.Get(0).MailId);
            Assert.Equal(MailFixture.OldestMailId, tables.Get(1).MailId);
            Assert.Equal(MailFixture.RecentMailId, tables.Get(2).MailId);
            Assert.Equal(0, tables.Get(0).SendDayOffset);
            Assert.Equal(1, tables.Get(1).SendDayOffset);
            Assert.Equal(2, tables.Get(2).SendDayOffset);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(-1);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(3);
                });

            // 按名字取表，不会退化成"随便找一张表"。
            Assert.Single(loader.RequestedTables);
            Assert.Equal(MailFixture.TableName, loader.RequestedTables[0]);
        }

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            FakeMailTableLoader loader = FakeMailTableLoader.WithoutTables();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new MailTables(loader);
                });
            Assert.Contains(MailFixture.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new MailHandler(new CqeBus(), new FakeMailTimeProvider(StartUtc), loader);
                });
            Assert.Contains(MailFixture.TableName, fromHandler.Message);

            Assert.Equal(2, loader.RequestedTables.Count);
            for (int i = 0; i < loader.RequestedTables.Count; i++)
            {
                Assert.Equal(MailFixture.TableName, loader.RequestedTables[i]);
            }
        }

        [Fact]
        public void LengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 3 行，实际只写 2 行：长度 4 + 32 = 36，不等于 4 + 3 * 16 = 52。
            byte[] payload = new MailPayloadBuilder()
                .Add(MailFixture.OldestMailId, MailFixture.OldestRewardItemId, MailFixture.OldestRewardCount, 0)
                .Add(MailFixture.TodayMailId, MailFixture.TodayRewardItemId, MailFixture.TodayRewardCount, 1)
                .BuildDeclaringRows(3);
            FakeMailTableLoader loader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, payload);

            InvalidDataException mismatch = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(loader);
                });
            Assert.Contains("长度", mismatch.Message);

            // 长度不符的极端形态：连 4 字节头部都不够。
            FakeMailTableLoader shortLoader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, new byte[3]);
            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(shortLoader);
                });

            // 头部行数为负：同样必须显式失败，不能当成空表。
            FakeMailTableLoader negativeLoader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, new MailPayloadBuilder().BuildDeclaringRows(-1));
            InvalidDataException negative = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(negativeLoader);
                });
            Assert.Contains("负", negative.Message);
        }

        [Fact]
        public void DuplicateMailId_ThrowsInvalidDataException()
        {
            byte[] payload = new MailPayloadBuilder()
                .Add(MailFixture.OldestMailId, MailFixture.OldestRewardItemId, MailFixture.OldestRewardCount, 0)
                .Add(MailFixture.OldestMailId, MailFixture.TodayRewardItemId, MailFixture.TodayRewardCount, 1)
                .Build();
            FakeMailTableLoader loader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(loader);
                });

            Assert.Contains("重复", exception.Message);
        }

        [Fact]
        public void NonPositiveRewardCount_ThrowsInvalidDataException()
        {
            byte[] zeroCount = new MailPayloadBuilder()
                .Add(MailFixture.OldestMailId, MailFixture.OldestRewardItemId, 0, 0)
                .Build();
            FakeMailTableLoader zeroLoader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, zeroCount);

            InvalidDataException zero = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(zeroLoader);
                });
            Assert.Contains("rewardCount", zero.Message);

            byte[] negativeCount = new MailPayloadBuilder()
                .Add(MailFixture.OldestMailId, MailFixture.OldestRewardItemId, -5, 0)
                .Build();
            FakeMailTableLoader negativeLoader = FakeMailTableLoader.WithoutTables()
                .AddTable(MailFixture.TableName, negativeCount);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MailTables(negativeLoader);
                });
        }
    }
}

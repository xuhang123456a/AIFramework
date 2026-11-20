#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="LeaderboardPager"/> 的行为契约验收：分页归一化、同分裁决、selfRow 与"我的排名"定位、
    /// 赛季归档、提交闸门、后端不可用/违约降级。
    /// <para>
    /// <b>这些用例守的是"名次必须可复现"</b>：排行榜直接决定发奖与称号，客户端多算一名/少算一名
    /// 都是运营事故。因此断言分四类——钉死顺序（分数降序 + 同分按玩家 id 升序，与提交顺序无关）、
    /// 钉死分页契约（越界空页但总人数有效、limit/offset 夹紧）、钉死"我的排名"的两次请求定位公式、
    /// 钉死赛季边界（切季前/后各一次）。
    /// </para>
    /// <para>
    /// 替身刻意只用"可拨动的假后端"：真实服务端的分页实现、并发提交、赛季定时任务在真机上极难复现，
    /// 只能靠假后端逐条钉住。假后端<b>复用</b> <see cref="LeaderboardPager.CompareRows"/> 产出顺序，
    /// 保证"客户端认定的同分顺序"与"服务端认定的"是同一套规则，而不是两份各写一遍的比较器。
    /// </para>
    /// <para>
    /// <b>真机未验证</b>：真实后端的排序/分页语义、防刷（重复提交、改包提交）、赛季切换的实际时刻，
    /// 本套用例只能验证客户端侧的契约与降级，服务端行为需要在联调环境另行验收。
    /// </para>
    /// </summary>
    public sealed class LeaderboardPagerTests
    {
        /// <summary>默认测试榜人数（25 人足够覆盖首页/中间页/末页/越界四种位置）。</summary>
        private const int DefaultBoardSize = 25;

        // ---------------------------------------------------------------- 构造与上限

        [Fact]
        public void Constructor_NullServer_Throws()
        {
            // 没有后端就没有分页可言：装配期编程错误必须立刻响（与 FriendService 同一口径）。
            Assert.Throws<ArgumentNullException>(() => new LeaderboardPager(null!));
        }

        [Fact]
        public void Constructor_DefaultMaxLimit_IsHundred()
        {
            var pager = new LeaderboardPager(new FakeLeaderboardServer());

            Assert.Equal(100, LeaderboardPager.DefaultMaxLimit);
            Assert.Equal(LeaderboardPager.DefaultMaxLimit, pager.MaxLimit);
        }

        [Fact]
        public void Constructor_NonPositiveMaxLimit_FailsClosedToOne()
        {
            // 上限配置事故不能变成"不设上限"：按 1 处理（仍然能翻页），而不是让后端被一页一万条打爆。
            Assert.Equal(1, new LeaderboardPager(new FakeLeaderboardServer(), 0).MaxLimit);
            Assert.Equal(1, new LeaderboardPager(new FakeLeaderboardServer(), -7).MaxLimit);
        }

        [Fact]
        public void Constructor_ExposesServerInstance()
        {
            var fake = new FakeLeaderboardServer();
            var pager = new LeaderboardPager(fake);

            // 赛季切换与当前赛季读数不在分页类包装范围内，装配入口要能经 Server 拿到后端。
            Assert.Same(fake, pager.Server);
        }

        // ---------------------------------------------------------------- 排序

        [Fact]
        public void FetchPage_OrdersByScoreDescending()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, 10);
            fake.Seed(1, 2, 30);
            fake.Seed(1, 3, 20);

            LeaderboardPage page = new LeaderboardPager(fake).Fetch(Request(1, 0, 10));

            Assert.Equal(new long[] { 2, 3, 1 }, Ids(page.Rows));
            Assert.Equal(new[] { 30, 20, 10 }, Scores(page.Rows));
            Assert.Equal(new[] { 1, 2, 3 }, Ranks(page.Rows));
        }

        [Fact]
        public void FetchPage_TiedScores_OrderByPlayerIdAscending()
        {
            var fake = new FakeLeaderboardServer();

            // 刻意按 id 乱序提交：同分顺序必须由 id 决定，而不是"谁先提交谁在前"。
            fake.Seed(1, 9, 500);
            fake.Seed(1, 3, 500);
            fake.Seed(1, 5, 500);
            fake.Seed(1, 1, 900);

            LeaderboardPage page = new LeaderboardPager(fake).Fetch(Request(1, 0, 10));

            Assert.Equal(new long[] { 1, 3, 5, 9 }, Ids(page.Rows));
            Assert.Equal(new[] { 900, 500, 500, 500 }, Scores(page.Rows));
        }

        [Fact]
        public void FetchPage_TiedScores_OrderIsIndependentOfSubmissionOrder()
        {
            var ascending = new FakeLeaderboardServer();
            ascending.Seed(1, 1, 100);
            ascending.Seed(1, 2, 100);
            ascending.Seed(1, 3, 100);

            var descending = new FakeLeaderboardServer();
            descending.Seed(1, 3, 100);
            descending.Seed(1, 2, 100);
            descending.Seed(1, 1, 100);

            var pager = new LeaderboardPager(ascending);
            var reversePager = new LeaderboardPager(descending);

            // 同分按玩家 id 是为了可复现，而不是按提交时间：两种提交顺序必须得到同一顺序。
            Assert.Equal(Ids(pager.Fetch(Request(1, 0, 10)).Rows), Ids(reversePager.Fetch(Request(1, 0, 10)).Rows));
        }

        [Fact]
        public void FetchPage_NegativeScores_RankBelowZeroScores()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, -5);
            fake.Seed(1, 2, 0);
            fake.Seed(1, 3, -10);
            fake.Seed(1, 4, 7);

            LeaderboardPage page = new LeaderboardPager(fake).Fetch(Request(1, 0, 10));

            // 负分是合法成绩：按数值降序排，不能因为"负"就被当成脏数据丢掉或排到最前面。
            Assert.Equal(new long[] { 4, 2, 1, 3 }, Ids(page.Rows));
            Assert.Equal(new[] { 7, 0, -5, -10 }, Scores(page.Rows));
        }

        [Fact]
        public void FetchPage_TiedScores_GetSequentialRanks()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 7, 100);
            fake.Seed(1, 8, 100);
            fake.Seed(1, 9, 100);

            LeaderboardPage page = new LeaderboardPager(fake).Fetch(Request(1, 0, 10));

            // 同分不并列：名次要能唯一反解出"从第几条开始翻页"，1/1/3 会让定位公式失去唯一解。
            Assert.Equal(new[] { 1, 2, 3 }, Ranks(page.Rows));
        }

        [Fact]
        public void CompareRows_OrdersByScoreThenPlayerId()
        {
            var high = new LeaderboardRow(1, "a", 100, 1, 0);
            var low = new LeaderboardRow(1, "a", 50, 2, 0);
            var highOtherPlayer = new LeaderboardRow(2, "b", 100, 1, 0);

            Assert.True(LeaderboardPager.CompareRows(high, low) < 0);
            Assert.True(LeaderboardPager.CompareRows(low, high) > 0);
            Assert.True(LeaderboardPager.CompareRows(high, highOtherPlayer) < 0);
            Assert.True(LeaderboardPager.CompareRows(highOtherPlayer, high) > 0);

            // 名次/展示名/时间都不参与比较：同分同 id 即等价（正常榜单里不会出现重复行）。
            Assert.Equal(0, LeaderboardPager.CompareRows(high, new LeaderboardRow(1, "z", 100, 99, 12345)));
        }

        [Fact]
        public void CompareRows_NegativeScores_CompareNumerically()
        {
            var zero = new LeaderboardRow(1, "a", 0, 1, 0);
            var negative = new LeaderboardRow(2, "b", -1, 2, 0);

            Assert.True(LeaderboardPager.CompareRows(zero, negative) < 0);
            Assert.True(LeaderboardPager.CompareRows(negative, zero) > 0);
        }

        [Fact]
        public void FetchPage_RepeatedRequests_ReturnSameOrder()
        {
            var pager = new LeaderboardPager(RankedBoard());

            long[] first = Ids(pager.Fetch(Request(1, 0, 10)).Rows);
            long[] second = Ids(pager.Fetch(Request(1, 0, 10)).Rows);

            // "同一个榜两次查询顺序不同"是最难复现的一类 bug；同分裁决必须让重算结果逐字相同。
            Assert.Equal(first, second);
        }

        // ---------------------------------------------------------------- 分页

        [Fact]
        public void FetchPage_FirstPage_ReturnsLeadingRowsWithGlobalRanks()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            Assert.Equal(0, page.Offset);
            Assert.Equal(5, page.Limit);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(page.Rows));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Ranks(page.Rows));
            Assert.Equal(new[] { 2500, 2400, 2300, 2200, 2100 }, Scores(page.Rows));
        }

        [Fact]
        public void FetchPage_MiddlePage_KeepsGlobalRanks()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 10, 5));

            // 名次是全局名次，不是页内序号：UI 的服务端排序全靠这一点，错了就会每页都从 1 开始。
            Assert.Equal(10, page.Offset);
            Assert.Equal(new long[] { 11, 12, 13, 14, 15 }, Ids(page.Rows));
            Assert.Equal(new[] { 11, 12, 13, 14, 15 }, Ranks(page.Rows));
            Assert.Equal(new[] { 1500, 1400, 1300, 1200, 1100 }, Scores(page.Rows));
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchPage_LastPage_ReturnsRemainder()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 22, 5));

            Assert.Equal(3, page.Rows.Length);
            Assert.Equal(new long[] { 23, 24, 25 }, Ids(page.Rows));
            Assert.Equal(new[] { 23, 24, 25 }, Ranks(page.Rows));
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchPage_OffsetAtTotalCount_ReturnsEmptyPageWithValidTotal()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, DefaultBoardSize, 5));

            Assert.Empty(page.Rows);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchPage_OffsetBeyondTotalCount_ReturnsEmptyPageWithValidTotal()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 40, 5));

            // 越界不是错误：空 rows + 有效 totalCount，UI 才能区分"翻过头"与"榜是空的"。
            Assert.Empty(page.Rows);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
            Assert.Equal(40, page.Offset);
        }

        [Fact]
        public void FetchPage_HugeOffset_DoesNotOverflowOrThrow()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, int.MaxValue, 5));

            Assert.Empty(page.Rows);
            Assert.Equal(int.MaxValue, page.Offset);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchPage_LimitAboveMaxLimit_IsClamped()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake, maxLimit: 10);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 1000));

            Assert.Equal(10, page.Rows.Length);
            Assert.Equal(10, page.Limit);
            Assert.Equal(10, fake.Requests[0].Limit);
        }

        [Fact]
        public void FetchPage_ZeroLimit_IsClampedToSingleRow()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 0));

            // limit=0 是"只想看一条"的写法；按 1 处理，而不是回空页（空页与"榜是空的"无法区分）。
            Assert.Single(page.Rows);
            Assert.Equal(1, page.Limit);
            Assert.Equal(1, fake.Requests[0].Limit);
        }

        [Fact]
        public void FetchPage_NegativeLimit_IsClampedToSingleRow()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, -9));

            Assert.Single(page.Rows);
            Assert.Equal(1, fake.Requests[0].Limit);
        }

        [Fact]
        public void FetchPage_NegativeOffset_IsClampedToZero()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, -20, 5));

            Assert.Equal(0, page.Offset);
            Assert.Equal(0, fake.Requests[0].Offset);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(page.Rows));
        }

        [Fact]
        public void FetchPage_EmptyBoard_ReturnsZeroTotalAndNoRows()
        {
            var pager = new LeaderboardPager(new FakeLeaderboardServer());

            LeaderboardPage page = pager.Fetch(Request(1, 0, 10));

            Assert.Empty(page.Rows);
            Assert.Equal(0, page.TotalCount);
            Assert.False(page.SelfRow.HasValue);
        }

        [Fact]
        public void FetchPage_UnknownLeaderboardId_ReturnsEmptyBoard()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(999, 0, 10));

            // 榜单不存在与"榜是空的"在数据上同形：不抛异常、空 rows + 0 总人数。
            Assert.Empty(page.Rows);
            Assert.Equal(0, page.TotalCount);
        }

        [Fact]
        public void FetchPage_EchoesLeaderboardIdAndSeason()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(SeasonRequest(1, LeaderboardSeason.Previous, 0, 5));

            Assert.Equal(1, page.LeaderboardId);
            Assert.Equal(LeaderboardSeason.Previous, page.Season);
            Assert.Equal(0, page.TotalCount);
        }

        [Fact]
        public void FetchPage_UnknownSeasonValue_FallsBackToCurrent()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            // 反序列化出一个坏字节时不能读到归档数据：按当前季处理。
            LeaderboardPage page = pager.Fetch(SeasonRequest(1, (LeaderboardSeason)9, 0, 5));

            Assert.Equal(LeaderboardSeason.Current, fake.Requests[0].Season);
            Assert.Equal(LeaderboardSeason.Current, page.Season);
            Assert.Equal(5, page.Rows.Length);
        }

        [Fact]
        public void FetchPage_LyingServerLimit_IsBoundedByRequestLimit()
        {
            var fake = RankedBoard();
            fake.ServerReportedLimit = 999; // 违约实现回显一个比请求更大的上限
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            // 直接采信会让调用方一次跳过头：本页上限取 min(请求, 回显)。
            Assert.Equal(5, page.Limit);
        }

        [Fact]
        public void FetchPage_ServerAppliesSmallerLimit_UsesServerLimit()
        {
            var fake = RankedBoard();
            fake.ServerAppliedLimit = 2; // 服务端自己的单页上限比客户端小
            var pager = new LeaderboardPager(fake);

            LeaderboardPage first = pager.Fetch(Request(1, 0, 5));

            // 服务端只给 2 条却回显 5，会让 offset += 5 的人跳过第 3、4 名：本页上限必须是 2。
            Assert.Equal(2, first.Rows.Length);
            Assert.Equal(2, first.Limit);
            Assert.Equal(new long[] { 1, 2 }, Ids(first.Rows));

            LeaderboardPage second = pager.Fetch(Request(1, first.Offset + first.Limit, first.Limit));
            Assert.Equal(new long[] { 3, 4 }, Ids(second.Rows));
        }

        // ---------------------------------------------------------------- selfRow（我的排名）

        [Fact]
        public void FetchPage_PlayerOnBoard_PopulatesSelfRow()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 10, 5, playerId: 13));

            Assert.True(page.SelfRow.HasValue);
            LeaderboardRow self = Self(page);
            Assert.Equal(13L, self.PlayerId);
            Assert.Equal(13, self.Rank);
            Assert.Equal(1300, self.Score);
        }

        [Fact]
        public void FetchPage_PlayerNotOnBoard_SelfRowIsNull()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5, playerId: 999));

            Assert.False(page.SelfRow.HasValue);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchPage_SelfRowMatchesVisibleRow()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 10, 5, playerId: 13));

            LeaderboardRow visible = page.Rows[2];
            LeaderboardRow self = Self(page);

            // selfRow 与页内同一行必须逐字段一致，否则"我的排名"卡片会和列表位置打架。
            Assert.Equal(visible.PlayerId, self.PlayerId);
            Assert.Equal(visible.Score, self.Score);
            Assert.Equal(visible.Rank, self.Rank);
        }

        [Fact]
        public void FetchPage_SelfRowRank_IsAvailableOutsideWindow()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(Request(1, 0, 3, playerId: 20));

            // 服务端即使不把第 20 名放进本页，也应给出 selfRow：一次请求就能拿到"我的排名"。
            Assert.Equal(3, page.Rows.Length);
            Assert.True(page.SelfRow.HasValue);
            Assert.Equal(20, Self(page).Rank);
        }

        [Fact]
        public void FetchPage_ServerWithoutSelfRow_LeavesItNullForDeepPlayer()
        {
            var fake = RankedBoard();
            fake.SupportsSelfRow = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 10, 5, playerId: 13));

            // 普通 Fetch 不改写服务端字段：后端不填 selfRow 就是 null（补 selfRow 只发生在 FetchAroundSelf）。
            Assert.False(page.SelfRow.HasValue);
            Assert.Equal(13L, page.Rows[2].PlayerId);
        }

        [Fact]
        public void FetchPage_PlayerIdIsPassedToServer()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5, playerId: 7));

            Assert.Equal(7L, fake.Requests[0].PlayerId);
            Assert.Equal(7L, Self(page).PlayerId);
        }

        // ---------------------------------------------------------------- FetchAroundSelf（我的排名入口）

        [Fact]
        public void FetchAroundSelf_PlayerInFirstWindow_ReturnsFirstPage()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 3, radius: 2);

            Assert.Equal(0, page.Offset);
            Assert.Equal(5, page.Limit);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(page.Rows));
            Assert.Equal(3, Self(page).Rank);
            Assert.Equal(1, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_PlayerDeeper_RequestsPageCenteredOnRank()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 13, radius: 2);

            // 契约公式 offset = rank - radius：rank 13、radius 2 → 11，自己落在页内下标 radius-1 = 1。
            Assert.Equal(11, page.Offset);
            Assert.Equal(11, fake.Requests[1].Offset);
            Assert.Equal(5, fake.Requests[1].Limit);
            Assert.Equal(new long[] { 12, 13, 14, 15, 16 }, Ids(page.Rows));
            Assert.Equal(1, IndexOfPlayer(page.Rows, 13));
            Assert.Equal(13, Self(page).Rank);
            Assert.Equal(2, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_RadiusZero_IsClampedToOne()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 2, radius: 0);

            // 一页只回一行会让 UI 画不出前后各是谁：radius 0 按 1（窗口 3 条）处理。
            Assert.Equal(3, fake.Requests[0].Limit);
            Assert.Equal(3, page.Limit);
            Assert.Equal(new long[] { 1, 2, 3 }, Ids(page.Rows));
            Assert.Equal(2, Self(page).Rank);
        }

        [Fact]
        public void FetchAroundSelf_RadiusAboveMaxLimit_IsClamped()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake, maxLimit: 10);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 3, radius: 1000);

            // radius 夹到 maxLimit(10) → 窗口 2*10+1 再被 maxLimit 夹成 10。
            Assert.Equal(10, fake.Requests[0].Limit);
            Assert.Equal(10, page.Limit);
            Assert.Equal(10, page.Rows.Length);
            Assert.Equal(3, Self(page).Rank);
        }

        [Fact]
        public void FetchAroundSelf_SecondRequestStillMissing_ReturnsEmptyPage()
        {
            var fake = RankedBoard();
            fake.ForcedSelfRank = 3; // 服务端声称自己在第 3 名，但榜上其实没有这个人
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 999, radius: 2);

            Assert.Equal(1, page.Offset);
            Assert.Empty(page.Rows);
            Assert.False(page.SelfRow.HasValue);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
            Assert.Equal(2, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_ServerWithoutSelfRow_ProbesNextWindow()
        {
            var fake = RankedBoard();
            fake.SupportsSelfRow = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 8, radius: 2);

            // 无 selfRow 的后端：名次无从得知，只能向下一页线性探测一次（offset = 首页条数）。
            Assert.Equal(5, fake.Requests[1].Offset);
            Assert.Equal(5, page.Offset);
            Assert.Equal(new long[] { 6, 7, 8, 9, 10 }, Ids(page.Rows));
            Assert.Equal(8, Self(page).Rank);
            Assert.Equal(2, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_ServerWithoutSelfRow_ProbesFromActualPageSize()
        {
            var fake = RankedBoard();
            fake.SupportsSelfRow = false;
            fake.ServerAppliedLimit = 2; // 服务端只给 2 条/页，比请求的窗口 5 小
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 4, radius: 2);

            // 探测起点必须是首页"实际返回的 2 条"，按请求窗口 5 推进就会跳过第 3、4 名。
            Assert.Equal(2, fake.Requests[1].Offset);
            Assert.Equal(2, page.Offset);
            Assert.Equal(2, page.Limit);
            Assert.Equal(new long[] { 3, 4 }, Ids(page.Rows));
            Assert.Equal(4, Self(page).Rank);
            Assert.Equal(2, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_ServerWithoutSelfRow_StopsAfterTwoRequests()
        {
            var fake = RankedBoard();
            fake.SupportsSelfRow = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 20, radius: 2);

            // 有界探测只救得回"差一页"的中游玩家；更深的玩家在无 selfRow 后端上无法定位——
            // 宁可显示"暂无排名"，也不能让客户端无限翻页把后端打爆。
            Assert.Equal(2, fake.FetchCallCount);
            Assert.Empty(page.Rows);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
        }
        [Fact]
        public void FetchAroundSelf_ServerWithoutSelfRow_SkipsProbeWhenBoardExhausted()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, 300);
            fake.Seed(1, 2, 200);
            fake.Seed(1, 3, 100);
            fake.SupportsSelfRow = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 999, radius: 2);

            // 首页没装满就说明榜已到底，第二次请求没有意义，省掉。
            Assert.Equal(1, fake.FetchCallCount);
            Assert.Empty(page.Rows);
            Assert.Equal(3, page.TotalCount);
        }

        [Fact]
        public void FetchAroundSelf_PlayerNotOnBoard_ReturnsEmptyPage()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 777, radius: 2);

            Assert.Empty(page.Rows);
            Assert.False(page.SelfRow.HasValue);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void FetchAroundSelf_EmptyBoard_ReturnsEmptyPageWithoutSecondRequest()
        {
            var fake = new FakeLeaderboardServer();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 1, radius: 2);

            Assert.Equal(1, fake.FetchCallCount);
            Assert.Empty(page.Rows);
            Assert.Equal(0, page.TotalCount);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
        }

        [Fact]
        public void FetchAroundSelf_InvalidLeaderboard_ReturnsEmptyPageWithoutBackendCall()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(0, playerId: 1, radius: 2);

            Assert.Equal(0, fake.FetchCallCount);
            Assert.Empty(page.Rows);
            Assert.Equal(LeaderboardPager.ReasonInvalidLeaderboard, page.Reason);
        }

        [Fact]
        public void FetchAroundSelf_Unavailable_ReturnsEmptyPageWithoutBackendCall()
        {
            var fake = RankedBoard();
            fake.Available = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 1, radius: 2);

            Assert.Equal(0, fake.FetchCallCount);
            Assert.Empty(page.Rows);
            Assert.Equal(LeaderboardPager.ReasonUnavailable, page.Reason);
        }

        [Fact]
        public void FetchAroundSelf_ThrowingServer_ReturnsEmptyPageWithThrowReason()
        {
            var fake = RankedBoard();
            fake.ThrowOnFetch = true;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 1, radius: 2);

            // 后端违约抛异常也必须降级成空页，绝不让一次榜单抖动把玩家打断。
            Assert.Empty(page.Rows);
            Assert.Equal(
                LeaderboardPager.ReasonServiceThrew + ": InvalidOperationException",
                page.Reason);
            Assert.Equal(1, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_AtMostTwoRequests()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: DefaultBoardSize, radius: 1);

            // 末名玩家：rank 25、radius 1 → offset 24，页内只剩自己一条。
            Assert.Equal(24, page.Offset);
            Assert.Single(page.Rows);
            Assert.Equal(DefaultBoardSize, Self(page).Rank);
            Assert.Equal(2, fake.FetchCallCount);
        }

        [Fact]
        public void FetchAroundSelf_ReadsCurrentSeasonOnly()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, 100);
            fake.RotateSeason();          // 玩家 1 归档到上一季
            fake.TrySubmit(1, 2, 50);     // 新赛季只有玩家 2
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 1, radius: 2);

            // "我的排名"永远看还能继续刷的那个榜：上一季只读归档，不参与定位。
            Assert.Empty(page.Rows);
            Assert.False(page.SelfRow.HasValue);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
        }

        // ---------------------------------------------------------------- 提交

        [Fact]
        public void TrySubmit_HigherScore_UpdatesBoard()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            Assert.True(pager.TrySubmit(1, playerId: 42, score: 9999));

            LeaderboardPage page = pager.Fetch(Request(1, 0, 3, playerId: 42));
            Assert.Equal(DefaultBoardSize + 1, page.TotalCount);
            Assert.Equal(42L, page.Rows[0].PlayerId);
            Assert.Equal(1L, page.Rows[1].PlayerId);
            Assert.Equal(42L, Self(page).PlayerId);
            Assert.Equal(1, Self(page).Rank);
        }

        [Fact]
        public void TrySubmit_LowerScore_IsRejectedAndBoardUnchanged()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            Assert.False(pager.TrySubmit(1, playerId: 1, score: 10));

            LeaderboardPage page = pager.Fetch(Request(1, 0, 1, playerId: 1));
            Assert.Equal(2500, Self(page).Score);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void TrySubmit_EqualScore_IsNotAPersonalBest()
        {
            var pager = new LeaderboardPager(RankedBoard());

            Assert.False(pager.TrySubmit(1, playerId: 1, score: 2500));
        }

        [Fact]
        public void TrySubmit_ServerAllowsOverwrite_AcceptsLowerScore()
        {
            var fake = RankedBoard();
            fake.AllowOverwrite = true; // "最近一次成绩"型榜单
            var pager = new LeaderboardPager(fake);

            Assert.True(pager.TrySubmit(1, playerId: 1, score: 10));

            LeaderboardPage page = pager.Fetch(Request(1, 0, DefaultBoardSize, playerId: 1));
            Assert.Equal(10, Self(page).Score);
        }

        [Fact]
        public void TrySubmit_NegativeScore_RejectedWithoutBackendCall()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            // 负分"提交"几乎一定是调用方传错了（未初始化变量/把扣分当得分），本地直接拦下。
            Assert.False(pager.TrySubmit(1, playerId: 1, score: -1));
            Assert.Equal(0, fake.SubmitCallCount);
        }

        [Fact]
        public void TrySubmit_InvalidLeaderboard_RejectedWithoutBackendCall()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            Assert.False(pager.TrySubmit(0, playerId: 1, score: 100));
            Assert.False(pager.TrySubmit(-3, playerId: 1, score: 100));
            Assert.Equal(0, fake.SubmitCallCount);
        }

        [Fact]
        public void TrySubmit_Unavailable_ReturnsFalseWithoutBackendCall()
        {
            var fake = RankedBoard();
            fake.Available = false;
            var pager = new LeaderboardPager(fake);

            Assert.False(pager.TrySubmit(1, playerId: 1, score: 100));
            Assert.Equal(0, fake.SubmitCallCount);
        }

        [Fact]
        public void TrySubmit_AvailabilityThrows_ReturnsFalse()
        {
            var fake = RankedBoard();
            fake.ThrowOnIsAvailable = true;
            var pager = new LeaderboardPager(fake);

            // 可用性读取本身违约时按"不可用"处理：降级总比崩溃好。
            Assert.False(pager.TrySubmit(1, playerId: 1, score: 100));
            Assert.Equal(0, fake.SubmitCallCount);
        }

        [Fact]
        public void TrySubmit_ServerThrows_ReturnsFalse()
        {
            var fake = RankedBoard();
            fake.ThrowOnSubmit = true;
            var pager = new LeaderboardPager(fake);

            Assert.False(pager.TrySubmit(1, playerId: 1, score: 100));
            Assert.Equal(1, fake.SubmitCallCount);
        }

        [Fact]
        public void TrySubmit_NonPositivePlayerId_DoesNotThrow()
        {
            var fake = new FakeLeaderboardServer();
            var pager = new LeaderboardPager(fake);

            // 非正 playerId 允许传（归属判定交给后端）；假实现把 <= 0 归一为 0，关键是客户端不崩。
            Assert.True(pager.TrySubmit(1, playerId: -5, score: 10));

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5, playerId: 0));
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(0L, Self(page).PlayerId);
        }

        // ---------------------------------------------------------------- 赛季归档

        [Fact]
        public void FetchPage_PreviousSeason_IsEmptyBeforeRotation()
        {
            var pager = new LeaderboardPager(RankedBoard());

            LeaderboardPage page = pager.Fetch(SeasonRequest(1, LeaderboardSeason.Previous, 0, 5));

            // 从未切过赛季：上一季是空榜，不是错误（UI 显示"暂无上季数据"）。
            Assert.Empty(page.Rows);
            Assert.Equal(0, page.TotalCount);
            Assert.False(page.SelfRow.HasValue);
        }

        [Fact]
        public void RotateSeason_MovesCurrentIntoPreviousAndClearsCurrent()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            pager.Server.RotateSeason();

            LeaderboardPage current = pager.Fetch(Request(1, 0, 5));
            LeaderboardPage previous = pager.Fetch(SeasonRequest(1, LeaderboardSeason.Previous, 0, 5));

            Assert.Empty(current.Rows);
            Assert.Equal(0, current.TotalCount);

            Assert.Equal(DefaultBoardSize, previous.TotalCount);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(previous.Rows));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Ranks(previous.Rows));
            Assert.Equal(0, fake.CountOf(1, LeaderboardSeason.Current));
            Assert.Equal(DefaultBoardSize, fake.CountOf(1, LeaderboardSeason.Previous));
        }

        [Fact]
        public void RotateSeason_PreviousSeasonKeepsRanksAndSelfRow()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            pager.Server.RotateSeason();

            LeaderboardPage previous = pager.Fetch(
                SeasonRequest(1, LeaderboardSeason.Previous, 10, 5, playerId: 13));

            Assert.Equal(13, Self(previous).Rank);
            Assert.Equal(13L, previous.Rows[2].PlayerId);
            Assert.Equal(new[] { 11, 12, 13, 14, 15 }, Ranks(previous.Rows));
        }

        [Fact]
        public void RotateSeason_Twice_KeepsOnlyLatestArchive()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, 300);
            fake.Seed(1, 2, 200);
            fake.Seed(1, 3, 100);
            var pager = new LeaderboardPager(fake);

            pager.Server.RotateSeason();

            fake.TrySubmit(1, 4, 50);
            fake.TrySubmit(1, 5, 40);

            pager.Server.RotateSeason();

            // 只保留最近一季：第二次归档覆盖第一次（更早的赛季属于"荣誉墙"，另开历史端口）。
            LeaderboardPage previous = pager.Fetch(
                SeasonRequest(1, LeaderboardSeason.Previous, 0, 10));
            Assert.Equal(2, previous.TotalCount);
            Assert.Equal(new long[] { 4, 5 }, Ids(previous.Rows));
            Assert.Equal(0, fake.CountOf(1, LeaderboardSeason.Current));
        }

        [Fact]
        public void RotateSeason_KeepsCurrentSeasonReadout()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            Assert.Equal(LeaderboardSeason.Current, pager.Server.CurrentSeason);

            pager.Server.RotateSeason();

            // 提交永远进当前季桶；当前赛季读数因此恒为 Current（赛季编号策略归服务端）。
            Assert.Equal(LeaderboardSeason.Current, pager.Server.CurrentSeason);
        }

        [Fact]
        public void RotateSeason_SubmissionsAfterRotationGoToNewCurrent()
        {
            var fake = new FakeLeaderboardServer();
            fake.Seed(1, 1, 100);
            fake.RotateSeason();
            var pager = new LeaderboardPager(fake);

            Assert.True(pager.TrySubmit(1, playerId: 2, score: 50));

            LeaderboardPage current = pager.Fetch(Request(1, 0, 10));
            LeaderboardPage previous = pager.Fetch(SeasonRequest(1, LeaderboardSeason.Previous, 0, 10));

            Assert.Equal(1, current.TotalCount);
            Assert.Equal(2L, current.Rows[0].PlayerId);
            Assert.Equal(1, previous.TotalCount);
            Assert.Equal(1L, previous.Rows[0].PlayerId);
        }

        // ---------------------------------------------------------------- 失败原因与实现违约

        [Fact]
        public void Fetch_InvalidLeaderboard_ReturnsStableReason()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(0, 0, 5));

            // 文案进日志/埋点，锁定它与常量，避免有人改成"invalid board"把告警规则打断。
            Assert.Equal("invalid leaderboard", page.Reason);
            Assert.Equal(LeaderboardPager.ReasonInvalidLeaderboard, page.Reason);
            Assert.Empty(page.Rows);
            Assert.Equal(0, page.TotalCount);
            Assert.Equal(0, fake.FetchCallCount);
        }

        [Fact]
        public void Fetch_Unavailable_ReturnsStableReason()
        {
            var fake = RankedBoard();
            fake.Available = false;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            Assert.Equal("leaderboard unavailable", page.Reason);
            Assert.Equal(LeaderboardPager.ReasonUnavailable, page.Reason);
            Assert.Empty(page.Rows);
            Assert.Equal(0, fake.FetchCallCount);
        }

        [Fact]
        public void Fetch_AvailabilityThrows_DegradesToUnavailable()
        {
            var fake = RankedBoard();
            fake.ThrowOnIsAvailable = true;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            Assert.Equal(LeaderboardPager.ReasonUnavailable, page.Reason);
            Assert.Equal(0, fake.FetchCallCount);
        }

        [Fact]
        public void Fetch_ThrowingServer_ReturnsEmptyPageWithExceptionType()
        {
            var fake = RankedBoard();
            fake.ThrowOnFetch = true;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            Assert.Empty(page.Rows);
            Assert.Equal(
                "leaderboard service threw: InvalidOperationException",
                page.Reason);
            Assert.Equal(0, page.Offset);
            Assert.Equal(5, page.Limit);
        }

        [Fact]
        public void Fetch_NullRowsFromServer_AreNormalizedToEmptyArray()
        {
            var fake = RankedBoard();
            fake.ReturnNullRows = true;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            // 契约要求 rows 永不为 null：违约实现返回 null 也要被归一成空数组，调用方不必判空。
            Assert.NotNull(page.Rows);
            Assert.Empty(page.Rows);
            Assert.Equal(DefaultBoardSize, page.TotalCount);
        }

        [Fact]
        public void Fetch_NegativeTotalCountFromServer_IsClampedToZero()
        {
            var fake = RankedBoard();
            fake.ReturnNegativeTotalCount = true;
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.Fetch(Request(1, 0, 5));

            // 负总人数会让"共 N 页"变成负数：构造期夹到 0。
            Assert.Equal(0, page.TotalCount);
        }

        [Fact]
        public void FetchAroundSelf_NotRankedReasonIsStable()
        {
            var fake = RankedBoard();
            var pager = new LeaderboardPager(fake);

            LeaderboardPage page = pager.FetchAroundSelf(1, playerId: 777, radius: 2);

            Assert.Equal("player not ranked", page.Reason);
            Assert.Equal(LeaderboardPager.ReasonNotRanked, page.Reason);
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>
        /// 取"我自己"那一行并断言它存在（把 20 处 <c>SelfRow!.Value</c> 收成一处，
        /// 既避免可空警告，也让失败信息带上"selfRow 缺失"这个语义）。
        /// </summary>
        /// <param name="page">要取 selfRow 的页。</param>
        /// <returns>selfRow 的值。</returns>
        private static LeaderboardRow Self(LeaderboardPage page)
        {
            Assert.True(page.SelfRow.HasValue, "期望 selfRow 存在，但服务端返回了 null。");
            return page.SelfRow.GetValueOrDefault();
        }

        /// <summary>
        /// 造一个 25 人的标准榜：玩家 i 的分数是 <c>(25 - i + 1) × 100</c>，
        /// 于是"玩家 1 = 第 1 名、玩家 N = 第 N 名"，让每一条分页断言都能直接对 id 写期望。
        /// </summary>
        /// <param name="leaderboardId">榜单 id（默认 1）。</param>
        /// <returns>已灌好数据的假后端。</returns>
        private static FakeLeaderboardServer RankedBoard(int leaderboardId = 1)
        {
            var fake = new FakeLeaderboardServer();
            for (int i = 1; i <= DefaultBoardSize; i++)
            {
                fake.Seed(leaderboardId, i, (DefaultBoardSize - i + 1) * 100);
            }

            return fake;
        }

        /// <summary>造一个当前季的分页请求（测试里只关心 offset/limit/playerId）。</summary>
        /// <param name="leaderboardId">榜单 id。</param>
        /// <param name="offset">起始下标。</param>
        /// <param name="limit">条数上限。</param>
        /// <param name="playerId">玩家 id（默认 0 = 不关心自己）。</param>
        /// <returns>分页请求。</returns>
        private static LeaderboardPageRequest Request(int leaderboardId, int offset, int limit, long playerId = 0)
        {
            return new LeaderboardPageRequest(leaderboardId, LeaderboardSeason.Current, offset, limit, playerId);
        }

        /// <summary>造一个指定赛季的分页请求（赛季读榜用）。</summary>
        /// <param name="leaderboardId">榜单 id。</param>
        /// <param name="season">赛季视图。</param>
        /// <param name="offset">起始下标。</param>
        /// <param name="limit">条数上限。</param>
        /// <param name="playerId">玩家 id（默认 0）。</param>
        /// <returns>分页请求。</returns>
        private static LeaderboardPageRequest SeasonRequest(
            int leaderboardId,
            LeaderboardSeason season,
            int offset,
            int limit,
            long playerId = 0)
        {
            return new LeaderboardPageRequest(leaderboardId, season, offset, limit, playerId);
        }

        /// <summary>抽出本页的玩家 id 序列（断言顺序用）。</summary>
        /// <param name="rows">本页的行。</param>
        /// <returns>玩家 id 数组。</returns>
        private static long[] Ids(LeaderboardRow[] rows)
        {
            var ids = new long[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                ids[i] = rows[i].PlayerId;
            }

            return ids;
        }

        /// <summary>抽出本页的分数序列。</summary>
        /// <param name="rows">本页的行。</param>
        /// <returns>分数数组。</returns>
        private static int[] Scores(LeaderboardRow[] rows)
        {
            var scores = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                scores[i] = rows[i].Score;
            }

            return scores;
        }

        /// <summary>抽出本页的名次序列（验证"全局名次而非页内序号"）。</summary>
        /// <param name="rows">本页的行。</param>
        /// <returns>名次数组。</returns>
        private static int[] Ranks(LeaderboardRow[] rows)
        {
            var ranks = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                ranks[i] = rows[i].Rank;
            }

            return ranks;
        }

        /// <summary>某玩家在本页中的下标（-1 = 不在本页）。</summary>
        /// <param name="rows">本页的行。</param>
        /// <param name="playerId">玩家 id。</param>
        /// <returns>页内下标。</returns>
        private static int IndexOfPlayer(LeaderboardRow[] rows, long playerId)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].PlayerId == playerId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 纯内存假排行榜后端：两季桶（当前季可写、上一季归档）、内存表、
        /// 按 <see cref="LeaderboardPager.CompareRows"/> 产出"分数降序 + 同分 id 升序"的顺序。
        /// <para>
        /// <b>时间戳不走系统时钟</b>：用从 1_700_000_000 开始的单调计数器，
        /// 让 <see cref="LeaderboardRow.UpdatedAtUnixSeconds"/> 在断言里可复现
        /// （纯逻辑层铁律：不得读 <c>DateTime.UtcNow</c>；测试替身同样不读，避免用例随真实时间漂移）。
        /// </para>
        /// <para>
        /// 各种 <c>Throw*</c> / <c>Return*</c> 开关用于模拟"实现违约"的后端：
        /// 契约要求这些情况都降级为 false/空页，测试必须能把违约造出来。
        /// </para>
        /// </summary>
        private sealed class FakeLeaderboardServer : ILeaderboardServer
        {
            /// <summary>当前季桶：可写，提交落这里。</summary>
            private readonly Dictionary<int, List<LeaderboardRow>> _current =
                new Dictionary<int, List<LeaderboardRow>>();

            /// <summary>上一季桶：只读归档，<see cref="RotateSeason"/> 时由当前季移交。</summary>
            private readonly Dictionary<int, List<LeaderboardRow>> _previous =
                new Dictionary<int, List<LeaderboardRow>>();

            /// <summary>假时间戳计数器（Unix 秒，单调递增，不读系统时钟）。</summary>
            private long _nextTimestamp = 1700000000L;

            /// <summary>后端是否可用（默认可用）。</summary>
            public bool Available { get; set; } = true;

            /// <summary>读可用性是否抛异常（模拟实现违约）。</summary>
            public bool ThrowOnIsAvailable { get; set; }

            /// <summary>拉页是否抛异常。</summary>
            public bool ThrowOnFetch { get; set; }

            /// <summary>提交是否抛异常。</summary>
            public bool ThrowOnSubmit { get; set; }

            /// <summary>是否填 selfRow（<c>false</c> = 模拟"不支持我的排名"的后端）。</summary>
            public bool SupportsSelfRow { get; set; } = true;

            /// <summary>强制返回的 selfRow 名次（无论玩家在不在榜；<c>null</c> = 正常行为）。</summary>
            public int? ForcedSelfRank { get; set; }

            /// <summary>是否把 rows 返回成 <c>null</c>（模拟实现违约）。</summary>
            public bool ReturnNullRows { get; set; }

            /// <summary>是否返回负总人数（模拟实现违约）。</summary>
            public bool ReturnNegativeTotalCount { get; set; }

            /// <summary>是否接受更低分覆盖（模拟"最近一次成绩"型榜单配置）。</summary>
            public bool AllowOverwrite { get; set; }

            /// <summary>服务端自己的单页上限（0 = 不额外夹；模拟"服务端上限比客户端小"的真实情况）。</summary>
            public int ServerAppliedLimit { get; set; }

            /// <summary>直接改写的回显上限（-1 = 回显实际应用值；用于模拟违约实现回显一个假上限）。</summary>
            public int ServerReportedLimit { get; set; } = -1;

            /// <summary>拉页调用次数（"最多 2 次请求"这类断言用）。</summary>
            public int FetchCallCount { get; private set; }

            /// <summary>提交调用次数（"非法入参不进后端"这类断言用）。</summary>
            public int SubmitCallCount { get; private set; }

            /// <summary>赛季切换调用次数。</summary>
            public int RotateCallCount { get; private set; }

            /// <summary>最近一次提交的玩家 id。</summary>
            public long LastSubmittedPlayerId { get; private set; }

            /// <summary>最近一次提交的分数。</summary>
            public int LastSubmittedScore { get; private set; }

            /// <summary>收到的全部请求（按下标断言 offset/limit/playerId/season）。</summary>
            public List<LeaderboardPageRequest> Requests { get; } = new List<LeaderboardPageRequest>();

            /// <summary>提交永远进当前季桶。</summary>
            public LeaderboardSeason CurrentSeason
            {
                get { return LeaderboardSeason.Current; }
            }

            /// <summary>后端可用性（可被 <see cref="ThrowOnIsAvailable"/> 变成违约异常）。</summary>
            public bool IsAvailable
            {
                get
                {
                    if (ThrowOnIsAvailable)
                    {
                        throw new InvalidOperationException("fake availability failure");
                    }

                    return Available;
                }
            }

            /// <summary>
            /// 直接往当前季灌一行（绕过"只接受更高分"的规则，用来造负分/并列等榜面）。
            /// </summary>
            /// <param name="leaderboardId">榜单 id。</param>
            /// <param name="playerId">玩家 id。</param>
            /// <param name="score">分数（允许为负）。</param>
            /// <param name="displayName">展示名（<c>null</c> 用默认名）。</param>
            public void Seed(int leaderboardId, long playerId, int score, string? displayName = null)
            {
                List<LeaderboardRow> board = GetOrCreateBoard(leaderboardId, LeaderboardSeason.Current);
                board.Add(new LeaderboardRow(
                    playerId,
                    displayName ?? ("Player " + playerId),
                    score,
                    0,
                    NextTimestamp()));
            }

            /// <summary>某季某榜的行数（断言"归档后当前季清空"用）。</summary>
            /// <param name="leaderboardId">榜单 id。</param>
            /// <param name="season">赛季视图。</param>
            /// <returns>行数。</returns>
            public int CountOf(int leaderboardId, LeaderboardSeason season)
            {
                Dictionary<int, List<LeaderboardRow>> source =
                    season == LeaderboardSeason.Previous ? _previous : _current;

                return source.TryGetValue(leaderboardId, out List<LeaderboardRow>? board) ? board.Count : 0;
            }

            /// <summary>
            /// 拉一页：先排序、再按 offset/limit 切、最后把名次补成 1 起（名次是派生值，不入库）。
            /// <para>越界返回空 rows + 有效总人数（与真实服务端契约一致）。</para>
            /// </summary>
            /// <param name="request">请求（调用方已归一化）。</param>
            /// <returns>一页快照。</returns>
            public LeaderboardPage FetchPage(in LeaderboardPageRequest request)
            {
                FetchCallCount++;
                Requests.Add(request);

                if (ThrowOnFetch)
                {
                    throw new InvalidOperationException("fake fetch failure");
                }

                LeaderboardRow[] ordered = SortedBoard(request.LeaderboardId, request.Season);
                int total = ordered.Length;
                int offset = request.Offset < 0 ? 0 : request.Offset;
                int limit = request.Limit < 1 ? 0 : request.Limit;

                // 服务端自己的上限：比请求更小就按自己的来（真实后端普遍如此）。
                if (ServerAppliedLimit > 0 && ServerAppliedLimit < limit)
                {
                    limit = ServerAppliedLimit;
                }

                int count = 0;
                if (offset < total && limit > 0)
                {
                    count = total - offset;
                    if (count > limit)
                    {
                        count = limit;
                    }
                }

                var rows = new LeaderboardRow[count];
                for (int i = 0; i < count; i++)
                {
                    LeaderboardRow source = ordered[offset + i];
                    rows[i] = new LeaderboardRow(
                        source.PlayerId,
                        source.DisplayName,
                        source.Score,
                        offset + i + 1,
                        source.UpdatedAtUnixSeconds);
                }

                LeaderboardRow? selfRow = null;
                if (ForcedSelfRank.HasValue)
                {
                    selfRow = new LeaderboardRow(
                        request.PlayerId,
                        "Player " + request.PlayerId,
                        0,
                        ForcedSelfRank.Value,
                        NextTimestamp());
                }
                else if (SupportsSelfRow)
                {
                    int index = IndexOfSelf(ordered, request.PlayerId);
                    if (index >= 0)
                    {
                        LeaderboardRow source = ordered[index];
                        selfRow = new LeaderboardRow(
                            source.PlayerId,
                            source.DisplayName,
                            source.Score,
                            index + 1,
                            source.UpdatedAtUnixSeconds);
                    }
                }

                int reportedLimit = ServerReportedLimit >= 0 ? ServerReportedLimit : limit;
                LeaderboardRow[] rowsOut = ReturnNullRows ? null! : rows;
                int totalOut = ReturnNegativeTotalCount ? -1 : total;

                return new LeaderboardPage(
                    request.LeaderboardId,
                    request.Season,
                    totalOut,
                    offset,
                    reportedLimit,
                    rowsOut,
                    selfRow,
                    string.Empty);
            }

            /// <summary>
            /// 提交分数：同玩家只保留更好成绩（<see cref="AllowOverwrite"/> 时接受更低分），
            /// 非正玩家 id 归一为 0（契约允许传，行为由实现决定）。
            /// </summary>
            /// <param name="leaderboardId">榜单 id。</param>
            /// <param name="playerId">玩家 id（≤ 0 归一为 0）。</param>
            /// <param name="score">分数。</param>
            /// <returns>是否刷新了个人最好成绩。</returns>
            public bool TrySubmit(int leaderboardId, long playerId, int score)
            {
                SubmitCallCount++;
                LastSubmittedPlayerId = playerId;
                LastSubmittedScore = score;

                if (ThrowOnSubmit)
                {
                    throw new InvalidOperationException("fake submit failure");
                }

                long id = playerId < 0 ? 0 : playerId;
                List<LeaderboardRow> board = GetOrCreateBoard(leaderboardId, LeaderboardSeason.Current);

                for (int i = 0; i < board.Count; i++)
                {
                    if (board[i].PlayerId != id)
                    {
                        continue;
                    }

                    if (board[i].Score == score)
                    {
                        return false;
                    }

                    if (board[i].Score > score && !AllowOverwrite)
                    {
                        return false;
                    }

                    board[i] = new LeaderboardRow(id, board[i].DisplayName, score, 0, NextTimestamp());
                    return true;
                }

                board.Add(new LeaderboardRow(id, "Player " + id, score, 0, NextTimestamp()));
                return true;
            }

            /// <summary>
            /// 赛季切换：当前季整体移交为上一季（旧归档被覆盖），当前季清空。
            /// </summary>
            public void RotateSeason()
            {
                RotateCallCount++;

                _previous.Clear();
                foreach (KeyValuePair<int, List<LeaderboardRow>> pair in _current)
                {
                    _previous[pair.Key] = pair.Value;
                }

                _current.Clear();
            }

            /// <summary>取某季某榜的已排序副本（排序规则复用 Core 的 <see cref="LeaderboardPager.CompareRows"/>）。</summary>
            /// <param name="leaderboardId">榜单 id。</param>
            /// <param name="season">赛季视图。</param>
            /// <returns>排好序的行副本（无数据时为空数组）。</returns>
            private LeaderboardRow[] SortedBoard(int leaderboardId, LeaderboardSeason season)
            {
                Dictionary<int, List<LeaderboardRow>> source =
                    season == LeaderboardSeason.Previous ? _previous : _current;

                if (!source.TryGetValue(leaderboardId, out List<LeaderboardRow>? board) || board.Count == 0)
                {
                    return Array.Empty<LeaderboardRow>();
                }

                LeaderboardRow[] ordered = board.ToArray();
                Array.Sort(ordered, (left, right) => LeaderboardPager.CompareRows(left, right));
                return ordered;
            }

            /// <summary>取（不存在则创建）某季某榜的可写列表。</summary>
            /// <param name="leaderboardId">榜单 id。</param>
            /// <param name="season">赛季视图。</param>
            /// <returns>可写列表。</returns>
            private List<LeaderboardRow> GetOrCreateBoard(int leaderboardId, LeaderboardSeason season)
            {
                Dictionary<int, List<LeaderboardRow>> target =
                    season == LeaderboardSeason.Previous ? _previous : _current;

                if (!target.TryGetValue(leaderboardId, out List<LeaderboardRow>? board))
                {
                    board = new List<LeaderboardRow>();
                    target[leaderboardId] = board;
                }

                return board;
            }

            /// <summary>在已排序副本里找玩家下标（-1 = 不在榜）。</summary>
            /// <param name="ordered">已排序的行。</param>
            /// <param name="playerId">玩家 id。</param>
            /// <returns>下标。</returns>
            private static int IndexOfSelf(LeaderboardRow[] ordered, long playerId)
            {
                for (int i = 0; i < ordered.Length; i++)
                {
                    if (ordered[i].PlayerId == playerId)
                    {
                        return i;
                    }
                }

                return -1;
            }

            /// <summary>下一个假时间戳（Unix 秒）。</summary>
            /// <returns>单调递增的时间戳。</returns>
            private long NextTimestamp()
            {
                long value = _nextTimestamp;
                _nextTimestamp++;
                return value;
            }
        }
    }
}

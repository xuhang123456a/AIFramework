using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleSocialService"/> 的行为契约验收：好友列表形状、赠送/领取的每日一次、
    /// 一键领取的结算、跨 UTC 自然日重置（含 <see cref="ConsoleSocialService.ExpireDaily"/> 的强制重置）、
    /// 不可用降级与环形日志。
    /// <para>
    /// 这些用例同时是"未来真实社交后端"的验收清单：假后端在这里被允许的行为，
    /// 就是真后端必须做到的最小语义。任何一条在这里放松（例如重复赠送返回成功、跨日后仍记着旧标记），
    /// 真机上线就是"体力凭空翻倍"或"跨天领不到"的真实事故。
    /// </para>
    /// <para>
    /// 时间一律用假时钟：跨日边界必须可精确断言，不能依赖真实等待，也不允许本类偷偷读系统时区。
    /// </para>
    /// </summary>
    public sealed class ConsoleSocialServiceTests
    {
        /// <summary>测试用固定 UTC 起点（最近活跃时间与跨日都必须可精确断言）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Constructor_Default_CreatesThreeAvailableFriends()
        {
            var service = new ConsoleSocialService();

            Assert.True(service.IsAvailable);
            Assert.Equal(ConsoleSocialService.DefaultFriendCount, service.FriendCount);

            FriendInfo[] friends = service.GetFriends();

            Assert.Equal(3, friends.Length);
            Assert.Equal("friend-1", friends[0].PlayerId);
            Assert.Equal("好友1", friends[0].DisplayName);
            Assert.Equal("friend-3", friends[2].PlayerId);
            Assert.Equal("好友3", friends[2].DisplayName);
        }

        [Fact]
        public void Constructor_NonPositiveFriendCount_YieldsEmptyList()
        {
            Assert.Equal(0, new ConsoleSocialService(friendCount: 0).FriendCount);
            Assert.Empty(new ConsoleSocialService(friendCount: 0).GetFriends());

            // 负数是配置事故：按 0 处理（空列表），不要抛异常把好友页整块打挂。
            Assert.Equal(0, new ConsoleSocialService(friendCount: -2).FriendCount);
        }

        [Fact]
        public void GetFriends_FreshDay_EveryoneCanBeGiftedAndClaimed()
        {
            FriendInfo[] friends = new ConsoleSocialService().GetFriends();

            for (int i = 0; i < friends.Length; i++)
            {
                Assert.True(friends[i].CanGift);
                Assert.True(friends[i].CanClaim);
            }
        }

        [Fact]
        public void GetFriends_ReturnsDefensiveCopy()
        {
            var service = new ConsoleSocialService();
            service.GiftStamina("friend-1");

            FriendInfo[] first = service.GetFriends();
            Assert.False(first[0].CanGift);

            // 调用方改数组不能影响服务内部状态（否则 UI 可以把按钮"改亮"再送一次）。
            first[0] = new FriendInfo("friend-1", "好友1", 0, true, true);

            FriendInfo[] second = service.GetFriends();
            Assert.False(second[0].CanGift);
        }

        [Fact]
        public void GetFriends_WhenUnavailable_ReturnsEmptyArray()
        {
            var service = new ConsoleSocialService(available: false);

            Assert.False(service.IsAvailable);
            Assert.Equal(0, service.FriendCount);
            Assert.Empty(service.GetFriends());
        }

        [Fact]
        public void GiftStamina_FirstCall_SucceedsAndMarksFriend()
        {
            var service = new ConsoleSocialService();

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.True(outcome.Success);
            Assert.Equal(FriendGiftResult.Success, outcome.Result);
            Assert.Equal(ConsoleSocialService.GiftStaminaAmount, outcome.Amount);
            Assert.Equal(ConsoleSocialService.ReasonGifted, outcome.Reason);

            FriendInfo[] friends = service.GetFriends();
            Assert.False(friends[0].CanGift);
            Assert.True(friends[1].CanGift);
        }

        [Fact]
        public void GiftStamina_SecondCall_ReturnsAlreadyGifted()
        {
            var service = new ConsoleSocialService();

            service.GiftStamina("friend-1");
            FriendOperationOutcome second = service.GiftStamina("friend-1");

            Assert.False(second.Success);
            Assert.Equal(FriendGiftResult.AlreadyGifted, second.Result);
            Assert.Equal(0, second.Amount);
            Assert.Equal(ConsoleSocialService.ReasonAlreadyGifted, second.Reason);
        }

        [Fact]
        public void GiftStamina_UnknownFriend_ReturnsNotFriend()
        {
            var service = new ConsoleSocialService();

            Assert.Equal(FriendGiftResult.NotFriend, service.GiftStamina("friend-999").Result);
            Assert.Equal(FriendGiftResult.NotFriend, service.GiftStamina("ghost").Result);
        }

        [Fact]
        public void GiftStamina_EmptyId_ReturnsNotFriend()
        {
            var service = new ConsoleSocialService();

            Assert.Equal(FriendGiftResult.NotFriend, service.GiftStamina(string.Empty).Result);
            Assert.Equal(FriendGiftResult.NotFriend, service.GiftStamina(null!).Result);
            Assert.Equal(FriendGiftResult.NotFriend, service.ClaimStamina(string.Empty).Result);
        }

        [Fact]
        public void GiftStamina_WhenUnavailable_ReturnsUnavailable()
        {
            var service = new ConsoleSocialService(available: false);

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.False(outcome.Success);
            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Equal(ConsoleSocialService.ReasonUnavailable, outcome.Reason);
        }

        [Fact]
        public void ClaimStamina_FirstCall_SucceedsAndMarksFriend()
        {
            var service = new ConsoleSocialService();

            FriendOperationOutcome outcome = service.ClaimStamina("friend-2");

            Assert.True(outcome.Success);
            Assert.Equal(ConsoleSocialService.ClaimStaminaAmount, outcome.Amount);
            Assert.Equal(ConsoleSocialService.ReasonClaimed, outcome.Reason);

            FriendInfo[] friends = service.GetFriends();
            Assert.False(friends[1].CanClaim);
            Assert.True(friends[0].CanClaim);
        }

        [Fact]
        public void ClaimStamina_SecondCall_ReturnsAlreadyGifted()
        {
            var service = new ConsoleSocialService();

            service.ClaimStamina("friend-1");
            FriendOperationOutcome second = service.ClaimStamina("friend-1");

            // 领取侧复用 AlreadyGifted，语义是"今天已经领过"（枚举形状由契约锁死，见契约文档）。
            Assert.False(second.Success);
            Assert.Equal(FriendGiftResult.AlreadyGifted, second.Result);
            Assert.Equal(ConsoleSocialService.ReasonAlreadyClaimed, second.Reason);
        }

        [Fact]
        public void ClaimStamina_UnknownFriend_ReturnsNotFriend()
        {
            Assert.Equal(FriendGiftResult.NotFriend, new ConsoleSocialService().ClaimStamina("friend-999").Result);
        }

        [Fact]
        public void ClaimAllStamina_ClaimsEveryClaimableFriend()
        {
            var service = new ConsoleSocialService();

            int total = service.ClaimAllStamina();

            Assert.Equal(ConsoleSocialService.DefaultFriendCount * ConsoleSocialService.ClaimStaminaAmount, total);

            FriendInfo[] friends = service.GetFriends();
            for (int i = 0; i < friends.Length; i++)
            {
                Assert.False(friends[i].CanClaim);
            }
        }

        [Fact]
        public void ClaimAllStamina_SecondCall_ReturnsZero()
        {
            var service = new ConsoleSocialService();

            service.ClaimAllStamina();

            Assert.Equal(0, service.ClaimAllStamina());
        }

        [Fact]
        public void ClaimAllStamina_EmptyFriendList_ReturnsZero()
        {
            var service = new ConsoleSocialService(friendCount: 0);

            Assert.Equal(0, service.ClaimAllStamina());
        }

        [Fact]
        public void ClaimAllStamina_WhenUnavailable_ReturnsZero()
        {
            var service = new ConsoleSocialService(available: false);

            Assert.Equal(0, service.ClaimAllStamina());
        }

        [Fact]
        public void DailyState_ResetsAcrossUtcDayBoundary()
        {
            var time = new FakeTimeProvider(StartUtc);
            var service = new ConsoleSocialService(time);

            service.GiftStamina("friend-1");
            service.ClaimStamina("friend-1");

            FriendInfo[] sameDay = service.GetFriends();
            Assert.False(sameDay[0].CanGift);
            Assert.False(sameDay[0].CanClaim);

            time.AdvanceDays(1);

            // 跨日由操作入口/读列表触发结算：不需要调用方手动通知"今天是新的一天"。
            FriendInfo[] nextDay = service.GetFriends();
            Assert.True(nextDay[0].CanGift);
            Assert.True(nextDay[0].CanClaim);

            Assert.True(service.GiftStamina("friend-1").Success);
            Assert.True(service.ClaimStamina("friend-1").Success);
        }

        [Fact]
        public void ExpireDaily_WithoutTimeProvider_ResetsDailyState()
        {
            // 没有时钟时这是唯一能模拟"过了一天"的手段（冒烟脚本/上线演练用）。
            var service = new ConsoleSocialService();
            service.GiftStamina("friend-1");
            service.ClaimStamina("friend-1");

            service.ExpireDaily();

            Assert.True(service.GiftStamina("friend-1").Success);
            Assert.True(service.ClaimStamina("friend-1").Success);
        }

        [Fact]
        public void Refresh_WithinSameDay_KeepsDailyState()
        {
            var time = new FakeTimeProvider(StartUtc);
            var service = new ConsoleSocialService(time);
            service.GiftStamina("friend-1");

            service.Refresh();
            service.Refresh();

            // 同一天内重复刷新是幂等的：不能把"今天已送"刷成"还能送"。
            Assert.False(service.GetFriends()[0].CanGift);
            Assert.Equal(FriendGiftResult.AlreadyGifted, service.GiftStamina("friend-1").Result);
        }

        [Fact]
        public void SetAvailable_RestoresServiceAfterUnavailable()
        {
            var service = new ConsoleSocialService(available: false);
            Assert.Equal(FriendGiftResult.Unavailable, service.GiftStamina("friend-1").Result);

            service.SetAvailable();

            Assert.True(service.IsAvailable);
            Assert.Equal(3, service.FriendCount);
            Assert.True(service.GiftStamina("friend-1").Success);

            service.SetUnavailable();

            Assert.False(service.IsAvailable);
            Assert.Equal(FriendGiftResult.Unavailable, service.GiftStamina("friend-2").Result);
        }

        [Fact]
        public void DrainLog_RecordsOperationsAndClearsBuffer()
        {
            var service = new ConsoleSocialService();

            service.GiftStamina("friend-1");
            service.ClaimStamina("friend-2");
            service.ClaimAllStamina();

            IReadOnlyList<string> log = service.DrainLog();

            Assert.Contains(log, entry => entry.Contains("gift friend-1"));
            Assert.Contains(log, entry => entry.Contains("claim friend-2"));
            Assert.Contains(log, entry => entry.Contains("claim-all"));

            // 取走即清空：同一批事件不能被重复打印。
            Assert.Empty(service.DrainLog());
        }

        [Fact]
        public void DrainLog_KeepsOnlyMostRecentEntries()
        {
            var service = new ConsoleSocialService();

            service.GiftStamina("friend-1");
            service.GiftStamina("friend-2");
            service.GiftStamina("friend-3");

            // 再补 30 条（共 33 条），环形缓冲只保留最近 32 条。
            for (int i = 0; i < 30; i++)
            {
                service.Refresh();
            }

            IReadOnlyList<string> log = service.DrainLog();

            Assert.Equal(ConsoleSocialService.LogCapacity, log.Count);
            Assert.DoesNotContain(log, entry => entry.Contains("gift friend-1"));
            Assert.Contains(log, entry => entry.Contains("gift friend-3"));
        }

        [Fact]
        public void LastActiveUnixSeconds_ComesFromTimeProvider()
        {
            var service = new ConsoleSocialService(new FakeTimeProvider(StartUtc));

            FriendInfo[] friends = service.GetFriends();

            int expected = (int)(StartUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Assert.Equal(expected, friends[0].LastActiveUnixSeconds);
            Assert.Equal(expected - 300, friends[1].LastActiveUnixSeconds);
            Assert.Equal(expected - 600, friends[2].LastActiveUnixSeconds);
        }

        [Fact]
        public void LastActiveUnixSeconds_WithoutTimeProvider_IsZero()
        {
            // 没有时钟宁可记 0（未知），也不读系统时间：无头测试必须完全可复现。
            Assert.Equal(0, new ConsoleSocialService().GetFriends()[0].LastActiveUnixSeconds);
        }

        /// <summary>
        /// 可推进的假时钟：跨日只使用 <see cref="UtcNow"/>，
        /// 推进它即可精确验证 UTC 00:00 边界，不依赖真实等待。
        /// </summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime? startUtc = null)
            {
                _utcNow = startUtc ?? StartUtc;
            }

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            /// <summary>同时推进单调钟与墙钟的整天数。</summary>
            public void AdvanceDays(int days)
            {
                _utcNow = _utcNow.AddDays(days);
                _elapsedSeconds += days * 86400d;
            }
        }
    }
}

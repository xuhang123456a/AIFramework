using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="FriendService"/> 的行为契约验收：每日赠送额度、跨 UTC 自然日重置、结果码如实透传、
    /// 后端异常/不可用降级、单个与一键领取、"可领数"读数的重建。
    /// <para>
    /// <b>这些用例守的是经济产出</b>：社交多送一次/多领一次就是白送体力，少送一次是玩家投诉。
    /// 因此断言分三类——钉死额度（送满即 <see cref="FriendGiftResult.Cooldown"/> 且不触达后端）、
    /// 钉死"不做事"（被服务端拒绝时不扣额度、异常时不崩）、钉死跨日边界（UTC 00:00 前后各一次）。
    /// </para>
    /// <para>
    /// 替身刻意只用"可拨动的假后端 + 可拨动的假时钟"：真人好友关系、服务端冷却、网络抖动在真机上
    /// 极难复现，只能靠假后端逐条钉住；时间一律用假时钟，绝不依赖真实等待或系统时区。
    /// </para>
    /// </summary>
    public sealed class FriendServiceTests
    {
        /// <summary>测试用固定 UTC 起点（跨日边界必须可精确断言，不能用真实时间）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            var time = new FakeTimeProvider();
            var social = new FakeSocialService();

            // 两个依赖缺一不可：没有后端无法操作，没有时钟无法判跨日（且纯逻辑层禁止读系统时间）。
            Assert.Throws<ArgumentNullException>(() => new FriendService(null!, time));
            Assert.Throws<ArgumentNullException>(() => new FriendService(social, null!));
        }

        [Fact]
        public void Constructor_ExposesSocialAndFullGiftQuota()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider());

            Assert.Same(social, service.Social);
            Assert.Equal(FriendService.DefaultMaxGiftPerDay, service.DailyGiftRemaining);

            // 未刷新前没有好友快照：可领数只能是 0，不能凭空猜一个数字出来。
            Assert.Equal(0, service.DailyClaimRemaining);
            Assert.Equal(0, social.RefreshCallCount);
        }

        [Fact]
        public void Constructor_NegativeGiftQuota_FailsClosed()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: -5);

            // 负配额是配置事故：按 0（今天全禁）而不是"无限送"，见构造函数注释。
            Assert.Equal(0, service.DailyGiftRemaining);

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.Equal(FriendGiftResult.Cooldown, outcome.Result);
            Assert.Equal(FriendService.ReasonDailyGiftLimit, outcome.Reason);
            Assert.Equal(0, social.GiftCallCount);
        }

        [Fact]
        public void GiftStamina_FirstCall_ReturnsSuccessAndConsumesQuota()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 3);

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.True(outcome.Success);
            Assert.Equal(FriendGiftResult.Success, outcome.Result);
            Assert.Equal(10, outcome.Amount);
            Assert.Equal(2, service.DailyGiftRemaining);
            Assert.Equal(1, social.GiftCallCount);
            Assert.Equal("friend-1", social.LastGiftFriendId);
        }

        [Fact]
        public void GiftStamina_AtDailyLimit_ReturnsCooldownWithoutTouchingBackend()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 2);

            service.GiftStamina("friend-1");
            service.GiftStamina("friend-2");

            FriendOperationOutcome third = service.GiftStamina("friend-3");

            Assert.False(third.Success);
            Assert.Equal(FriendGiftResult.Cooldown, third.Result);
            Assert.Equal(0, third.Amount);
            Assert.Equal(0, service.DailyGiftRemaining);

            // 超额必须"连网络都不打"：连点十次不该换来十次请求。
            Assert.Equal(2, social.GiftCallCount);
        }

        [Fact]
        public void GiftStamina_WhenBackendSaysAlreadyGifted_PassesThroughAndKeepsQuota()
        {
            var social = new FakeSocialService
            {
                GiftOutcome = new FriendOperationOutcome(false, FriendGiftResult.AlreadyGifted, 0, "already")
            };
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 3);

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.False(outcome.Success);
            Assert.Equal(FriendGiftResult.AlreadyGifted, outcome.Result);
            Assert.Equal("already", outcome.Reason);

            // 被服务端拒绝不能扣本地额度：否则玩家会看到"明明没送出去却少了一次"。
            Assert.Equal(3, service.DailyGiftRemaining);
        }

        [Fact]
        public void GiftStamina_WhenBackendSaysCooldown_PassesThrough()
        {
            var social = new FakeSocialService
            {
                GiftOutcome = new FriendOperationOutcome(false, FriendGiftResult.Cooldown, 0, "backend cooldown")
            };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.Equal(FriendGiftResult.Cooldown, outcome.Result);
            Assert.Equal("backend cooldown", outcome.Reason);
            Assert.Equal(FriendService.DefaultMaxGiftPerDay, service.DailyGiftRemaining);
        }

        [Fact]
        public void GiftStamina_WhenBackendSaysNotFriend_PassesThrough()
        {
            var social = new FakeSocialService
            {
                GiftOutcome = new FriendOperationOutcome(false, FriendGiftResult.NotFriend, 0, "not a friend")
            };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.GiftStamina("ghost");

            Assert.Equal(FriendGiftResult.NotFriend, outcome.Result);
            Assert.Equal(FriendService.DefaultMaxGiftPerDay, service.DailyGiftRemaining);
        }

        [Fact]
        public void GiftStamina_WhenBackendThrows_ReturnsUnavailableAndKeepsQuota()
        {
            var social = new FakeSocialService { ThrowOnGift = true };
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 2);

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.False(outcome.Success);
            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Equal(0, outcome.Amount);

            // 原因只带异常类型名（不含堆栈/内部路径），并且额度不被这次失败吃掉。
            Assert.Contains(FriendService.ReasonServiceThrew, outcome.Reason);
            Assert.Contains("InvalidOperationException", outcome.Reason);
            Assert.Equal(2, service.DailyGiftRemaining);
        }

        [Fact]
        public void GiftStamina_WhenBackendUnavailable_ReturnsUnavailableWithoutTouchingBackend()
        {
            var social = new FakeSocialService { Available = false };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Equal(FriendService.ReasonUnavailable, outcome.Reason);
            Assert.Equal(0, social.GiftCallCount);
            Assert.Equal(FriendService.DefaultMaxGiftPerDay, service.DailyGiftRemaining);
        }

        [Fact]
        public void GiftStamina_WhenAvailabilityGetterThrows_TreatedAsUnavailable()
        {
            var social = new FakeSocialService { ThrowOnIsAvailable = true };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.GiftStamina("friend-1");

            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Equal(0, social.GiftCallCount);
            Assert.Equal(0, service.DailyClaimRemaining);
        }

        [Fact]
        public void GiftStamina_NullEmptyOrWhitespaceId_Throws()
        {
            var service = new FriendService(new FakeSocialService(), new FakeTimeProvider());

            // 空 id 是调用方编程错误，必须立刻响，不能混进 NotFriend（否则日志里与"对方删了我"无法区分）。
            Assert.Throws<ArgumentException>(() => service.GiftStamina(null!));
            Assert.Throws<ArgumentException>(() => service.GiftStamina(string.Empty));
            Assert.Throws<ArgumentException>(() => service.GiftStamina("   "));
            Assert.Throws<ArgumentException>(() => service.ClaimStamina(null!));
            Assert.Throws<ArgumentException>(() => service.ClaimStamina(string.Empty));
        }

        [Fact]
        public void GiftStamina_NullId_ThrowsEvenWhenBackendUnavailable()
        {
            // 参数错误的优先级高于可用性判定：这条规则被刻意固定在实现里（见类型注释）。
            var service = new FriendService(new FakeSocialService { Available = false }, new FakeTimeProvider());

            Assert.Throws<ArgumentException>(() => service.GiftStamina(null!));
        }

        [Fact]
        public void GiftStamina_TimeProviderThrows_FailsClosed()
        {
            var time = new FakeTimeProvider();
            var service = new FriendService(new FakeSocialService(), time, maxGiftPerDay: 1);

            service.GiftStamina("friend-1");
            time.ThrowOnUtcNow = true;

            // 时间源违约时日序号视为不变 ⇒ 当天额度不会恢复（宁可少送，也不放行额外额度）。
            Assert.Equal(0, service.DailyGiftRemaining);
            Assert.Equal(FriendGiftResult.Cooldown, service.GiftStamina("friend-2").Result);
        }

        [Fact]
        public void Refresh_SameDayTwice_DoesNotResetQuota()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 2);

            service.GiftStamina("friend-1");
            service.GiftStamina("friend-2");
            service.Refresh();
            service.Refresh();

            Assert.Equal(0, service.DailyGiftRemaining);
            Assert.Equal(2, social.RefreshCallCount);
        }

        [Fact]
        public void Refresh_AfterUtcDayRollover_RestoresQuota()
        {
            var time = new FakeTimeProvider();
            var social = new FakeSocialService();
            var service = new FriendService(social, time, maxGiftPerDay: 2);

            service.GiftStamina("friend-1");
            service.GiftStamina("friend-2");
            Assert.Equal(0, service.DailyGiftRemaining);

            time.AdvanceDays(1);
            service.Refresh();

            Assert.Equal(2, service.DailyGiftRemaining);
            Assert.True(service.GiftStamina("friend-1").Success);
            Assert.Equal(3, social.GiftCallCount);
        }

        [Fact]
        public void GiftStamina_AfterUtcDayRolloverWithoutRefresh_LazilyRestoresQuota()
        {
            // 玩家跨日后直接点"赠送"（没刷新页面）也必须能送：操作入口自己结算跨日。
            var time = new FakeTimeProvider();
            var service = new FriendService(new FakeSocialService(), time, maxGiftPerDay: 1);

            service.GiftStamina("friend-1");
            time.AdvanceDays(1);

            Assert.Equal(1, service.DailyGiftRemaining);
            Assert.True(service.GiftStamina("friend-1").Success);
            Assert.Equal(0, service.DailyGiftRemaining);
        }

        [Fact]
        public void Refresh_WhenBackendRefreshThrows_DoesNotThrowAndStillSettlesDay()
        {
            var time = new FakeTimeProvider();
            var social = new FakeSocialService();
            var service = new FriendService(social, time, maxGiftPerDay: 1);

            service.GiftStamina("friend-1");
            time.AdvanceDays(1);
            social.ThrowOnRefresh = true;

            // 不抛：刷新失败不该打断"进入好友页"这条路径；额度依然按新的一天恢复。
            service.Refresh();

            Assert.Equal(1, service.DailyGiftRemaining);
        }

        [Fact]
        public void Refresh_WhenBackendUnavailable_ClearsSnapshotWithoutTouchingBackend()
        {
            var social = new FakeSocialService();
            var service = new FriendService(social, new FakeTimeProvider());
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, true));
            service.Refresh();
            Assert.Equal(1, service.DailyClaimRemaining);

            social.Available = false;
            service.Refresh();

            Assert.Equal(0, service.DailyClaimRemaining);
            Assert.Equal(1, social.RefreshCallCount);
        }

        [Fact]
        public void Refresh_RebuildsClaimableCountFromBackendSnapshot()
        {
            var social = new FakeSocialService();
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, true));
            social.Friends.Add(new FriendInfo("friend-2", "好友2", 0, true, false));
            social.Friends.Add(new FriendInfo("friend-3", "好友3", 0, true, true));

            var service = new FriendService(social, new FakeTimeProvider());
            service.Refresh();

            // 读数只统计"服务端说还能领"的人（friend-2 已领过），不是好友总数。
            Assert.Equal(2, service.DailyClaimRemaining);
        }

        [Fact]
        public void Refresh_WhenGetFriendsThrows_KeepsPreviousSnapshot()
        {
            var social = new FakeSocialService();
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, true));
            social.Friends.Add(new FriendInfo("friend-2", "好友2", 0, true, true));

            var service = new FriendService(social, new FakeTimeProvider());
            service.Refresh();
            Assert.Equal(2, service.DailyClaimRemaining);

            social.ThrowOnGetFriends = true;
            service.Refresh();

            // 违约实现抛异常时保留旧快照：把一次异常当成"没有好友"会让 UI 误报空列表。
            Assert.Equal(2, service.DailyClaimRemaining);
        }

        [Fact]
        public void Refresh_WhenGetFriendsReturnsNull_TreatedAsEmptyList()
        {
            var social = new FakeSocialService { ReturnNullFriends = true };
            var service = new FriendService(social, new FakeTimeProvider());

            service.Refresh();

            Assert.Equal(0, service.DailyClaimRemaining);
        }

        [Fact]
        public void ClaimStamina_Success_ReturnsAmountAndRefreshesClaimableCount()
        {
            var social = new FakeSocialService();
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, true));
            social.Friends.Add(new FriendInfo("friend-2", "好友2", 0, true, true));

            var service = new FriendService(social, new FakeTimeProvider());
            service.Refresh();
            Assert.Equal(2, service.DailyClaimRemaining);

            // 模拟服务端在领取后把该好友标成"今日已领"。
            social.Friends[0] = new FriendInfo("friend-1", "好友1", 0, true, false);

            FriendOperationOutcome outcome = service.ClaimStamina("friend-1");

            Assert.True(outcome.Success);
            Assert.Equal(10, outcome.Amount);
            Assert.Equal("friend-1", social.LastClaimFriendId);
            Assert.Equal(1, service.DailyClaimRemaining);
        }

        [Fact]
        public void ClaimStamina_WhenBackendSaysAlreadyClaimed_PassesThrough()
        {
            var social = new FakeSocialService
            {
                ClaimOutcome = new FriendOperationOutcome(false, FriendGiftResult.AlreadyGifted, 0, "already claimed")
            };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.ClaimStamina("friend-1");

            Assert.False(outcome.Success);
            Assert.Equal(FriendGiftResult.AlreadyGifted, outcome.Result);
            Assert.Equal("already claimed", outcome.Reason);
        }

        [Fact]
        public void ClaimStamina_WhenBackendUnavailable_ReturnsUnavailable()
        {
            var social = new FakeSocialService { Available = false };
            var service = new FriendService(social, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.ClaimStamina("friend-1");

            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Equal(0, social.ClaimCallCount);
        }

        [Fact]
        public void ClaimStamina_WhenBackendThrows_ReturnsUnavailable()
        {
            var service = new FriendService(new FakeSocialService { ThrowOnClaim = true }, new FakeTimeProvider());

            FriendOperationOutcome outcome = service.ClaimStamina("friend-1");

            Assert.Equal(FriendGiftResult.Unavailable, outcome.Result);
            Assert.Contains("InvalidOperationException", outcome.Reason);
        }

        [Fact]
        public void ClaimAllStamina_SumsBackendTotalAndRefreshesClaimableCount()
        {
            var social = new FakeSocialService { ClaimAllTotal = 30 };
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, true));
            social.Friends.Add(new FriendInfo("friend-2", "好友2", 0, true, true));
            social.Friends.Add(new FriendInfo("friend-3", "好友3", 0, true, true));

            var service = new FriendService(social, new FakeTimeProvider());
            service.Refresh();
            Assert.Equal(3, service.DailyClaimRemaining);

            // 模拟服务端：一键领取后所有人都变成"今日已领"。
            for (int i = 0; i < social.Friends.Count; i++)
            {
                FriendInfo friend = social.Friends[i];
                social.Friends[i] = new FriendInfo(friend.PlayerId, friend.DisplayName, 0, friend.CanGift, false);
            }

            int total = service.ClaimAllStamina();

            Assert.Equal(30, total);
            Assert.Equal(1, social.ClaimAllCallCount);

            // 一键领取后按钮必须立刻熄灭（否则玩家会一直点，换来一串"今日已领"）。
            Assert.Equal(0, service.DailyClaimRemaining);
        }

        [Fact]
        public void ClaimAllStamina_ZeroIsAValidResult()
        {
            var social = new FakeSocialService { ClaimAllTotal = 0 };
            var service = new FriendService(social, new FakeTimeProvider());

            // 0 = 今天没有可领的，是正常结果，不是错误。
            Assert.Equal(0, service.ClaimAllStamina());
            Assert.Equal(1, social.ClaimAllCallCount);
        }

        [Fact]
        public void ClaimAllStamina_WhenBackendUnavailable_ReturnsZero()
        {
            var social = new FakeSocialService { Available = false };
            var service = new FriendService(social, new FakeTimeProvider());

            Assert.Equal(0, service.ClaimAllStamina());
            Assert.Equal(0, social.ClaimAllCallCount);
        }

        [Fact]
        public void ClaimAllStamina_WhenBackendThrows_ReturnsZero()
        {
            var service = new FriendService(new FakeSocialService { ThrowOnClaimAll = true }, new FakeTimeProvider());

            // 签名只能回 int，没有地方放失败原因：宁可静默 0，也不能把异常抛给一次普通点击。
            Assert.Equal(0, service.ClaimAllStamina());
        }

        [Fact]
        public void ClaimAllStamina_NegativeBackendTotal_ClampedToZero()
        {
            var service = new FriendService(
                new FakeSocialService { ClaimAllTotal = -30 }, new FakeTimeProvider());

            // 体力不能倒扣：违约实现返回负数时按 0 处理。
            Assert.Equal(0, service.ClaimAllStamina());
        }

        [Fact]
        public void ClaimAllStamina_AfterUtcDayRollover_ClaimsAgain()
        {
            var time = new FakeTimeProvider();
            var social = new FakeSocialService { ClaimAllTotal = 20 };
            var service = new FriendService(social, time);

            Assert.Equal(20, service.ClaimAllStamina());
            time.AdvanceDays(1);

            // 跨日后无需刷新：操作入口自己结算，服务端也会接受新一天的领取。
            Assert.Equal(20, service.ClaimAllStamina());
            Assert.Equal(2, social.ClaimAllCallCount);
        }

        [Fact]
        public void DailyClaimRemaining_AfterDayRollover_OptimisticallyCountsAllFriends()
        {
            var time = new FakeTimeProvider();
            var social = new FakeSocialService();
            social.Friends.Add(new FriendInfo("friend-1", "好友1", 0, true, false));
            social.Friends.Add(new FriendInfo("friend-2", "好友2", 0, true, false));

            var service = new FriendService(social, time, maxGiftPerDay: 1);
            service.Refresh();
            Assert.Equal(0, service.DailyClaimRemaining);

            time.AdvanceDays(1);

            // 跨日后还没重新拉列表：按"全员可领"乐观填充（服务端跨日重置），下一次 Refresh 纠正。
            Assert.Equal(2, service.DailyClaimRemaining);

            service.Refresh();
            Assert.Equal(0, service.DailyClaimRemaining);
        }

        [Fact]
        public void GiftQuota_IsIndependentFromClaims()
        {
            var social = new FakeSocialService { ClaimAllTotal = 10 };
            var service = new FriendService(social, new FakeTimeProvider(), maxGiftPerDay: 1);

            service.GiftStamina("friend-1");
            Assert.Equal(0, service.DailyGiftRemaining);

            // 领取不消耗赠送额度，也不会被赠送额度拦住。
            Assert.Equal(10, service.ClaimAllStamina());
            Assert.Equal(0, service.DailyGiftRemaining);

            // 但赠送额度确实已经用尽（两条额度互不串台）。
            Assert.Equal(FriendGiftResult.Cooldown, service.GiftStamina("friend-2").Result);
        }

        [Fact]
        public void DailyGiftRemaining_WithZeroQuota_BlocksAllGifts()
        {
            // 额度在构造期固定；这里用一个极小额度验证读数不会出现负数（UI 直接展示 0）。
            var service = new FriendService(new FakeSocialService(), new FakeTimeProvider(), maxGiftPerDay: 0);

            Assert.Equal(0, service.DailyGiftRemaining);
            Assert.Equal(FriendGiftResult.Cooldown, service.GiftStamina("friend-1").Result);
            Assert.Equal(0, service.DailyGiftRemaining);
        }

        /// <summary>
        /// 可拨动的假社交后端：把真机上极难复现的分支（不可用、抛异常、服务端拒绝、null 列表）
        /// 变成一次赋值，用来逐条钉死业务层的透传与降级行为。
        /// </summary>
        private sealed class FakeSocialService : ISocialService
        {
            /// <summary>后端是否可用。</summary>
            public bool Available { get; set; } = true;

            /// <summary>读可用性是否抛异常（模拟实现违约）。</summary>
            public bool ThrowOnIsAvailable { get; set; }

            /// <summary>刷新是否抛异常。</summary>
            public bool ThrowOnRefresh { get; set; }

            /// <summary>拉列表是否抛异常。</summary>
            public bool ThrowOnGetFriends { get; set; }

            /// <summary>拉列表是否返回 <c>null</c>（模拟实现违约）。</summary>
            public bool ReturnNullFriends { get; set; }

            /// <summary>赠送是否抛异常。</summary>
            public bool ThrowOnGift { get; set; }

            /// <summary>领取是否抛异常。</summary>
            public bool ThrowOnClaim { get; set; }

            /// <summary>一键领取是否抛异常。</summary>
            public bool ThrowOnClaimAll { get; set; }

            /// <summary>赠送返回的结果（默认成功 +10）。</summary>
            public FriendOperationOutcome GiftOutcome { get; set; } =
                new FriendOperationOutcome(true, FriendGiftResult.Success, 10, "gifted");

            /// <summary>领取返回的结果（默认成功 +10）。</summary>
            public FriendOperationOutcome ClaimOutcome { get; set; } =
                new FriendOperationOutcome(true, FriendGiftResult.Success, 10, "claimed");

            /// <summary>一键领取返回的总量（默认 0）。</summary>
            public int ClaimAllTotal { get; set; }

            /// <summary>当前好友列表（测试直接改）。</summary>
            public List<FriendInfo> Friends { get; } = new List<FriendInfo>();

            /// <summary>刷新调用次数。</summary>
            public int RefreshCallCount { get; private set; }

            /// <summary>拉列表调用次数。</summary>
            public int GetFriendsCallCount { get; private set; }

            /// <summary>赠送调用次数。</summary>
            public int GiftCallCount { get; private set; }

            /// <summary>领取调用次数。</summary>
            public int ClaimCallCount { get; private set; }

            /// <summary>一键领取调用次数。</summary>
            public int ClaimAllCallCount { get; private set; }

            /// <summary>最近一次赠送的好友 id。</summary>
            public string? LastGiftFriendId { get; private set; }

            /// <summary>最近一次领取的好友 id。</summary>
            public string? LastClaimFriendId { get; private set; }

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

            public int FriendCount
            {
                get { return Friends.Count; }
            }

            public FriendInfo[] GetFriends()
            {
                GetFriendsCallCount++;

                if (ThrowOnGetFriends)
                {
                    throw new InvalidOperationException("fake list failure");
                }

                return ReturnNullFriends ? null! : Friends.ToArray();
            }

            public FriendOperationOutcome GiftStamina(string friendId)
            {
                GiftCallCount++;
                LastGiftFriendId = friendId;

                if (ThrowOnGift)
                {
                    throw new InvalidOperationException("fake gift failure");
                }

                return GiftOutcome;
            }

            public FriendOperationOutcome ClaimStamina(string friendId)
            {
                ClaimCallCount++;
                LastClaimFriendId = friendId;

                if (ThrowOnClaim)
                {
                    throw new InvalidOperationException("fake claim failure");
                }

                return ClaimOutcome;
            }

            public int ClaimAllStamina()
            {
                ClaimAllCallCount++;

                if (ThrowOnClaimAll)
                {
                    throw new InvalidOperationException("fake claim-all failure");
                }

                return ClaimAllTotal;
            }

            public void Refresh()
            {
                RefreshCallCount++;

                if (ThrowOnRefresh)
                {
                    throw new InvalidOperationException("fake refresh failure");
                }
            }
        }

        /// <summary>
        /// 可推进的假时钟：跨日判定只使用 <see cref="UtcNow"/>，
        /// 推进它即可精确验证 UTC 00:00 边界，不依赖真实等待或系统时区。
        /// </summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime? startUtc = null)
            {
                _utcNow = startUtc ?? StartUtc;
            }

            /// <summary>读时间是否抛异常（模拟时间源违约）。</summary>
            public bool ThrowOnUtcNow { get; set; }

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get
                {
                    if (ThrowOnUtcNow)
                    {
                        throw new InvalidOperationException("fake clock failure");
                    }

                    return _utcNow;
                }
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

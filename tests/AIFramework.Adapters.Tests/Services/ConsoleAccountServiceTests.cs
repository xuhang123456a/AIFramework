using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleAccountService"/> 的行为契约验收：游客号稳定性、第三方 token 映射、绑定=合并、
    /// 登出保号、封禁/过期/不可用降级与排空日志。
    /// <para>
    /// 这些用例同时是"未来真实账号后端"的验收清单：假后端在这里被允许的行为，
    /// 就是真后端必须做到的最小语义。任何一条在这里放松（例如登出后游客号变了），
    /// 真机上线就会变成"玩家换号即丢档"的真实事故。
    /// </para>
    /// </summary>
    public sealed class ConsoleAccountServiceTests
    {
        /// <summary>测试用固定 UTC 起点（账号创建时间必须可精确断言，不能用真实时间）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void IsAvailable_ReflectsConstructorConfiguration()
        {
            Assert.True(new ConsoleAccountService().IsAvailable);

            var offline = new ConsoleAccountService(new FakeTimeProvider(StartUtc), available: false);
            Assert.False(offline.IsAvailable);
        }

        [Fact]
        public void SignInAsGuest_WhenUnavailable_ReturnsUnavailableWithoutChangingState()
        {
            var service = new ConsoleAccountService(new FakeTimeProvider(StartUtc), available: false);

            AccountLoginOutcome outcome = service.SignInAsGuest();

            Assert.Equal(AccountLoginResult.Unavailable, outcome.Result);
            Assert.Equal(ConsoleAccountService.ReasonUnavailable, outcome.Reason);
            Assert.False(outcome.IsSuccess);
            Assert.Equal(AccountState.SignedOut, service.State);
            Assert.False(service.TryGetAccount(out _));
        }

        [Fact]
        public void SignInAsGuest_CreatesGuestOneWithCreationTime()
        {
            var service = new ConsoleAccountService(new FakeTimeProvider(StartUtc));

            AccountLoginOutcome outcome = service.SignInAsGuest();

            Assert.True(outcome.IsSuccess);
            Assert.Equal("guest-1", outcome.Info.PlayerId);
            Assert.Equal(AccountKind.Guest, outcome.Info.Kind);
            Assert.Equal("Guest-1", outcome.Info.DisplayName);
            Assert.Equal(ExpectedUnixSeconds(StartUtc), outcome.Info.CreatedAtUnixSeconds);

            Assert.Equal(AccountState.SignedIn, service.State);
            Assert.True(service.TryGetAccount(out AccountInfo current));
            Assert.Equal("guest-1", current.PlayerId);
        }

        [Fact]
        public void SignInAsGuest_WithoutTimeProvider_RecordsZeroCreationTime()
        {
            // 没有时钟时刻意记 0：账号创建时间不参与任何判定，宁可空着也不要读系统时间。
            AccountLoginOutcome outcome = new ConsoleAccountService().SignInAsGuest();

            Assert.Equal(0L, outcome.Info.CreatedAtUnixSeconds);
        }

        [Fact]
        public void SignInAsGuest_WhenAlreadySignedIn_ReturnsAlreadySignedIn()
        {
            var service = new ConsoleAccountService();

            service.SignInAsGuest();
            AccountLoginOutcome second = service.SignInAsGuest();

            Assert.Equal(AccountLoginResult.AlreadySignedIn, second.Result);
            Assert.Equal(ConsoleAccountService.ReasonAlreadySignedIn, second.Reason);
            Assert.False(second.IsSuccess);
        }

        [Fact]
        public void SignInAsGuest_AfterSignOut_ReturnsSameGuestAccount()
        {
            // 游客号是设备级的：登出再登录必须回同一个号，否则进度永久留在旧号上。
            var service = new ConsoleAccountService();
            string first = service.SignInAsGuest().Info.PlayerId;

            service.SignOut();
            AccountLoginOutcome second = service.SignInAsGuest();

            Assert.Equal("guest-1", first);
            Assert.Equal(first, second.Info.PlayerId);
        }

        [Fact]
        public void SignOut_ClearsAccountAndIsIdempotent()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            service.SignOut();
            service.SignOut();

            Assert.Equal(AccountState.SignedOut, service.State);
            Assert.False(service.TryGetAccount(out _));
        }

        [Fact]
        public void SignInWithThirdParty_EmptyToken_Rejected()
        {
            var service = new ConsoleAccountService();

            AccountLoginOutcome outcome = service.SignInWithThirdParty(string.Empty);

            Assert.Equal(AccountLoginResult.Rejected, outcome.Result);
            Assert.Equal(ConsoleAccountService.ReasonEmptyToken, outcome.Reason);
            Assert.Equal(AccountState.SignedOut, service.State);
        }

        [Fact]
        public void SignInWithThirdParty_TokenLengthBoundary()
        {
            var service = new ConsoleAccountService();
            var maxToken = new string('a', ConsoleAccountService.MaxPlatformTokenLength);
            var tooLongToken = new string('a', ConsoleAccountService.MaxPlatformTokenLength + 1);

            AccountLoginOutcome rejected = service.SignInWithThirdParty(tooLongToken);
            Assert.Equal(AccountLoginResult.Rejected, rejected.Result);
            Assert.Equal(ConsoleAccountService.ReasonTokenTooLong, rejected.Reason);

            // 正好 512 必须放行（上限是"超过才拒绝"）。
            AccountLoginOutcome accepted = service.SignInWithThirdParty(maxToken);
            Assert.True(accepted.IsSuccess);
            Assert.Equal("tp-1", accepted.Info.PlayerId);
        }

        [Fact]
        public void SignInWithThirdParty_SameToken_AlwaysResolvesToSameAccount()
        {
            var service = new ConsoleAccountService();

            string first = service.SignInWithThirdParty("token-a").Info.PlayerId;
            service.SignOut();
            AccountLoginOutcome second = service.SignInWithThirdParty("token-a");

            Assert.Equal("tp-1", first);
            Assert.Equal(first, second.Info.PlayerId);
            Assert.Equal(1, service.PlatformAccountCount);
        }

        [Fact]
        public void SignInWithThirdParty_DifferentTokens_ProduceDifferentAccounts()
        {
            // 换号：不同平台号必须是不同玩家 id，否则两个平台账号会共用一个存档。
            var service = new ConsoleAccountService();

            string first = service.SignInWithThirdParty("token-a").Info.PlayerId;
            service.SignOut();
            string second = service.SignInWithThirdParty("token-b").Info.PlayerId;

            Assert.NotEqual(first, second);
            Assert.Equal(2, service.PlatformAccountCount);
        }

        [Fact]
        public void SignInWithThirdParty_BannedToken_ReturnsBannedAndStaysBanned()
        {
            var service = new ConsoleAccountService();

            AccountLoginOutcome outcome = service.SignInWithThirdParty(ConsoleAccountService.BannedToken);

            Assert.Equal(AccountLoginResult.Banned, outcome.Result);
            Assert.Equal(ConsoleAccountService.ReasonBanned, outcome.Reason);
            Assert.Equal(AccountState.Banned, service.State);
            Assert.False(service.TryGetAccount(out _));

            // 封禁是终态：游客登录也不许"绕过"它。
            Assert.Equal(AccountLoginResult.Banned, service.SignInAsGuest().Result);
        }

        [Fact]
        public void TryBindThirdParty_WhenSignedOut_FailsWithNotSignedIn()
        {
            var service = new ConsoleAccountService();

            Assert.False(service.TryBindThirdParty("token-a", out string reason));
            Assert.Equal(ConsoleAccountService.ReasonNotSignedIn, reason);
        }

        [Fact]
        public void TryBindThirdParty_EmptyOrTooLongToken_Fails()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            Assert.False(service.TryBindThirdParty(string.Empty, out string emptyReason));
            Assert.Equal(ConsoleAccountService.ReasonEmptyToken, emptyReason);

            Assert.False(service.TryBindThirdParty(
                new string('a', ConsoleAccountService.MaxPlatformTokenLength + 1), out string longReason));
            Assert.Equal(ConsoleAccountService.ReasonTokenTooLong, longReason);
        }

        [Fact]
        public void TryBindThirdParty_BannedToken_FailsWithoutKickingSession()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            Assert.False(service.TryBindThirdParty(ConsoleAccountService.BannedToken, out string reason));
            Assert.Equal(ConsoleAccountService.ReasonBanned, reason);

            // 绑定失败是附加操作失败，不该把玩家从当前会话里踢出去。
            Assert.Equal(AccountState.SignedIn, service.State);
            Assert.True(service.TryGetAccount(out _));
        }

        [Fact]
        public void TryBindThirdParty_Success_UpgradesKindAndKeepsPlayerId()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            Assert.True(service.TryBindThirdParty("token-a", out string reason));
            Assert.Equal(ConsoleAccountService.ReasonBound, reason);

            Assert.True(service.TryGetAccount(out AccountInfo bound));
            Assert.Equal("guest-1", bound.PlayerId);
            Assert.Equal(AccountKind.ThirdParty, bound.Kind);
            Assert.Equal("Guest-1", bound.DisplayName);
        }

        [Fact]
        public void TryBindThirdParty_SameTokenTwice_SecondReturnsAlreadyBound()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();
            service.TryBindThirdParty("token-a", out _);

            Assert.False(service.TryBindThirdParty("token-a", out string reason));
            Assert.Equal(ConsoleAccountService.ReasonAlreadyBound, reason);
        }

        [Fact]
        public void TryBindThirdParty_TokenOfAnotherAccount_RejectedAsClaimed()
        {
            // token-a 已经属于 tp-1；此时再把它绑到 guest-1 等于账号劫持，必须拒绝。
            var service = new ConsoleAccountService();
            service.SignInWithThirdParty("token-a");
            service.SignOut();
            service.SignInAsGuest();

            Assert.False(service.TryBindThirdParty("token-a", out string reason));
            Assert.Equal(ConsoleAccountService.ReasonPlatformClaimed, reason);
        }

        [Fact]
        public void BindThenSignInWithSameToken_ReturnsMergedAccount()
        {
            // 绑定 = 合并：之后用同一 token 重新登录拿回的是被合并的那个号，而不是新建 tp-N。
            var service = new ConsoleAccountService();
            service.SignInAsGuest();
            service.TryBindThirdParty("token-a", out _);
            service.SignOut();

            AccountLoginOutcome relogin = service.SignInWithThirdParty("token-a");

            Assert.True(relogin.IsSuccess);
            Assert.Equal("guest-1", relogin.Info.PlayerId);
            Assert.Equal(AccountKind.ThirdParty, relogin.Info.Kind);
            Assert.Equal(1, service.PlatformAccountCount);
        }

        [Fact]
        public void ExpireSession_ClearsAccountAndAllowsGuestReloginWithSameId()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            service.ExpireSession();

            Assert.Equal(AccountState.Expired, service.State);
            Assert.False(service.TryGetAccount(out _));

            AccountLoginOutcome relogin = service.SignInAsGuest();
            Assert.True(relogin.IsSuccess);
            Assert.Equal("guest-1", relogin.Info.PlayerId);
        }

        [Fact]
        public void ExpireSession_WhenSignedOut_IsIgnored()
        {
            var service = new ConsoleAccountService();

            service.ExpireSession();

            Assert.Equal(AccountState.SignedOut, service.State);
        }

        [Fact]
        public void SetUnavailable_KeepsExistingSessionButBlocksNewSignIn()
        {
            var service = new ConsoleAccountService();
            service.SignInAsGuest();

            service.SetUnavailable();

            // 已建立的会话不会被"断网"销毁（否则离线玩家会被强制踢下线）。
            Assert.False(service.IsAvailable);
            Assert.Equal(AccountState.SignedIn, service.State);
            Assert.True(service.TryGetAccount(out _));

            service.SignOut();
            Assert.Equal(AccountLoginResult.Unavailable, service.SignInAsGuest().Result);
        }

        [Fact]
        public void SetAvailable_RestoresSignInAfterOutage()
        {
            var service = new ConsoleAccountService(available: false);

            service.SetAvailable();
            AccountLoginOutcome outcome = service.SignInAsGuest();

            Assert.True(service.IsAvailable);
            Assert.True(outcome.IsSuccess);
            Assert.Equal("guest-1", outcome.Info.PlayerId);
        }

        [Fact]
        public void TryGetAccount_IsTrueExactlyWhenSignedIn()
        {
            var service = new ConsoleAccountService();

            Assert.False(service.TryGetAccount(out _));

            service.SignInAsGuest();
            Assert.True(service.TryGetAccount(out _));

            service.ExpireSession();
            Assert.False(service.TryGetAccount(out _));

            service.SignInAsGuest();
            Assert.True(service.TryGetAccount(out _));

            service.SignOut();
            Assert.False(service.TryGetAccount(out _));
        }

        [Fact]
        public void DrainLog_RecordsLifecycleAndClears()
        {
            var service = new ConsoleAccountService();

            service.SignInAsGuest();
            service.TryBindThirdParty("token-a", out _);
            service.SignOut();

            IReadOnlyList<string> log = service.DrainLog();

            Assert.NotEmpty(log);
            Assert.Contains("guest sign-in -> Success(guest-1)", log);
            Assert.Contains("bind -> Success(guest-1)", log);
            Assert.Contains("sign-out -> SignedOut", log);

            // 排空语义：取走即清空，避免同一批事件被重复打印。
            Assert.Empty(service.DrainLog());
        }

        /// <summary>把 UTC 时间换算成 Unix 秒（与实现同一口径，但独立计算，避免"实现自证"）。</summary>
        private static long ExpectedUnixSeconds(DateTime utc)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(utc - epoch).TotalSeconds;
        }

        /// <summary>固定时刻的假时钟：账号创建时间必须可精确断言。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private readonly DateTime _utcNow;

            public FakeTimeProvider(DateTime utcNow)
            {
                _utcNow = utcNow;
            }

            /// <summary>账号服务不看单调钟，这里恒为 0。</summary>
            public double ElapsedSeconds
            {
                get { return 0d; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }
        }
    }
}

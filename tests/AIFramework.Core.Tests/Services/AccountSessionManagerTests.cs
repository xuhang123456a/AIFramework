using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="AccountSessionManager"/> 的行为契约验收：启动静默登录、TTL 过期、登出、绑定、
    /// 换号、后端不可用/违约/抛异常的降级，以及"状态与账号快照必须一致"这条不变量。
    /// <para>
    /// <b>这些用例守的是账号</b>：账号错了不是"显示不对"，而是存档写到别人名下、或玩家换机后找不到进度。
    /// 所以断言分成两类——一类钉死状态（<see cref="AccountState"/>），一类钉死<b>不做事</b>
    /// （不静默重登、不静默换号、不因为一次失败踢掉现有会话）。后者比前者更重要。
    /// </para>
    /// <para>
    /// 替身刻意只用"可拨动的假后端"：过期、封禁、抛异常这些分支在真实 SDK 上极难复现，
    /// 只能靠假后端逐条钉住；时间用假时钟（<see cref="FakeTimeProvider"/>），绝不依赖真实等待。
    /// </para>
    /// </summary>
    public sealed class AccountSessionManagerTests
    {
        /// <summary>默认游客账号（假后端首次登录返回它）。</summary>
        private static readonly AccountInfo GuestAccount =
            new AccountInfo("guest-1", AccountKind.Guest, "Guest-1", 1763596800L);

        /// <summary>默认第三方账号。</summary>
        private static readonly AccountInfo PlatformAccount =
            new AccountInfo("tp-1", AccountKind.ThirdParty, "ThirdParty-1", 1763596800L);

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            var time = new FakeTimeProvider();
            var service = new FakeAccountService();

            // 两个依赖缺一不可：没有后端无法登录，没有时钟无法判过期（且纯逻辑层禁止读系统时间）。
            Assert.Throws<ArgumentNullException>(() => new AccountSessionManager(null!, time));
            Assert.Throws<ArgumentNullException>(() => new AccountSessionManager(service, null!));
        }

        [Fact]
        public void Constructor_StartsSignedOutWithoutAccount()
        {
            var service = new FakeAccountService();
            var manager = CreateManager(service, new FakeTimeProvider());

            Assert.Equal(AccountState.SignedOut, manager.State);
            Assert.False(manager.TryGetAccount(out _));
            Assert.False(manager.HasPlatformToken);
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void Constructor_AdoptsExistingBackendSession()
        {
            // 装配顺序（Host 先建服务、后建管理器）不该让已登录玩家被当成未登录。
            var service = new FakeAccountService { State = AccountState.SignedIn, Current = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.True(manager.TryGetAccount(out AccountInfo info));
            Assert.Equal(GuestAccount.PlayerId, info.PlayerId);

            // 接管发生在构造期：没有触发任何一次登录调用。
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void Constructor_AdoptsBackendBannedState()
        {
            var service = new FakeAccountService { State = AccountState.Banned };
            var manager = CreateManager(service, new FakeTimeProvider());

            Assert.Equal(AccountState.Banned, manager.State);
            Assert.False(manager.TryGetAccount(out _));
        }

        [Fact]
        public void EnsureSignedIn_FirstCall_SignsInAsGuestAndExposesAccount()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.EnsureSignedIn();

            Assert.Equal(AccountLoginResult.Success, outcome.Result);
            Assert.True(outcome.IsSuccess);

            // 成功原因由后端给出（各实现文案不同，本类只保证失败/续期那几条自有原因稳定）。
            Assert.Equal("fake signed in", outcome.Reason);
            Assert.Equal(GuestAccount.PlayerId, outcome.Info.PlayerId);
            Assert.Equal(AccountKind.Guest, outcome.Info.Kind);

            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(GuestAccount.PlayerId, current.PlayerId);
            Assert.Equal(1, service.SignInCallCount);
        }

        [Fact]
        public void EnsureSignedIn_SecondCall_ReusesSessionWithoutTouchingBackend()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.EnsureSignedIn();
            AccountLoginOutcome second = manager.EnsureSignedIn();
            AccountLoginOutcome third = manager.EnsureSignedIn();

            Assert.Equal(AccountLoginResult.Success, second.Result);
            Assert.Equal(AccountSessionManager.ReasonSessionReused, second.Reason);
            Assert.True(third.IsSuccess);

            // 静默登录不是"再登一次"：后端只被调用过一次。
            Assert.Equal(1, service.SignInCallCount);
            Assert.Equal(AccountState.SignedIn, manager.State);
        }

        [Fact]
        public void EnsureSignedIn_BackendUnavailable_ReturnsUnavailableAndStaysSignedOut()
        {
            var service = new FakeAccountService { Available = false };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.EnsureSignedIn();

            Assert.Equal(AccountLoginResult.Unavailable, outcome.Result);
            Assert.False(outcome.IsSuccess);
            Assert.Equal(AccountSessionManager.ReasonUnavailable, outcome.Reason);
            Assert.Equal(AccountState.SignedOut, manager.State);

            // 不可用时连后端都不该碰（省掉一次注定失败的平台调用）。
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void EnsureSignedIn_RepeatedWhileUnavailable_NeverThrows()
        {
            var service = new FakeAccountService { Available = false };
            var manager = CreateManager(service, new FakeTimeProvider());

            // 断网 + 每帧重试是最常见的崩溃场景，必须稳定降级。
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(AccountLoginResult.Unavailable, manager.EnsureSignedIn().Result);
            }

            Assert.Equal(AccountState.SignedOut, manager.State);
        }

        [Fact]
        public void EnsureSignedIn_WhenBanned_ReturnsBanned()
        {
            var service = new FakeAccountService { State = AccountState.Banned };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.EnsureSignedIn();

            Assert.Equal(AccountLoginResult.Banned, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonBanned, outcome.Reason);
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void SignInAsGuest_WhenBackendThrows_ReturnsUnavailableWithTypeNameOnly()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount, ThrowOnSignIn = true };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInAsGuest();

            Assert.Equal(AccountLoginResult.Unavailable, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonServiceThrew + ": InvalidOperationException", outcome.Reason);

            // 原因里不能有堆栈/换行（会污染日志、泄露内部路径）。
            Assert.DoesNotContain(" at ", outcome.Reason);
            Assert.DoesNotContain("\n", outcome.Reason);

            // 异常不许穿透，也不许把状态改成"已登录"。
            Assert.Equal(AccountState.SignedOut, manager.State);
        }

        [Fact]
        public void SignInWithThirdParty_WhenBackendThrows_ReturnsUnavailableAndKeepsTokenOutOfState()
        {
            var service = new FakeAccountService { ThrowOnSignIn = true };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInWithThirdParty("platform-token-a");

            Assert.Equal(AccountLoginResult.Unavailable, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonServiceThrew + ": InvalidOperationException", outcome.Reason);
            Assert.Equal("platform-token-a", service.LastSignInToken);
            Assert.Equal(AccountState.SignedOut, manager.State);
            Assert.False(manager.HasPlatformToken);
        }

        [Fact]
        public void SignInWithThirdParty_EmptyOrNullToken_RejectedWithoutBackendCall()
        {
            var service = new FakeAccountService();
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome empty = manager.SignInWithThirdParty(string.Empty);
            AccountLoginOutcome nullToken = manager.SignInWithThirdParty(null!);

            Assert.Equal(AccountLoginResult.Rejected, empty.Result);
            Assert.Equal(AccountSessionManager.ReasonEmptyToken, empty.Reason);
            Assert.Equal(AccountLoginResult.Rejected, nullToken.Result);
            Assert.Equal(AccountSessionManager.ReasonEmptyToken, nullToken.Reason);
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void SignInWithThirdParty_TooLongToken_RejectedWithoutBackendCall()
        {
            var service = new FakeAccountService();
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInWithThirdParty(
                new string('a', AccountSessionManager.MaxPlatformTokenLength + 1));

            Assert.Equal(AccountLoginResult.Rejected, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonTokenTooLong, outcome.Reason);
            Assert.Equal(0, service.SignInCallCount);
        }

        [Fact]
        public void SignInWithThirdParty_MaxLengthToken_ReachesBackend()
        {
            // 边界：正好 512 必须放行（上限是"超过 512 才拒绝"）。
            var service = new FakeAccountService { NextAccount = PlatformAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInWithThirdParty(
                new string('a', AccountSessionManager.MaxPlatformTokenLength));

            Assert.Equal(AccountLoginResult.Success, outcome.Result);
            Assert.Equal(1, service.SignInCallCount);
            Assert.Equal(AccountKind.ThirdParty, outcome.Info.Kind);
        }

        [Fact]
        public void SignInWithThirdParty_Success_MarksPlatformToken()
        {
            var service = new FakeAccountService { NextAccount = PlatformAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInWithThirdParty("platform-token-a");

            Assert.True(outcome.IsSuccess);
            Assert.Equal(PlatformAccount.PlayerId, outcome.Info.PlayerId);
            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.True(manager.HasPlatformToken);
        }

        [Fact]
        public void SignInAsGuest_WhenAlreadySignedIn_ReturnsAlreadySignedInAndKeepsAccount()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            AccountLoginOutcome second = manager.SignInAsGuest();

            // 没换号就是没换号：不能谎报 Success（否则玩家以为切了号，实际还在旧号上）。
            Assert.Equal(AccountLoginResult.AlreadySignedIn, second.Result);
            Assert.False(second.IsSuccess);
            Assert.Equal(GuestAccount.PlayerId, service.Current.PlayerId);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(GuestAccount.PlayerId, current.PlayerId);
        }

        [Fact]
        public void SignInWithThirdParty_WhenAlreadySignedIn_DoesNotSwitchAccount()
        {
            // 换号必须先登出：一次"用平台号登录"的调用不允许静默顶掉当前会话。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            AccountLoginOutcome attempted = manager.SignInWithThirdParty("platform-token-a");

            Assert.Equal(AccountLoginResult.AlreadySignedIn, attempted.Result);
            Assert.False(manager.HasPlatformToken);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(GuestAccount.PlayerId, current.PlayerId);
            Assert.Equal(AccountKind.Guest, current.Kind);
        }

        [Fact]
        public void SignOut_FromSignedIn_ClearsEverythingAndRaisesOneEvent()
        {
            var service = new FakeAccountService { NextAccount = PlatformAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.SignInWithThirdParty("platform-token-a");
            manager.SignOut();

            Assert.Equal(AccountState.SignedOut, manager.State);
            Assert.False(manager.TryGetAccount(out _));
            Assert.False(manager.HasPlatformToken);
            Assert.Equal(1, service.SignOutCallCount);
            Assert.Equal(
                new[] { AccountState.SignedIn, AccountState.SignedOut },
                events);
        }

        [Fact]
        public void SignOut_Repeated_IsIdempotentAndRaisesNoSecondEvent()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.SignInAsGuest();
            manager.SignOut();
            manager.SignOut();
            manager.SignOut();

            Assert.Equal(AccountState.SignedOut, manager.State);

            // 只有一次 SignedOut（重复登出不该刷事件，否则 UI 会重复弹"已退出"）。
            Assert.Collection(
                events,
                state => Assert.Equal(AccountState.SignedIn, state),
                state => Assert.Equal(AccountState.SignedOut, state));
        }

        [Fact]
        public void SignOut_WhenBackendThrows_StillClearsLocalState()
        {
            // 服务端异常也不能把玩家卡在"退不出去"的状态（共享设备上是安全问题）。
            var service = new FakeAccountService { NextAccount = GuestAccount, ThrowOnSignOut = true };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            manager.SignOut();

            Assert.Equal(AccountState.SignedOut, manager.State);
            Assert.False(manager.TryGetAccount(out _));
        }

        [Fact]
        public void SignOut_FromExpired_TransitionsToSignedOut()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 10);

            manager.SignInAsGuest();
            time.Advance(11);
            manager.Tick();
            Assert.Equal(AccountState.Expired, manager.State);

            manager.SignOut();
            Assert.Equal(AccountState.SignedOut, manager.State);
        }

        [Fact]
        public void Tick_AfterTtlExpires_RaisesExpiredEventOnlyOnce()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 100);
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.EnsureSignedIn();
            time.Advance(99);
            manager.Tick();
            Assert.Equal(AccountState.SignedIn, manager.State);

            time.Advance(2);
            manager.Tick();
            manager.Tick();
            manager.Tick();

            Assert.Equal(AccountState.Expired, manager.State);
            Assert.False(manager.TryGetAccount(out _));

            Assert.Collection(
                events,
                state => Assert.Equal(AccountState.SignedIn, state),
                state => Assert.Equal(AccountState.Expired, state));

            // 三次 Tick 只多出一条事件：Expired 之后不再重复通知。
            Assert.Equal(1, CountOf(events, AccountState.Expired));
        }

        [Fact]
        public void Tick_WhenSignedOut_DoesNothing()
        {
            var manager = CreateManager(new FakeAccountService(), new FakeTimeProvider());
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.Tick();
            manager.Tick();

            Assert.Empty(events);
            Assert.Equal(AccountState.SignedOut, manager.State);
        }

        [Fact]
        public void Tick_WhenBackendReportsExpired_MarksExpiredEvenBeforeTtl()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider(), ttlSeconds: 86400);

            manager.SignInAsGuest();
            service.State = AccountState.Expired;
            manager.Tick();

            Assert.Equal(AccountState.Expired, manager.State);
            Assert.False(manager.TryGetAccount(out _));
        }

        [Fact]
        public void Tick_WhenBackendReportsBanned_MarksBannedAndDropsPlatformToken()
        {
            var service = new FakeAccountService { NextAccount = PlatformAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInWithThirdParty("platform-token-a");
            Assert.True(manager.HasPlatformToken);

            service.State = AccountState.Banned;
            manager.Tick();

            Assert.Equal(AccountState.Banned, manager.State);
            Assert.False(manager.TryGetAccount(out _));

            // 封禁后凭据必须丢弃：后续请求不该继续带一个已被封的 token。
            Assert.False(manager.HasPlatformToken);
        }

        [Fact]
        public void Tick_WhenBackendReportsSignedOut_KeepsLocalSession()
        {
            // 真实 SDK 在初始化/重连期间会短暂报 SignedOut，跟着它走会把在线玩家莫名踢下线。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            service.State = AccountState.SignedOut;
            manager.Tick();

            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.True(manager.TryGetAccount(out _));
        }

        [Fact]
        public void EnsureSignedIn_AfterExpiry_ReturnsRejectedAndNeverAutoRelogins()
        {
            // 静默重登最可能的结局是拿到一个全新游客号 = 静默丢档，因此刻意不重登。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 100);

            manager.EnsureSignedIn();
            time.Advance(101);

            AccountLoginOutcome outcome = manager.EnsureSignedIn();

            Assert.Equal(AccountLoginResult.Rejected, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonSessionExpired, outcome.Reason);
            Assert.Equal(AccountState.Expired, manager.State);
            Assert.Equal(1, service.SignInCallCount);
        }

        [Fact]
        public void SignInAsGuest_AfterExpiry_RenewsSameAccount()
        {
            // 显式重新登录命中后端既有会话 → 续期成功且玩家 id 不变（不换号）。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 100);

            manager.EnsureSignedIn();
            time.Advance(101);
            manager.Tick();

            AccountLoginOutcome renewed = manager.SignInAsGuest();

            Assert.Equal(AccountLoginResult.Success, renewed.Result);
            Assert.Equal(AccountSessionManager.ReasonSessionRenewed, renewed.Reason);
            Assert.Equal(GuestAccount.PlayerId, renewed.Info.PlayerId);
            Assert.Equal(AccountState.SignedIn, manager.State);

            // 续期刷新 TTL：从续期那一刻重新计时。
            time.Advance(99);
            manager.Tick();
            Assert.Equal(AccountState.SignedIn, manager.State);

            time.Advance(2);
            manager.Tick();
            Assert.Equal(AccountState.Expired, manager.State);
        }

        [Fact]
        public void SignInWithThirdParty_AfterSignOut_SwitchesToNewAccount()
        {
            // 换号：登出 → 平台登录，账号与类型都变。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            manager.SignOut();

            service.NextAccount = PlatformAccount;
            AccountLoginOutcome switched = manager.SignInWithThirdParty("platform-token-b");

            Assert.True(switched.IsSuccess);
            Assert.Equal(PlatformAccount.PlayerId, switched.Info.PlayerId);
            Assert.Equal(AccountKind.ThirdParty, switched.Info.Kind);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(PlatformAccount.PlayerId, current.PlayerId);
            Assert.True(manager.HasPlatformToken);
        }

        [Fact]
        public void SignInAsGuest_AfterSignOut_ClearsPlatformToken()
        {
            // 从平台号退回游客号：旧平台凭据必须丢弃，否则"已绑定第三方"的展示会是错的。
            var service = new FakeAccountService { NextAccount = PlatformAccount };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInWithThirdParty("platform-token-a");
            manager.SignOut();

            service.NextAccount = GuestAccount;
            manager.SignInAsGuest();

            Assert.False(manager.HasPlatformToken);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(AccountKind.Guest, current.Kind);
        }

        [Fact]
        public void SignIn_BackendSuccessWithoutPlayerId_TreatedAsUnavailable()
        {
            // 违约后端：Success 却没有玩家 id。采纳它会让所有写操作挂到空主键上。
            var service = new FakeAccountService { NextAccount = default };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInAsGuest();

            Assert.Equal(AccountLoginResult.Unavailable, outcome.Result);
            Assert.Equal(AccountSessionManager.ReasonEmptyPlayerId, outcome.Reason);
            Assert.Equal(AccountState.SignedOut, manager.State);
            Assert.False(manager.TryGetAccount(out _));

            // 后端那个空会话必须被复位：否则它会一直回 AlreadySignedIn，之后每次登录都拿不回可用会话。
            Assert.Equal(1, service.SignOutCallCount);
            Assert.Equal(AccountState.SignedOut, service.State);

            // 复位之后重试能正常回到登录态。
            service.NextAccount = GuestAccount;
            Assert.True(manager.SignInAsGuest().IsSuccess);
            Assert.Equal(AccountState.SignedIn, manager.State);
        }

        [Fact]
        public void TryBindThirdParty_Success_UpgradesKindAndKeepsPlayerId()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.SignInAsGuest();
            bool bound = manager.TryBindThirdParty("platform-token-a", out string reason);

            Assert.True(bound);
            Assert.Equal(FakeAccountService.DefaultBindReason, reason);
            Assert.True(manager.TryGetAccount(out AccountInfo boundAccount));
            Assert.Equal(GuestAccount.PlayerId, boundAccount.PlayerId);
            Assert.Equal(AccountKind.ThirdParty, boundAccount.Kind);
            Assert.True(manager.HasPlatformToken);

            // 绑定不改状态（还是 SignedIn），因此不额外发事件。
            Assert.Single(events);
        }

        [Fact]
        public void TryBindThirdParty_WhenBackendDoesNotRefreshKind_UpgradesLocally()
        {
            // 后端只回 bool、不刷新账号类型时，绑定成功也必须体现为"不再是纯游客"。
            var service = new FakeAccountService { NextAccount = GuestAccount, BindUpgradesKind = false };
            var manager = CreateManager(service, new FakeTimeProvider());

            manager.SignInAsGuest();
            Assert.True(manager.TryBindThirdParty("platform-token-a", out _));

            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(AccountKind.ThirdParty, current.Kind);
            Assert.Equal(GuestAccount.PlayerId, current.PlayerId);
        }

        [Fact]
        public void TryBindThirdParty_WhenSignedOut_FailsWithoutBackendCall()
        {
            var service = new FakeAccountService();
            var manager = CreateManager(service, new FakeTimeProvider());

            bool bound = manager.TryBindThirdParty("platform-token-a", out string reason);

            Assert.False(bound);
            Assert.Equal(AccountSessionManager.ReasonNotSignedIn, reason);
            Assert.Equal(0, service.BindCallCount);
        }

        [Fact]
        public void TryBindThirdParty_WhenExpired_FailsWithSessionExpired()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 10);

            manager.SignInAsGuest();
            time.Advance(11);
            manager.Tick();

            Assert.False(manager.TryBindThirdParty("platform-token-a", out string reason));
            Assert.Equal(AccountSessionManager.ReasonSessionExpired, reason);
            Assert.Equal(0, service.BindCallCount);
        }

        [Fact]
        public void TryBindThirdParty_EmptyToken_FailsWithoutBackendCall()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            manager.SignInAsGuest();

            Assert.False(manager.TryBindThirdParty(string.Empty, out string reason));
            Assert.Equal(AccountSessionManager.ReasonEmptyToken, reason);
            Assert.Equal(0, service.BindCallCount);
        }

        [Fact]
        public void TryBindThirdParty_TooLongToken_FailsWithoutBackendCall()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            manager.SignInAsGuest();

            Assert.False(manager.TryBindThirdParty(
                new string('a', AccountSessionManager.MaxPlatformTokenLength + 1), out string reason));
            Assert.Equal(AccountSessionManager.ReasonTokenTooLong, reason);
            Assert.Equal(0, service.BindCallCount);
        }

        [Fact]
        public void TryBindThirdParty_BackendRejects_PassesThroughBackendReason()
        {
            var service = new FakeAccountService
            {
                NextAccount = GuestAccount,
                BindResult = false,
                BindReason = "platform account claimed"
            };
            var manager = CreateManager(service, new FakeTimeProvider());
            manager.SignInAsGuest();

            Assert.False(manager.TryBindThirdParty("platform-token-a", out string reason));
            Assert.Equal("platform account claimed", reason);
            Assert.False(manager.HasPlatformToken);
            Assert.True(manager.TryGetAccount(out AccountInfo current));
            Assert.Equal(AccountKind.Guest, current.Kind);
        }

        [Fact]
        public void TryBindThirdParty_BackendThrows_FailsWithTypeNameAndKeepsSession()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount, ThrowOnBind = true };
            var manager = CreateManager(service, new FakeTimeProvider());
            manager.SignInAsGuest();

            Assert.False(manager.TryBindThirdParty("platform-token-a", out string reason));
            Assert.Equal(AccountSessionManager.ReasonServiceThrew + ": InvalidOperationException", reason);

            // 绑定失败不踢人：会话照旧可用。
            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.True(manager.TryGetAccount(out _));
        }

        [Fact]
        public void Tick_WithDisabledTtl_NeverExpires()
        {
            // ttl <= 0 视为关闭 TTL（0 秒有效期是配置事故，不是"登录即过期"的需求）。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 0);

            manager.SignInAsGuest();
            time.Advance(1000000);
            manager.Tick();

            Assert.Equal(AccountState.SignedIn, manager.State);
        }

        [Fact]
        public void StateChanged_SubscriberThrows_DoesNotCorruptStateOrBlockOtherSubscribers()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var manager = CreateManager(service, new FakeTimeProvider());
            var observed = new List<AccountState>();

            manager.StateChanged += _ => throw new InvalidOperationException("ui exploded");
            manager.StateChanged += observed.Add;

            // 一个坏订阅者不许把"登录成功"变成异常，也不许挡住后面的订阅者。
            AccountLoginOutcome outcome = manager.EnsureSignedIn();

            Assert.True(outcome.IsSuccess);
            Assert.Equal(AccountState.SignedIn, manager.State);
            Assert.Single(observed);
            Assert.Equal(AccountState.SignedIn, observed[0]);
        }

        [Fact]
        public void StateChanged_FullLifecycle_ReportsOrderedTransitions()
        {
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 10);
            var events = new List<AccountState>();
            manager.StateChanged += events.Add;

            manager.EnsureSignedIn();
            time.Advance(11);
            manager.Tick();
            manager.SignOut();

            Assert.Equal(
                new[] { AccountState.SignedIn, AccountState.Expired, AccountState.SignedOut },
                events);
        }

        [Fact]
        public void TryGetAccount_IsTrueExactlyWhenSignedIn()
        {
            // 这条不变量是调用方判断"能不能写存档"的闸门，必须全程成立。
            var service = new FakeAccountService { NextAccount = GuestAccount };
            var time = new FakeTimeProvider();
            var manager = CreateManager(service, time, ttlSeconds: 10);

            Assert.False(manager.TryGetAccount(out _));

            manager.EnsureSignedIn();
            Assert.True(manager.TryGetAccount(out _));

            time.Advance(11);
            manager.Tick();
            Assert.False(manager.TryGetAccount(out _));

            manager.SignOut();
            Assert.False(manager.TryGetAccount(out _));

            service.NextAccount = GuestAccount;
            manager.SignInAsGuest();
            Assert.True(manager.TryGetAccount(out _));
        }

        [Fact]
        public void SignInAsGuest_WhenBackendAlreadyBanned_ReturnsBannedAndStaysBanned()
        {
            var service = new FakeAccountService { State = AccountState.Banned };
            var manager = CreateManager(service, new FakeTimeProvider());

            AccountLoginOutcome outcome = manager.SignInAsGuest();

            Assert.Equal(AccountLoginResult.Banned, outcome.Result);
            Assert.Equal(AccountState.Banned, manager.State);
        }

        /// <summary>创建被测管理器（默认 TTL）。</summary>
        private static AccountSessionManager CreateManager(
            FakeAccountService service,
            FakeTimeProvider time,
            long ttlSeconds = AccountSessionManager.DefaultSessionTtlSeconds)
        {
            return new AccountSessionManager(service, time, ttlSeconds);
        }

        /// <summary>统计某个状态在事件序列里出现的次数。</summary>
        private static int CountOf(List<AccountState> events, AccountState state)
        {
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] == state)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 可拨动的假账号后端：把真实 SDK 里极难复现的分支（不可用、抛异常、已登录、
        /// 封禁、绑定被拒）变成一次赋值，用来逐条钉死管理器的降级行为。
        /// </summary>
        private sealed class FakeAccountService : IAccountService
        {
            /// <summary>绑定成功时的默认原因。</summary>
            public const string DefaultBindReason = "bound";

            /// <summary>后端是否可用。</summary>
            public bool Available { get; set; } = true;

            /// <summary>后端状态（测试直接拨）。</summary>
            public AccountState State { get; set; } = AccountState.SignedOut;

            /// <summary>后端当前账号。</summary>
            public AccountInfo Current { get; set; }

            /// <summary>下一次成功登录会返回的账号。</summary>
            public AccountInfo NextAccount { get; set; }

            /// <summary>登录是否抛异常（模拟 SDK 违约）。</summary>
            public bool ThrowOnSignIn { get; set; }

            /// <summary>登出是否抛异常。</summary>
            public bool ThrowOnSignOut { get; set; }

            /// <summary>绑定是否抛异常。</summary>
            public bool ThrowOnBind { get; set; }

            /// <summary>绑定是否成功。</summary>
            public bool BindResult { get; set; } = true;

            /// <summary>绑定返回的原因。</summary>
            public string BindReason { get; set; } = DefaultBindReason;

            /// <summary>绑定成功后是否同步升级 <see cref="Current"/> 的类型。</summary>
            public bool BindUpgradesKind { get; set; } = true;

            /// <summary>登录调用次数。</summary>
            public int SignInCallCount { get; private set; }

            /// <summary>登出调用次数。</summary>
            public int SignOutCallCount { get; private set; }

            /// <summary>绑定调用次数。</summary>
            public int BindCallCount { get; private set; }

            /// <summary>最近一次登录使用的 token。</summary>
            public string? LastSignInToken { get; private set; }

            public bool IsAvailable
            {
                get { return Available; }
            }

            public AccountLoginOutcome SignInAsGuest()
            {
                return SignIn(null);
            }

            public AccountLoginOutcome SignInWithThirdParty(string platformToken)
            {
                return SignIn(platformToken);
            }

            public bool TryBindThirdParty(string platformToken, out string reason)
            {
                BindCallCount++;

                if (ThrowOnBind)
                {
                    throw new InvalidOperationException("fake bind failure");
                }

                if (!BindResult)
                {
                    reason = BindReason;
                    return false;
                }

                if (BindUpgradesKind)
                {
                    Current = new AccountInfo(
                        Current.PlayerId, AccountKind.ThirdParty, Current.DisplayName, Current.CreatedAtUnixSeconds);
                }

                reason = BindReason;
                return true;
            }

            public void SignOut()
            {
                SignOutCallCount++;

                if (ThrowOnSignOut)
                {
                    throw new InvalidOperationException("fake sign-out failure");
                }

                State = AccountState.SignedOut;
                Current = default;
            }

            public bool TryGetAccount(out AccountInfo info)
            {
                if (State == AccountState.SignedIn)
                {
                    info = Current;
                    return true;
                }

                info = default;
                return false;
            }

            /// <summary>统一的登录实现（真实后端在已登录时同样回 AlreadySignedIn）。</summary>
            private AccountLoginOutcome SignIn(string? platformToken)
            {
                SignInCallCount++;
                LastSignInToken = platformToken;

                if (ThrowOnSignIn)
                {
                    throw new InvalidOperationException("fake sign-in failure");
                }

                if (State == AccountState.SignedIn)
                {
                    return new AccountLoginOutcome(AccountLoginResult.AlreadySignedIn, default, "fake already signed in");
                }

                if (State == AccountState.Banned)
                {
                    return new AccountLoginOutcome(AccountLoginResult.Banned, default, "fake banned");
                }

                Current = NextAccount;
                State = AccountState.SignedIn;
                return new AccountLoginOutcome(AccountLoginResult.Success, NextAccount, "fake signed in");
            }
        }

        /// <summary>
        /// 可推进的假时钟：TTL 判定只使用 <see cref="ElapsedSeconds"/>（单调钟），
        /// 推进它即可精确验证"24 小时会话"的边界，不依赖真实等待。
        /// </summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime? startUtc = null)
            {
                _utcNow = startUtc ?? new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
            }

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            /// <summary>同时推进单调钟与墙钟。</summary>
            public void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }
    }
}

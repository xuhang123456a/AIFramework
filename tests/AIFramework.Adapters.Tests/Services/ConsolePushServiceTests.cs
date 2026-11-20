using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsolePushService"/> 的行为契约验收：权限三态、token 登记与校验、排期/覆盖/取消、
    /// 计划排序、不可用降级、环形日志。
    /// <para>
    /// 这些用例同时是"未来真实推送后端"的验收清单：假后端在这里被允许的行为，就是真后端必须做到的最小语义。
    /// 任何一条在这里放松（例如未授权也允许登记 token、同 id 变成两条计划、未启用时仍报可用），
    /// 真机上线就是"换号后收到前一个账号的提醒"或"改时间后一天弹两次"的真实事故。
    /// </para>
    /// <para>时间一律用假时钟：排期必须可精确断言，不能依赖真实等待，也不允许本类偷偷读系统时区。</para>
    /// </summary>
    public sealed class ConsolePushServiceTests
    {
        /// <summary>测试用固定 UTC 起点（排期与"立即"锚点都必须可精确断言）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Unix 纪元（换算用）。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Constructor_Default_IsAvailableWithUnknownPermission()
        {
            var service = new ConsolePushService();

            Assert.True(service.IsAvailable);

            // 构造后"还没问过"，必须先 RequestPermission；假实现不替你自动授权，
            // 否则"忘了申请权限"这个真机上最典型的 bug 在无头测试里永远看不见。
            Assert.Equal(PushPermissionState.Unknown, service.PermissionState);
            Assert.Equal(string.Empty, service.RegisteredToken);
            Assert.Empty(service.GetScheduled());
        }

        [Fact]
        public void Constructor_Unavailable_ReportsUnavailableAndNoPlan()
        {
            var service = new ConsolePushService(available: false);

            Assert.False(service.IsAvailable);
            Assert.Equal(PushPermissionState.Unknown, service.PermissionState);
            Assert.Empty(service.GetScheduled());
        }

        [Fact]
        public void RequestPermission_WhenAvailable_Grants()
        {
            var service = new ConsolePushService();

            Assert.Equal(PushPermissionState.Granted, service.RequestPermission());
            Assert.Equal(PushPermissionState.Granted, service.PermissionState);
        }

        [Fact]
        public void RequestPermission_WhenUnavailable_Denies()
        {
            var service = new ConsolePushService(available: false);

            // 请求发出去了却没被授权：停在 Unknown 会让 UI 以为"还没问过"并反复触发授权流程。
            Assert.Equal(PushPermissionState.Denied, service.RequestPermission());
            Assert.Equal(PushPermissionState.Denied, service.PermissionState);
        }

        [Fact]
        public void ExpirePermission_ResetsToUnknownAndCanBeGrantedAgain()
        {
            var service = new ConsolePushService();
            service.RequestPermission();

            service.ExpirePermission();

            Assert.Equal(PushPermissionState.Unknown, service.PermissionState);

            // 撤销后重新申请必须能再次拿到授权（真机上对应"玩家去系统设置里打开通知"）。
            Assert.Equal(PushPermissionState.Granted, service.RequestPermission());
        }

        [Fact]
        public void RegisterToken_WithoutPermission_ReturnsPermissionDenied()
        {
            var service = new ConsolePushService();

            // 权限还是 Unknown：真机上没有授权就拿不到可用 token，登记必然失败。
            Assert.Equal(PushRegisterResult.PermissionDenied, service.RegisterToken("token-a"));
            Assert.Equal(string.Empty, service.RegisteredToken);
        }

        [Fact]
        public void RegisterToken_AfterPermission_SucceedsAndExposesToken()
        {
            var service = new ConsolePushService();
            service.RequestPermission();

            Assert.Equal(PushRegisterResult.Success, service.RegisterToken("fcm:APA91bF-example_token.123"));
            Assert.Equal("fcm:APA91bF-example_token.123", service.RegisteredToken);
        }

        [Fact]
        public void RegisterToken_InvalidToken_ReturnsInvalidToken()
        {
            var service = new ConsolePushService();
            service.RequestPermission();

            // 校验规则与 Core 共用同一套（PushScheduler.IsValidToken），不另写一份。
            Assert.Equal(PushRegisterResult.InvalidToken, service.RegisterToken(null!));
            Assert.Equal(PushRegisterResult.InvalidToken, service.RegisterToken(string.Empty));
            Assert.Equal(PushRegisterResult.InvalidToken, service.RegisterToken("   "));
            Assert.Equal(PushRegisterResult.InvalidToken, service.RegisterToken("abc def"));
            Assert.Equal(PushRegisterResult.InvalidToken, service.RegisterToken("中文token"));
            Assert.Equal(
                PushRegisterResult.InvalidToken,
                service.RegisterToken(new string('a', PushScheduler.MaxTokenLength + 1)));

            Assert.Equal(string.Empty, service.RegisteredToken);
        }

        [Fact]
        public void RegisterToken_WhenUnavailable_ReturnsUnavailable()
        {
            var service = new ConsolePushService(available: false);

            Assert.Equal(PushRegisterResult.Unavailable, service.RegisterToken("token-a"));
        }

        [Fact]
        public void UnregisterToken_ClearsRegisteredToken()
        {
            var service = new ConsolePushService();
            service.RequestPermission();
            service.RegisterToken("token-a");

            service.UnregisterToken();

            // 登出必须清掉凭据，否则下一个账号会继承它（隐私泄露 + 客诉）。
            Assert.Equal(string.Empty, service.RegisteredToken);
        }

        [Fact]
        public void UnregisterToken_WhenNothingRegistered_IsIdempotent()
        {
            var service = new ConsolePushService();

            service.UnregisterToken();
            service.UnregisterToken();

            Assert.Equal(string.Empty, service.RegisteredToken);
        }

        [Fact]
        public void Schedule_WhenUnavailable_ReturnsFalseAndKeepsPlanEmpty()
        {
            var service = new ConsolePushService(available: false);

            Assert.False(service.Schedule(new PushNotificationRequest(1, "t", "b", 2000, 0)));
            Assert.Empty(service.GetScheduled());
        }

        [Fact]
        public void Schedule_DoesNotRequirePermission_MatchingPlatformBehaviour()
        {
            var service = new ConsolePushService();

            // 刻意的不对称：本地通知的排期接口在未授权时通常照样接受，只是到点不显示；
            // 而 token 登记在未授权时必然失败（见 RegisterToken_WithoutPermission）。
            Assert.True(service.Schedule(new PushNotificationRequest(1, "t", "b", 2000, 0)));
            Assert.Equal(PushPermissionState.Unknown, service.PermissionState);
            Assert.Single(service.GetScheduled());
        }

        [Fact]
        public void Schedule_ThenGetScheduled_SortsByFireTime()
        {
            var service = new ConsolePushService();

            // 刻意乱序排入，验证输出按触发时间升序。
            service.Schedule(new PushNotificationRequest(3, "第三", "b", 3000, 0));
            service.Schedule(new PushNotificationRequest(1, "第一", "b", 1000, 0));
            service.Schedule(new PushNotificationRequest(2, "第二", "b", 2000, 0));

            PushNotification[] scheduled = service.GetScheduled();

            Assert.Equal(3, scheduled.Length);
            Assert.Equal(1, scheduled[0].NotificationId);
            Assert.Equal(2, scheduled[1].NotificationId);
            Assert.Equal(3, scheduled[2].NotificationId);
            Assert.Equal("第一", scheduled[0].Title);
            Assert.Equal(1000, scheduled[0].FireAtUnixSeconds);
        }

        [Fact]
        public void Schedule_SameIdTwice_OverwritesPrevious()
        {
            var service = new ConsolePushService();

            service.Schedule(new PushNotificationRequest(1, "旧文案", "b", 1000, 0));
            service.Schedule(new PushNotificationRequest(1, "新文案", "b", 5000, 0));

            PushNotification[] scheduled = service.GetScheduled();

            // 同 id 覆盖：改时间不能变成"两条提醒"。
            Assert.Single(scheduled);
            Assert.Equal("新文案", scheduled[0].Title);
            Assert.Equal(5000, scheduled[0].FireAtUnixSeconds);
        }

        [Fact]
        public void Schedule_ImmediateNotification_IsAnchoredToClock()
        {
            var time = new FakeTimeProvider(StartUtc);
            var service = new ConsolePushService(time);

            // FireAtUnixSeconds <= 0 表示"立即"：落成当前时刻，让计划表的顺序与"实际先后"一致。
            service.Schedule(new PushNotificationRequest(1, "t", "b", 0, 0));

            long now = (long)(StartUtc - UnixEpoch).TotalSeconds;
            Assert.Equal(now, service.GetScheduled()[0].FireAtUnixSeconds);
        }

        [Fact]
        public void Schedule_ImmediateNotification_WithoutClock_StaysAtZero()
        {
            var service = new ConsolePushService();

            // 没有时钟就保持 0：它仍表达"立即"，且在排序里稳定地排在最前，不需要猜一个时间。
            service.Schedule(new PushNotificationRequest(1, "t", "b", -5, 0));

            Assert.Equal(0, service.GetScheduled()[0].FireAtUnixSeconds);
        }

        [Fact]
        public void Schedule_RepeatingFlagFollowsInterval()
        {
            var service = new ConsolePushService();

            service.Schedule(new PushNotificationRequest(1, "每日", "b", 1000, PushScheduler.DailyIntervalSeconds));
            service.Schedule(new PushNotificationRequest(2, "一次性", "b", 2000, 0));
            service.Schedule(new PushNotificationRequest(3, "脏数据", "b", 3000, -60));

            PushNotification[] scheduled = service.GetScheduled();

            Assert.True(scheduled[0].IsRepeating);
            Assert.False(scheduled[1].IsRepeating);

            // 负间隔按"一次性"处理，而不是"每 -60 秒"：脏配置应退化成最保守的行为。
            Assert.False(scheduled[2].IsRepeating);
        }

        [Fact]
        public void Cancel_Existing_ReturnsTrue_Unknown_ReturnsFalse()
        {
            var service = new ConsolePushService();
            service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0));

            Assert.True(service.Cancel(1));
            Assert.Empty(service.GetScheduled());

            // false 是合法结果（本来就没有），不是错误。
            Assert.False(service.Cancel(1));
            Assert.False(service.Cancel(99));
        }

        [Fact]
        public void CancelAll_ClearsPlan()
        {
            var service = new ConsolePushService();
            service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0));
            service.Schedule(new PushNotificationRequest(2, "t", "b", 2000, 0));

            service.CancelAll();

            Assert.Empty(service.GetScheduled());
        }

        [Fact]
        public void GetScheduled_ReturnsDefensiveCopy()
        {
            var service = new ConsolePushService();
            service.Schedule(new PushNotificationRequest(1, "原文案", "b", 1000, 0));

            PushNotification[] first = service.GetScheduled();
            first[0] = new PushNotification(1, "被改过", "b", 1000, true);

            // 调用方改数组不能影响服务内部计划（否则 UI 能把一条已取消的提醒"改回来"）。
            Assert.Equal("原文案", service.GetScheduled()[0].Title);
        }

        [Fact]
        public void GetScheduled_WhenUnavailable_ReturnsEmptyEvenIfPlanWasBuiltEarlier()
        {
            var service = new ConsolePushService();
            service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0));

            service.SetUnavailable();

            // 不可用时"计划"没有意义：平台弹不出来，报出来只会让宿主做无用功。
            Assert.Empty(service.GetScheduled());
        }

        [Fact]
        public void SetAvailable_RestoresServiceAfterUnavailable()
        {
            var service = new ConsolePushService(available: false);
            Assert.False(service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0)));

            service.SetAvailable();

            Assert.True(service.IsAvailable);
            Assert.True(service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0)));
            Assert.Single(service.GetScheduled());

            service.SetUnavailable();

            Assert.False(service.IsAvailable);
            Assert.False(service.Schedule(new PushNotificationRequest(2, "t", "b", 2000, 0)));
        }

        [Fact]
        public void DrainLog_RecordsOperationsAndClearsBuffer()
        {
            var service = new ConsolePushService();
            service.RequestPermission();
            service.RegisterToken("token-a");
            service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0));
            service.Cancel(1);

            IReadOnlyList<string> log = service.DrainLog();

            Assert.Contains(log, entry => entry.Contains("permission -> Granted"));
            Assert.Contains(log, entry => entry.Contains("register-token(len=7) -> Success"));
            Assert.Contains(log, entry => entry.Contains("schedule #1 at 1000"));
            Assert.Contains(log, entry => entry.Contains("cancel #1 -> True"));

            // 取走即清空：同一批事件不能被重复打印。
            Assert.Empty(service.DrainLog());
        }

        [Fact]
        public void DrainLog_NeverContainsTheTokenItself()
        {
            var service = new ConsolePushService();
            service.RequestPermission();

            const string token = "fcm:APA91bF-secret-token";
            service.RegisterToken(token);

            IReadOnlyList<string> log = service.DrainLog();

            // token 是"能冒充本设备收推送"的凭据，写进日志等于泄露：只允许记长度。
            Assert.DoesNotContain(log, entry => entry.Contains("secret"));
            Assert.Contains(log, entry => entry.Contains("register-token(len=" + token.Length + ")"));
        }

        [Fact]
        public void DrainLog_KeepsOnlyMostRecentEntries()
        {
            var service = new ConsolePushService();

            service.Schedule(new PushNotificationRequest(1, "t", "b", 1000, 0));

            // 再补 40 条（共 41 条），环形缓冲只保留最近 32 条。
            for (int i = 0; i < 40; i++)
            {
                service.Schedule(new PushNotificationRequest(100 + i, "t", "b", 2000 + i, 0));
            }

            IReadOnlyList<string> log = service.DrainLog();

            Assert.Equal(ConsolePushService.LogCapacity, log.Count);
            Assert.DoesNotContain(log, entry => entry.Contains("schedule #1 at"));
            Assert.Contains(log, entry => entry.Contains("schedule #139 at"));
        }

        /// <summary>
        /// 可推进的假时钟：只用于把"立即"锚成具体时刻，推进它即可精确断言落表值，不依赖真实等待。
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

            /// <summary>同时推进单调钟与墙钟的秒数。</summary>
            /// <param name="seconds">秒数。</param>
            public void AdvanceSeconds(long seconds)
            {
                _utcNow = _utcNow.AddSeconds(seconds);
                _elapsedSeconds += seconds;
            }
        }
    }
}

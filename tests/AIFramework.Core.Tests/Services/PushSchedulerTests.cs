using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="PushScheduler"/> 的行为契约验收：token 校验与幂等登记、每日提醒的下一次触发时间、
    /// 容量上限、同 id 覆盖、到期派发（只派一次）、重复提醒顺延、不可用/异常降级、时间来源。
    /// <para>
    /// <b>这些用例守的是"玩家会不会被骚扰/会不会漏提醒"</b>：多弹一次是骚扰，少弹一次是功能失效，
    /// 时间算错一天是"活动结束了才提醒我"。因此断言分四类——钉死时刻（今天/明天/00:00/23:59 四个边界）、
    /// 钉死不重复（同 id 只派发一次）、钉死不丢（未派发的到期项不许被顺延吞掉）、
    /// 钉死降级（后端不可用/时钟异常一律返回失败或空，不抛）。
    /// </para>
    /// <para>
    /// 替身刻意只用"可拨动的假后端 + 可拨动的假时钟"：真机上的时区、系统调度、APNs 抖动无法在无头环境复现，
    /// 只能靠假时钟把边界逐条钉住；本类也<b>不允许</b>读系统时钟，否则这些断言会随机器时区漂移。
    /// </para>
    /// </summary>
    public sealed class PushSchedulerTests
    {
        /// <summary>测试用固定 UTC 起点（正午，便于同时构造"今天还没到"与"今天已过"两种时刻）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Unix 纪元（换算用；不读系统时钟）。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();

            // 两个依赖缺一不可：没有后端无处排期，没有时钟算不出"下一次"（且纯逻辑层禁止读系统时间）。
            Assert.Throws<ArgumentNullException>(() => new PushScheduler(null!, time));
            Assert.Throws<ArgumentNullException>(() => new PushScheduler(push, null!));
        }

        [Fact]
        public void Constructor_StartsEmpty_AndExposesDocumentedLimits()
        {
            var scheduler = new PushScheduler(new FakePushService(), new FakeTimeProvider());

            Assert.Equal(0, scheduler.ScheduledCount);

            // 这三个数值会出现在业务侧配置与日志里，改动必须是显式决定，不能被顺手改掉。
            Assert.Equal(32, PushScheduler.DefaultMaxScheduled);
            Assert.Equal(4096, PushScheduler.MaxTokenLength);
            Assert.Equal(86400, PushScheduler.DailyIntervalSeconds);
        }

        [Fact]
        public void EnsureRegistered_EmptyOrWhitespaceToken_ReturnsInvalidTokenWithoutTouchingBackend()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered(null));
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered(string.Empty));
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered("   "));

            // 脏 token 换一条就能修好，不该浪费一次网络/授权往返。
            Assert.Equal(0, push.RegisterCallCount);
        }

        [Fact]
        public void EnsureRegistered_OverlongToken_ReturnsInvalidToken()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            string tooLong = new string('a', PushScheduler.MaxTokenLength + 1);

            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered(tooLong));
            Assert.Equal(0, push.RegisterCallCount);
        }

        [Fact]
        public void EnsureRegistered_TokenAtMaxLength_IsAccepted()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 边界取"等于上限仍合法"：上限是护栏，不是"少一位才算对"。
            string atLimit = new string('a', PushScheduler.MaxTokenLength);

            Assert.Equal(PushRegisterResult.Success, scheduler.EnsureRegistered(atLimit));
            Assert.Equal(1, push.RegisterCallCount);
        }

        [Fact]
        public void EnsureRegistered_NonAsciiOrUnprintableToken_ReturnsInvalidToken()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 真实 token 是 base64/hex：中文、空格、换行、制表符只可能来自"把 JSON 转义串直接塞进来"。
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered("中文token"));
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered("abc def"));
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered("abc\ndef"));
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered("abc\tdef"));
            Assert.Equal(0, push.RegisterCallCount);
        }

        [Fact]
        public void IsValidToken_IsTheSingleSharedRule()
        {
            // 适配层假实现直接复用这个静态口径（见 ConsolePushService / UnityPushService），
            // 因此它自己必须被钉住，否则三处校验会悄悄漂移。
            Assert.True(PushScheduler.IsValidToken("fcm:APA91bF-example_token.123"));
            Assert.True(PushScheduler.IsValidToken(new string('a', PushScheduler.MaxTokenLength)));

            Assert.False(PushScheduler.IsValidToken(null));
            Assert.False(PushScheduler.IsValidToken(string.Empty));
            Assert.False(PushScheduler.IsValidToken(" "));
            Assert.False(PushScheduler.IsValidToken("汉字"));
            Assert.False(PushScheduler.IsValidToken(new string('a', PushScheduler.MaxTokenLength + 1)));
        }

        [Fact]
        public void EnsureRegistered_ValidToken_RegistersOnceAndReturnsSuccess()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            const string token = "fcm:APA91bF-example_token.123";

            Assert.Equal(PushRegisterResult.Success, scheduler.EnsureRegistered(token));
            Assert.Equal(1, push.RegisterCallCount);
            Assert.Equal(token, push.RegisteredToken);
        }

        [Fact]
        public void EnsureRegistered_SameTokenTwice_SkipsBackendOnSecondCall()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.Equal(PushRegisterResult.Success, scheduler.EnsureRegistered("token-a"));
            Assert.Equal(PushRegisterResult.Success, scheduler.EnsureRegistered("token-a"));

            // 每次冷启动都会调一次"确保登记"：已登记同一条就不该再打后端，
            // 否则后端一抖动就会把一次"本来已经就绪"的推送说成失败。
            Assert.Equal(1, push.RegisterCallCount);
        }

        [Fact]
        public void EnsureRegistered_DifferentToken_RegistersAgain()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            scheduler.EnsureRegistered("token-a");
            Assert.Equal(PushRegisterResult.Success, scheduler.EnsureRegistered("token-b"));

            // 换号/换设备后 token 会变：必须重新登记，不能因为"已有一条"就跳过。
            Assert.Equal(2, push.RegisterCallCount);
            Assert.Equal("token-b", push.RegisteredToken);
        }

        [Fact]
        public void EnsureRegistered_WhenBackendUnavailable_ReturnsUnavailable()
        {
            var push = new FakePushService { Available = false };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.Equal(PushRegisterResult.Unavailable, scheduler.EnsureRegistered("token-a"));
            Assert.Equal(0, push.RegisterCallCount);
        }

        [Fact]
        public void EnsureRegistered_WhenBackendThrows_ReturnsUnavailable()
        {
            var push = new FakePushService { ThrowOnRegister = true };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 契约要求不抛；违约实现抛异常时收敛成"现在登记不上"，不把异常漏给登录流程。
            Assert.Equal(PushRegisterResult.Unavailable, scheduler.EnsureRegistered("token-a"));
        }

        [Fact]
        public void EnsureRegistered_WhenBackendThrowsOnAvailability_ReturnsUnavailable()
        {
            var push = new FakePushService { ThrowOnIsAvailable = true };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.Equal(PushRegisterResult.Unavailable, scheduler.EnsureRegistered("token-a"));
        }

        [Fact]
        public void EnsureRegistered_WhenPermissionDenied_PropagatesResult()
        {
            var push = new FakePushService { RegisterResult = PushRegisterResult.PermissionDenied };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 权限是平台事实，Core 不替后端判断，只如实透传（重试无意义的那一类失败）。
            Assert.Equal(PushRegisterResult.PermissionDenied, scheduler.EnsureRegistered("token-a"));
        }

        [Fact]
        public void EnsureRegistered_InvalidTokenWinsOverUnavailable()
        {
            var push = new FakePushService { Available = false };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 参数错误的优先级高于可用性：脏 token 是调用方 bug，不该被"断网"掩盖成一次可重试的失败。
            Assert.Equal(PushRegisterResult.InvalidToken, scheduler.EnsureRegistered(string.Empty));
        }

        [Fact]
        public void ScheduleDaily_SchedulesTodayWhenTheTimeHasNotPassedYet()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            Assert.True(scheduler.ScheduleDaily(1, "体力已满", "回来看看吧", 18, 30));

            PushNotificationRequest request = push.LastRequest!.Value;
            Assert.Equal(Unix(new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Utc)), request.FireAtUnixSeconds);

            // 每日提醒必须以 86400 秒为重复间隔交给平台，否则"每天"就变成"仅今天"。
            Assert.Equal(PushScheduler.DailyIntervalSeconds, request.RepeatIntervalSeconds);
            Assert.Equal("体力已满", request.Title);
            Assert.Equal("回来看看吧", request.Body);
            Assert.Equal(1, scheduler.ScheduledCount);
        }

        [Fact]
        public void ScheduleDaily_WhenTheTimeAlreadyPassed_SchedulesTomorrow()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            // 现在是 12:00，排 09:00：今天的 09:00 已经过去，只能排到明天。
            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 9, 0));

            Assert.Equal(
                Unix(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);
        }

        [Fact]
        public void ScheduleDaily_WhenTheTimeEqualsNow_SchedulesTomorrow()
        {
            var time = new FakeTimeProvider(new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            // 边界口径：恰好等于现在也算"已过"——平台在"现在"这一刻排期通常已经来不及触发，
            // 与其排一条立刻到期的提醒（可能在登录动画里就弹出来），不如稳稳排到明天。
            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 9, 0));

            Assert.Equal(
                Unix(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);
        }

        [Fact]
        public void ScheduleDaily_AtMidnight_SchedulesTomorrowWhenNowIsMidnight()
        {
            var time = new FakeTimeProvider(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 0, 0));

            Assert.Equal(
                Unix(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);
        }

        [Fact]
        public void ScheduleDaily_LastMinuteOfDay_SchedulesToday()
        {
            var time = new FakeTimeProvider(new DateTime(2026, 11, 20, 23, 58, 0, DateTimeKind.Utc));
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            // 23:59 是合法输入（分钟上限是 59），且今天还没到，必须排在今天而不是明天。
            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 23, 59));

            Assert.Equal(
                Unix(new DateTime(2026, 11, 20, 23, 59, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);
        }

        [Fact]
        public void ScheduleDaily_OutOfRangeTime_Throws()
        {
            var scheduler = new PushScheduler(new FakePushService(), new FakeTimeProvider());

            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ScheduleDaily(1, "t", "b", 24, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ScheduleDaily(1, "t", "b", -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ScheduleDaily(1, "t", "b", 0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ScheduleDaily(1, "t", "b", 0, -1));
        }

        [Fact]
        public void ScheduleDaily_OutOfRangeTime_ThrowsEvenWhenBackendUnavailable()
        {
            var push = new FakePushService { Available = false };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 参数错误优先于可用性：否则一次"传了 24 点"的 UI bug 会在断网时被静默降级成 false。
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ScheduleDaily(1, "t", "b", 24, 0));
            Assert.Equal(0, push.ScheduleCallCount);
        }

        [Fact]
        public void ScheduleDaily_WhenAtCapacity_RejectsAndKeepsExistingPlan()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time, maxScheduled: 2);

            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.True(scheduler.ScheduleDaily(2, "t", "b", 19, 0));

            // 超额：拒绝，但绝不为了塞进新的而挤掉已有提醒（那是已经承诺给玩家的提醒）。
            Assert.False(scheduler.ScheduleDaily(3, "t", "b", 20, 0));

            Assert.Equal(2, scheduler.ScheduledCount);
            Assert.Equal(2, push.ScheduleCallCount);
            Assert.Equal(new[] { 1, 2 }, push.ScheduledIds);
        }

        [Fact]
        public void ScheduleDaily_WithZeroCapacity_RejectsEverything()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider(), maxScheduled: 0);

            // 负配额/零配额按"一条都不许排"处理（fail-closed），而不是"无上限"。
            Assert.False(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.Equal(0, push.ScheduleCallCount);
        }

        [Fact]
        public void Constructor_WithHugeCapacity_DoesNotPreallocateUpToTheLimit()
        {
            var push = new FakePushService();
            var time = new FakeTimeProvider();

            // 配置笔误给到 int.MaxValue 时，构造期不许照着上限预分配（那会直接 OOM，把启动搞崩）。
            var scheduler = new PushScheduler(push, time, maxScheduled: int.MaxValue);

            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.True(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(1, scheduler.ScheduledCount);
        }

        [Fact]
        public void ScheduleDaily_OverwriteSameId_ReplacesWithoutConsumingCapacity()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time, maxScheduled: 2);

            scheduler.ScheduleDaily(1, "旧文案", "b", 8, 0);
            scheduler.ScheduleDaily(2, "t", "b", 9, 0);

            // 改时间必须复用同一个 id：否则玩家会在旧时间与新时间各收到一次。
            Assert.True(scheduler.ScheduleDaily(1, "新文案", "b2", 10, 30));

            Assert.Equal(2, scheduler.ScheduledCount);
            Assert.Equal(3, push.ScheduleCallCount);

            // 现在 12:00，覆盖成 10:30 → 今天的 10:30 也已过去，落在明天 10:30。
            Assert.Equal(Unix(new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc)), push.LastRequest!.Value.FireAtUnixSeconds);
            Assert.Equal("新文案", push.LastRequest!.Value.Title);

            // 覆盖后只有一条 id=1（不是两条并存）。
            Assert.Equal(2, push.ScheduledIds.Length);
        }

        [Fact]
        public void ScheduleDaily_WhenBackendUnavailable_ReturnsFalseWithoutTouchingBackend()
        {
            var push = new FakePushService { Available = false };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.False(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.Equal(0, push.ScheduleCallCount);
        }

        [Fact]
        public void ScheduleDaily_WhenBackendRejects_ReturnsFalseAndDoesNotRecord()
        {
            var push = new FakePushService { ScheduleAccepted = false };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // 先让后端接受、再记账：否则会出现"本地以为排上了、平台里什么都没有"的幽灵计划。
            Assert.False(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.Empty(push.ScheduledIds);
        }

        [Fact]
        public void ScheduleDaily_WhenBackendThrows_ReturnsFalseAndDoesNotRecord()
        {
            var push = new FakePushService { ThrowOnSchedule = true };
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            Assert.False(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(0, scheduler.ScheduledCount);
        }

        [Fact]
        public void ScheduleDaily_WhenClockThrows_ReturnsFalse()
        {
            var time = new FakeTimeProvider { ThrowOnUtcNow = true };
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);

            // 算不出"下一次"就宁可这一轮不排：拿一个错的时间去排比不排更糟。
            Assert.False(scheduler.ScheduleDaily(1, "t", "b", 18, 0));
            Assert.Equal(0, push.ScheduleCallCount);
        }

        [Fact]
        public void Cancel_ExistingNotification_ReturnsTrueAndCancelsOnBackend()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());
            scheduler.ScheduleDaily(1, "t", "b", 18, 0);

            Assert.True(scheduler.Cancel(1));

            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.Equal(new[] { 1 }, push.CancelledIds);
        }

        [Fact]
        public void Cancel_UnknownNotification_ReturnsFalseWithoutTouchingBackend()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());

            // false 是合法结果（计划里本来就没有），不是错误；也没必要去打扰平台。
            Assert.False(scheduler.Cancel(99));
            Assert.Equal(0, push.CancelCallCount);
        }

        [Fact]
        public void CancelAll_ClearsPlanAndBackend()
        {
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, new FakeTimeProvider());
            scheduler.ScheduleDaily(1, "t", "b", 18, 0);
            scheduler.ScheduleDaily(2, "t", "b", 19, 0);

            scheduler.CancelAll();

            Assert.Equal(0, scheduler.ScheduledCount);
            Assert.Equal(1, push.CancelAllCallCount);
            Assert.Empty(push.ScheduledIds);
        }

        [Fact]
        public void GetDueNotifications_NotYetDue_ReturnsEmpty()
        {
            var time = new FakeTimeProvider();
            var scheduler = new PushScheduler(new FakePushService(), time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            // 12:00 排 18:30，现在还没到：不能提前弹。
            Assert.Empty(scheduler.GetDueNotifications());
            Assert.Equal(1, scheduler.ScheduledCount);
        }

        [Fact]
        public void GetDueNotifications_Due_ReturnsOnceAndOnlyOnce()
        {
            var time = new FakeTimeProvider();
            var scheduler = new PushScheduler(new FakePushService(), time);
            scheduler.ScheduleDaily(1, "体力已满", "回来看看吧", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));

            PushNotification[] due = scheduler.GetDueNotifications();

            Assert.Single(due);
            Assert.Equal(1, due[0].NotificationId);
            Assert.Equal("体力已满", due[0].Title);
            Assert.Equal(Unix(new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Utc)), due[0].FireAtUnixSeconds);
            Assert.True(due[0].IsRepeating);

            // 返回即标记：同一轮里再取一次不能重复弹（否则玩家会连收两条一样的提醒）。
            Assert.Empty(scheduler.GetDueNotifications());

            // 派发后仍在计划里——每日提醒明天还要响。
            Assert.Equal(1, scheduler.ScheduledCount);
        }

        [Fact]
        public void GetDueNotifications_ReturnsAscendingByFireTime()
        {
            var time = new FakeTimeProvider();
            var scheduler = new PushScheduler(new FakePushService(), time);

            // 刻意乱序排入，验证输出按触发时间升序（同一时刻再按 id 升序）。
            scheduler.ScheduleDaily(3, "t", "b", 18, 0);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);
            scheduler.ScheduleDaily(2, "t", "b", 19, 0);

            time.AdvanceSeconds(SecondsUntil(20, 0));

            PushNotification[] due = scheduler.GetDueNotifications();

            Assert.Equal(3, due.Length);
            Assert.Equal(3, due[0].NotificationId);
            Assert.Equal(1, due[1].NotificationId);
            Assert.Equal(2, due[2].NotificationId);
        }

        [Fact]
        public void GetDueNotifications_WhenBackendUnavailable_ReturnsEmpty()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            push.Available = false;

            // 不可用时"到期"没有意义：平台根本弹不出来，报到期只会让宿主做无用功。
            Assert.Empty(scheduler.GetDueNotifications());
        }

        [Fact]
        public void GetDueNotifications_WhenClockThrows_ReturnsEmpty()
        {
            var time = new FakeTimeProvider();
            var scheduler = new PushScheduler(new FakePushService(), time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            time.ThrowOnUtcNow = true;

            // 没有可靠的"现在"就没有可靠的"到期"：宁可这一轮不弹，也不要按错时间批量弹出积压提醒。
            Assert.Empty(scheduler.GetDueNotifications());
        }

        [Fact]
        public void Refresh_AfterDispatch_AdvancesByOneDayAndReSchedules()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            Assert.Single(scheduler.GetDueNotifications());

            scheduler.Refresh();

            // 顺延到明天的同一时刻，并把新时间重新推给平台（否则平台还记着昨天那条）。
            Assert.Equal(
                Unix(new DateTime(2026, 11, 21, 18, 30, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);
            Assert.Equal(2, push.ScheduleCallCount);

            // 顺延后立刻取：还没到期，不能又弹一次。
            Assert.Empty(scheduler.GetDueNotifications());

            // 再走一天，同一条提醒应该再次到期（每日提醒的核心语义）。
            time.AdvanceSeconds(PushScheduler.DailyIntervalSeconds);
            PushNotification[] nextDay = scheduler.GetDueNotifications();

            Assert.Single(nextDay);
            Assert.Equal(1, nextDay[0].NotificationId);
            Assert.Equal(
                Unix(new DateTime(2026, 11, 21, 18, 30, 0, DateTimeKind.Utc)),
                nextDay[0].FireAtUnixSeconds);
        }

        [Fact]
        public void Refresh_WhenNotificationNotDispatched_KeepsOriginalFireTime()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));

            // 到点了但宿主还没轮询：此时 Refresh 不许把它顺延走，否则这条提醒永远收不到（宁可晚，不可丢）。
            scheduler.Refresh();

            Assert.Equal(1, push.ScheduleCallCount);
            Assert.Single(scheduler.GetDueNotifications());
        }

        [Fact]
        public void Refresh_AfterSeveralMissedCycles_JumpsToNextFutureOccurrence()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            Assert.Single(scheduler.GetDueNotifications());

            // 玩家离线 5 天：不该补弹 5 条昨天的提醒，而是直接跳到下一个未来时刻。
            time.AdvanceDays(5);
            scheduler.Refresh();

            Assert.Equal(
                Unix(new DateTime(2026, 11, 26, 18, 30, 0, DateTimeKind.Utc)),
                push.LastRequest!.Value.FireAtUnixSeconds);

            // 顺延后的时间严格晚于现在：立刻取不该有到期项。
            Assert.Empty(scheduler.GetDueNotifications());
        }

        [Fact]
        public void Refresh_WhenBackendUnavailable_DoesNotChangePlan()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            Assert.Single(scheduler.GetDueNotifications());

            push.Available = false;
            scheduler.Refresh();

            // 后端不可用时不做任何改动：本地单方面顺延只会制造"本地说下次是明天、平台还记着昨天"的两份漂移计划。
            Assert.Equal(1, push.ScheduleCallCount);
        }

        [Fact]
        public void Refresh_WhenClockThrows_DoesNotChangePlan()
        {
            var time = new FakeTimeProvider();
            var push = new FakePushService();
            var scheduler = new PushScheduler(push, time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            time.AdvanceSeconds(SecondsUntil(18, 30));
            scheduler.GetDueNotifications();

            time.ThrowOnUtcNow = true;
            scheduler.Refresh();

            Assert.Equal(1, push.ScheduleCallCount);
        }

        [Fact]
        public void TimeSource_IsTheOnlyClock_DueFollowsInjectedTime()
        {
            var time = new FakeTimeProvider();
            var scheduler = new PushScheduler(new FakePushService(), time);
            scheduler.ScheduleDaily(1, "t", "b", 18, 30);

            // 差一秒不算到期，正好到点算到期：证明本类只认注入的时间源，不读系统时钟。
            time.AdvanceSeconds(SecondsUntil(18, 29) + 59);
            Assert.Empty(scheduler.GetDueNotifications());

            time.AdvanceSeconds(1);
            Assert.Single(scheduler.GetDueNotifications());
        }

        /// <summary>UTC 时刻 → Unix 秒（只做算术换算，不读系统时钟）。</summary>
        /// <param name="utc">UTC 时刻。</param>
        /// <returns>Unix 秒。</returns>
        private static long Unix(DateTime utc)
        {
            return (long)(utc - UnixEpoch).TotalSeconds;
        }

        /// <summary>从 <see cref="StartUtc"/>（当天 12:00）到当天指定 UTC 时刻的秒数。</summary>
        /// <param name="hourUtc">小时。</param>
        /// <param name="minuteUtc">分钟。</param>
        /// <returns>秒数（负数表示该时刻早于起点）。</returns>
        private static long SecondsUntil(int hourUtc, int minuteUtc)
        {
            DateTime target = StartUtc.Date.AddHours(hourUtc).AddMinutes(minuteUtc);
            return (long)(target - StartUtc).TotalSeconds;
        }

        /// <summary>
        /// 可拨动的假推送后端：把真机上极难复现的分支（不可用、抛异常、平台拒绝、权限拒绝）
        /// 变成一次赋值，用来逐条钉死业务层的透传与降级行为。
        /// </summary>
        private sealed class FakePushService : IPushService
        {
            /// <summary>后端是否可用。</summary>
            public bool Available { get; set; } = true;

            /// <summary>读可用性是否抛异常（模拟实现违约）。</summary>
            public bool ThrowOnIsAvailable { get; set; }

            /// <summary>排期是否抛异常。</summary>
            public bool ThrowOnSchedule { get; set; }

            /// <summary>登记 token 是否抛异常。</summary>
            public bool ThrowOnRegister { get; set; }

            /// <summary>排期是否被平台接受（<c>false</c> = 平台拒绝）。</summary>
            public bool ScheduleAccepted { get; set; } = true;

            /// <summary>登记 token 返回的结果（默认成功）。</summary>
            public PushRegisterResult RegisterResult { get; set; } = PushRegisterResult.Success;

            /// <summary>当前已登记的 token（空串 = 未登记）。</summary>
            public string Token { get; private set; } = string.Empty;

            /// <summary>平台侧当前计划（按排入顺序）。</summary>
            private readonly List<PushNotificationRequest> _requests = new List<PushNotificationRequest>();

            /// <summary>被取消过的通知 id（按调用顺序）。</summary>
            private readonly List<int> _cancelled = new List<int>();

            /// <summary>登记调用次数。</summary>
            public int RegisterCallCount { get; private set; }

            /// <summary>排期调用次数（含被拒绝的调用）。</summary>
            public int ScheduleCallCount { get; private set; }

            /// <summary>单条取消调用次数。</summary>
            public int CancelCallCount { get; private set; }

            /// <summary>取消全部调用次数。</summary>
            public int CancelAllCallCount { get; private set; }

            /// <summary>最近一次排期请求（从未排过时为 <c>null</c>）。</summary>
            public PushNotificationRequest? LastRequest
            {
                get { return _requests.Count == 0 ? (PushNotificationRequest?)null : _requests[_requests.Count - 1]; }
            }

            /// <summary>平台侧当前计划的 id（按排入顺序）。</summary>
            public int[] ScheduledIds
            {
                get
                {
                    var ids = new int[_requests.Count];
                    for (int i = 0; i < _requests.Count; i++)
                    {
                        ids[i] = _requests[i].NotificationId;
                    }

                    return ids;
                }
            }

            /// <summary>被取消过的通知 id（按调用顺序）。</summary>
            public int[] CancelledIds
            {
                get { return _cancelled.ToArray(); }
            }

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

            public PushPermissionState PermissionState
            {
                get { return PushPermissionState.Granted; }
            }

            public PushPermissionState RequestPermission()
            {
                return PushPermissionState.Granted;
            }

            public PushRegisterResult RegisterToken(string token)
            {
                RegisterCallCount++;

                if (ThrowOnRegister)
                {
                    throw new InvalidOperationException("fake register failure");
                }

                if (RegisterResult == PushRegisterResult.Success)
                {
                    Token = token;
                }

                return RegisterResult;
            }

            public void UnregisterToken()
            {
                Token = string.Empty;
            }

            public string? RegisteredToken
            {
                get { return Token; }
            }

            public bool Schedule(in PushNotificationRequest request)
            {
                ScheduleCallCount++;

                if (ThrowOnSchedule)
                {
                    throw new InvalidOperationException("fake schedule failure");
                }

                if (!ScheduleAccepted)
                {
                    return false;
                }

                // 模拟契约里的"同 id 覆盖旧计划"。
                // 先把 id 与请求拷成局部变量：in 参数不能被 lambda 捕获（CS1628）。
                int notificationId = request.NotificationId;
                PushNotificationRequest copy = request;
                _requests.RemoveAll(existing => existing.NotificationId == notificationId);
                _requests.Add(copy);
                return true;
            }

            public bool Cancel(int notificationId)
            {
                CancelCallCount++;
                _cancelled.Add(notificationId);
                return _requests.RemoveAll(existing => existing.NotificationId == notificationId) > 0;
            }

            public void CancelAll()
            {
                CancelAllCallCount++;
                _requests.Clear();
            }

            public PushNotification[] GetScheduled()
            {
                return Array.Empty<PushNotification>();
            }
        }

        /// <summary>
        /// 可拨动的假时钟：本类只使用 <see cref="UtcNow"/>，推进它即可精确验证每日提醒的边界，
        /// 不依赖真实等待或系统时区。
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

            /// <summary>同时推进单调钟与墙钟的秒数（可为负）。</summary>
            /// <param name="seconds">秒数。</param>
            public void AdvanceSeconds(long seconds)
            {
                _utcNow = _utcNow.AddSeconds(seconds);
                _elapsedSeconds += seconds;
            }

            /// <summary>同时推进单调钟与墙钟的整天数。</summary>
            /// <param name="days">天数。</param>
            public void AdvanceDays(int days)
            {
                _utcNow = _utcNow.AddDays(days);
                _elapsedSeconds += days * 86400d;
            }
        }
    }
}

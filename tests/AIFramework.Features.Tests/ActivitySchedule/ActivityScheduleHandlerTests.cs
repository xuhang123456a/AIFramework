using System;
using AIFramework.Features.ActivitySchedule;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.ActivitySchedule
{
    /// <summary>
    /// 活动排期行为验收：左闭右开窗口、剩余秒折算、同组轮换优先级、跨日每日重置判定、
    /// 事件只在集合变化时发、tick 幂等、空表退化、快照时间来自注入的时间源。
    /// <para>所有用例都用可快进假时间源，不碰真实时钟（AIF003）。</para>
    /// </summary>
    public sealed class ActivityScheduleHandlerTests
    {
        /// <summary>默认锚点折算成 int32 Unix 秒（表内时间列是 int32）。</summary>
        private static readonly int AnchorUnix =
            ActivityScheduleHarness.ToUnixSecondsInt(ActivityScheduleHarness.DefaultNow);

        private const int DaySeconds = 86400;

        [Fact]
        public void Query_BeforeAnyTick_ReturnsClosedSetWithCurrentTime()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 3600, AnchorUnix + 3600)));

            ActivityScheduleSnapshot snapshot = harness.Query();

            // 查询不重算：还没 tick 过，谁都不该是开启态。
            Assert.Equal(0, snapshot.OpenCount);
            Assert.Single(snapshot.Activities);
            Assert.False(snapshot.Activities[0].IsOpen);
            Assert.Equal(ActivityScheduleHarness.ToUnixSeconds(ActivityScheduleHarness.DefaultNow), snapshot.NowUnixSeconds);
        }

        [Fact]
        public void Tick_OpensActivityInsideWindow_AndLeavesOthersClosed()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 3600, AnchorUnix + 3600, name: "进行中"),
                Row(1002, AnchorUnix + 3600, AnchorUnix + 7200, name: "未开始"),
                Row(1003, AnchorUnix - 7200, AnchorUnix - 3600, name: "已结束")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.Equal(1, snapshot.OpenCount);
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1001).IsOpen);
            Assert.Equal(3600, ActivityScheduleHarness.StateOf(snapshot, 1001).SecondsRemaining);
            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1002).IsOpen);
            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1003).IsOpen);
        }

        [Fact]
        public void Window_StartBoundary_IsInclusive()
        {
            // start == now：左闭，开始那一秒已经算开启。
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix, AnchorUnix + 60)));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1001).IsOpen);
        }

        [Fact]
        public void Window_EndBoundary_IsExclusive()
        {
            // end == now：右开，结束那一秒已经算关闭（相邻两期活动不会在同一秒同时开启）。
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 60, AnchorUnix)));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1001).IsOpen);
            Assert.Equal(0, ActivityScheduleHarness.StateOf(snapshot, 1001).SecondsRemaining);
        }

        [Fact]
        public void Window_LastSecondBeforeEnd_StaysOpenThenCloses()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix, AnchorUnix + 60)));

            harness.TickAndQuery();
            harness.AdvanceSeconds(59);

            ActivityScheduleSnapshot lastSecond = harness.TickAndQuery();
            Assert.True(ActivityScheduleHarness.StateOf(lastSecond, 1001).IsOpen);
            Assert.Equal(1, ActivityScheduleHarness.StateOf(lastSecond, 1001).SecondsRemaining);

            harness.AdvanceSeconds(1);
            ActivityScheduleSnapshot expired = harness.TickAndQuery();
            Assert.False(ActivityScheduleHarness.StateOf(expired, 1001).IsOpen);
            Assert.Equal(0, ActivityScheduleHarness.StateOf(expired, 1001).SecondsRemaining);
        }

        [Fact]
        public void Snapshot_SecondsRemaining_IsZeroWhenNotStartedOrAlreadyClosed()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix + 600, AnchorUnix + 1200, name: "未开始"),
                Row(1002, AnchorUnix - 1200, AnchorUnix - 600, name: "已结束")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.Equal(0, ActivityScheduleHarness.StateOf(snapshot, 1001).SecondsRemaining);
            Assert.Equal(0, ActivityScheduleHarness.StateOf(snapshot, 1002).SecondsRemaining);
        }

        [Fact]
        public void Snapshot_SecondsRemaining_CountsDownFromEnd()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 60, AnchorUnix + 3600)));

            Assert.Equal(3600, ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).SecondsRemaining);

            harness.AdvanceSeconds(600);

            // 剩余秒按查询时刻折算，不需要再 tick 一次才准确。
            Assert.Equal(3000, ActivityScheduleHarness.StateOf(harness.Query(), 1001).SecondsRemaining);
        }

        [Fact]
        public void Rotation_HigherPriorityWinsWithinSameGroup()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 60, priority: 10, name: "低优先"),
                Row(1002, AnchorUnix - 60, AnchorUnix + 60, priority: 20, name: "高优先")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.Equal(1, snapshot.OpenCount);
            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1001).IsOpen);
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1002).IsOpen);
        }

        [Fact]
        public void Rotation_EqualPriority_LowerActivityIdWins()
        {
            // 同优先级取 activityId 小者：与配置表行顺序无关（小 id 写在第二行也照样胜出）。
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1005, AnchorUnix - 60, AnchorUnix + 60, priority: 20, name: "大 id"),
                Row(1003, AnchorUnix - 60, AnchorUnix + 60, priority: 20, name: "小 id")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.Equal(1, snapshot.OpenCount);
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1003).IsOpen);
            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1005).IsOpen);
        }

        [Fact]
        public void Rotation_DifferentGroups_BothStayOpen()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 60, group: 1, priority: 10),
                Row(2001, AnchorUnix - 60, AnchorUnix + 60, group: 2, priority: 99)));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            // 不同轮换组互不竞争：高优先级只在组内生效。
            Assert.Equal(2, snapshot.OpenCount);
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1001).IsOpen);
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 2001).IsOpen);
        }

        [Fact]
        public void Rotation_DemotedActivity_StaysVisibleInSnapshot()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 60, priority: 10, name: "被顶替的活动"),
                Row(1002, AnchorUnix - 60, AnchorUnix + 60, priority: 20, name: "当前活动")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            // 被挤掉的活动仍在快照里（isOpen=false），UI 才能展示"本期被顶替"。
            Assert.Equal(2, snapshot.Activities.Count);
            ActivityState demoted = ActivityScheduleHarness.StateOf(snapshot, 1001);
            Assert.False(demoted.IsOpen);
            Assert.Equal("被顶替的活动", demoted.Name);
            Assert.Equal(0, demoted.SecondsRemaining);
        }

        [Fact]
        public void Events_ArePublishedInTableRowOrder()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1002, AnchorUnix - 60, AnchorUnix + 60, group: 2),
                Row(1001, AnchorUnix - 60, AnchorUnix + 60, group: 1)));

            harness.Tick();

            Assert.Equal(new[] { 1002, 1001 }, harness.OpenedIds());
            Assert.Empty(harness.ClosedIds());
        }

        [Fact]
        public void Events_CarryActivityId_WhenWindowExpires()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix, AnchorUnix + 60)));

            harness.Tick();
            Assert.Equal(new[] { 1001 }, harness.OpenedIds());

            harness.AdvanceSeconds(60);
            harness.Tick();

            Assert.Equal(new[] { 1001 }, harness.ClosedIds());
            Assert.Single(harness.OpenedIds());
        }

        [Fact]
        public void Tick_RepeatedWithoutChange_PublishesNothingExtra()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 3600, AnchorUnix + 3600)));

            harness.Tick();
            harness.Tick();
            harness.Tick();

            // 幂等：同样的时间窗重复 tick 只发一次开启事件。
            Assert.Single(harness.OpenedIds());
            Assert.Empty(harness.ClosedIds());
        }

        [Fact]
        public void Events_OnlyPublishedWhenOpenSetChanges()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 7200, group: 1, name: "常驻"),
                Row(1002, AnchorUnix + 3600, AnchorUnix + 7200, group: 2, name: "后续")));

            harness.Tick();
            harness.AdvanceSeconds(1800);
            harness.Tick();

            // 窗口内推进但集合没变：不重复发事件。
            Assert.Equal(new[] { 1001 }, harness.OpenedIds());

            harness.AdvanceSeconds(1800);
            harness.Tick();

            // 第二期活动开始：开集合从 {1001} 变成 {1001, 1002}，只补发新增那一个。
            Assert.Equal(new[] { 1001, 1002 }, harness.OpenedIds());
            Assert.Empty(harness.ClosedIds());
        }

        [Fact]
        public void Tick_RotationTakeover_PublishesCloseThenOpen()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 7200, priority: 10, name: "低优先"),
                Row(1002, AnchorUnix + 3600, AnchorUnix + 7200, priority: 20, name: "高优先")));

            harness.Tick();
            Assert.Equal(new[] { 1001 }, harness.OpenedIds());

            harness.AdvanceSeconds(3600);
            harness.Tick();

            // 高优先级开窗后把同组低优先级挤下线：先关后开，顺序按配置表行顺序。
            Assert.Equal(new[] { 1001, 1002 }, harness.OpenedIds());
            Assert.Equal(new[] { 1001 }, harness.ClosedIds());
        }

        [Fact]
        public void DailyReset_BeforeResetHour_IsNotDueOnFirstTick()
        {
            // 锚点 08:00Z，重置整点 12:00Z：今天还没到点。
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + DaySeconds, resetHour: 12)));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_AfterResetHour_ReportedOnFirstTickThenOncePerDay()
        {
            // 锚点 08:00Z 已过 04:00Z：进程启动（或跨午夜重启）后的首拍必须补报一次当天的重置。
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + DaySeconds, resetHour: 4)));

            Assert.True(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);

            // 同一 UTC 日内不再重复报。
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_SameDay_LaterTicksStayFalse()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + DaySeconds, resetHour: 4)));

            harness.Tick();
            harness.AdvanceSeconds(3600);
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);

            harness.AdvanceSeconds(3600);
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_AtResetHourBoundary_IsDue()
        {
            // 04:00:00Z 整点就算到点（与窗口边界一样按整秒口径，不看毫秒）。
            var atResetHour = new DateTime(2026, 11, 20, 4, 0, 0, DateTimeKind.Utc);
            int now = ActivityScheduleHarness.ToUnixSecondsInt(atResetHour);
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, now - 60, now + 3600, resetHour: 4)),
                atResetHour);

            Assert.True(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_CrossDayBeforeResetHour_IsNotDue()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + (3 * DaySeconds), resetHour: 4)));

            harness.Tick();

            // 跨到次日 01:00Z：日序号变了，但还没到 04:00Z 的重置整点。
            harness.AdvanceSeconds(17 * 3600);
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_CrossDayAfterResetHour_IsDue()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + (3 * DaySeconds), resetHour: 4)));

            harness.Tick();

            // 次日 05:00Z：跨日且过了 04:00Z → 报一次。
            harness.AdvanceSeconds(21 * 3600);
            Assert.True(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_LateSameDayTick_StillFiresAfterPreHourTick()
        {
            // 关键反例：跨日后的第一次 tick 落在 reset 小时之前时，不得把跨日信号提前吃掉，
            // 否则同一天稍后过了 reset 小时的那次 tick 会永远判不出重置。
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + (3 * DaySeconds), resetHour: 4)));

            harness.Tick();

            harness.AdvanceSeconds(17 * 3600);
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);

            harness.AdvanceSeconds(4 * 3600);
            Assert.True(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);

            // 报过之后同一天不再重复。
            harness.AdvanceSeconds(3600);
            Assert.False(ActivityScheduleHarness.StateOf(harness.TickAndQuery(), 1001).IsDailyResetDue);
        }

        [Fact]
        public void DailyReset_OnlyReportedForOpenActivities()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + DaySeconds, resetHour: 4, name: "开启中"),
                Row(1002, AnchorUnix - 7200, AnchorUnix - 3600, resetHour: 4, group: 2, name: "已结束")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            // 只对已开启活动报"该重置"：已关闭的活动没有今天的进度要清。
            Assert.True(ActivityScheduleHarness.StateOf(snapshot, 1001).IsDailyResetDue);
            Assert.False(ActivityScheduleHarness.StateOf(snapshot, 1002).IsDailyResetDue);
        }

        [Fact]
        public void Snapshot_NowUnixSeconds_ReflectsTimeProvider()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 60, AnchorUnix + 3600)));

            harness.Tick();
            harness.AdvanceSeconds(1234);

            ActivityScheduleSnapshot snapshot = harness.Query();

            Assert.Equal(ActivityScheduleHarness.ToUnixSeconds(ActivityScheduleHarness.DefaultNow) + 1234, snapshot.NowUnixSeconds);
        }

        [Fact]
        public void Snapshot_ExposesCompleteFieldsPerActivity()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 600, AnchorUnix + 3600, resetHour: 0, name: "限时挑战"),
                Row(1002, AnchorUnix - 7200, AnchorUnix - 3600, resetHour: 0, group: 2, name: "已结束")));

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            ActivityState open = ActivityScheduleHarness.StateOf(snapshot, 1001);
            Assert.Equal(1001, open.ActivityId);
            Assert.Equal("限时挑战", open.Name);
            Assert.True(open.IsOpen);
            Assert.Equal(3600, open.SecondsRemaining);
            Assert.True(open.IsDailyResetDue);

            ActivityState closed = ActivityScheduleHarness.StateOf(snapshot, 1002);
            Assert.Equal(1002, closed.ActivityId);
            Assert.Equal("已结束", closed.Name);
            Assert.False(closed.IsOpen);
            Assert.Equal(0, closed.SecondsRemaining);
            Assert.False(closed.IsDailyResetDue);

            Assert.Equal(1, snapshot.OpenCount);
            Assert.Equal(2, snapshot.Activities.Count);
        }

        [Fact]
        public void OpenCount_PropertyTracksLastTick()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix - 60, AnchorUnix + 60, group: 1),
                Row(2001, AnchorUnix - 60, AnchorUnix + 60, group: 2)));

            Assert.Equal(0, harness.Handler.OpenCount);

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            Assert.Equal(2, harness.Handler.OpenCount);
            Assert.Equal(2, snapshot.OpenCount);
        }

        [Fact]
        public void EmptyTable_TickAndQuery_DegradeWithoutEvents()
        {
            var harness = ActivityScheduleHarness.Create(ActivityScheduleTableStub.WithRows());

            ActivityScheduleSnapshot snapshot = harness.TickAndQuery();

            // 空表是合法配置：不崩、不抛、不发事件。
            Assert.Empty(snapshot.Activities);
            Assert.Equal(0, snapshot.OpenCount);
            Assert.Equal(ActivityScheduleHarness.ToUnixSeconds(ActivityScheduleHarness.DefaultNow), snapshot.NowUnixSeconds);
            Assert.Empty(harness.OpenedIds());
            Assert.Empty(harness.ClosedIds());

            harness.AdvanceSeconds(DaySeconds);
            Assert.Empty(harness.TickAndQuery().Activities);
            Assert.Empty(harness.OpenedIds());
        }

        [Fact]
        public void Query_DoesNotPublishEventsOrChangeState()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 60, AnchorUnix + 60)));

            harness.Query();
            harness.Query();

            Assert.Empty(harness.OpenedIds());
            Assert.Empty(harness.ClosedIds());
            Assert.False(harness.Query().Activities[0].IsOpen);
        }

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            var bus = new RecordingBus();
            var time = new FakeTimeProvider(ActivityScheduleHarness.DefaultNow);
            var loader = ActivityScheduleTableStub.WithRows();

            Assert.Throws<ArgumentNullException>(() => new ActivityScheduleHandler(null!, time, loader));
            Assert.Throws<ArgumentNullException>(() => new ActivityScheduleHandler(bus, null!, loader));
            Assert.Throws<ArgumentNullException>(() => new ActivityScheduleHandler(bus, time, null!));
        }

        [Fact]
        public void Constructor_MissingTable_ThrowsInvalidOperationException()
        {
            var bus = new RecordingBus();
            var time = new FakeTimeProvider(ActivityScheduleHarness.DefaultNow);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new ActivityScheduleHandler(bus, time, ActivityScheduleTableStub.WithoutTable()));

            Assert.Contains(ActivityScheduleTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Constructor_InvalidTableData_ThrowsInvalidDataException()
        {
            var bus = new RecordingBus();
            var time = new FakeTimeProvider(ActivityScheduleHarness.DefaultNow);
            var loader = ActivityScheduleTableStub.WithRows(
                Row(1001, AnchorUnix + 3600, AnchorUnix - 3600, name: "时间倒置"));

            Assert.Throws<System.IO.InvalidDataException>(() => new ActivityScheduleHandler(bus, time, loader));
        }

        [Fact]
        public void Register_NullBus_Throws()
        {
            var harness = ActivityScheduleHarness.Create(
                ActivityScheduleTableStub.WithRows(Row(1001, AnchorUnix - 60, AnchorUnix + 60)));

            Assert.Throws<ArgumentNullException>(() => harness.Handler.Register(null!));
        }

        /// <summary>按绝对 int32 Unix 秒构造一行（默认落在轮换组 1、优先级 10、04:00Z 重置）。</summary>
        private static ActivityScheduleRowSpec Row(
            int activityId,
            int startUnixSeconds,
            int endUnixSeconds,
            int resetHour = 4,
            int group = 1,
            int priority = 10,
            string name = "测试活动")
        {
            return new ActivityScheduleRowSpec(
                activityId,
                name,
                startUnixSeconds,
                endUnixSeconds,
                resetHour,
                group,
                priority);
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.ActivitySchedule
{
    /// <summary>
    /// 单个活动的展示状态：排期列 + 最近一次 tick 算出的开启/重置标记。
    /// <para>
    /// <see cref="IsOpen"/> 是**轮换裁决之后**的最终开启态：同组竞争里被挤掉的活动
    /// 仍然出现在快照里（<c>IsOpen=false</c>），方便 UI 展示"该活动被本期活动顶替"。
    /// </para>
    /// <para>
    /// <see cref="SecondsRemaining"/> 按**查询时刻**的时间源实时折算（不是 tick 时刻的残值）：
    /// 已关闭或未开始恒为 0，不会出现负数。
    /// </para>
    /// </summary>
    public readonly struct ActivityState
    {
        public ActivityState(
            int activityId,
            string name,
            bool isOpen,
            int secondsRemaining,
            bool isDailyResetDue)
        {
            ActivityId = activityId;
            Name = name;
            IsOpen = isOpen;
            SecondsRemaining = secondsRemaining;
            IsDailyResetDue = isDailyResetDue;
        }

        /// <summary>活动 id（配置主键）。</summary>
        public int ActivityId { get; }

        /// <summary>活动显示名。</summary>
        public string Name { get; }

        /// <summary>轮换裁决后的最终开启态（窗口内且未被同组更高优先级顶替）。</summary>
        public bool IsOpen { get; }

        /// <summary>距关闭还剩多少秒（查询时刻折算）；已关闭或未开始为 0。</summary>
        public int SecondsRemaining { get; }

        /// <summary>本拍是否上报"今天该做每日重置"（只在跨日后的首拍为 true，消费方按事件/快照当拍处理）。</summary>
        public bool IsDailyResetDue { get; }
    }

    /// <summary>
    /// 活动排期全量只读快照：构造后不可变，活动顺序 = 配置表行顺序。
    /// <para>
    /// 退化约定：子表 0 行时 <see cref="Activities"/> 为空数组、<see cref="OpenCount"/> 为 0，
    /// 不抛异常也不发事件。
    /// </para>
    /// <para>
    /// 快照里的开启态来自**最近一次 <see cref="ActivityTickCommand"/>**，查询本身不重新计算：
    /// 这样"事件序列"与"快照内容"永远讲同一个故事，不会出现"快照已变成开启、事件却还没发"。
    /// </para>
    /// </summary>
    public sealed class ActivityScheduleSnapshot
    {
        public ActivityScheduleSnapshot(ActivityState[] activities, int openCount, long nowUnixSeconds)
        {
            if (activities == null)
            {
                throw new ArgumentNullException(nameof(activities));
            }

            Activities = activities;
            OpenCount = openCount;
            NowUnixSeconds = nowUnixSeconds;
        }

        /// <summary>全部活动状态，顺序 = 配置表行顺序；空表为空数组。</summary>
        public IReadOnlyList<ActivityState> Activities { get; }

        /// <summary>当前处于开启态的活动条数（= Activities 中 IsOpen 为 true 的条数，轮换后口径）。</summary>
        public int OpenCount { get; }

        /// <summary>生成快照时的 UTC Unix 秒（来自注入的 <c>ITimeProvider</c>，不是本机时钟）。</summary>
        public long NowUnixSeconds { get; }
    }

    /// <summary>活动排期全量查询请求：无参数，返回最近一次 tick 的结果 + 查询时刻。</summary>
    public readonly struct ActivityQueryRequest : IQuery<ActivityScheduleSnapshot>
    {
    }
}

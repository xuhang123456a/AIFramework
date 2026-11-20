#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.ActivitySchedule
{
    /// <summary>
    /// 表驱动活动排期机：唯一允许裁定"此刻哪些活动是开启的"的地方。
    /// <para>
    /// <b>只做三件事</b>：按 <c>[start, end)</c> 判定窗口 → 同 <c>rotationGroup</c> 内做优先级轮换 →
    /// 跨日时上报"该做每日重置"。活动进度、奖励领取、红点、UI、存档一律不碰：
    /// 本切片不持有任何玩家数据，只产出开启/关闭事件与只读快照。
    /// </para>
    /// <para>
    /// <b>时间策略</b>：所有时间只经构造注入的 <see cref="ITimeProvider"/>（AIF003），
    /// 秒数为 <c>UtcNow</c> 折算的 UTC Unix 秒；跨日判定用 <c>now / 86400</c> 的商（UTC 日序号）
    /// 与该日内的 UTC 小时数，因此与玩家本地时区、夏令时无关。
    /// </para>
    /// <para>
    /// <b>内存策略</b>：4 个与子表同长的定长数组在构造时一次分配；<see cref="ActivityTickCommand"/>
    /// 走双缓冲交换，tick 内零堆分配（快照只在查询时分配）。
    /// </para>
    /// <para>
    /// <b>状态归属</b>：开启集合只在 tick 里变更；查询只读最近一次 tick 的结果，
    /// 因此"事件流"与"快照"永远同源，不会出现快照已开、事件未发的撕裂。
    /// </para>
    /// </summary>
    public sealed class ActivityScheduleHandler
        : ICommandHandler<ActivityTickCommand>,
          IQueryHandler<ActivityQueryRequest, ActivityScheduleSnapshot>
    {
        /// <summary>Unix 纪元（1970-01-01T00:00:00Z）：只用于把 <see cref="ITimeProvider.UtcNow"/> 折成秒。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>一天的秒数：跨日判定与"当日第几小时"都按它切。</summary>
        private const long SecondsPerDay = 86400L;

        /// <summary>一小时的秒数：把当日秒数折成 UTC 小时。</summary>
        private const long SecondsPerHour = 3600L;

        /// <summary>重置水位初值：<c>long.MinValue</c> 表示"这一天还没报过重置"。</summary>
        private const long NoResetDay = long.MinValue;

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly ActivityScheduleTables _tables;

        /// <summary>
        /// 与子表行对齐的"已报过每日重置"的 UTC 日序号水位。
        /// <para>
        /// 只在真正报出 <c>isDailyResetDue=true</c> 的那一拍推进：若改成"每拍都记下当日序号"，
        /// 跨日后第一拍落在 reset 小时之前时就会把跨日信号提前吃掉，同一天稍后过了 reset 小时那拍
        /// 永远判不出跨日（漏重置）。初值 <see cref="NoResetDay"/> 让进程跨午夜重启后
        /// 首拍仍能补报当天应做的重置。
        /// </para>
        /// </summary>
        private readonly long[] _resetDay;

        /// <summary>最近一次 tick 裁决后的最终开启位（与子表行对齐）。</summary>
        private bool[] _open;

        /// <summary>tick 期双缓冲的另一半：构造时分配，tick 内零分配。</summary>
        private bool[] _scratchOpen;

        /// <summary>最近一次 tick 的每日重置标记（与子表行对齐，每拍整体重写）。</summary>
        private readonly bool[] _resetDue;

        /// <summary>最近一次 tick 的开启条数（轮换后的口径）。</summary>
        private int _openCount;

        /// <summary>加载子表并初始化排期状态；构造里不 tick、不注册总线、不发事件。</summary>
        /// <param name="bus">切片消息总线（只用于广播开启/关闭事实）。</param>
        /// <param name="time">可快进时间源：窗口判定与跨日判定都读它。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/>、<paramref name="time"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">活动排期子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">活动排期子表数据非法。</exception>
        public ActivityScheduleHandler(ICqeBus bus, ITimeProvider time, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _time = time;
            _tables = new ActivityScheduleTables(loader);

            int count = _tables.Count;
            _open = new bool[count];
            _scratchOpen = new bool[count];
            _resetDue = new bool[count];
            _resetDay = new long[count];
            for (int i = 0; i < count; i++)
            {
                _resetDay[i] = NoResetDay;
            }

            _openCount = 0;
        }

        /// <summary>当前处于开启态的活动条数（轮换后口径，= 最近一次 tick 的结果）。</summary>
        public int OpenCount
        {
            get { return _openCount; }
        }

        /// <summary>
        /// 注册排期 tick 命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（广播用的是构造注入的总线）。
        /// </summary>
        /// <param name="bus">切片消息总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<ActivityTickCommand>(this);
            bus.RegisterQueryHandler<ActivityQueryRequest, ActivityScheduleSnapshot>(this);
        }

        /// <summary>
        /// 按当前时间重算开启集合（幂等）：
        /// <list type="number">
        /// <item>时间窗口判定：<c>start &lt;= now &lt; end</c>（左闭右开，结束那一秒已关闭）；</item>
        /// <item>轮换裁决：同 <c>rotationGroup</c> 内只保留 <c>priority</c> 最大者，相等取
        /// <c>activityId</c> 小者；其余在快照里可见但 <c>IsOpen=false</c>；</item>
        /// <item>每日重置：对已开启活动，若 UTC 日序号与上次报过重置的日序号不同、
        /// 且当前 UTC 小时数 &gt;= <c>dailyResetHourUtc</c> → 本拍报一次 <c>isDailyResetDue=true</c>；</item>
        /// <item>事件：与上一拍逐一比对，新开 → <see cref="ActivityOpenedEvent"/>，
        /// 关闭 → <see cref="ActivityClosedEvent"/>，无变化不发。</item>
        /// </list>
        /// <para>
        /// 顺序固定：先把新集合与重置标记整体落定，再按配置表行顺序广播事件——
        /// 订阅方在事件回调里查询到的一定是最终状态。
        /// </para>
        /// <para>空表：直接退化为"无活动"，不广播任何事件、不抛异常。</para>
        /// </summary>
        public void Handle(in ActivityTickCommand command)
        {
            long now = CurrentUnixSeconds();
            long dayIndex = FloorDivide(now, SecondsPerDay);
            int hourUtc = (int)((now - (dayIndex * SecondsPerDay)) / SecondsPerHour);

            bool[] current = _scratchOpen;
            bool[] previous = _open;

            for (int i = 0; i < _tables.Count; i++)
            {
                current[i] = IsWithinWindow(_tables.Get(i), now);
            }

            ApplyRotation(current, now);

            int openCount = 0;
            for (int i = 0; i < _tables.Count; i++)
            {
                bool isOpen = current[i];
                if (!isOpen)
                {
                    _resetDue[i] = false;
                    continue;
                }

                openCount++;
                ActivityScheduleDef def = _tables.Get(i);

                // 跨日判定口径：UTC 日序号（now / 86400 向下取整）+ 当日 UTC 小时数。
                // 过了 reset 整点即算到点（04:00:00 当秒已经是新的重置日）。
                bool resetDue = hourUtc >= def.DailyResetHourUtc && _resetDay[i] != dayIndex;
                if (resetDue)
                {
                    _resetDay[i] = dayIndex;
                }

                _resetDue[i] = resetDue;
            }

            // 双缓冲交换：状态先整体落定，再广播事件，保证订阅方回调里读到的是最终态。
            _openCount = openCount;
            _open = current;
            _scratchOpen = previous;

            for (int i = 0; i < _tables.Count; i++)
            {
                if (current[i] == previous[i])
                {
                    continue;
                }

                int activityId = _tables.Get(i).ActivityId;
                if (current[i])
                {
                    _bus.Publish(new ActivityOpenedEvent(activityId));
                }
                else
                {
                    _bus.Publish(new ActivityClosedEvent(activityId));
                }
            }
        }

        /// <summary>
        /// 返回活动排期全量只读快照：开启态与重置标记来自最近一次 tick，
        /// <see cref="ActivityScheduleSnapshot.NowUnixSeconds"/> 与 <c>SecondsRemaining</c> 按查询时刻折算。
        /// <para>空表返回空数组、<c>OpenCount = 0</c>，不崩也不抛。</para>
        /// </summary>
        public ActivityScheduleSnapshot Handle(in ActivityQueryRequest query)
        {
            long now = CurrentUnixSeconds();
            int count = _tables.Count;
            ActivityState[] activities = new ActivityState[count];

            for (int i = 0; i < count; i++)
            {
                ActivityScheduleDef def = _tables.Get(i);
                bool isOpen = _open[i];
                activities[i] = new ActivityState(
                    def.ActivityId,
                    def.Name,
                    isOpen,
                    isOpen ? RemainingSeconds(def, now) : 0,
                    _resetDue[i]);
            }

            return new ActivityScheduleSnapshot(activities, _openCount, now);
        }

        /// <summary>左闭右开窗口判定：<c>start &lt;= now &lt; end</c>（用 long 比较，避免 int 减法溢出）。</summary>
        private static bool IsWithinWindow(ActivityScheduleDef def, long now)
        {
            return now >= def.StartUnixSeconds && now < def.EndUnixSeconds;
        }

        /// <summary>
        /// 同 <c>rotationGroup</c> 内做轮换裁决：只要存在另一个"同样落在窗口内、且更优先"的活动，
        /// 本活动就从 <paramref name="openBits"/> 里被降为关闭（同组胜者唯一，无需多轮迭代）。
        /// </summary>
        private void ApplyRotation(bool[] openBits, long now)
        {
            int count = _tables.Count;
            for (int i = 0; i < count; i++)
            {
                if (!openBits[i])
                {
                    continue;
                }

                ActivityScheduleDef candidate = _tables.Get(i);
                for (int j = 0; j < count; j++)
                {
                    if (j == i || !IsWithinWindow(_tables.Get(j), now))
                    {
                        continue;
                    }

                    ActivityScheduleDef rival = _tables.Get(j);
                    if (rival.RotationGroup != candidate.RotationGroup)
                    {
                        continue;
                    }

                    if (!IsPreferred(rival, candidate))
                    {
                        continue;
                    }

                    openBits[i] = false;
                    break;
                }
            }
        }

        /// <summary>
        /// 轮换优先级：<c>priority</c> 大者胜；相等时 <c>activityId</c> 小者胜（保证同一份配置结果唯一）。
        /// </summary>
        private static bool IsPreferred(ActivityScheduleDef left, ActivityScheduleDef right)
        {
            if (left.Priority != right.Priority)
            {
                return left.Priority > right.Priority;
            }

            return left.ActivityId < right.ActivityId;
        }

        /// <summary>距关闭剩余秒：<c>end - now</c> 夹到 [0, int.MaxValue]（表内 end 是 int32，差值不会溢出 int，仍按 long 算保险）。</summary>
        private static int RemainingSeconds(ActivityScheduleDef def, long now)
        {
            long remaining = def.EndUnixSeconds - now;
            if (remaining <= 0)
            {
                return 0;
            }

            return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
        }

        /// <summary>当前 UTC 时刻折成 Unix 秒；<see cref="ITimeProvider.UtcNow"/> 按 UTC 语义解释（Local 先转 UTC）。</summary>
        private long CurrentUnixSeconds()
        {
            DateTime utcNow = _time.UtcNow;
            if (utcNow.Kind == DateTimeKind.Local)
            {
                utcNow = utcNow.ToUniversalTime();
            }
            else if (utcNow.Kind != DateTimeKind.Utc)
            {
                // Unspecified 一律按 UTC 解释：逻辑层不猜测本机时区（AIF003）。
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            }

            return (long)Math.Floor((utcNow - UnixEpoch).TotalSeconds);
        }

        /// <summary>向下取整的整数除法：Unix 秒在 1970 年之前为负时也能得到正确的日序号。</summary>
        private static long FloorDivide(long value, long divisor)
        {
            long quotient = value / divisor;
            if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
            {
                quotient--;
            }

            return quotient;
        }
    }
}

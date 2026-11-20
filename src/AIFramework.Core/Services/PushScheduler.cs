#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 推送大脑：把 <see cref="IPushService"/>（排期 / token 登记 / 权限）包成"带容量上限、可算下一次触发、
    /// 可派发到期提醒、可降级"的业务端口，让切片与 UI 只需要问四件事——token 登记好了没、
    /// 这条提醒排上了没、现在有哪些该弹了、下一次是几点。
    /// <para>
    /// <b>为什么这段逻辑必须在 Core 而不是某个适配器里</b>：容量上限、每日提醒的下一次触发时间、
    /// "已派发不重复派发"、重复通知顺延这四条规则在 Console、Unity 与未来的真实 APNs/FCM 适配器上
    /// 必须<b>逐字一致</b>。写进任一适配器，另一端就会漂移出"只在真机上一天弹两次"或
    /// "改时间后收到两条"的差异；而这类差异是玩家直接感知的骚扰，不是内部细节。
    /// Core 是唯一能被两个装配入口共用、又能被无头单测逐条钉死的位置
    /// （与 <see cref="FriendService"/>、<see cref="AdShowScheduler"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>本类持有"计划台账"，后端只是执行者</b>：<see cref="GetDueNotifications"/> 需要知道
    /// "这条已经派发过没有"，而 <see cref="IPushService"/> 的 <c>GetScheduled</c> 只描述平台侧还有什么。
    /// 因此本类自己维护一份 <see cref="Entry"/> 台账（最多 <see cref="_maxScheduled"/> 条），
    /// 并把"已派发"这个只属于业务层的状态记在台账上——平台侧没有这个概念（它只知道"什么时候响"）。
    /// </para>
    /// <para>
    /// <b>容量上限的取舍：宁可拒绝，也不静默挤掉已有提醒</b>。超额时
    /// <see cref="ScheduleDaily"/> 直接返回 <c>false</c> 并<b>完整保留</b>原计划，
    /// 绝不为了塞进新提醒而淘汰一条旧的。理由：旧提醒多半是"体力已满""活动即将结束"这类
    /// 已经承诺给玩家的提醒，静默丢掉是违约；而新提醒的调用方拿到 <c>false</c> 还能自己决定
    /// （降级、延后、提示），信息没有丢失。容量是防"业务把排期表当队列用"的护栏，不是调度策略。
    /// </para>
    /// <para>
    /// <b>每日提醒的下一次触发时间</b>：按 <see cref="ITimeProvider.UtcNow"/> 的 UTC 小时/分钟换算，
    /// 今天该时刻<b>已过（含恰好等于现在）</b>则顺延 86400 秒到明天。用 UTC 而不是本地时区，是为了
    /// "同一账号换时区不会把提醒挪到半夜"，与 <see cref="FriendService"/> / <see cref="AdShowScheduler"/>
    /// 的 UTC 自然日口径保持同一套约定。<b>时区与夏令时由平台的本地通知调度器在真机上处理</b>：
    /// 本类算的是"下一次触发的绝对时刻"，玩家飞到另一个时区后系统按绝对时刻触发，
    /// 不会自动变成"新时区的 09:00"——这是已知限制，见交付报告。
    /// </para>
    /// <para>
    /// <b>派发与顺延是两件事，必须分开</b>：<see cref="GetDueNotifications"/> 只<b>返回</b>到期的提醒
    /// （不真派发，弹窗由宿主/适配层负责），并把它从"到期集合"里移出（<see cref="Entry.Dispatched"/>）；
    /// <see cref="Refresh"/> 才负责把已派发的提醒顺延到下一周期。这样"取一次 → 崩溃 → 再取"不会重复弹，
    /// 而"取到了但还没 Refresh"的提醒也不会被静默吞掉。
    /// </para>
    /// <para>
    /// <b>本类只排"每日提醒"这一种重复通知</b>：<see cref="ScheduleDaily"/> 是唯一的排期入口，
    /// 因此台账里每一条都是重复通知，<see cref="PushNotification.IsRepeating"/> 恒为 <c>true</c>，
    /// 派发后<b>不会</b>从计划里消失（否则"每日"就变成"仅今天一次"）。一次性提醒（例如"活动 1 小时后结束"）
    /// 需要新增排期入口，届时在 <see cref="GetDueNotifications"/> 里补"派发后移出计划"的分支；
    /// 本阶段刻意不做，避免留一段没有入口、无法被测试覆盖的死代码。
    /// </para>
    /// <para>
    /// <b>不可用降级</b>：<see cref="IPushService.IsAvailable"/> 为 <c>false</c>（或读取时抛异常）时，
    /// <see cref="ScheduleDaily"/> 返回 <c>false</c>、<see cref="EnsureRegistered"/> 返回
    /// <see cref="PushRegisterResult.Unavailable"/>、<see cref="GetDueNotifications"/> 返回空数组，
    /// <b>不抛异常</b>。推送是外围系统，断网不该拦住玩家进游戏。
    /// </para>
    /// <para>
    /// <b>参数错误优先于可用性判定</b>：<paramref name="hourUtc"/> / <paramref name="minuteUtc"/> 越界一律抛
    /// <see cref="ArgumentOutOfRangeException"/>，<b>即使后端当时不可用</b>；token 非法一律返回
    /// <see cref="PushRegisterResult.InvalidToken"/>，<b>即使后端不可用</b>。理由是这两类错误的性质不同——
    /// 越界时刻与脏 token 是调用方的编程错误（必须立刻响，否则会被静默"降级"成一次看不出问题的失败），
    /// 而"后端不可用"是对一次合法调用的业务回答。把编程错误混进 <c>Unavailable</c>，
    /// 会让真实事故（UI 传了 24 点、服务端下发了带换行的 token）在日志里跟"断网"一模一样。
    /// </para>
    /// <para>
    /// <b>时间全部来自 <see cref="ITimeProvider"/></b>：本类不读系统时钟（逻辑层铁律）。
    /// 时间源违约（抛异常）时按"算不出下一次触发"处理——排期返回 <c>false</c>、
    /// 到期列表返回空数组，方向刻意 fail-closed：宁可这一轮不弹，也不要拿一个错的时间去排。
    /// </para>
    /// <para>线程模型：<b>非线程安全</b>。排期/取消/派发都在主线程串行调用；本类不加锁。</para>
    /// </summary>
    public sealed class PushScheduler
    {
        /// <summary>默认计划容量上限（32 条，够覆盖签到/体力/活动三类提醒各若干条）。</summary>
        public const int DefaultMaxScheduled = 32;

        /// <summary>
        /// token 长度上限（4096 字符）。
        /// <para>真实 APNs/FCM token 是 100~300 字符的 base64/hex；4096 是"明显不正常"的护栏，
        /// 用来挡住把整个 JWT/JSON 塞进 token 字段的调用方。</para>
        /// </summary>
        public const int MaxTokenLength = 4096;

        /// <summary>每日提醒的重复间隔（86400 秒），也是"下一次触发"的顺延步长。</summary>
        public const int DailyIntervalSeconds = 86400;

        /// <summary>参数错误原因：小时越界（<see cref="ArgumentOutOfRangeException.Message"/> 用）。</summary>
        public const string ReasonHourOutOfRange = "hourUtc must be in 0..23";

        /// <summary>参数错误原因：分钟越界。</summary>
        public const string ReasonMinuteOutOfRange = "minuteUtc must be in 0..59";

        /// <summary>一天有多少秒（算术用，避免与 <see cref="DailyIntervalSeconds"/> 的 int/long 混用）。</summary>
        private const long SecondsPerDay = 86400L;

        /// <summary>Unix 纪元（UTC）。只做算术换算，不读系统时钟。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>推送后端（构造后不可变）。</summary>
        private readonly IPushService _push;

        /// <summary>时间源（唯一时钟来源；构造后不可变）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>计划容量上限（负值已在构造期夹到 0，即"一条都不许排"）。</summary>
        private readonly int _maxScheduled;

        /// <summary>计划台账（顺序不参与语义；到期列表按触发时间现排序）。</summary>
        private readonly List<Entry> _entries;

        /// <summary>创建推送大脑。</summary>
        /// <param name="push">推送后端（非 <c>null</c>）。</param>
        /// <param name="time">时间源（非 <c>null</c>；下一次触发时间必须经由此端口，逻辑层禁止直接读系统时钟）。</param>
        /// <param name="maxScheduled">
        /// 计划容量上限（默认 <see cref="DefaultMaxScheduled"/>）。
        /// <para>
        /// <b>负值按 0 处理（fail-closed）</b>：容量配成负数是配置事故，按 0 处理意味着"一条都排不进去"，
        /// 而不是"无上限"。宁可少一个功能，也不让配置错误变成一张可以无限增长、把平台调度表塞爆的计划表
        /// （与 <see cref="FriendService"/> 的负配额口径一致）。
        /// </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="push"/> 或 <paramref name="time"/> 为 <c>null</c>（装配期编程错误，必须立刻响）。
        /// </exception>
        public PushScheduler(IPushService push, ITimeProvider time, int maxScheduled = DefaultMaxScheduled)
        {
            _push = push ?? throw new ArgumentNullException(nameof(push));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _maxScheduled = maxScheduled < 0 ? 0 : maxScheduled;

            // 刻意不按上限预分配：上限是可配的（配置笔误可能给到 int.MaxValue），
            // 照着它 new 一个数组会在装配期直接 OOM —— 一个推送容量配置不该把游戏启动搞崩。
            // 8 覆盖常见业务（签到/体力/活动各一两条），超出部分由 List 自己增长。
            _entries = new List<Entry>(_maxScheduled < 8 ? _maxScheduled : 8);
        }

        /// <summary>
        /// 当前计划中的通知条数（每日提醒派发后仍占一条——它明天还要再响；只有 <see cref="Cancel"/> /
        /// <see cref="CancelAll"/> 会让它减少）。
        /// </summary>
        public int ScheduledCount
        {
            get { return _entries.Count; }
        }

        /// <summary>
        /// 确保远端 token 已登记（<b>幂等</b>：已登记同一条 token 时直接返回
        /// <see cref="PushRegisterResult.Success"/>，不触达后端）。
        /// <para>
        /// <b>校验顺序固定为"token 合法性 → 已登记短路 → 后端可用性 → 真正登记"</b>：
        /// <list type="bullet">
        /// <item>token 非法（空/纯空白、超长、含非可打印 ASCII）→ <see cref="PushRegisterResult.InvalidToken"/>，
        /// <b>不触达后端</b>。脏 token 换一条才有意义，不该浪费一次网络/授权往返。</item>
        /// <item>已登记同一条 → <see cref="PushRegisterResult.Success"/>，<b>不触达后端</b>。这是"启动即确保"路径的
        /// 常态：每次冷启动都会调一次，若每次都真的打后端，等于给服务端加一份无意义的 QPS，
        /// 而且后端一抖动就会把一次"本来已经就绪"的推送说成失败。</item>
        /// <item>后端不可用 → <see cref="PushRegisterResult.Unavailable"/>（不是 <c>InvalidToken</c>，也不是成功）。</item>
        /// <item>其余交给后端裁决：授权未授予时它会如实返回 <see cref="PushRegisterResult.PermissionDenied"/>，
        /// 本类<b>不</b>替它判断权限（权限是平台事实，见 <see cref="IPushService"/>）。</item>
        /// </list>
        /// </para>
        /// <para>后端违约抛异常时收敛成 <see cref="PushRegisterResult.Unavailable"/>（不把异常漏给登录流程）。</para>
        /// </summary>
        /// <param name="token">远端下发的 token（<c>null</c>/空/纯空白 → <see cref="PushRegisterResult.InvalidToken"/>）。</param>
        /// <returns>登记结果（<b>永不抛异常</b>）。</returns>
        public PushRegisterResult EnsureRegistered(string? token)
        {
            if (!IsValidToken(token))
            {
                return PushRegisterResult.InvalidToken;
            }

            string candidate = token!;

            if (string.Equals(SafeRegisteredToken(), candidate, StringComparison.Ordinal))
            {
                return PushRegisterResult.Success;
            }

            if (!SafeIsAvailable())
            {
                return PushRegisterResult.Unavailable;
            }

            try
            {
                return _push.RegisterToken(candidate);
            }
            catch (Exception)
            {
                // 契约要求不抛；实现违约时按"现在登记不上"处理，稍后由下一次 EnsureRegistered 重试。
                return PushRegisterResult.Unavailable;
            }
        }

        /// <summary>
        /// 排一条每日提醒：按 UTC 小时/分钟算出<b>下一次</b>触发时间（今天已过则明天），
        /// 并以 <see cref="DailyIntervalSeconds"/> 为重复间隔交给后端。
        /// <para>
        /// <b>同 id 覆盖旧计划</b>：改提醒时间必须复用同一个 id，否则玩家会在旧时间与新时间各收到一次。
        /// 覆盖<b>不消耗</b>容量（它替换的是已有的一格，不是新增）。
        /// </para>
        /// <para>
        /// <b>容量超额 → 返回 <c>false</c> 且完整保留原计划</b>（见类型注释：宁可拒绝，也不静默挤掉已有提醒）。
        /// </para>
        /// <para>
        /// <b>后端拒绝 → 返回 <c>false</c> 且不写本地台账</b>：先让后端接受、再记账，避免出现
        /// "本地以为排上了、平台里什么都没有"的幽灵计划。
        /// </para>
        /// </summary>
        /// <param name="notificationId">通知 id（唯一键；同 id 覆盖）。</param>
        /// <param name="title">标题（<c>null</c> 归一化为空串）。</param>
        /// <param name="body">正文（<c>null</c> 归一化为空串）。</param>
        /// <param name="hourUtc">UTC 小时（0..23）。</param>
        /// <param name="minuteUtc">UTC 分钟（0..59）。</param>
        /// <returns><c>true</c> = 已排入计划；<c>false</c> = 被容量/可用性/时钟/后端拒绝（<b>永不抛异常</b>，参数越界除外）。</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="hourUtc"/> 不在 0..23，或 <paramref name="minuteUtc"/> 不在 0..59
        /// （<b>即使后端不可用也照抛</b>，见类型注释的参数错误优先级）。
        /// </exception>
        public bool ScheduleDaily(int notificationId, string title, string body, int hourUtc, int minuteUtc)
        {
            if (hourUtc < 0 || hourUtc > 23)
            {
                throw new ArgumentOutOfRangeException(nameof(hourUtc), hourUtc, ReasonHourOutOfRange);
            }

            if (minuteUtc < 0 || minuteUtc > 59)
            {
                throw new ArgumentOutOfRangeException(nameof(minuteUtc), minuteUtc, ReasonMinuteOutOfRange);
            }

            int index = IndexOf(notificationId);

            // 容量护栏只拦"新增"：覆盖已有的一格不占额外容量（否则改一次时间就会被自己的上限拒掉）。
            if (index < 0 && _entries.Count >= _maxScheduled)
            {
                return false;
            }

            if (!SafeIsAvailable())
            {
                return false;
            }

            long now = SafeNowUnixSeconds();
            if (now < 0)
            {
                // 时间源违约：算不出"下一次"，宁可这一轮不排，也不要拿一个错的时间去排。
                return false;
            }

            long fireAt = NextDailyFireAt(now, hourUtc, minuteUtc);
            var request = new PushNotificationRequest(
                notificationId,
                title,
                body,
                fireAt,
                DailyIntervalSeconds);

            if (!SafeSchedule(request))
            {
                // 后端拒绝（平台额度/参数不被接受/不可用）：本地不记账，保持原计划不动。
                return false;
            }

            var entry = new Entry(notificationId, title, body, fireAt);

            if (index >= 0)
            {
                _entries[index] = entry;
            }
            else
            {
                _entries.Add(entry);
            }

            return true;
        }

        /// <summary>
        /// 取消指定通知（同时清掉平台侧的那一条）。
        /// <para>
        /// <b>只取消本地台账里存在的通知</b>：不在计划里直接返回 <c>false</c> 且不触达后端——
        /// 本类是计划的所有者，"台账里没有"就是"没排过"，没有理由去打扰平台。
        /// </para>
        /// <para>
        /// <b>取消方向 fail-safe</b>：本地台账一定先删掉；后端调用失败或后端不可用都不影响返回值。
        /// 宁可多取消一次（玩家少收一条），也不要因为一次后端异常留下一条谁也取消不掉的幽灵提醒。
        /// </para>
        /// </summary>
        /// <param name="notificationId">通知 id。</param>
        /// <returns><c>true</c> = 原本在计划里、已取消；<c>false</c> = 计划里本来就没有（合法结果）。</returns>
        public bool Cancel(int notificationId)
        {
            int index = IndexOf(notificationId);
            if (index < 0)
            {
                return false;
            }

            _entries.RemoveAt(index);
            SafeCancel(notificationId);
            return true;
        }

        /// <summary>
        /// 取消全部计划（<b>登出 / 玩家关闭推送时必须调用</b>）。
        /// <para>
        /// 本地台账先清空，再尽力通知后端；后端异常被吞掉——登出路径不允许因为一次推送异常而卡住
        /// （账号已经切了却还留着上一个账号的提醒，是比"取消失败"严重得多的事故）。
        /// </para>
        /// </summary>
        public void CancelAll()
        {
            _entries.Clear();

            try
            {
                _push.CancelAll();
            }
            catch (Exception)
            {
                // 契约要求不抛；违约时按"平台侧没清干净"处理，本地已经清空，不会再有新的派发。
            }
        }

        /// <summary>
        /// 取出<b>已到期且未派发过</b>的通知（按触发时间升序，同一时刻按 id 升序），
        /// 并立即把它们标记为已派发。
        /// <para>
        /// <b>本方法只返回、不真派发</b>：真正弹窗/写通知中心由宿主或适配层完成（那是平台能力）。
        /// 把"取"和"弹"分开，才能让无头测试断言"该弹哪几条、弹了几次"而不依赖任何引擎。
        /// </para>
        /// <para>
        /// <b>同一 id 只会返回一次</b>：返回即标记，重复调用不会重复返回同一条。
        /// 提醒仍留在计划里等 <see cref="Refresh"/> 顺延到下一周期（每日提醒派发后不能从计划里消失）。
        /// </para>
        /// <para>
        /// <b>后端不可用 / 时间源违约 → 返回空数组</b>：没有可靠的"现在"，就没有可靠的"到期"，
        /// 宁可这一轮不弹，也不要按错误时间批量弹出积压的提醒。
        /// </para>
        /// </summary>
        /// <returns>到期通知快照数组（永不为 <c>null</c>；没有到期项时为空数组）。</returns>
        public PushNotification[] GetDueNotifications()
        {
            if (_entries.Count == 0)
            {
                return Array.Empty<PushNotification>();
            }

            if (!SafeIsAvailable())
            {
                return Array.Empty<PushNotification>();
            }

            long now = SafeNowUnixSeconds();
            if (now < 0)
            {
                return Array.Empty<PushNotification>();
            }

            // 先收集再统一标记：避免"边遍历边改台账"把同一轮的到期项漏掉。
            List<Entry>? due = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Dispatched || entry.FireAtUnixSeconds > now)
                {
                    continue;
                }

                if (due == null)
                {
                    due = new List<Entry>(4);
                }

                due.Add(entry);
            }

            if (due == null)
            {
                return Array.Empty<PushNotification>();
            }

            due.Sort(CompareByFireTime);

            var result = new PushNotification[due.Count];
            for (int i = 0; i < due.Count; i++)
            {
                Entry entry = due[i];

                // 返回即标记：同一条提醒在 Refresh 顺延之前不会再次出现在到期列表里。
                // 刻意<b>不</b>从台账移出——每日提醒派发后仍要留在计划里等明天（见类型注释）。
                entry.Dispatched = true;
                result[i] = entry.ToNotification();
            }

            return result;
        }

        /// <summary>
        /// 重新计算"下一次触发"：把<b>已派发</b>的重复通知顺延到下一个未来周期，并把新时间重新推给后端。
        /// <para>
        /// <b>只顺延"已派发"的重复通知</b>：还没被 <see cref="GetDueNotifications"/> 取走的到期通知
        /// 必须原地保留——否则"到点了但宿主还没轮询"的那一条会被静默吞掉，玩家永远收不到。
        /// 这条规则是"宁可晚，不可丢"的落地：漏一条提醒是功能事故，晚一轮是体验问题。
        /// </para>
        /// <para>
        /// <b>跨多天缺失只顺延一次</b>：玩家离线 5 天后回来，不会补弹 5 条昨天的提醒，而是直接跳到
        /// 下一个未来时刻（用整除算周期数，不做循环推进，避免脏时间导致长循环）。
        /// </para>
        /// <para>
        /// <b>后端不可用时本方法不做任何改动</b>：平台侧没有可对齐的计划，本地单方面顺延只会制造
        /// "本地说下次是明天、平台还记着昨天"的两份漂移计划。等后端恢复后调用一次即可整体对齐。
        /// </para>
        /// <para>
        /// <b>重新推给后端失败只影响平台侧</b>：本地时间已经顺延（保证不会被重复派发），
        /// 平台仍记着旧时间——最坏结果是多弹一条旧提醒，比"同一条提醒被本地反复派发"轻得多。
        /// </para>
        /// </summary>
        public void Refresh()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            if (!SafeIsAvailable())
            {
                return;
            }

            long now = SafeNowUnixSeconds();
            if (now < 0)
            {
                return;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (!entry.Dispatched)
                {
                    continue;
                }

                entry.FireAtUnixSeconds = AdvanceToFuture(entry.FireAtUnixSeconds, DailyIntervalSeconds, now);
                entry.Dispatched = false;
                SafeSchedule(entry.ToRequest());
            }
        }

        /// <summary>
        /// token 合法性（<b>本仓唯一口径</b>，适配层假实现直接复用，避免三处各写一份校验规则）。
        /// <para>
        /// 规则：非 <c>null</c>/非空/非纯空白；长度 ≤ <see cref="MaxTokenLength"/>；
        /// 每个字符都落在<b>可打印 ASCII</b>（0x21..0x7E）。
        /// </para>
        /// <para>
        /// <b>为什么连空格与控制字符一起拒绝</b>（比"仅拒绝非 ASCII"更严）：真实 token 是 base64/hex，
        /// 空格与换行只可能来自"把 JSON 转义后的字符串直接塞进来"或"被截断的日志"。
        /// 让这种脏数据通过，平台侧会以一条毫无线索的失败回报，排障成本远高于在这里直接拒绝。
        /// </para>
        /// </summary>
        /// <param name="token">待校验的 token（可为 <c>null</c>）。</param>
        /// <returns><c>true</c> = 形状合法（不代表平台一定会接受）。</returns>
        public static bool IsValidToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string value = token!;
            if (value.Length > MaxTokenLength)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '\u0021' || c > '\u007E')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// UTC 时刻 + 小时/分钟 → 下一次触发的 Unix 秒（今天该时刻已过则明天）。
        /// <para>
        /// 边界口径：候选时刻 <b>&lt;= 现在</b> 就算"已过"（恰好等于现在也算），顺延 86400 秒。
        /// 取"等于也算已过"是因为平台在"现在"这一刻排期通常已经来不及触发，
        /// 与其排一条立刻到期的提醒（可能在登录动画里就弹出来），不如稳稳排到明天。
        /// </para>
        /// </summary>
        /// <param name="nowUnixSeconds">当前 UTC 秒（必须 ≥ 0）。</param>
        /// <param name="hourUtc">UTC 小时（已校验 0..23）。</param>
        /// <param name="minuteUtc">UTC 分钟（已校验 0..59）。</param>
        /// <returns>下一次触发的 Unix 秒（严格大于 <paramref name="nowUnixSeconds"/>）。</returns>
        private static long NextDailyFireAt(long nowUnixSeconds, int hourUtc, int minuteUtc)
        {
            long dayStart = nowUnixSeconds - (nowUnixSeconds % SecondsPerDay);
            long candidate = dayStart + (hourUtc * 3600L) + (minuteUtc * 60L);

            return candidate <= nowUnixSeconds ? candidate + SecondsPerDay : candidate;
        }

        /// <summary>
        /// 把触发时间推到严格晚于 <paramref name="now"/> 的下一个周期（用整除算周期数，不做循环推进）。
        /// </summary>
        /// <param name="fireAt">原触发时间。</param>
        /// <param name="intervalSeconds">重复间隔（&lt;= 0 时原样返回，调用方已保证只对重复通知调用）。</param>
        /// <param name="now">当前 UTC 秒。</param>
        /// <returns>顺延后的触发时间（严格大于 <paramref name="now"/>）。</returns>
        private static long AdvanceToFuture(long fireAt, int intervalSeconds, long now)
        {
            if (intervalSeconds <= 0)
            {
                return fireAt;
            }

            if (fireAt > now)
            {
                return fireAt;
            }

            long missedCycles = ((now - fireAt) / intervalSeconds) + 1;
            return fireAt + (missedCycles * intervalSeconds);
        }

        /// <summary>按 id 找台账下标（找不到返回 -1；线性扫描，容量上限只有 32）。</summary>
        /// <param name="notificationId">通知 id。</param>
        /// <returns>台账下标；-1 = 不在计划里。</returns>
        private int IndexOf(int notificationId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].NotificationId == notificationId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>到期列表的排序：触发时间升序，同一时刻按 id 升序（保证顺序稳定可断言）。</summary>
        /// <param name="left">左侧条目。</param>
        /// <param name="right">右侧条目。</param>
        /// <returns>比较结果。</returns>
        private static int CompareByFireTime(Entry left, Entry right)
        {
            int byTime = left.FireAtUnixSeconds.CompareTo(right.FireAtUnixSeconds);
            return byTime != 0 ? byTime : left.NotificationId.CompareTo(right.NotificationId);
        }

        /// <summary>读后端可用性；实现违约抛异常时按"不可用"处理（降级总比崩溃好）。</summary>
        /// <returns>后端是否可用。</returns>
        private bool SafeIsAvailable()
        {
            try
            {
                return _push.IsAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读已登记 token；实现违约（抛异常/返回 <c>null</c>）时一律按"未登记"处理。</summary>
        /// <returns>已登记 token；未登记为空串。</returns>
        private string SafeRegisteredToken()
        {
            try
            {
                return _push.RegisteredToken ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>交给后端排期；违约抛异常按"被拒绝"处理。</summary>
        /// <param name="request">排期请求。</param>
        /// <returns>后端是否接受。</returns>
        private bool SafeSchedule(in PushNotificationRequest request)
        {
            try
            {
                return _push.Schedule(request);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>尽力取消平台侧的一条通知（失败不影响本地台账，见 <see cref="Cancel"/> 的 fail-safe 约定）。</summary>
        /// <param name="notificationId">通知 id。</param>
        private void SafeCancel(int notificationId)
        {
            try
            {
                _push.Cancel(notificationId);
            }
            catch (Exception)
            {
                // 契约要求不抛；本地已移出计划，平台侧残留由下一次 CancelAll 或平台自身清理兜底。
            }
        }

        /// <summary>
        /// 当前 UTC 时间的 Unix 秒；时间源违约或早于 1970 时返回 -1（哨兵，调用方据此放弃本轮计算）。
        /// <para>用 -1 而不是 0 作哨兵：0 是"1970-01-01"这个合法时刻，与"读不到时间"必须区分开。</para>
        /// </summary>
        /// <returns>Unix 秒；-1 = 时间不可用。</returns>
        private long SafeNowUnixSeconds()
        {
            try
            {
                long seconds = (long)(_time.UtcNow - UnixEpoch).TotalSeconds;
                return seconds < 0 ? -1 : seconds;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 计划台账的一条：id + 文案 + 当前触发时间 + "是否已派发"标记。
        /// <para>
        /// <b>为什么不是 <see cref="PushNotificationRequest"/> 直接存台账</b>：请求是 <c>readonly struct</c>，
        /// 而顺延需要就地改触发时间、派发需要就地改标记。用一个可变的小对象，比每次替换整个结构体
        /// 更不容易漏改（漏改 <c>Dispatched</c> 的后果就是同一条提醒被反复派发）。
        /// </para>
        /// <para>
        /// <b>不存重复间隔</b>：本类只排每日提醒，间隔恒为 <see cref="DailyIntervalSeconds"/>
        /// （见类型注释"只排每日提醒"）。存一个永远是 86400 的字段只会让人以为它可变。
        /// </para>
        /// </summary>
        private sealed class Entry
        {
            /// <summary>构造一条台账记录（新排入或覆盖旧计划时一律重置为"未派发"）。</summary>
            /// <param name="notificationId">通知 id。</param>
            /// <param name="title">标题（可为 <c>null</c>，由结构体归一化）。</param>
            /// <param name="body">正文（可为 <c>null</c>，由结构体归一化）。</param>
            /// <param name="fireAtUnixSeconds">触发时间（UTC 秒）。</param>
            public Entry(int notificationId, string title, string body, long fireAtUnixSeconds)
            {
                NotificationId = notificationId;
                Title = title;
                Body = body;
                FireAtUnixSeconds = fireAtUnixSeconds;
            }

            /// <summary>通知 id（唯一键）。</summary>
            public int NotificationId { get; }

            /// <summary>标题（原样保存，输出时由结构体兜底空串）。</summary>
            public string Title { get; }

            /// <summary>正文（原样保存，输出时由结构体兜底空串）。</summary>
            public string Body { get; }

            /// <summary>当前触发时间（<see cref="Refresh"/> 会就地顺延）。</summary>
            public long FireAtUnixSeconds { get; set; }

            /// <summary>是否已经由 <see cref="GetDueNotifications"/> 返回过（返回即置位，<see cref="Refresh"/> 清位）。</summary>
            public bool Dispatched { get; set; }

            /// <summary>转回排期请求（顺延后重新推给后端用）。</summary>
            /// <returns>当前状态的请求。</returns>
            public PushNotificationRequest ToRequest()
            {
                return new PushNotificationRequest(NotificationId, Title, Body, FireAtUnixSeconds, DailyIntervalSeconds);
            }

            /// <summary>转成对外的只读快照（每日提醒恒为重复，见类型注释）。</summary>
            /// <returns>通知快照。</returns>
            public PushNotification ToNotification()
            {
                return new PushNotification(NotificationId, Title, Body, FireAtUnixSeconds, true);
            }
        }
    }
}

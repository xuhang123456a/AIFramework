#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台特效服务：无头环境下验证"特效端口契约"的参考实现。
    /// <para>
    /// <b>它不是空实现</b>：无头测试要能验收"哪些特效被播了、有没有被非法参数挡住、清场后是否归零"，
    /// 所以这里维护了真实的环形播放日志与最近 64 条活跃特效记录，只是不生成任何图形。
    /// 行为与 <c>UnityVfxService</c> 保持同一套语义（谁被拒、谁被淘汰、Clear 怎么算），
    /// 切换适配器时验收用例不用改。
    /// </para>
    /// <para>
    /// <b>时间源是可选的</b>：
    /// <list type="bullet">
    /// <item>注入 <see cref="ITimeProvider"/>：<see cref="ActiveCount"/> = 最近 64 条有效播放中
    /// 尚未到期的条数（按 <c>起播 + duration</c> 判定，过期自然衰减），用于"特效真的有寿命"的验收。</item>
    /// <item>不注入（<c>null</c>）：没有统一时钟可读（AIF003 禁止逻辑层读系统时间），
    /// "到期"无从判定，于是 <see cref="ActiveCount"/> = <b>自上次 <see cref="Clear"/> 起累计播放数</b>。
    /// 这不是偷懒，而是把"不可知"显式写成保守上界：只增不减，直到显式清场。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么非法请求"静默"拒绝</b>：与音频端口同一纪律——表现层不允许把业务流程炸掉。
    /// "静默"指不抛异常、不写播放日志；但仍计入 <see cref="RejectedCount"/>，让装配自检能看见
    /// "调用方在提交非法请求"这件事。
    /// </para>
    /// <para>线程模型：非线程安全。播放调用与帧推进同线程。</para>
    /// </summary>
    public sealed class ConsoleVfxService : IVfxService
    {
        /// <summary>播放日志环形容量（最近 64 条）。</summary>
        public const int LogCapacity = 64;

        /// <summary>活跃特效记录的环形容量：与日志同宽，便于"看到什么就数到什么"。</summary>
        public const int ActiveCapacity = 64;

        /// <summary>清场事件的日志文本（只在确实有活跃特效时写一条，避免刷噪声）。</summary>
        public const string ClearLogEntry = "vfx clear";

        /// <summary>可选时间源；<c>null</c> 时退化为"只增不减的累计数"模型。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>最近播放事件的环形日志（定长 <see cref="LogCapacity"/>，只存已格式化的诊断文本）。</summary>
        private readonly string[] _playLog;

        /// <summary>最近有效播放的环形记录（定长 <see cref="ActiveCapacity"/>，用于按时长判定活跃）。</summary>
        private readonly ActiveEffect[] _active;

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置。</summary>
        private int _logNext;

        /// <summary>活跃记录里当前有效条数。</summary>
        private int _activeCount;

        /// <summary>活跃记录的下一个写入位置；满员时它同时指向最老的一条。</summary>
        private int _activeNext;

        /// <summary>无时间源时的活跃口径：自上次 <see cref="Clear"/> 起的累计播放数。</summary>
        private int _activeTotal;

        /// <summary>累计被静默拒绝的请求数（诊断用，不受 <see cref="Clear"/> 影响）。</summary>
        private int _rejectedCount;

        /// <summary>构造控制台特效服务。</summary>
        /// <param name="timeProvider">可选时间源；传 <c>null</c> 表示按"累计播放数"统计活跃特效。</param>
        public ConsoleVfxService(ITimeProvider? timeProvider = null)
        {
            _time = timeProvider;
            _playLog = new string[LogCapacity];
            _active = new ActiveEffect[ActiveCapacity];
        }

        /// <summary>累计被静默拒绝的请求数（非法参数）。</summary>
        public int RejectedCount
        {
            get { return _rejectedCount; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 注入时间源时按"最近 <see cref="ActiveCapacity"/> 条有效播放里未到期的条数"计算；
        /// 超过 64 条同屏特效时该值会低估（环形只留最近 64 条），属诊断可接受的近似。
        /// 未注入时间源时返回自上次 <see cref="Clear"/> 起的累计播放数，只增不减。
        /// </remarks>
        public int ActiveCount
        {
            get
            {
                ITimeProvider? time = _time;
                if (time == null)
                {
                    return _activeTotal;
                }

                return CountUnexpired(time.ElapsedSeconds);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 参数非法时静默丢弃（不写日志、不进活跃记录、不动活跃计数），只累加 <see cref="RejectedCount"/>。
        /// </remarks>
        public void Play(in VfxRequest request)
        {
            if (!VfxRequestValidator.IsValid(in request))
            {
                _rejectedCount++;
                return;
            }

            AppendLog(FormatPlay(in request));
            RecordActive(in request);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 清场会写一条 <see cref="ClearLogEntry"/>（仅当确有活跃特效时），并重置活跃口径；
        /// <see cref="RejectedCount"/> 是生命周期诊断值，不回退。
        /// </remarks>
        public void Clear()
        {
            if (ActiveCount > 0)
            {
                AppendLog(ClearLogEntry);
            }

            Array.Clear(_active, 0, _active.Length);
            _activeCount = 0;
            _activeNext = 0;
            _activeTotal = 0;
        }

        /// <summary>
        /// 取走并清空最近播放日志（按时间顺序返回）。<b>会清空</b>：这是"排空"语义，
        /// 方便冒烟/测试分阶段断言，而不会把同一批事件重复打印两次。
        /// </summary>
        /// <returns>自上次排空以来的播放事件，最多 <see cref="LogCapacity"/> 条。</returns>
        public IReadOnlyList<string> DrainLog()
        {
            var entries = new List<string>(_logCount);
            int start = _logCount < LogCapacity ? 0 : _logNext;
            for (int i = 0; i < _logCount; i++)
            {
                int index = start + i;
                if (index >= LogCapacity)
                {
                    index -= LogCapacity;
                }

                entries.Add(_playLog[index]);
            }

            Array.Clear(_playLog, 0, _playLog.Length);
            _logCount = 0;
            _logNext = 0;
            return entries;
        }

        /// <summary>把一条请求记进活跃环（时刻在无时间源时记 0，过期判定因此永远不会生效）。</summary>
        private void RecordActive(in VfxRequest request)
        {
            ITimeProvider? time = _time;
            double startSeconds = time != null ? time.ElapsedSeconds : 0d;

            _active[_activeNext] = new ActiveEffect(request.EffectId, startSeconds, request.DurationSeconds);

            _activeNext++;
            if (_activeNext == _active.Length)
            {
                _activeNext = 0;
            }

            if (_activeCount < _active.Length)
            {
                _activeCount++;
            }

            _activeTotal++;
        }

        /// <summary>数一遍未到期的活跃记录（只扫写过的槽，见 <see cref="_activeCount"/> 注释）。</summary>
        private int CountUnexpired(double now)
        {
            int active = 0;
            for (int i = 0; i < _activeCount; i++)
            {
                ActiveEffect effect = _active[i];
                if (effect.StartSeconds + effect.DurationSeconds > now)
                {
                    active++;
                }
            }

            return active;
        }

        /// <summary>把一条事件写进环形日志；满了覆盖最旧的一条。</summary>
        private void AppendLog(string entry)
        {
            _playLog[_logNext] = entry;

            _logNext++;
            if (_logNext == LogCapacity)
            {
                _logNext = 0;
            }

            if (_logCount < LogCapacity)
            {
                _logCount++;
            }
        }

        /// <summary>
        /// 格式化成一行可读诊断文本，例如 <c>vfx#1001 @(1.5,2.25) x1.5 0.5s</c>。
        /// <b>固定不变文化</b>：否则德语/法语环境下小数点会变成逗号，CI 与日志比对会集体漂移。
        /// </summary>
        private static string FormatPlay(in VfxRequest request)
        {
            return "vfx#" + request.EffectId
                + " @(" + FormatNumber(request.X) + "," + FormatNumber(request.Y) + ")"
                + " x" + FormatNumber(request.Scale)
                + " " + FormatNumber(request.DurationSeconds) + "s";
        }

        /// <summary>数值格式化：最多两位小数、去掉多余的零（<c>1.5</c> / <c>2</c>），固定不变文化。</summary>
        private static string FormatNumber(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>活跃记录：够诊断"谁在播、什么时候起播、多久到期"即可。</summary>
        private readonly struct ActiveEffect
        {
            public ActiveEffect(int effectId, double startSeconds, float durationSeconds)
            {
                EffectId = effectId;
                StartSeconds = startSeconds;
                DurationSeconds = durationSeconds;
            }

            /// <summary>特效 ID。</summary>
            public int EffectId { get; }

            /// <summary>起播时刻（秒）；无时间源时为 0。</summary>
            public double StartSeconds { get; }

            /// <summary>请求的播放时长（秒）。</summary>
            public float DurationSeconds { get; }
        }
    }
}

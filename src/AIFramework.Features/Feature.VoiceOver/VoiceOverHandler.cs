#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.VoiceOver
{
    /// <summary>
    /// 表驱动语音/字幕机：唯一允许裁定"这一行此刻用哪条语种配音、字幕在不在窗口内、播完了没"的地方。
    /// <para>
    /// <b>只做四件事</b>：按"当前语种 → 默认语种 → 任意语种"三级选行 →
    /// 用 <c>[startMs, endMs)</c> 判定字幕可见性 → 用调用方喂进来的 <c>deltaMs</c> 推进进度 →
    /// 状态变化时广播 <see cref="VoiceLineChangedEvent"/> / <see cref="VoiceLineCompletedEvent"/>。
    /// 它不加载音频、不解析文案、不画 UI、不落存档。
    /// </para>
    /// <para>
    /// <b>为什么不调 IAudioService</b>：切片不该拥有"静音 / 战斗内不出语音 / 走哪条混音通道"这类全局策略。
    /// 切片只把"应播的 audioKey"放进快照，由适配层收到 <see cref="VoiceLineChangedEvent"/> 后查询快照再桥接
    /// <c>UnityAudioService.PlayVoice</c> / <c>ConsoleAudioService</c>；这样删掉音频不影响任何玩法逻辑。
    /// </para>
    /// <para>
    /// <b>时间策略</b>：本切片完全不读时钟（AIF003），进度只由 <see cref="Tick"/> 的 <c>deltaMs</c> 推进：
    /// 参数是 <see cref="int"/>（毫秒），NaN/Infinity 在类型层就不可达，因此只校验"非正数忽略"。
    /// </para>
    /// <para>
    /// <b>语种切换语义</b>：已在播的行沿用**播放时选中**的那一行（含其 startMs/endMs 与 audioKey），
    /// 切语言只影响下一次 <see cref="Play"/> 与非当前行的快照字段——语音不中途换轨，字幕时间轴也不跳变。
    /// </para>
    /// </summary>
    public sealed class VoiceOverHandler
        : ICommandHandler<VoiceLinePlayCommand>,
          IQueryHandler<VoiceLineQueryRequest, VoiceOverSnapshot>
    {
        /// <summary>"没有当前台词"的哨兵值：合法 lineId 恒为正，因此 0 不会与任何真实行冲突。</summary>
        public const int NoLine = 0;

        /// <summary>默认语种（0 = 简体中文）：当前语种缺行时先回退到它。</summary>
        public const int DefaultLanguage = 0;

        /// <summary>语种数值下界（含）。</summary>
        private const int MinLanguage = 0;

        /// <summary>语种数值上界（含）。</summary>
        private const int MaxLanguage = 2;

        private readonly ICqeBus _bus;
        private readonly VoiceOverTables _tables;

        /// <summary>当前语种：只影响"下一次选行"与非当前行，不影响已在播的行。</summary>
        private int _language;

        /// <summary>当前（或最后）播放的台词 id；<see cref="NoLine"/> 表示还没播过。</summary>
        private int _currentLineId = NoLine;

        /// <summary>播放时选中的行：进度、完成边界、当前行的字幕/配音口径都以它为准。</summary>
        private VoiceLineDef _currentLine;

        /// <summary>播放时选中的行是否来自"具体语种"层（false = 落到任意语种兜底层）。</summary>
        private bool _currentLineHasLanguage;

        /// <summary>相对当前行播放起点的毫秒进度；用 <see cref="long"/> 承接 <c>elapsed + int.MaxValue</c> 不溢出。</summary>
        private long _elapsedMs;

        /// <summary>是否正在播放（完成广播前置 false，因此完成事件只发一次）。</summary>
        private bool _isPlaying;

        /// <summary>加载子表并设定当前语种；构造里不注册总线、不发事件、不读时钟。</summary>
        /// <param name="bus">切片消息总线（只用于广播开始/完成事实）。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <param name="language">当前语种初值（0=简中 / 1=英语 / 2=日语）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="language"/> 不在 0..2（装配期错误，不静默兜底）。</exception>
        /// <exception cref="InvalidOperationException">语音/字幕子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">语音/字幕子表数据非法。</exception>
        public VoiceOverHandler(ICqeBus bus, IBinaryTableLoader loader, int language = DefaultLanguage)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (!IsSupportedLanguage(language))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(language),
                    language,
                    "当前语种必须在 " + MinLanguage + ".." + MaxLanguage + "。");
            }

            _bus = bus;
            _tables = new VoiceOverTables(loader);
            _language = language;
        }

        /// <summary>去重后的台词数（快照长度）；0 表示表里没有任何台词。</summary>
        public int LineCount
        {
            get { return _tables.LineCount; }
        }

        /// <summary>当前语种（下一次播放的选行起点）。</summary>
        public int CurrentLanguage
        {
            get { return _language; }
        }

        /// <summary>当前（或最后）播放的台词 id；<see cref="NoLine"/> 表示还没播过。</summary>
        public int CurrentLineId
        {
            get { return _currentLineId; }
        }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying
        {
            get { return _isPlaying; }
        }

        /// <summary>相对当前行播放起点的毫秒进度（未播放过为 0，播完为当前行 endMs）。</summary>
        public int ElapsedMs
        {
            get { return (int)_elapsedMs; }
        }

        /// <summary>注册播放命令与全量查询处理器；装配入口必须传入构造本对象时的同一个总线实例。</summary>
        /// <param name="bus">切片消息总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<VoiceLinePlayCommand>(this);
            bus.RegisterQueryHandler<VoiceLineQueryRequest, VoiceOverSnapshot>(this);
        }

        /// <summary>CQE 入口：播放台词。逐次结果请直接用 <see cref="Play"/>。</summary>
        /// <param name="command">播放命令。</param>
        public void Handle(in VoiceLinePlayCommand command)
        {
            Play(command.LineId);
        }

        /// <summary>
        /// 播放某一行：重置进度到 0、置为播放中，并广播一次 <see cref="VoiceLineChangedEvent"/>。
        /// <para>
        /// 未知 lineId（含空表）返回 <c>false</c> 且**零副作用**：不改 <see cref="CurrentLineId"/>、
        /// 不改进度、不发事件——剧情脚本配错一行不该把正在播的那一行打断。
        /// </para>
        /// <para>
        /// 已在播时再次播放同一行 = 重播（进度归零），换行 = 切行；两种都广播新的 <c>Changed</c>，
        /// 且不额外补发上一行的 <see cref="VoiceLineCompletedEvent"/>（那是"播到 endMs"的事实，不是"被打断"）。
        /// </para>
        /// </summary>
        /// <param name="lineId">要播放的台词 id。</param>
        /// <returns>是否真的进入了播放态。</returns>
        public bool Play(int lineId)
        {
            VoiceLineDef line;
            bool hasLanguage;
            if (!TryResolveLine(lineId, out line, out hasLanguage))
            {
                return false;
            }

            _currentLineId = line.LineId;
            _currentLine = line;
            _currentLineHasLanguage = hasLanguage;
            _elapsedMs = 0L;
            _isPlaying = true;

            _bus.Publish(new VoiceLineChangedEvent(line.LineId, line.Speaker, line.TextKey, line.StartMs));
            return true;
        }

        /// <summary>
        /// 推进播放进度（毫秒）。
        /// <para>
        /// 未播放时忽略；<paramref name="deltaMs"/> 非正时忽略（0 不推进、负数代表"时间倒流"，
        /// 两者都不改状态也不再广播）。<c>elapsed + delta &gt;= endMs</c> 时把进度夹到 <c>endMs</c>、
        /// 置 <see cref="IsPlaying"/> = false 并广播**一次** <see cref="VoiceLineCompletedEvent"/>：
        /// 因为播放态已结束，后续 Tick 会被"未播放"分支直接挡住，天然不会重复广播。
        /// </para>
        /// </summary>
        /// <param name="deltaMs">本拍经过的毫秒数（int：NaN/Infinity 在类型层不可达）。</param>
        public void Tick(int deltaMs)
        {
            if (!_isPlaying || deltaMs <= 0)
            {
                return;
            }

            long next = _elapsedMs + deltaMs;
            if (next >= _currentLine.EndMs)
            {
                // 先夹进度与播放态，再广播：订阅方在回调里查到的快照已是"播完那一刻"的口径。
                _elapsedMs = _currentLine.EndMs;
                _isPlaying = false;
                _bus.Publish(new VoiceLineCompletedEvent(_currentLineId));
                return;
            }

            _elapsedMs = next;
        }

        /// <summary>
        /// 切换当前语种：非法数值（不在 0..2）与"与当前相同"都静默返回 <c>false</c>，不改状态、不发事件。
        /// <para>
        /// 只影响下一次 <see cref="Play"/> 与非当前行的快照字段：已在播的行沿用播放时选中的语种行，
        /// 因此切语言不会让正在播的语音换轨、也不会让字幕时间轴跳变。
        /// </para>
        /// </summary>
        /// <param name="language">目标语种。</param>
        /// <returns>是否真的改变了当前语种。</returns>
        public bool SetLanguage(int language)
        {
            if (!IsSupportedLanguage(language) || language == _language)
            {
                return false;
            }

            _language = language;
            return true;
        }

        /// <summary>
        /// 语音/字幕全量只读快照：每条台词按当前语种三级选行（当前 → 默认 → 任意语种），
        /// 字幕可见性只对当前播放行按 <c>[startMs, endMs)</c> 实时折算。
        /// <para>
        /// 查询**只读**：不推进进度、不改播放态；空表返回空数组、<c>CurrentLineId = 0</c>，不崩也不抛。
        /// </para>
        /// </summary>
        /// <param name="query">全量查询请求（无参数）。</param>
        /// <returns>按配置首次出现顺序排列的快照。</returns>
        public VoiceOverSnapshot Handle(in VoiceLineQueryRequest query)
        {
            return Snapshot();
        }

        /// <summary>与 CQE 查询同源的同步入口（供 Presenter 直接调用，省一次总线往返）。</summary>
        /// <returns>当前语种下的全量快照。</returns>
        public VoiceOverSnapshot Snapshot()
        {
            int lineCount = _tables.LineCount;
            VoiceLineState[] lines = new VoiceLineState[lineCount];

            for (int i = 0; i < lineCount; i++)
            {
                int lineId = _tables.GetLineId(i);

                VoiceLineDef line;
                bool hasLanguage;
                if (lineId == _currentLineId && _currentLineId != NoLine)
                {
                    // 当前行用"播放时选中的行"：语种中途切换也不改已播语音的资源与时间轴。
                    line = _currentLine;
                    hasLanguage = _currentLineHasLanguage;
                }
                else if (!TryResolveLine(lineId, out line, out hasLanguage))
                {
                    // 不可达：lineId 来自表本身，三级选行至少能命中"任意语种"层。
                    continue;
                }

                bool hasAudio = hasLanguage && line.AudioKey.Length > 0;
                bool hasSubtitle = hasLanguage
                    && lineId == _currentLineId
                    && _elapsedMs >= line.StartMs
                    && _elapsedMs < line.EndMs;

                lines[i] = new VoiceLineState(
                    line.LineId,
                    line.Speaker,
                    line.TextKey,
                    line.AudioKey,
                    line.Language,
                    hasSubtitle,
                    hasAudio);
            }

            return new VoiceOverSnapshot(lines, _currentLineId, _isPlaying, (int)_elapsedMs);
        }

        /// <summary>
        /// 三级选行：当前语种行 → 默认语种（0）行 → 该 lineId 的任意语种行。
        /// <para>
        /// 前两级 <paramref name="hasLanguage"/> = true（可以正常播字幕/配音）；
        /// 第三级只取元数据、<paramref name="hasLanguage"/> = false，调用方必须把字幕与配音都置 false。
        /// </para>
        /// </summary>
        private bool TryResolveLine(int lineId, out VoiceLineDef line, out bool hasLanguage)
        {
            if (_tables.TryFindRow(lineId, _language, out line))
            {
                hasLanguage = true;
                return true;
            }

            if (_language != DefaultLanguage && _tables.TryFindRow(lineId, DefaultLanguage, out line))
            {
                hasLanguage = true;
                return true;
            }

            if (_tables.TryFindAnyRow(lineId, out line))
            {
                hasLanguage = false;
                return true;
            }

            hasLanguage = false;
            return false;
        }

        /// <summary>语种数值是否已定义（0..2）：未定义值一律不参与选行。</summary>
        private static bool IsSupportedLanguage(int language)
        {
            return language >= MinLanguage && language <= MaxLanguage;
        }
    }
}

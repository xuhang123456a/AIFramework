#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.VoiceOver
{
    /// <summary>
    /// 语音行：纯数据，对应 <c>TbVoiceLine</c> 的一行。
    /// <para>
    /// 一个 <see cref="LineId"/> 在表里按语言拆成多行（语音资源天然分语种），
    /// <see cref="StartMs"/> / <see cref="EndMs"/> 是**相对该行播放起点**的毫秒区间，左闭右开。
    /// </para>
    /// <para>
    /// <see cref="AudioKey"/> 允许为空：空串表示"这一行只有字幕、没有配音"（<c>hasAudio = false</c>），
    /// 是合法配置而不是缺配置——缺配置的判定在表解析阶段完成。
    /// </para>
    /// </summary>
    public readonly struct VoiceLineDef
    {
        public VoiceLineDef(
            int lineId,
            string speaker,
            string textKey,
            string audioKey,
            int language,
            int startMs,
            int endMs)
        {
            LineId = lineId;
            Speaker = speaker;
            TextKey = textKey;
            AudioKey = audioKey;
            Language = language;
            StartMs = startMs;
            EndMs = endMs;
        }

        /// <summary>台词 id（配置主键的一半，恒为正；与 <see cref="Language"/> 共同唯一）。</summary>
        public int LineId { get; }

        /// <summary>说话人显示名（非空；允许按语种本地化，因此逐行配置）。</summary>
        public string Speaker { get; }

        /// <summary>字幕文案键（非空，形如 <c>vo.hero.greet</c>）：本切片只透出，不解析文案。</summary>
        public string TextKey { get; }

        /// <summary>该语种的配音资源键；空串表示只有字幕没有配音。</summary>
        public string AudioKey { get; }

        /// <summary>该行语种（0=简中 / 1=英语 / 2=日语）。</summary>
        public int Language { get; }

        /// <summary>字幕出现时刻（毫秒，相对播放起点，左闭）。</summary>
        public int StartMs { get; }

        /// <summary>字幕消失时刻（毫秒，相对播放起点，右开）；恒大于 <see cref="StartMs"/>。</summary>
        public int EndMs { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbVoiceLine</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成）：
    /// <c>[int32 rowCount]</c> + rowCount ×
    /// { <c>int32 lineId</c>; <c>[int32 len]</c> + UTF-8 speaker;
    /// <c>[int32 len]</c> + UTF-8 textKey; <c>[int32 len]</c> + UTF-8 audioKey;
    /// <c>int32 language</c>; <c>int32 startMs</c>; <c>int32 endMs</c> }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——"语音表没打进包"必须当场暴露；
    /// 而 0 行是合法配置（<see cref="Count"/> == 0）：切片此时没有可播台词。
    /// 行顺序即配置顺序（不排序），<see cref="GetLineId"/> 的去重顺序按每行首次出现。
    /// </para>
    /// </summary>
    public sealed class VoiceOverTables
    {
        /// <summary>子表名（与 <c>tables/TbVoiceLine.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbVoiceLine";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>一行最少可读的字节数：4 个 int32 字段 + 3 个字符串长度前缀（共 7 个 int32）。</summary>
        private const int MinRowBytes = Int32Bytes * 7;

        /// <summary>语种数值下界（含）：0 = 简体中文。</summary>
        private const int MinLanguage = 0;

        /// <summary>语种数值上界（含）：2 = 日语。</summary>
        private const int MaxLanguage = 2;

        /// <summary>行数据，顺序 = 配置表顺序。</summary>
        private readonly VoiceLineDef[] _rows;

        /// <summary>去重后的 lineId，顺序 = 首次出现顺序（快照按它遍历）。</summary>
        private readonly int[] _lineIds;

        /// <summary>(lineId, language) → 行下标；表解析保证不重复。</summary>
        private readonly Dictionary<long, int> _rowIndex;

        /// <summary>lineId → 该 lineId 首次出现的行下标（"任意语言"兜底层用）。</summary>
        private readonly Dictionary<int, int> _firstRowIndex;

        /// <summary>加载并解析 <c>TbVoiceLine</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、行数非法、lineId 非正、空 speaker/textKey、语种越界、时间倒置、同语种重复。</exception>
        public VoiceOverTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("语音/字幕子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            ParsedTable parsed = Parse(payload);
            _rows = parsed.Rows;
            _lineIds = parsed.LineIds;
            _rowIndex = parsed.RowIndex;
            _firstRowIndex = parsed.FirstRowIndex;
        }

        /// <summary>子表行数（= 配置里"语种 × 台词"的条目数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>去重后的台词行数（一个 lineId 多语种只算一条）；快照长度即此值。</summary>
        public int LineCount
        {
            get { return _lineIds.Length; }
        }

        /// <summary>按配置顺序取一行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        /// <param name="index">0 起行下标。</param>
        public VoiceLineDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "语音/字幕子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        /// <summary>按首次出现顺序取第 <paramref name="index"/> 个 lineId；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        /// <param name="index">0 起台词下标。</param>
        public int GetLineId(int index)
        {
            if (index < 0 || index >= _lineIds.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "语音/字幕台词下标越界，当前台词数 " + _lineIds.Length + "。");
            }

            return _lineIds[index];
        }

        /// <summary>精确定位某台词某语种的行；缺失返回 <c>false</c>（回退链由切片 Handler 决定）。</summary>
        /// <param name="lineId">台词 id。</param>
        /// <param name="language">语种数值；越界视为未命中。</param>
        /// <param name="row">命中的行。</param>
        public bool TryFindRow(int lineId, int language, out VoiceLineDef row)
        {
            if (language < MinLanguage || language > MaxLanguage)
            {
                row = default;
                return false;
            }

            long key = ((long)lineId << 3) | (uint)language;
            int index;
            if (!_rowIndex.TryGetValue(key, out index))
            {
                row = default;
                return false;
            }

            row = _rows[index];
            return true;
        }

        /// <summary>
        /// 取该台词在表里首次出现的行（任意语种）：只在"当前语言与默认语言都缺"时用来拿元数据，
        /// 调用方必须把 <c>hasSubtitle</c> / <c>hasAudio</c> 强制置 false（禁止串语种播配音）。
        /// </summary>
        /// <param name="lineId">台词 id。</param>
        /// <param name="row">命中的行。</param>
        public bool TryFindAnyRow(int lineId, out VoiceLineDef row)
        {
            int index;
            if (!_firstRowIndex.TryGetValue(lineId, out index))
            {
                row = default;
                return false;
            }

            row = _rows[index];
            return true;
        }

        /// <summary>全量解析：先做"声明行数 × 最小行宽 vs 剩余字节"的前置校验，再逐行读字段并校验。</summary>
        private static ParsedTable Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("语音/字幕子表头部长度不足 " + HeaderBytes + " 字节，实际 " + payload.Length + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);
            int rowCount = reader.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new InvalidDataException("语音/字幕子表 rowCount 为负：" + rowCount + "。");
            }

            // 畸形头部（声明海量行）必须先挡住，否则会先按 rowCount 分配出一个巨大数组。
            if ((long)rowCount * MinRowBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "语音/字幕子表声明 " + rowCount + " 行，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            VoiceLineDef[] rows = new VoiceLineDef[rowCount];
            var lineIds = new List<int>(rowCount);
            var rowIndex = new Dictionary<long, int>(rowCount);
            var firstRowIndex = new Dictionary<int, int>(rowCount);

            for (int i = 0; i < rowCount; i++)
            {
                int lineId = reader.ReadInt32("lineId");
                string speaker = reader.ReadUtf8("speaker");
                string textKey = reader.ReadUtf8("textKey");
                string audioKey = reader.ReadUtf8("audioKey");
                int language = reader.ReadInt32("language");
                int startMs = reader.ReadInt32("startMs");
                int endMs = reader.ReadInt32("endMs");

                if (lineId <= 0)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表 lineId 必须为正，第 " + i + " 行实际为 " + lineId + "。");
                }

                if (speaker.Length == 0)
                {
                    throw new InvalidDataException("语音/字幕子表 speaker 不能为空，lineId " + lineId + "。");
                }

                if (textKey.Length == 0)
                {
                    throw new InvalidDataException("语音/字幕子表 textKey 不能为空，lineId " + lineId + "。");
                }

                if (language < MinLanguage || language > MaxLanguage)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表 language 必须在 " + MinLanguage + ".." + MaxLanguage
                        + "，lineId " + lineId + " 实际为 " + language + "。");
                }

                if (startMs < 0)
                {
                    // 进度从 0 起算，负的起点永远等不到：这是配置错误，不是"延迟播放"。
                    throw new InvalidDataException(
                        "语音/字幕子表 startMs 不能为负，lineId " + lineId + " 语种 " + language
                        + " 实际为 " + startMs + "。");
                }

                if (endMs <= startMs)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表字幕区间必须满足 endMs > startMs，lineId " + lineId + " 语种 " + language
                        + " 实际为 [" + startMs + ", " + endMs + ")。");
                }

                long key = ((long)lineId << 3) | (uint)language;
                if (rowIndex.ContainsKey(key))
                {
                    throw new InvalidDataException(
                        "语音/字幕子表 lineId " + lineId + " 的语种 " + language + " 重复配置。");
                }

                rowIndex.Add(key, i);
                if (!firstRowIndex.ContainsKey(lineId))
                {
                    firstRowIndex.Add(lineId, i);
                    lineIds.Add(lineId);
                }

                rows[i] = new VoiceLineDef(lineId, speaker, textKey, audioKey, language, startMs, endMs);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "语音/字幕子表尾部残留 " + reader.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            return new ParsedTable(rows, lineIds.ToArray(), rowIndex, firstRowIndex);
        }

        /// <summary>解析产物：构造期一次性建好全部索引，之后整表只读。</summary>
        private sealed class ParsedTable
        {
            internal ParsedTable(
                VoiceLineDef[] rows,
                int[] lineIds,
                Dictionary<long, int> rowIndex,
                Dictionary<int, int> firstRowIndex)
            {
                Rows = rows;
                LineIds = lineIds;
                RowIndex = rowIndex;
                FirstRowIndex = firstRowIndex;
            }

            internal VoiceLineDef[] Rows { get; }

            internal int[] LineIds { get; }

            internal Dictionary<long, int> RowIndex { get; }

            internal Dictionary<int, int> FirstRowIndex { get; }
        }

        /// <summary>
        /// 小端字节流游标：每次读取都做越界检查，越界统一转 <see cref="InvalidDataException"/>。
        /// 只在构造期使用（允许分配），因此不做零分配优化。
        /// </summary>
        private sealed class PayloadReader
        {
            private readonly byte[] _payload;
            private int _offset;

            internal PayloadReader(byte[] payload)
            {
                _payload = payload;
                _offset = 0;
            }

            /// <summary>尚未读取的字节数。</summary>
            internal int Remaining
            {
                get { return _payload.Length - _offset; }
            }

            /// <summary>读一个小端 int32；不足 4 字节即判定截断。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal int ReadInt32(string fieldName)
            {
                if (Remaining < Int32Bytes)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset
                        + "，剩余 " + Remaining + " 字节。");
                }

                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += Int32Bytes;
                return value;
            }

            /// <summary>读"长度前缀 + UTF-8 字节"的 string；负长度或超出剩余字节即判定非法。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal string ReadUtf8(string fieldName)
            {
                int length = ReadInt32(fieldName + "Len");
                if (length < 0)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "语音/字幕子表读取 " + fieldName + " 时字节流已截断：声明 " + length
                        + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}

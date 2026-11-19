#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Localization
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbLocalization</c>）。
    /// <para>
    /// 布局（小端）：<c>[int32 keyCount]</c> + keyCount × { <c>[int32 keyLen]</c> + UTF-8 key
    /// + <c>[int32 langCount]</c> + langCount × { <c>[int32 lang]</c> + <c>[int32 textLen]</c> + UTF-8 text } }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// 解析在构造期一次完成，之后整张表只读：查询路径只做两次字典查找，不建临时集合、不拼字符串。
    /// </para>
    /// <para>
    /// 同 key 允许拆成多条（语言不重叠时合并），因此 <see cref="Count"/>（条目数）与
    /// <see cref="KeyCount"/>（去重后的 key 数）不保证相等。
    /// </para>
    /// </summary>
    public sealed class LocalizationTables
    {
        private const string TableNameValue = "TbLocalization";

        /// <summary>头部：<c>[int32 keyCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>
        /// 一个 key 条目的最小<b>可读</b>字节数：keyLen(4) + langCount(4)。
        /// key 允许为空、语言条目另行校验，这里只用它挡住"声明数远超字节流"的巨额分配。
        /// </summary>
        private const int MinKeyEntryBytes = 8;

        /// <summary>一个语言条目的最小字节数：lang(4) + textLen(4)，文本可为空。</summary>
        private const int MinLanguageEntryBytes = 8;

        private readonly Dictionary<string, LocalizationRow> _rows;
        private readonly int _entryCount;

        /// <summary>加载并解析本切片子表；表缺失或任一校验失败即抛异常。</summary>
        /// <param name="loader">配置表加载端口，只按固定表名取字节。</param>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">表不存在（loader 返回 <c>null</c>）。</exception>
        /// <exception cref="InvalidDataException">字节布局不合法（截断、负长度、重复语言、未知语言、尾部残留）。</exception>
        public LocalizationTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("本地化子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            Dictionary<string, LocalizationRow> rows;
            _entryCount = Parse(payload, out rows);
            _rows = rows;
        }

        /// <summary>表内条目数（等于头部 <c>keyCount</c>，同 key 多条目分别计数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _entryCount; }
        }

        /// <summary>去重后的 key 数；无重复 key 时等于 <see cref="Count"/>。</summary>
        public int KeyCount
        {
            get { return _rows.Count; }
        }

        /// <summary>按 key + 语言取文案；缺该语言或 key 返回 null。</summary>
        /// <param name="key">文案键；<c>null</c> 视为未命中。</param>
        /// <param name="language">目标语言；未定义的语言数值视为未命中。</param>
        /// <returns>命中文案或 <c>null</c>（本方法只报"有没有"，回退链由切片 Handler 决定）。</returns>
        public string? Find(string key, GameLanguage language)
        {
            if (key == null)
            {
                return null;
            }

            LocalizationRow row;
            if (!_rows.TryGetValue(key, out row))
            {
                return null;
            }

            return row.Find(language);
        }

        /// <summary>
        /// 全量解析：先做"声明数量 vs 剩余字节"的前置校验，避免畸形头部的巨额计数触发超大分配。
        /// </summary>
        /// <param name="payload">子表原始字节。</param>
        /// <param name="rows">解析结果（按 key 归并）。</param>
        /// <returns>条目数（头部 <c>keyCount</c>）。</returns>
        private static int Parse(byte[] payload, out Dictionary<string, LocalizationRow> rows)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException(
                    "本地化子表头部长度不足 " + HeaderBytes + " 字节，实际 " + payload.Length + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);

            int keyCount = reader.ReadInt32("keyCount");
            if (keyCount < 0)
            {
                throw new InvalidDataException("本地化子表的 keyCount 为负：" + keyCount + "。");
            }

            // 每个 key 条目至少有 keyLen + langCount 两个 int32：声明数超过剩余字节必然是截断，先拒绝再分配。
            if ((long)keyCount * MinKeyEntryBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "本地化子表声明 " + keyCount + " 个 key，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            rows = new Dictionary<string, LocalizationRow>(keyCount, StringComparer.Ordinal);
            int entryCount = 0;

            for (int i = 0; i < keyCount; i++)
            {
                int keyLength = reader.ReadInt32("keyLen");
                string key = reader.ReadUtf8(keyLength, "key");

                int languageCount = reader.ReadInt32("langCount");
                if (languageCount <= 0)
                {
                    // 0 与负数都是非法：同一 key 至少要声明 1 种语言（"只留占位不写文案"不允许）。
                    throw new InvalidDataException(
                        "本地化子表 key '" + key + "' 的 langCount 必须 ≥ 1，实际 " + languageCount + "。");
                }

                if ((long)languageCount * MinLanguageEntryBytes > reader.Remaining)
                {
                    throw new InvalidDataException(
                        "本地化子表 key '" + key + "' 声明 " + languageCount + " 种语言，但只剩 "
                        + reader.Remaining + " 字节，字节流被截断。");
                }

                LocalizationRow row;
                if (!rows.TryGetValue(key, out row))
                {
                    row = new LocalizationRow(languageCount);
                    rows.Add(key, row);
                }

                for (int j = 0; j < languageCount; j++)
                {
                    int rawLanguage = reader.ReadInt32("lang");
                    GameLanguage language = (GameLanguage)rawLanguage;
                    if (!LocalizationLanguage.IsSupported(language))
                    {
                        throw new InvalidDataException(
                            "本地化子表 key '" + key + "' 出现未知语言数值 " + rawLanguage + "。");
                    }

                    int textLength = reader.ReadInt32("textLen");
                    string text = reader.ReadUtf8(textLength, "text");

                    if (!row.TryAdd(language, text))
                    {
                        throw new InvalidDataException(
                            "本地化子表 key '" + key + "' 的语言重复：" + rawLanguage + "。");
                    }
                }

                entryCount++;
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "本地化子表尾部残留 " + reader.Remaining + " 字节，声明条目与字节流长度不符。");
            }

            return entryCount;
        }

        /// <summary>单个 key 的文案集合：语言 → 文本，构造期写入后只读。</summary>
        private sealed class LocalizationRow
        {
            private readonly Dictionary<GameLanguage, string> _texts;

            internal LocalizationRow(int expectedLanguageCount)
            {
                _texts = new Dictionary<GameLanguage, string>(expectedLanguageCount);
            }

            /// <summary>写入一条语言文案；同一语言重复返回 <c>false</c>（由调用方转成异常）。</summary>
            internal bool TryAdd(GameLanguage language, string text)
            {
                if (_texts.ContainsKey(language))
                {
                    return false;
                }

                _texts.Add(language, text);
                return true;
            }

            /// <summary>取该语言文案；缺该语言返回 <c>null</c>。</summary>
            internal string? Find(GameLanguage language)
            {
                string text;
                return _texts.TryGetValue(language, out text) ? text : null;
            }
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
                        "本地化子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset
                        + "，剩余 " + Remaining + " 字节。");
                }

                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += Int32Bytes;
                return value;
            }

            /// <summary>按声明长度读一段 UTF-8；负长度或超出剩余字节即判定非法。</summary>
            /// <param name="length">声明长度（字节）。</param>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal string ReadUtf8(int length, string fieldName)
            {
                if (length < 0)
                {
                    throw new InvalidDataException(
                        "本地化子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "本地化子表读取 " + fieldName + " 时字节流已截断：声明 " + length
                        + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}

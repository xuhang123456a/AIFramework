#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Features.RichText
{
    /// <summary>
    /// 邮件/公告富文本的**唯一解析入口**：只认白名单标记，其余一律剥离成纯文本。
    /// <para>
    /// <b>它只做一件事</b>：把外部来的富文本切成"可直接渲染的片段 + 被剥离/修正清单"。
    /// 它不查本地化表、不碰 UI、不判业务、不读时钟、不引用任何引擎类型。
    /// </para>
    /// <para>
    /// <b>安全口径</b>：不支持的标签（<c>&lt;script&gt;</c>、<c>&lt;a href&gt;</c>、<c>&lt;img&gt;</c> …）
    /// 不是"转义成文本"而是**整段剥离**——公告正文里出现脚本标签只可能是配置事故或注入，
    /// 把它显示给玩家比丢掉更糟；剥离的事实通过 <see cref="RichTextDiagnostic"/> 上报。
    /// </para>
    /// <para>
    /// <b>为什么不抛异常</b>：邮件正文是运营数据，一条脏数据不该让打开邮箱的流程崩掉；
    /// 所有异常输入都降级成"可读文本 + 诊断"，由 QA / 运营后台按诊断清单修数据。
    /// </para>
    /// </summary>
    public static class RichTextParser
    {
        /// <summary>嵌套深度上限（含）：第 9 层开标签整段剥离，防止构造深层嵌套拖垮 UI。</summary>
        public const int MaxDepth = 8;

        /// <summary>输入长度上限（UTF-16 code unit）：超出部分不解析，按截断处理。</summary>
        public const int MaxInputLength = 4096;

        /// <summary>字号下界（含）。</summary>
        public const int MinFontSize = 10;

        /// <summary>字号上界（含）。</summary>
        public const int MaxFontSize = 200;

        private const string HexDigits = "0123456789ABCDEF";

        private const string ReasonUnknownTag = "标签不在白名单内（仅支持 b/i/u/color/size），已剥离";
        private const string ReasonUnknownCloseTag = "闭合标签不在白名单内（仅支持 b/i/u/color/size），已剥离";
        private const string ReasonOrphanClose = "孤立闭合标签：没有对应的开标签，已剥离";
        private const string ReasonMismatchClose = "孤立闭合标签：与最近的开标签不匹配，已剥离";
        private const string ReasonCloseWithAttribute = "闭合标签不支持属性，已剥离";
        private const string ReasonDepthLimit = "嵌套深度超过 8 层，已剥离";
        private const string ReasonUnclosed = "标签未闭合，已在文本末尾自动闭合";
        private const string ReasonTruncated = "文本超过 4096 字符上限，超出部分已截断";

        /// <summary>
        /// 解析一段富文本。
        /// <para>
        /// <c>null</c> 与空串都返回 <see cref="RichTextDocument.Empty"/>（0 片段、0 诊断），不抛异常；
        /// 超过 <see cref="MaxInputLength"/> 的部分不解析并记 1 条修正类诊断。
        /// </para>
        /// </summary>
        /// <param name="text">外部来的富文本（允许 <c>null</c>）。</param>
        /// <returns>净化后的文档。</returns>
        public static RichTextDocument Parse(string? text)
        {
            if (text == null || text.Length == 0)
            {
                return RichTextDocument.Empty;
            }

            int limit = text.Length <= MaxInputLength ? text.Length : MaxInputLength;
            bool truncated = text.Length > MaxInputLength;

            var spans = new List<RichTextSpan>();
            var diagnostics = new List<RichTextDiagnostic>();
            var stack = new List<OpenTag>();

            int index = 0;
            while (index < limit)
            {
                if (text[index] != '<')
                {
                    // 普通文本整段吞掉，减少 Substring 次数；遇到下一个 '<' 再停。
                    int runStart = index;
                    while (index < limit && text[index] != '<')
                    {
                        index++;
                    }

                    AppendText(spans, CurrentTag(stack), text.Substring(runStart, index - runStart), runStart);
                    continue;
                }

                TagToken token;
                if (!TryReadTag(text, index, limit, out token))
                {
                    // 规则 6：'<' 不构成标记时按普通字符输出，绝不吞字符。
                    AppendText(spans, CurrentTag(stack), "<", index);
                    index++;
                    continue;
                }

                if (token.Kind == TagKind.Unknown)
                {
                    diagnostics.Add(new RichTextDiagnostic(
                        index,
                        token.Raw,
                        token.IsClose ? ReasonUnknownCloseTag : ReasonUnknownTag));
                    index += token.Raw.Length;
                    continue;
                }

                if (token.Kind == TagKind.Invalid)
                {
                    diagnostics.Add(new RichTextDiagnostic(index, token.Raw, token.Reason));
                    index += token.Raw.Length;
                    continue;
                }

                if (token.Kind == TagKind.SelfClosing)
                {
                    // 白名单标签的自闭合（如 <b/>）= 开 + 立刻关，不产生片段、不改样式、不记诊断。
                    index += token.Raw.Length;
                    continue;
                }

                if (token.Kind == TagKind.Open)
                {
                    if (stack.Count >= MaxDepth)
                    {
                        diagnostics.Add(new RichTextDiagnostic(index, token.Raw, ReasonDepthLimit));
                        index += token.Raw.Length;
                        continue;
                    }

                    stack.Add(new OpenTag(token.Name, token.Style, index, token.Raw, token.ColorRgb, token.FontSize));
                    index += token.Raw.Length;
                    continue;
                }

                // TagKind.Close：只认栈顶同名；空栈或错配都按孤立闭合剥离（规则 5）。
                if (stack.Count == 0)
                {
                    diagnostics.Add(new RichTextDiagnostic(index, token.Raw, ReasonOrphanClose));
                    index += token.Raw.Length;
                    continue;
                }

                // 标签名大小写不敏感：<B>x</b> 必须能正常闭合。
                if (!string.Equals(stack[stack.Count - 1].Name, token.Name, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new RichTextDiagnostic(index, token.Raw, ReasonMismatchClose));
                    index += token.Raw.Length;
                    continue;
                }

                stack.RemoveAt(stack.Count - 1);
                index += token.Raw.Length;
            }

            // 规则 4：未闭合标签到文本末尾自动闭合，文本一个字符都不能丢；按从内到外补诊断。
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                OpenTag unclosed = stack[i];
                diagnostics.Add(new RichTextDiagnostic(
                    unclosed.Offset,
                    unclosed.Raw,
                    ReasonUnclosed,
                    RichTextDiagnosticKind.Warning));
            }

            if (truncated)
            {
                diagnostics.Add(new RichTextDiagnostic(
                    MaxInputLength,
                    string.Empty,
                    ReasonTruncated,
                    RichTextDiagnosticKind.Warning));
            }

            return new RichTextDocument(spans.ToArray(), diagnostics.ToArray());
        }

        /// <summary>当前生效样式：栈顶（最内层）标签；空栈即 Normal。</summary>
        private static OpenTag CurrentTag(List<OpenTag> stack)
        {
            if (stack.Count == 0)
            {
                return OpenTag.None;
            }

            return stack[stack.Count - 1];
        }

        /// <summary>
        /// 追加一段文本：相邻且(样式 + 颜色 + 字号)完全相同的片段合并，偏移保留第一段的位置。
        /// 空文本直接忽略——渲染层不该拿到零长度片段。
        /// </summary>
        private static void AppendText(List<RichTextSpan> spans, OpenTag tag, string text, int offset)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (spans.Count > 0)
            {
                RichTextSpan last = spans[spans.Count - 1];
                if (last.Style == tag.Style && last.ColorRgb == tag.ColorRgb && last.FontSize == tag.FontSize)
                {
                    spans[spans.Count - 1] = new RichTextSpan(
                        last.Style,
                        last.Text + text,
                        last.SourceOffset,
                        last.ColorRgb,
                        last.FontSize);
                    return;
                }
            }

            spans.Add(new RichTextSpan(tag.Style, text, offset, tag.ColorRgb, tag.FontSize));
        }

        /// <summary>
        /// 尝试把 <paramref name="start"/> 处的 '&lt;' 读成一个标记。
        /// 返回 <c>false</c> 表示"这根本不是标记"（没有 '&gt;'、或 '&lt;' 后不是字母），
        /// 调用方必须把它当普通字符输出。标记一律以**第一个 '&gt;'** 结束（不解析引号）。
        /// </summary>
        private static bool TryReadTag(string text, int start, int limit, out TagToken token)
        {
            token = default;

            int end = -1;
            for (int i = start + 1; i < limit; i++)
            {
                if (text[i] == '>')
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                // 到截断边界/文本末尾都没有 '>'：不是标记。
                return false;
            }

            int cursor = start + 1;
            bool isClose = false;
            if (cursor < end && text[cursor] == '/')
            {
                isClose = true;
                cursor++;
            }

            if (cursor >= end || !IsAsciiLetter(text[cursor]))
            {
                // "<3"、"</>"、"<<b>" 等：不是标记，'<' 按普通字符处理。
                return false;
            }

            int nameStart = cursor;
            while (cursor < end && (IsAsciiLetter(text[cursor]) || IsAsciiDigit(text[cursor])))
            {
                cursor++;
            }

            string name = text.Substring(nameStart, cursor - nameStart);
            string raw = text.Substring(start, end - start + 1);
            string rest = text.Substring(cursor, end - cursor).Trim();

            if (isClose)
            {
                if (rest.Length > 0)
                {
                    token = TagToken.Invalid(raw, name, true, ReasonCloseWithAttribute);
                    return true;
                }

                if (!IsWhitelistedName(name))
                {
                    token = TagToken.Unknown(raw, name, true);
                    return true;
                }

                token = TagToken.Close(raw, name);
                return true;
            }

            bool selfClosing = false;
            if (rest.EndsWith("/", StringComparison.Ordinal))
            {
                selfClosing = true;
                rest = rest.Substring(0, rest.Length - 1).Trim();
            }

            RichTextStyle style;
            if (IsName(name, "b"))
            {
                style = RichTextStyle.Bold;
            }
            else if (IsName(name, "i"))
            {
                style = RichTextStyle.Italic;
            }
            else if (IsName(name, "u"))
            {
                style = RichTextStyle.Underline;
            }
            else if (IsName(name, "color"))
            {
                return TryReadColor(raw, name, rest, selfClosing, out token);
            }
            else if (IsName(name, "size"))
            {
                return TryReadSize(raw, name, rest, selfClosing, out token);
            }
            else
            {
                token = TagToken.Unknown(raw, name, false);
                return true;
            }

            // b / i / u 不接受任何属性：<b foo> 属于"白名单标签的错误形态"，剥离并记剥离类诊断。
            if (rest.Length > 0)
            {
                token = TagToken.Invalid(raw, name, false, "标签 <" + name + "> 不支持属性，已剥离");
                return true;
            }

            token = selfClosing
                ? TagToken.SelfClosingTag(raw, name, style)
                : TagToken.OpenTag(raw, name, style);
            return true;
        }

        /// <summary>读 <c>color=#RRGGBB</c>：只认 6 位十六进制，短写 / 带 alpha / 颜色名一律剥离。</summary>
        private static bool TryReadColor(string raw, string name, string rest, bool selfClosing, out TagToken token)
        {
            string value;
            if (!TryReadAttributeValue(rest, out value))
            {
                token = TagToken.Invalid(raw, name, false, "color 需要 color=#RRGGBB 形式，已剥离");
                return true;
            }

            int rgb;
            if (!TryParseRgb(value, out rgb))
            {
                token = TagToken.Invalid(raw, name, false, "color 只接受 #RRGGBB（6 位十六进制），已剥离");
                return true;
            }

            token = selfClosing
                ? TagToken.SelfClosingTag(raw, name, RichTextStyle.Color, rgb, 0)
                : TagToken.OpenTag(raw, name, RichTextStyle.Color, rgb, 0);
            return true;
        }

        /// <summary>读 <c>size=NN</c>：只认 10..200 的十进制整数，越界 / 非数字一律剥离。</summary>
        private static bool TryReadSize(string raw, string name, string rest, bool selfClosing, out TagToken token)
        {
            string value;
            if (!TryReadAttributeValue(rest, out value))
            {
                token = TagToken.Invalid(raw, name, false, "size 需要 size=NN 形式，已剥离");
                return true;
            }

            int size;
            string reason;
            if (!TryParseFontSize(value, out size, out reason))
            {
                token = TagToken.Invalid(raw, name, false, reason + "，已剥离");
                return true;
            }

            token = selfClosing
                ? TagToken.SelfClosingTag(raw, name, RichTextStyle.Size, 0, size)
                : TagToken.OpenTag(raw, name, RichTextStyle.Size, 0, size);
            return true;
        }

        /// <summary>取 <c>=值</c> 里的值（两侧空白容忍）；没有 '=' 或值为空都算非法。</summary>
        private static bool TryReadAttributeValue(string rest, out string value)
        {
            value = string.Empty;
            if (rest.Length == 0 || rest[0] != '=')
            {
                return false;
            }

            value = rest.Substring(1).Trim();
            return value.Length > 0;
        }

        /// <summary>严格解析 <c>#RRGGBB</c>（大小写均可，长度必须正好 7）。</summary>
        private static bool TryParseRgb(string value, out int rgb)
        {
            rgb = 0;
            if (value.Length != 7 || value[0] != '#')
            {
                return false;
            }

            int parsed = 0;
            for (int i = 1; i < 7; i++)
            {
                int digit = ToHexDigit(value[i]);
                if (digit < 0)
                {
                    return false;
                }

                parsed = (parsed << 4) | digit;
            }

            rgb = parsed;
            return true;
        }

        /// <summary>严格解析 10..200 的十进制整数：只允许数字字符，且不做任何符号/区域设置解释。</summary>
        private static bool TryParseFontSize(string value, out int size, out string reason)
        {
            size = 0;
            reason = string.Empty;

            long parsed = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '0' || c > '9')
                {
                    reason = "size 只接受十进制整数";
                    return false;
                }

                if (parsed <= MaxFontSize)
                {
                    // 超过上界后不再累加，避免长数字串把 long 撑爆；反正结果一定是越界。
                    parsed = (parsed * 10) + (c - '0');
                }
            }

            if (parsed < MinFontSize || parsed > MaxFontSize)
            {
                reason = "size 超出 10..200 范围";
                return false;
            }

            size = (int)parsed;
            return true;
        }

        private static bool IsWhitelistedName(string name)
        {
            return IsName(name, "b")
                || IsName(name, "i")
                || IsName(name, "u")
                || IsName(name, "color")
                || IsName(name, "size");
        }

        private static bool IsName(string name, string expected)
        {
            // 标签名大小写不敏感（TMP 同样容忍），序列化时统一收敛成小写。
            return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsAsciiDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static int ToHexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return (c - 'a') + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return (c - 'A') + 10;
            }

            return -1;
        }

        /// <summary>一处已打开的白名单标签（栈帧）：样式负载 + 原文 + 起始偏移（自动闭合诊断要用）。</summary>
        private readonly struct OpenTag
        {
            internal OpenTag(string name, RichTextStyle style, int offset, string raw, int colorRgb, int fontSize)
            {
                Name = name;
                Style = style;
                Offset = offset;
                Raw = raw;
                ColorRgb = colorRgb;
                FontSize = fontSize;
            }

            /// <summary>空栈时的"当前样式"：Normal。</summary>
            internal static OpenTag None
            {
                get { return new OpenTag(string.Empty, RichTextStyle.Normal, 0, string.Empty, 0, 0); }
            }

            internal string Name { get; }

            internal RichTextStyle Style { get; }

            internal int Offset { get; }

            internal string Raw { get; }

            internal int ColorRgb { get; }

            internal int FontSize { get; }
        }

        /// <summary>标记种类：结构（Open/Close/SelfClosing）与判定（Unknown/Invalid）分开。</summary>
        private enum TagKind : byte
        {
            Open = 0,
            Close = 1,
            SelfClosing = 2,
            Unknown = 3,
            Invalid = 4,
        }

        /// <summary>一次词法读取的结果；<see cref="End"/> 是 '&gt;' 之后的下一下标。</summary>
        private readonly struct TagToken
        {
            private TagToken(
                TagKind kind,
                string raw,
                string name,
                bool isClose,
                RichTextStyle style,
                int colorRgb,
                int fontSize,
                string reason)
            {
                Kind = kind;
                Raw = raw;
                Name = name;
                IsClose = isClose;
                Style = style;
                ColorRgb = colorRgb;
                FontSize = fontSize;
                Reason = reason;
            }

            internal TagKind Kind { get; }

            internal string Raw { get; }

            internal string Name { get; }

            internal bool IsClose { get; }

            internal RichTextStyle Style { get; }

            internal int ColorRgb { get; }

            internal int FontSize { get; }

            internal string Reason { get; }

            internal static TagToken OpenTag(string raw, string name, RichTextStyle style)
            {
                return new TagToken(TagKind.Open, raw, name, false, style, 0, 0, string.Empty);
            }

            internal static TagToken OpenTag(string raw, string name, RichTextStyle style, int colorRgb, int fontSize)
            {
                return new TagToken(TagKind.Open, raw, name, false, style, colorRgb, fontSize, string.Empty);
            }

            internal static TagToken SelfClosingTag(string raw, string name, RichTextStyle style)
            {
                return new TagToken(TagKind.SelfClosing, raw, name, false, style, 0, 0, string.Empty);
            }

            internal static TagToken SelfClosingTag(string raw, string name, RichTextStyle style, int colorRgb, int fontSize)
            {
                return new TagToken(TagKind.SelfClosing, raw, name, false, style, colorRgb, fontSize, string.Empty);
            }

            internal static TagToken Close(string raw, string name)
            {
                return new TagToken(TagKind.Close, raw, name, true, RichTextStyle.Normal, 0, 0, string.Empty);
            }

            internal static TagToken Unknown(string raw, string name, bool isClose)
            {
                return new TagToken(TagKind.Unknown, raw, name, isClose, RichTextStyle.Normal, 0, 0, string.Empty);
            }

            internal static TagToken Invalid(string raw, string name, bool isClose, string reason)
            {
                return new TagToken(TagKind.Invalid, raw, name, isClose, RichTextStyle.Normal, 0, 0, reason);
            }
        }
    }
}

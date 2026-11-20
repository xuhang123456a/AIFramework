#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace AIFramework.Features.RichText
{
    /// <summary>
    /// 把解析后的片段序列**重新序列化**成 TMP 富文本：只输出白名单标签，绝不输出任何别的标记。
    /// <para>
    /// <b>为什么不转义文本里的 <c>&lt;</c></b>：片段文本已经是解析后的纯文本，能留在文本里的 <c>&lt;</c>
    /// 只可能来自"不构成标记的字符"（<c>1 &lt; 2</c>）或调用方透传的实体（<c>&amp;lt;</c>）。
    /// 原样输出有两个好处：纯文本语义不丢（复制出去的正文与显示一致），且重新解析时这些 <c>&lt;</c>
    /// 依旧不构成标记，文本与样式完全不变（往返测试已锁定）。若下游换成会二次解析的实现，
    /// 由调用方自行做转义，本切片不替它决定。
    /// </para>
    /// <para>
    /// <b>负载夹紧</b>：手工构造的片段若带越界颜色/字号，序列化时把颜色截到 24 位、字号夹到
    /// 10..200，保证本方法**永远**只产出合法 TMP 标记；解析器产出的片段本就在范围内，往返不受影响。
    /// </para>
    /// </summary>
    public static class RichTextFormatter
    {
        private const string HexDigits = "0123456789ABCDEF";

        /// <summary>
        /// 序列化片段序列。
        /// <para>普通文本直接原样输出；带样式的片段逐个包一层白名单标签。空序列返回空串。</para>
        /// </summary>
        /// <param name="spans">解析结果（允许 <c>null</c> 与空数组）。</param>
        /// <returns>可直接交给 TMP 的富文本。</returns>
        public static string ToSafeTmp(RichTextSpan[]? spans)
        {
            if (spans == null || spans.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(spans.Length * 16);
            for (int i = 0; i < spans.Length; i++)
            {
                RichTextSpan span = spans[i];
                if (span.Text.Length == 0)
                {
                    continue;
                }

                if (span.Style == RichTextStyle.Normal)
                {
                    builder.Append(span.Text);
                    continue;
                }

                AppendOpenTag(builder, span);
                builder.Append(span.Text);
                AppendCloseTag(builder, span.Style);
            }

            return builder.ToString();
        }

        /// <summary>写一个白名单开标签（统一下写；颜色统一大写 <c>#RRGGBB</c>）。</summary>
        /// <param name="builder">输出缓冲。</param>
        /// <param name="span">要写的片段。</param>
        public static void AppendOpenTag(StringBuilder builder, in RichTextSpan span)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            switch (span.Style)
            {
                case RichTextStyle.Bold:
                    builder.Append("<b>");
                    return;
                case RichTextStyle.Italic:
                    builder.Append("<i>");
                    return;
                case RichTextStyle.Underline:
                    builder.Append("<u>");
                    return;
                case RichTextStyle.Color:
                    builder.Append("<color=");
                    AppendRgb(builder, span.ColorRgb);
                    builder.Append('>');
                    return;
                case RichTextStyle.Size:
                    builder.Append("<size=");
                    builder.Append(ClampFontSize(span.FontSize).ToString(CultureInfo.InvariantCulture));
                    builder.Append('>');
                    return;
                default:
                    // Normal 没有开标签：调用方不该走到这里，静默当文本处理。
                    return;
            }
        }

        /// <summary>写一个白名单闭标签；<see cref="RichTextStyle.Normal"/> 无事可做。</summary>
        /// <param name="builder">输出缓冲。</param>
        /// <param name="style">要闭合的样式。</param>
        public static void AppendCloseTag(StringBuilder builder, RichTextStyle style)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            switch (style)
            {
                case RichTextStyle.Bold:
                    builder.Append("</b>");
                    return;
                case RichTextStyle.Italic:
                    builder.Append("</i>");
                    return;
                case RichTextStyle.Underline:
                    builder.Append("</u>");
                    return;
                case RichTextStyle.Color:
                    builder.Append("</color>");
                    return;
                case RichTextStyle.Size:
                    builder.Append("</size>");
                    return;
                default:
                    return;
            }
        }

        /// <summary>把 <c>0xRRGGBB</c> 写成 <c>#RRGGBB</c>（大写、定长，不做区域设置格式化）。</summary>
        private static void AppendRgb(StringBuilder builder, int rgb)
        {
            int value = rgb & 0xFFFFFF;
            builder.Append('#');
            for (int shift = 20; shift >= 0; shift -= 4)
            {
                builder.Append(HexDigits[(value >> shift) & 0xF]);
            }
        }

        /// <summary>字号夹到 10..200：保证输出侧永远合法，哪怕片段是手工构造的。</summary>
        private static int ClampFontSize(int fontSize)
        {
            if (fontSize < RichTextParser.MinFontSize)
            {
                return RichTextParser.MinFontSize;
            }

            if (fontSize > RichTextParser.MaxFontSize)
            {
                return RichTextParser.MaxFontSize;
            }

            return fontSize;
        }
    }
}

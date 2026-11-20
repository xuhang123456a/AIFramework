#nullable enable
using System;
using System.Text;

namespace AIFramework.Features.RichText
{
    /// <summary>
    /// 安全富文本的**唯一有效样式**：解析结果里每个 <see cref="RichTextSpan"/> 只带一个样式值。
    /// <para>
    /// 数值稳定，序列化 / 表现层映射可直接按数值存；禁止重排或复用数值。
    /// </para>
    /// <para>
    /// <b>为什么是单值而不是 [Flags]</b>：切片只保证"剥离非法标记后文本仍可读"，
    /// 组合样式（粗 + 斜）由 UI 层自行决定；嵌套标签在解析时按**最内层**归一，
    /// 详见 <see cref="RichTextParser"/> 与 CONTRACT.md。
    /// </para>
    /// </summary>
    public enum RichTextStyle : byte
    {
        /// <summary>无样式（普通文本，0）。</summary>
        Normal = 0,

        /// <summary>粗体（1，对应白名单 <c>&lt;b&gt;</c>）。</summary>
        Bold = 1,

        /// <summary>斜体（2，对应白名单 <c>&lt;i&gt;</c>）。</summary>
        Italic = 2,

        /// <summary>下划线（3，对应白名单 <c>&lt;u&gt;</c>）。</summary>
        Underline = 3,

        /// <summary>颜色（4，对应白名单 <c>&lt;color=#RRGGBB&gt;</c>，值见 <see cref="RichTextSpan.ColorRgb"/>）。</summary>
        Color = 4,

        /// <summary>字号（5，对应白名单 <c>&lt;size=NN&gt;</c>，值见 <see cref="RichTextSpan.FontSize"/>）。</summary>
        Size = 5,
    }

    /// <summary>
    /// 诊断级别：**剥离类（Error）** 表示内容被丢弃，**修正类（Warning）** 表示内容保留但被夹紧/补全。
    /// <para>
    /// 只有 Error 才把 <see cref="RichTextDocument.HasErrors"/> 置为 true：
    /// 一个忘写 <c>&lt;/b&gt;</c> 的公告不该让 UI 报错，但一个 <c>&lt;script&gt;</c> 必须能被上层感知。
    /// </para>
    /// </summary>
    public enum RichTextDiagnosticKind : byte
    {
        /// <summary>修正类（0）：文本未丢，只是被自动闭合或被长度上限截断。</summary>
        Warning = 0,

        /// <summary>剥离类（1）：非法标记 / 越界嵌套被整段丢弃。</summary>
        Error = 1,
    }

    /// <summary>
    /// 一条解析诊断：哪个位置、哪个标记、为什么被剥离或修正。
    /// <para>
    /// <see cref="Offset"/> 是**源文本**里的字符下标（标记起始的 <c>&lt;</c>）；长度截断类诊断的偏移是 4096。
    /// <see cref="Tag"/> 是标记原文（含尖括号与属性，如 <c>&lt;a href="x"&gt;</c>）；
    /// 长度截断类诊断的 <see cref="Tag"/> 为空串。
    /// </para>
    /// </summary>
    public readonly struct RichTextDiagnostic
    {
        /// <summary>构造一条**剥离类**诊断（等价于 <see cref="RichTextDiagnosticKind.Error"/>）。</summary>
        /// <param name="offset">源文本偏移（<c>&lt;</c> 的下标）。</param>
        /// <param name="tag">标记原文；<c>null</c> 按空串处理。</param>
        /// <param name="reason">可读原因（面向日志与 QA，不做程序判定）。</param>
        public RichTextDiagnostic(int offset, string tag, string reason)
            : this(offset, tag, reason, RichTextDiagnosticKind.Error)
        {
        }

        /// <summary>构造一条指定级别的诊断。</summary>
        /// <param name="offset">源文本偏移（<c>&lt;</c> 的下标）。</param>
        /// <param name="tag">标记原文；<c>null</c> 按空串处理。</param>
        /// <param name="reason">可读原因。</param>
        /// <param name="kind">诊断级别。</param>
        public RichTextDiagnostic(int offset, string tag, string reason, RichTextDiagnosticKind kind)
        {
            Offset = offset;
            Tag = tag ?? string.Empty;
            Reason = reason ?? string.Empty;
            Kind = kind;
        }

        /// <summary>源文本偏移：出问题标记的 <c>&lt;</c> 下标（截断类为 4096）。</summary>
        public int Offset { get; }

        /// <summary>标记原文（含尖括号与属性）；截断类为空串。</summary>
        public string Tag { get; }

        /// <summary>可读原因。</summary>
        public string Reason { get; }

        /// <summary>诊断级别：剥离（Error）还是修正（Warning）。</summary>
        public RichTextDiagnosticKind Kind { get; }

        /// <summary>是否为修正类（文本未丢失）。</summary>
        public bool IsWarning
        {
            get { return Kind == RichTextDiagnosticKind.Warning; }
        }

        /// <summary>是否为剥离类（内容被丢弃）。</summary>
        public bool IsError
        {
            get { return Kind == RichTextDiagnosticKind.Error; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "[" + Kind + "] @" + Offset + " " + Tag + " : " + Reason;
        }
    }

    /// <summary>
    /// 一段**已净化**的文本 + 它该用的样式，可直接交给 UI 渲染。
    /// <para>
    /// <see cref="Text"/> 里不含任何标记：解析阶段已把标签吃掉，因此 UI 不需要（也不应该）再解析一遍。
    /// 文本中的 <c>&lt;</c> 只有当它在源文本里不构成标记时才会出现（例如 <c>1 &lt; 2</c>）。
    /// </para>
    /// <para>
    /// <see cref="SourceOffset"/> 指向该段**首个字符**在源文本里的下标；相邻同样式文本会被合并成一个 span，
    /// 因此合并后 span 的偏移仍是第一段的位置（诊断才能与源文本对得上）。
    /// </para>
    /// </summary>
    public readonly struct RichTextSpan
    {
        /// <summary>构造一个无负载样式（Normal / Bold / Italic / Underline）的片段。</summary>
        /// <param name="style">样式。</param>
        /// <param name="text">已净化的文本；<c>null</c> 按空串处理。</param>
        /// <param name="sourceOffset">文本首字符在源文本里的下标。</param>
        public RichTextSpan(RichTextStyle style, string text, int sourceOffset)
            : this(style, text, sourceOffset, 0, 0)
        {
        }

        /// <summary>构造一个带负载的片段（Color 用 <paramref name="colorRgb"/>，Size 用 <paramref name="fontSize"/>）。</summary>
        /// <param name="style">样式。</param>
        /// <param name="text">已净化的文本；<c>null</c> 按空串处理。</param>
        /// <param name="sourceOffset">文本首字符在源文本里的下标。</param>
        /// <param name="colorRgb">颜色值 <c>0xRRGGBB</c>（仅 <see cref="RichTextStyle.Color"/> 有意义）。</param>
        /// <param name="fontSize">字号（仅 <see cref="RichTextStyle.Size"/> 有意义）。</param>
        public RichTextSpan(RichTextStyle style, string text, int sourceOffset, int colorRgb, int fontSize)
        {
            Style = style;
            Text = text ?? string.Empty;
            SourceOffset = sourceOffset;
            ColorRgb = colorRgb;
            FontSize = fontSize;
        }

        /// <summary>该段的样式。</summary>
        public RichTextStyle Style { get; }

        /// <summary>已净化的文本（不含标记）。</summary>
        public string Text { get; }

        /// <summary>文本首字符在源文本里的下标。</summary>
        public int SourceOffset { get; }

        /// <summary>颜色值 <c>0xRRGGBB</c>；非 Color 样式时为 0。</summary>
        public int ColorRgb { get; }

        /// <summary>字号（10..200）；非 Size 样式时为 0。</summary>
        public int FontSize { get; }
    }

    /// <summary>
    /// 一次解析的完整结果：可直接渲染的片段序列 + 被剥离/修正的内容清单。
    /// <para>
    /// 构造后不可变（数组按只读约定暴露，调用方不得改写）。<see cref="PlainText"/> 与
    /// <see cref="HasErrors"/> 在构造时算好，渲染路径上零重复扫描。
    /// </para>
    /// </summary>
    public readonly struct RichTextDocument
    {
        private static readonly RichTextSpan[] NoSpans = new RichTextSpan[0];
        private static readonly RichTextDiagnostic[] NoDiagnostics = new RichTextDiagnostic[0];

        private readonly RichTextSpan[]? _spans;
        private readonly RichTextDiagnostic[]? _diagnostics;
        private readonly string? _plainText;
        private readonly bool _hasErrors;

        /// <summary>构造文档。</summary>
        /// <param name="spans">已净化的片段（可为空数组，不可为 <c>null</c>）。</param>
        /// <param name="diagnostics">诊断清单（可为空数组，不可为 <c>null</c>）。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>（编程错误，不静默兜底）。</exception>
        public RichTextDocument(RichTextSpan[] spans, RichTextDiagnostic[] diagnostics)
        {
            if (spans == null)
            {
                throw new ArgumentNullException(nameof(spans));
            }

            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            _spans = spans;
            _diagnostics = diagnostics;
            _plainText = ConcatPlainText(spans);
            _hasErrors = HasErrorDiagnostics(diagnostics);
        }

        /// <summary>空文档（0 片段、0 诊断）：空输入与 <c>null</c> 输入都归到它。</summary>
        public static RichTextDocument Empty
        {
            get { return default; }
        }

        /// <summary>可直接渲染的片段序列；空文档为空数组（永不 <c>null</c>）。</summary>
        public RichTextSpan[] Spans
        {
            get { return _spans ?? NoSpans; }
        }

        /// <summary>被剥离/修正的内容清单；空文档为空数组（永不 <c>null</c>）。</summary>
        public RichTextDiagnostic[] Diagnostics
        {
            get { return _diagnostics ?? NoDiagnostics; }
        }

        /// <summary>是否存在**剥离类**诊断（自动闭合 / 长度截断这类修正不算错误）。</summary>
        public bool HasErrors
        {
            get { return _hasErrors; }
        }

        /// <summary>全部片段文本的无标记拼接（换行、空格原样保留）；空文档为空串。</summary>
        public string PlainText
        {
            get { return _plainText ?? string.Empty; }
        }

        /// <summary>
        /// 按白名单重新序列化成 TMP 富文本（标签统一小写、颜色统一大写 <c>#RRGGBB</c>、字号为十进制）。
        /// <para>
        /// <b>不转义文本里的 <c>&lt;</c></b>：span 文本已经是解析后的纯文本，`&lt;` 只可能来自
        /// "不构成标记的字符"（如 <c>1 &lt; 2</c>）或透传实体（如 <c>&amp;lt;</c>），原样输出既保持纯文本语义，
        /// 重新解析也不会改变文本与样式（测试已锁定）。详见 CONTRACT.md §5。
        /// </para>
        /// </summary>
        /// <returns>TMP 可直接渲染的字符串；空文档为空串。</returns>
        public string ToSafeTmp()
        {
            return RichTextFormatter.ToSafeTmp(Spans);
        }

        private static string ConcatPlainText(RichTextSpan[] spans)
        {
            if (spans.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(spans.Length * 8);
            for (int i = 0; i < spans.Length; i++)
            {
                builder.Append(spans[i].Text);
            }

            return builder.ToString();
        }

        private static bool HasErrorDiagnostics(RichTextDiagnostic[] diagnostics)
        {
            for (int i = 0; i < diagnostics.Length; i++)
            {
                if (diagnostics[i].Kind == RichTextDiagnosticKind.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

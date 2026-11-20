#nullable enable
using AIFramework.Features.RichText;
using Xunit;

namespace AIFramework.Features.Tests.RichText
{
    /// <summary>
    /// 富文本解析验收：白名单（b/i/u/color/size）正常路径、非法标记剥离、颜色/字号边界、
    /// 未闭合自动闭合、孤立闭合、'&lt;' 普通字符与实体透传、深度上限、长度截断、PlainText 拼接。
    /// <para>
    /// 断言口径与 CONTRACT.md 一一对应：诊断的 <c>Offset</c> 是源文本下标，<c>Tag</c> 是标记原文，
    /// 自动闭合 / 截断是 <see cref="RichTextDiagnosticKind.Warning"/>，剥离类是
    /// <see cref="RichTextDiagnosticKind.Error"/>。
    /// </para>
    /// </summary>
    public sealed class RichTextParserTests
    {
        [Fact]
        public void Parse_PlainText_ReturnsSingleNormalSpan()
        {
            RichTextDocument document = RichTextParser.Parse("hello");

            AssertSingleSpan(document, RichTextStyle.Normal, "hello", 0);
            Assert.Empty(document.Diagnostics);
            Assert.False(document.HasErrors);
            Assert.Equal("hello", document.PlainText);
            Assert.Equal("hello", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_NullInput_ReturnsEmptyDocument()
        {
            RichTextDocument document = RichTextParser.Parse(null);

            Assert.Empty(document.Spans);
            Assert.Empty(document.Diagnostics);
            Assert.False(document.HasErrors);
            Assert.Equal(string.Empty, document.PlainText);
            Assert.Equal(string.Empty, document.ToSafeTmp());
        }

        [Fact]
        public void Parse_EmptyInput_ReturnsEmptyDocument()
        {
            RichTextDocument document = RichTextParser.Parse(string.Empty);

            Assert.Empty(document.Spans);
            Assert.Empty(document.Diagnostics);
            Assert.Equal(string.Empty, document.PlainText);
        }

        [Fact]
        public void Parse_Bold_TagsAreConsumed_TextKept()
        {
            RichTextDocument document = RichTextParser.Parse("<b>bold</b>");

            // 标签本身不能出现在 PlainText / 片段文本里。
            AssertSingleSpan(document, RichTextStyle.Bold, "bold", 3);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("bold", document.PlainText);
        }

        [Fact]
        public void Parse_ItalicAndUnderline_ProduceTheirOwnStyles()
        {
            RichTextDocument document = RichTextParser.Parse("<i>a</i><u>b</u>");

            Assert.Equal(2, document.Spans.Length);
            Assert.Equal(RichTextStyle.Italic, document.Spans[0].Style);
            Assert.Equal("a", document.Spans[0].Text);
            Assert.Equal(3, document.Spans[0].SourceOffset);
            Assert.Equal(RichTextStyle.Underline, document.Spans[1].Style);
            Assert.Equal("b", document.Spans[1].Text);
            Assert.Equal(11, document.Spans[1].SourceOffset);
        }

        [Fact]
        public void Parse_NestedTags_KeepTextOrderAndInnermostStyle()
        {
            RichTextDocument document = RichTextParser.Parse("<b>a<i>b</i>c</b>");

            Assert.Equal(3, document.Spans.Length);
            Assert.Equal(RichTextStyle.Bold, document.Spans[0].Style);
            Assert.Equal("a", document.Spans[0].Text);
            Assert.Equal(RichTextStyle.Italic, document.Spans[1].Style);
            Assert.Equal("b", document.Spans[1].Text);
            Assert.Equal(RichTextStyle.Bold, document.Spans[2].Style);
            Assert.Equal("c", document.Spans[2].Text);
            Assert.Equal("abc", document.PlainText);
            Assert.Empty(document.Diagnostics);
        }

        [Fact]
        public void Parse_SelfClosingTags_AreNoOps()
        {
            RichTextDocument bold = RichTextParser.Parse("<b/>x");
            AssertSingleSpan(bold, RichTextStyle.Normal, "x", 4);
            Assert.Empty(bold.Diagnostics);

            RichTextDocument color = RichTextParser.Parse("<color=#FF0000/>y");
            AssertSingleSpan(color, RichTextStyle.Normal, "y", 16);
            Assert.Empty(color.Diagnostics);
        }

        [Fact]
        public void Parse_ScriptTag_IsStrippedAndDiagnosed()
        {
            RichTextDocument document = RichTextParser.Parse("a<script>alert</script>z");

            // 剥离标记本身，保留中间的可读文本。
            AssertSingleSpan(document, RichTextStyle.Normal, "aalertz", 0);
            Assert.Equal(2, document.Diagnostics.Length);
            Assert.True(document.Diagnostics[0].IsError);
            Assert.Equal(1, document.Diagnostics[0].Offset);
            Assert.Equal("<script>", document.Diagnostics[0].Tag);
            Assert.Equal(14, document.Diagnostics[1].Offset);
            Assert.Equal("</script>", document.Diagnostics[1].Tag);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_AnchorWithAttribute_IsStrippedWhole()
        {
            RichTextDocument document = RichTextParser.Parse("<a href=\"x\">link</a>");

            AssertSingleSpan(document, RichTextStyle.Normal, "link", 12);
            Assert.Equal(2, document.Diagnostics.Length);
            Assert.Equal("<a href=\"x\">", document.Diagnostics[0].Tag);
            Assert.Equal(0, document.Diagnostics[0].Offset);
            Assert.Equal("</a>", document.Diagnostics[1].Tag);
        }

        [Fact]
        public void Parse_ImageTag_LeavesNoTextAndOneError()
        {
            RichTextDocument document = RichTextParser.Parse("<img src=x>");

            Assert.Empty(document.Spans);
            Assert.Equal(string.Empty, document.PlainText);
            Assert.Single(document.Diagnostics);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_ColorUppercase_IsValidAndCarriesRgb()
        {
            RichTextDocument document = RichTextParser.Parse("<color=#FF0000>red</color>");

            AssertSingleSpan(document, RichTextStyle.Color, "red", 15);
            Assert.Equal(0xFF0000, document.Spans[0].ColorRgb);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("<color=#FF0000>red</color>", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_ColorLowercase_IsValidAndNormalizedOnOutput()
        {
            RichTextDocument document = RichTextParser.Parse("<color=#00ff00>green</color>");

            AssertSingleSpan(document, RichTextStyle.Color, "green", 15);
            Assert.Equal(0x00FF00, document.Spans[0].ColorRgb);
            Assert.Empty(document.Diagnostics);

            // 序列化统一大写，保证输出稳定可断言。
            Assert.Equal("<color=#00FF00>green</color>", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_ColorShortForm_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<color=#F00>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 12);
            Assert.Single(document.Diagnostics);
            Assert.True(document.Diagnostics[0].IsError);
            Assert.Equal("<color=#F00>", document.Diagnostics[0].Tag);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_ColorWithAlpha_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<color=#FF0000AA>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 17);
            Assert.Single(document.Diagnostics);
            Assert.Contains("6 位十六进制", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_ColorName_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<color=red>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 11);
            Assert.Single(document.Diagnostics);
            Assert.True(document.Diagnostics[0].IsError);
        }

        [Fact]
        public void Parse_ColorNonHex_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<color=#GGGGGG>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 15);
            Assert.Single(document.Diagnostics);
            Assert.Equal("<color=#GGGGGG>", document.Diagnostics[0].Tag);
        }

        [Fact]
        public void Parse_SizeAtLowerBound_IsValid()
        {
            RichTextDocument document = RichTextParser.Parse("<size=10>x</size>");

            AssertSingleSpan(document, RichTextStyle.Size, "x", 9);
            Assert.Equal(10, document.Spans[0].FontSize);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("<size=10>x</size>", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_SizeAtUpperBound_IsValid()
        {
            RichTextDocument document = RichTextParser.Parse("<size=200>x</size>");

            AssertSingleSpan(document, RichTextStyle.Size, "x", 10);
            Assert.Equal(200, document.Spans[0].FontSize);
            Assert.Empty(document.Diagnostics);
        }

        [Fact]
        public void Parse_SizeBelowRange_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<size=9>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 8);
            Assert.Single(document.Diagnostics);
            Assert.Contains("10..200", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_SizeAboveRange_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<size=201>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 10);
            Assert.Single(document.Diagnostics);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_SizeHuge_IsStrippedWithDiagnostic()
        {
            RichTextDocument document = RichTextParser.Parse("<size=9999>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 11);
            Assert.Single(document.Diagnostics);
            Assert.Equal("<size=9999>", document.Diagnostics[0].Tag);
            Assert.Equal(0, document.Diagnostics[0].Offset);
        }

        [Fact]
        public void Parse_SizeNonNumeric_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<size=abc>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 10);
            Assert.Single(document.Diagnostics);
            Assert.Contains("整数", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_UnclosedTag_AutoClosesAndKeepsText()
        {
            RichTextDocument document = RichTextParser.Parse("<b>x");

            AssertSingleSpan(document, RichTextStyle.Bold, "x", 3);
            Assert.Single(document.Diagnostics);
            Assert.True(document.Diagnostics[0].IsWarning);
            Assert.Equal(0, document.Diagnostics[0].Offset);
            Assert.Contains("自动闭合", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);

            // 自动闭合是修正而不是错误：文本没丢，UI 不该报错。
            Assert.False(document.HasErrors);
        }

        [Fact]
        public void Parse_MultipleUnclosedTags_ReportInnermostFirst()
        {
            RichTextDocument document = RichTextParser.Parse("<b><i>x");

            AssertSingleSpan(document, RichTextStyle.Italic, "x", 6);
            Assert.Equal(2, document.Diagnostics.Length);
            Assert.Equal(3, document.Diagnostics[0].Offset);
            Assert.Equal("<i>", document.Diagnostics[0].Tag);
            Assert.Equal(0, document.Diagnostics[1].Offset);
            Assert.Equal("<b>", document.Diagnostics[1].Tag);
        }

        [Fact]
        public void Parse_OrphanClosingTag_IsStrippedAndDiagnosed()
        {
            RichTextDocument document = RichTextParser.Parse("</b>x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 4);
            Assert.Single(document.Diagnostics);
            Assert.True(document.Diagnostics[0].IsError);
            Assert.Equal(0, document.Diagnostics[0].Offset);
            Assert.Equal("</b>", document.Diagnostics[0].Tag);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_MismatchedClosingTag_IsTreatedAsOrphan()
        {
            // </b> 出现时栈顶是 <i>：不猜"应该闭合谁"，按孤立闭合剥离，文本保持最内层样式。
            RichTextDocument document = RichTextParser.Parse("<b><i>x</b>");

            AssertSingleSpan(document, RichTextStyle.Italic, "x", 6);
            Assert.Equal(3, document.Diagnostics.Length);
            Assert.Equal(7, document.Diagnostics[0].Offset);
            Assert.Contains("不匹配", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_LessThanSign_IsOrdinaryChar()
        {
            RichTextDocument document = RichTextParser.Parse("1 < 2");

            AssertSingleSpan(document, RichTextStyle.Normal, "1 < 2", 0);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("1 < 2", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_LessThanWithoutGreaterThan_IsOrdinaryChar()
        {
            // 没有闭合 '>' 的 '<b' 不是标记：不能吞掉 '<'，也不能吞掉后面的 'b'。
            RichTextDocument document = RichTextParser.Parse("a<b");

            AssertSingleSpan(document, RichTextStyle.Normal, "a<b", 0);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("a<b", document.PlainText);
        }

        [Fact]
        public void Parse_EntityReference_PassesThroughUntouched()
        {
            RichTextDocument document = RichTextParser.Parse("&lt;b&gt;");

            AssertSingleSpan(document, RichTextStyle.Normal, "&lt;b&gt;", 0);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("&lt;b&gt;", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_DepthLimit_StripsTheNinthLevel()
        {
            // 9 层同种开标签：第 9 层剥离；末尾第 9 个闭标签因此变成孤立闭合。
            string input = Repeat("<b>", 9) + "x" + Repeat("</b>", 9);
            RichTextDocument document = RichTextParser.Parse(input);

            AssertSingleSpan(document, RichTextStyle.Bold, "x", 27);
            Assert.Equal(2, document.Diagnostics.Length);
            Assert.Equal(24, document.Diagnostics[0].Offset);
            Assert.Contains("嵌套深度", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
            Assert.Equal(60, document.Diagnostics[1].Offset);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Parse_LengthLimit_TruncatesAt4096()
        {
            string input = new string('a', 5000);
            RichTextDocument document = RichTextParser.Parse(input);

            Assert.Single(document.Spans);
            Assert.Equal(4096, document.Spans[0].Text.Length);
            Assert.Equal(4096, document.PlainText.Length);
            Assert.Single(document.Diagnostics);
            Assert.Equal(4096, document.Diagnostics[0].Offset);
            Assert.Equal(string.Empty, document.Diagnostics[0].Tag);
            Assert.True(document.Diagnostics[0].IsWarning);
            Assert.False(document.HasErrors);
        }

        [Fact]
        public void Parse_AdjacentSameStyleRuns_AreMerged()
        {
            RichTextDocument document = RichTextParser.Parse("<b>a</b><b>b</b>");

            // 合并成一段连续样式：渲染层不该为同样式文本做多次绘制。
            AssertSingleSpan(document, RichTextStyle.Bold, "ab", 3);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("<b>ab</b>", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_TagsOnlyWithoutText_ProduceNoSpans()
        {
            RichTextDocument document = RichTextParser.Parse("<b></b>");

            Assert.Empty(document.Spans);
            Assert.Empty(document.Diagnostics);
            Assert.Equal(string.Empty, document.PlainText);
            Assert.Equal(string.Empty, document.ToSafeTmp());
        }

        [Fact]
        public void Parse_ConsecutiveTags_ResolveToInnermostStyle()
        {
            RichTextDocument document = RichTextParser.Parse("<b><i>x</i></b>");

            AssertSingleSpan(document, RichTextStyle.Italic, "x", 6);
            Assert.Empty(document.Diagnostics);
        }

        [Fact]
        public void Parse_PlainText_ConcatenatesEverySpan()
        {
            RichTextDocument document = RichTextParser.Parse("<b>a</b><i>b</i>c");

            Assert.Equal(3, document.Spans.Length);
            Assert.Equal("abc", document.PlainText);
        }

        [Fact]
        public void Parse_SourceOffsets_TrackInputPositions()
        {
            RichTextDocument document = RichTextParser.Parse("<b>a</b><i>b</i>c");

            Assert.Equal(3, document.Spans[0].SourceOffset);
            Assert.Equal(11, document.Spans[1].SourceOffset);
            Assert.Equal(16, document.Spans[2].SourceOffset);
        }

        [Fact]
        public void Parse_DiagnosticOffset_PointsAtLessThanSign()
        {
            RichTextDocument document = RichTextParser.Parse("ab<script>x");

            Assert.Single(document.Diagnostics);
            Assert.Equal(2, document.Diagnostics[0].Offset);
            Assert.Equal("<script>", document.Diagnostics[0].Tag);
        }

        [Fact]
        public void Parse_BoldWithAttribute_IsStripped()
        {
            RichTextDocument document = RichTextParser.Parse("<b foo=\"1\">x");

            AssertSingleSpan(document, RichTextStyle.Normal, "x", 11);
            Assert.Single(document.Diagnostics);
            Assert.Contains("不支持属性", document.Diagnostics[0].Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_UppercaseTagNames_AreAcceptedAndNormalized()
        {
            RichTextDocument document = RichTextParser.Parse("<B>X</B>");

            AssertSingleSpan(document, RichTextStyle.Bold, "X", 3);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("<b>X</b>", document.ToSafeTmp());
        }

        [Fact]
        public void Parse_MixedCaseOpenAndClose_StillCloses()
        {
            // 标签名大小写不敏感：开标签 <B> 与闭标签 </b> 必须配对，不能误判成孤立闭合。
            RichTextDocument document = RichTextParser.Parse("<B>x</b>");

            AssertSingleSpan(document, RichTextStyle.Bold, "x", 3);
            Assert.Empty(document.Diagnostics);
            Assert.False(document.HasErrors);
        }

        [Fact]
        public void Parse_TagEndsAtFirstGreaterThan_SoQuotedGreaterThanIsNotSpecial()
        {
            // 文档化行为：标签以第一个 '>' 结束，属性区不解析引号。
            // 因此引号里的 '>' 提前截断标签，剩余字符按普通文本透传（不吞字符）。
            RichTextDocument document = RichTextParser.Parse("<a href=\"x>y\">z</a>");

            Assert.Equal(2, document.Diagnostics.Length);
            Assert.Equal("<a href=\"x>", document.Diagnostics[0].Tag);
            Assert.Equal("y\">z", document.PlainText);
        }

        [Fact]
        public void Parse_FullSample_KeepsOnlyWhitelistedStyles()
        {
            const string input =
                "<b>标题</b><color=#FF8800>橙色</color><size=24>字号</size><script>bad()</script>尾巴";
            RichTextDocument document = RichTextParser.Parse(input);

            Assert.Equal(4, document.Spans.Length);
            Assert.Equal(RichTextStyle.Bold, document.Spans[0].Style);
            Assert.Equal("标题", document.Spans[0].Text);
            Assert.Equal(RichTextStyle.Color, document.Spans[1].Style);
            Assert.Equal(0xFF8800, document.Spans[1].ColorRgb);
            Assert.Equal(RichTextStyle.Size, document.Spans[2].Style);
            Assert.Equal(24, document.Spans[2].FontSize);
            Assert.Equal("bad()尾巴", document.Spans[3].Text);
            Assert.Equal("标题橙色字号bad()尾巴", document.PlainText);
            Assert.Equal(2, document.Diagnostics.Length);
            Assert.True(document.HasErrors);
        }

        [Fact]
        public void Constructor_NullArrays_Throw()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new RichTextDocument(null!, new RichTextDiagnostic[0]));
            Assert.Throws<System.ArgumentNullException>(
                () => new RichTextDocument(new RichTextSpan[0], null!));
        }

        [Fact]
        public void DefaultDocument_IsEmptyButSafe()
        {
            RichTextDocument document = default;

            Assert.Empty(document.Spans);
            Assert.Empty(document.Diagnostics);
            Assert.False(document.HasErrors);
            Assert.Equal(string.Empty, document.PlainText);
            Assert.Equal(string.Empty, document.ToSafeTmp());
        }

        private static void AssertSingleSpan(
            in RichTextDocument document,
            RichTextStyle style,
            string text,
            int sourceOffset)
        {
            Assert.Single(document.Spans);
            Assert.Equal(style, document.Spans[0].Style);
            Assert.Equal(text, document.Spans[0].Text);
            Assert.Equal(sourceOffset, document.Spans[0].SourceOffset);
        }

        private static string Repeat(string value, int count)
        {
            var builder = new System.Text.StringBuilder(value.Length * count);
            for (int i = 0; i < count; i++)
            {
                builder.Append(value);
            }

            return builder.ToString();
        }
    }
}

#nullable enable
using AIFramework.Features.RichText;
using Xunit;

namespace AIFramework.Features.Tests.RichText
{
    /// <summary>
    /// <c>ToSafeTmp</c> 验收：只输出白名单标签、颜色/字号规范化与夹紧、
    /// 以及"解析 → 序列化 → 再解析"往返后文本与样式完全一致（含 <c>&lt;</c> 原样保留的口径）。
    /// </summary>
    public sealed class RichTextFormatterTests
    {
        [Fact]
        public void ToSafeTmp_EmptyInputs_ReturnEmptyString()
        {
            Assert.Equal(string.Empty, RichTextFormatter.ToSafeTmp(null));
            Assert.Equal(string.Empty, RichTextFormatter.ToSafeTmp(new RichTextSpan[0]));
            Assert.Equal(string.Empty, RichTextDocument.Empty.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_PlainSpan_ReturnsBareText()
        {
            var document = new RichTextDocument(
                new[] { new RichTextSpan(RichTextStyle.Normal, "abc", 0) },
                new RichTextDiagnostic[0]);

            Assert.Equal("abc", document.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_SerializesEveryWhitelistedStyle()
        {
            var document = new RichTextDocument(
                new[]
                {
                    new RichTextSpan(RichTextStyle.Bold, "b", 0),
                    new RichTextSpan(RichTextStyle.Italic, "i", 1),
                    new RichTextSpan(RichTextStyle.Underline, "u", 2),
                    new RichTextSpan(RichTextStyle.Color, "c", 3, 0x112233, 0),
                    new RichTextSpan(RichTextStyle.Size, "s", 4, 0, 32),
                },
                new RichTextDiagnostic[0]);

            Assert.Equal(
                "<b>b</b><i>i</i><u>u</u><color=#112233>c</color><size=32>s</size>",
                document.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_MasksColorTo24Bit()
        {
            // 手工构造的片段可能带高位脏数据：序列化必须只吐 6 位十六进制。
            var document = new RichTextDocument(
                new[] { new RichTextSpan(RichTextStyle.Color, "x", 0, unchecked((int)0xFF112233), 0) },
                new RichTextDiagnostic[0]);

            Assert.Equal("<color=#112233>x</color>", document.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_ClampsOutOfRangeFontSize()
        {
            var tooBig = new RichTextDocument(
                new[] { new RichTextSpan(RichTextStyle.Size, "x", 0, 0, 9999) },
                new RichTextDiagnostic[0]);
            Assert.Equal("<size=200>x</size>", tooBig.ToSafeTmp());

            var tooSmall = new RichTextDocument(
                new[] { new RichTextSpan(RichTextStyle.Size, "x", 0, 0, 1) },
                new RichTextDiagnostic[0]);
            Assert.Equal("<size=10>x</size>", tooSmall.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_SkipsEmptyTextSpans()
        {
            var document = new RichTextDocument(
                new[]
                {
                    new RichTextSpan(RichTextStyle.Normal, string.Empty, 0),
                    new RichTextSpan(RichTextStyle.Bold, "x", 5),
                },
                new RichTextDiagnostic[0]);

            Assert.Equal("<b>x</b>", document.ToSafeTmp());
            Assert.Equal("x", document.PlainText);
        }

        [Fact]
        public void ToSafeTmp_KeepsLessThanSignRaw()
        {
            // 决策：不把文本里的 '<' 转义成 &lt;，保证纯文本语义与显示一致。
            RichTextDocument document = RichTextParser.Parse("1 < 2 且 a<b");

            Assert.Equal("1 < 2 且 a<b", document.ToSafeTmp());
        }

        [Fact]
        public void ToSafeTmp_RoundTrip_PreservesTextAndStyles()
        {
            string[] inputs =
            {
                "plain",
                "<b>bold</b>",
                "<i>a</i><u>b</u>",
                "<b>a<i>b</i>c</b>",
                "<color=#00ff00>green</color>",
                "<size=42>size</size>",
                "a<script>x</script>b",
                "1 < 2",
                "</b>orphan",
                "<b>unclosed",
                "<b>a</b><b>b</b>",
            };

            for (int i = 0; i < inputs.Length; i++)
            {
                RichTextDocument first = RichTextParser.Parse(inputs[i]);
                string safe = first.ToSafeTmp();
                RichTextDocument second = RichTextParser.Parse(safe);

                Assert.Equal(first.PlainText, second.PlainText);
                Assert.Equal(first.Spans.Length, second.Spans.Length);
                for (int j = 0; j < first.Spans.Length; j++)
                {
                    Assert.Equal(first.Spans[j].Style, second.Spans[j].Style);
                    Assert.Equal(first.Spans[j].Text, second.Spans[j].Text);
                    Assert.Equal(first.Spans[j].ColorRgb, second.Spans[j].ColorRgb);
                    Assert.Equal(first.Spans[j].FontSize, second.Spans[j].FontSize);
                }

                // 序列化结果自身是规范形式：再跑一遍必须逐字节相同。
                Assert.Equal(safe, second.ToSafeTmp());

                // 第一遍已被净化，第二遍不该再产生任何诊断。
                Assert.Empty(second.Diagnostics);
            }
        }

        [Fact]
        public void Span_NullText_IsCoercedToEmpty()
        {
            var span = new RichTextSpan(RichTextStyle.Bold, null!, 7);

            Assert.Equal(string.Empty, span.Text);
            Assert.Equal(7, span.SourceOffset);
            Assert.Equal(RichTextStyle.Bold, span.Style);
        }

        [Fact]
        public void Diagnostic_NullTagAndReason_AreCoercedToEmpty()
        {
            var diagnostic = new RichTextDiagnostic(3, null!, null!);

            Assert.Equal(string.Empty, diagnostic.Tag);
            Assert.Equal(string.Empty, diagnostic.Reason);
            Assert.Equal(3, diagnostic.Offset);
            Assert.True(diagnostic.IsError);
            Assert.False(diagnostic.IsWarning);
        }
    }
}

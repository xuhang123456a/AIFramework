#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.VoiceOver;
using Xunit;

namespace AIFramework.Features.Tests.VoiceOver
{
    /// <summary>
    /// <c>TbVoiceLine</c> 子表解析验收：合法字节逐字段回读（含 UTF-8 说话人名）；
    /// 截断 / 尾部残留 / 时间倒置 / 负起点 / 非正 lineId / 空 speaker / 空 textKey /
    /// 语种越界 / 同语种重复一律显式抛 <see cref="InvalidDataException"/>；
    /// 0 行是合法配置；表缺失抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public sealed class VoiceOverTablesTests
    {
        private static readonly VoiceLineRowSpec[] ThreeRows =
        {
            new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "vo_hero_greet_zh", 0, 0, 1500),
            new VoiceLineRowSpec(1, "リア", "vo.hero.greet", "vo_hero_greet_ja", 2, 120, 1600),
            new VoiceLineRowSpec(2, "铁匠", "vo.smith.repair", string.Empty, 0, 300, 2100),
        };

        [Fact]
        public void Parse_ThreeRows_RoundTripsEveryColumn()
        {
            var tables = new VoiceOverTables(VoiceLineTableStub.WithRows(ThreeRows));

            Assert.Equal(3, tables.Count);
            Assert.Equal(2, tables.LineCount);
            Assert.Equal(1, tables.GetLineId(0));
            Assert.Equal(2, tables.GetLineId(1));

            VoiceLineDef first = tables.Get(0);
            Assert.Equal(1, first.LineId);
            Assert.Equal("莉亚", first.Speaker);
            Assert.Equal("vo.hero.greet", first.TextKey);
            Assert.Equal("vo_hero_greet_zh", first.AudioKey);
            Assert.Equal(0, first.Language);
            Assert.Equal(0, first.StartMs);
            Assert.Equal(1500, first.EndMs);

            // UTF-8 多字节说话人（"リア" = 6 字节）必须按字节长度前缀正确回读。
            VoiceLineDef second = tables.Get(1);
            Assert.Equal("リア", second.Speaker);
            Assert.Equal(2, second.Language);
            Assert.Equal(120, second.StartMs);
            Assert.Equal(1600, second.EndMs);

            // 空 audioKey = 只有字幕没有配音，是合法配置。
            VoiceLineDef third = tables.Get(2);
            Assert.Equal(2, third.LineId);
            Assert.Equal(string.Empty, third.AudioKey);
            Assert.Equal(300, third.StartMs);
            Assert.Equal(2100, third.EndMs);
        }

        [Fact]
        public void Parse_EmptyTable_IsAllowed()
        {
            var tables = new VoiceOverTables(VoiceLineTableStub.WithRows(new VoiceLineRowSpec[0]));

            Assert.Equal(0, tables.Count);
            Assert.Equal(0, tables.LineCount);
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new VoiceOverTables(null!));
        }

        [Fact]
        public void Parse_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithoutTable()));

            Assert.Contains(VoiceLineTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_HeaderShorterThanFourBytes_Throws()
        {
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(new byte[] { 1, 0 })));

            Assert.Contains("头部长度不足", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NegativeRowCount_Throws()
        {
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(VoiceLineTableStub.BuildRaw(-1))));

            Assert.Contains("rowCount 为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DeclaredRowsExceedBytes_ThrowsTruncated()
        {
            // 头部声明 100 行、物理上只有 1 行：必须在按声明行数分配前用"行数 × 最小行宽"挡住。
            byte[] payload = VoiceLineTableStub.BuildPayload(new[] { ThreeRows[0] }, declaredRowCount: 100);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(payload)));

            Assert.Contains("字节流被截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TruncatedStringBody_Throws()
        {
            // 头部 4 字节 + 剩余正好 28 字节（刚好绕开"最小行宽"前置校验），
            // 但 speaker 声明 30 字节、实际只剩 20 字节：必须在读字符串体时报截断并点名字段。
            byte[] payload = VoiceLineTableStub.BuildRaw(1, 1, 30, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
            Assert.Contains("speaker", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NegativeStringLength_Throws()
        {
            // rowCount=1 + 足量填充（绕过最小行宽前置校验），speakerLen = -1。
            byte[] payload = VoiceLineTableStub.BuildRaw(1, 1, -1, 0, 0, 0, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(payload)));

            Assert.Contains("长度为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrailingResidual_Throws()
        {
            byte[] payload = VoiceLineTableStub.AppendPadding(VoiceLineTableStub.BuildPayload(ThreeRows), 4);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithPayload(payload)));

            Assert.Contains("尾部残留", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_SubtitleWindowInverted_Throws()
        {
            var zeroLength = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", 0, 200, 200),
            };

            var reversed = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", 0, 900, 100),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(zeroLength)));
            Assert.Contains("endMs > startMs", exception.Message, StringComparison.Ordinal);

            // 倒序同样非法：字幕窗口必须非空且正向。
            Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(reversed)));
        }

        [Fact]
        public void Parse_NegativeStartMs_Throws()
        {
            var rows = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", 0, -1, 500),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(rows)));

            Assert.Contains("startMs 不能为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NonPositiveLineId_Throws()
        {
            var zero = new[]
            {
                new VoiceLineRowSpec(0, "莉亚", "vo.hero.greet", "a", 0, 0, 500),
            };

            var negative = new[]
            {
                new VoiceLineRowSpec(-3, "莉亚", "vo.hero.greet", "a", 0, 0, 500),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(zero)));
            Assert.Contains("lineId 必须为正", exception.Message, StringComparison.Ordinal);

            Assert.Throws<InvalidDataException>(() => new VoiceOverTables(VoiceLineTableStub.WithRows(negative)));
        }

        [Fact]
        public void Parse_EmptySpeaker_Throws()
        {
            var rows = new[]
            {
                new VoiceLineRowSpec(1, string.Empty, "vo.hero.greet", "a", 0, 0, 500),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(rows)));

            Assert.Contains("speaker 不能为空", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EmptyTextKey_Throws()
        {
            var rows = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", string.Empty, "a", 0, 0, 500),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(rows)));

            Assert.Contains("textKey 不能为空", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_LanguageOutOfRange_Throws()
        {
            var above = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", 3, 0, 500),
            };

            var below = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", -1, 0, 500),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(above)));
            Assert.Contains("language 必须在 0..2", exception.Message, StringComparison.Ordinal);

            Assert.Throws<InvalidDataException>(() => new VoiceOverTables(VoiceLineTableStub.WithRows(below)));
        }

        [Fact]
        public void Parse_DuplicateLineAndLanguage_Throws()
        {
            var rows = new[]
            {
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "a", 0, 0, 500),
                new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "b", 0, 0, 900),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VoiceOverTables(VoiceLineTableStub.WithRows(rows)));

            Assert.Contains("重复配置", exception.Message, StringComparison.Ordinal);
            Assert.Contains("lineId 1", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_SameLineDifferentLanguages_IsAllowed()
        {
            // 同一 lineId 拆多语种是常态：唯一性约束是 (lineId, language) 而不是 lineId。
            var tables = new VoiceOverTables(VoiceLineTableStub.WithRows(ThreeRows));

            Assert.Equal(2, tables.LineCount);
            Assert.Equal(1, tables.Get(0).LineId);
            Assert.Equal(1, tables.Get(1).LineId);
        }

        [Fact]
        public void Lookup_ExactHitAnyLanguageAndOutOfRange()
        {
            var tables = new VoiceOverTables(VoiceLineTableStub.WithRows(ThreeRows));

            VoiceLineDef exact;
            Assert.True(tables.TryFindRow(1, 2, out exact));
            Assert.Equal("リア", exact.Speaker);

            // 语言 1（英语）这一行没配：精确定位必须失败，回退由 Handler 决定。
            VoiceLineDef missing;
            Assert.False(tables.TryFindRow(1, 1, out missing));

            // 未定义语种数值一律视为未命中，不抛。
            Assert.False(tables.TryFindRow(1, 7, out missing));
            Assert.False(tables.TryFindRow(999, 0, out missing));

            // "任意语种"兜底取该 lineId 首次出现的行。
            VoiceLineDef any;
            Assert.True(tables.TryFindAnyRow(2, out any));
            Assert.Equal(0, any.Language);
            Assert.False(tables.TryFindAnyRow(999, out any));
        }

        [Fact]
        public void Get_OutOfRange_Throws()
        {
            var tables = new VoiceOverTables(VoiceLineTableStub.WithRows(ThreeRows));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.GetLineId(2));
        }

        /// <summary>
        /// 端到端守卫：直接解析仓库已导出的 <c>TbVoiceLine.bin</c>，防止"tablegen 列顺序"与
        /// "切片解析器列顺序"各改各的。定长部分 4 字节表头 + 8 行 × 28 字节 + 字符串体 305 = 537。
        /// </summary>
        [Fact]
        public void Parse_CommittedRepoBinary_MatchesEightRowContract()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(
                repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables", "TbVoiceLine.bin");
            byte[] payload = File.ReadAllBytes(path);

            var tables = new VoiceOverTables(VoiceLineTableStub.WithPayload(payload));

            Assert.Equal(537, payload.Length);
            Assert.Equal(8, tables.Count);
            Assert.Equal(3, tables.LineCount);

            VoiceLineDef first = tables.Get(0);
            Assert.Equal(1, first.LineId);
            Assert.Equal("莉亚", first.Speaker);
            Assert.Equal("vo.hero.greet", first.TextKey);
            Assert.Equal("vo_hero_greet_zh", first.AudioKey);
            Assert.Equal(0, first.Language);
            Assert.Equal(0, first.StartMs);
            Assert.Equal(1500, first.EndMs);

            // 第 2 行只配了简中（配音缺失）与英语，日语刻意缺一条：语种回退的演示数据。
            VoiceLineDef secondLineChinese = tables.Get(3);
            Assert.Equal(2, secondLineChinese.LineId);
            Assert.Equal(string.Empty, secondLineChinese.AudioKey);
            Assert.Equal(300, secondLineChinese.StartMs);
            Assert.Equal(2100, secondLineChinese.EndMs);

            VoiceLineDef missing;
            Assert.False(tables.TryFindRow(2, 2, out missing), "TbVoiceLine 的 lineId 2 不应有日语行。");
            Assert.True(tables.TryFindAnyRow(2, out missing));
            Assert.Equal(0, missing.Language);

            // 最后一行是旁白的日语版本：验证尾部与多字节（"ナレーター" = 15 字节）都读对。
            VoiceLineDef last = tables.Get(7);
            Assert.Equal(3, last.LineId);
            Assert.Equal("ナレーター", last.Speaker);
            Assert.Equal("vo.narrator.intro", last.TextKey);
            Assert.Equal(2, last.Language);
            Assert.Equal(0, last.StartMs);
            Assert.Equal(3100, last.EndMs);

            for (int i = 0; i < tables.Count; i++)
            {
                VoiceLineDef row = tables.Get(i);
                Assert.True(row.LineId > 0, "第 " + i + " 行 lineId 非正。");
                Assert.InRange(row.Language, 0, 2);
                Assert.True(row.EndMs > row.StartMs, "第 " + i + " 行字幕区间倒置。");
            }
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.Features.Tests/VoiceOver/VoiceOverTablesTests.cs 向上三级
            DirectoryInfo? directory = new FileInfo(callerFilePath).Directory;
            for (int i = 0; i < 3 && directory != null; i++)
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}

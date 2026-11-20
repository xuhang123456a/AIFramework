using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleMetricsExport"/> 的行为契约：CSV 文本与表头一致、追加不重复写表头、
    /// 覆盖写截断、缺失目录自动创建、损坏/手工编辑过的文件能读回、坏格式被拒绝。
    /// <para>
    /// <b>这些用例守的是"报表文件能被别人读"</b>：看板 CSV 会被运营用 Excel 打开、被后台脚本导入。
    /// 重复表头、缺行尾换行导致两行粘连、BOM 让第一列变成 <c>\ufeffdateUtc</c>，这三种事故都不会抛异常，
    /// 只会在数据侧悄悄出现错行——所以必须在这里逐条钉死。
    /// </para>
    /// <para>每个用例独占一个临时目录（<see cref="TempDirectoryTest"/>），结束即删，绝不落进仓库。</para>
    /// </summary>
    [Collection("AdapterFileSystem")]
    public sealed class ConsoleMetricsExportTests : TempDirectoryTest
    {
        /// <summary>固定起始时刻：2026-11-20 13:00 UTC。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 13, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------- 表头契约

        [Fact]
        public void CsvHeader_MatchesCoreDefinitionAndColumnCount()
        {
            // 适配层若自己抄一份表头字面量，Core 加列后两边就会悄悄分叉。
            Assert.Equal(PlayerMetrics.CsvHeader, ConsoleMetricsExport.CsvHeader);
            Assert.Equal(PlayerMetricsSnapshot.CsvColumnCount, ConsoleMetricsExport.ColumnCount);
            Assert.Equal(ConsoleMetricsExport.ColumnCount, ConsoleMetricsExport.CsvHeader.Split(',').Length);
        }

        // ---------------------------------------------------------------- 内存写入

        [Fact]
        public void Write_ProducesHeaderPlusOneDataRow()
        {
            var metrics = CreateMetrics();
            var writer = new StringWriter();

            int rows = ConsoleMetricsExport.Write(metrics, writer);

            string[] lines = SplitLines(writer.ToString());
            Assert.Equal(1, rows);
            Assert.Equal(2, lines.Length);
            Assert.Equal(ConsoleMetricsExport.CsvHeader, lines[0]);
            Assert.Equal(ConsoleMetricsExport.ColumnCount, lines[1].Split(',').Length);
        }

        [Fact]
        public void Write_ReflectsMetricsState()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);
            metrics.BeginSession();
            time.AdvanceSeconds(95);
            metrics.EndSession();

            var writer = new StringWriter();
            ConsoleMetricsExport.Write(metrics, writer);

            string[] fields = SplitLines(writer.ToString())[1].Split(',');
            Assert.Equal("2026-11-20", fields[0]);
            Assert.Equal("1", fields[1]);
            Assert.Equal("95", fields[2]);
        }

        [Fact]
        public void Write_NullMetrics_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ConsoleMetricsExport.Write(null!, new StringWriter()));
        }

        [Fact]
        public void Write_NullWriter_Throws()
        {
            var metrics = CreateMetrics();

            Assert.Throws<ArgumentNullException>(() => ConsoleMetricsExport.Write(metrics, null!));
        }

        [Fact]
        public void Append_WritesOnlyTheDataRow()
        {
            var metrics = CreateMetrics();
            var writer = new StringWriter();

            int rows = ConsoleMetricsExport.Append(metrics, writer);

            string text = writer.ToString();
            Assert.Equal(1, rows);
            Assert.DoesNotContain(ConsoleMetricsExport.CsvHeader, text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);

            string[] lines = SplitLines(text);
            Assert.Single(lines);
            Assert.Equal("2026-11-20", lines[0].Split(',')[0]);
        }

        [Fact]
        public void Append_NullWriter_Throws()
        {
            var metrics = CreateMetrics();

            Assert.Throws<ArgumentNullException>(() => ConsoleMetricsExport.Append(metrics, null!));
        }

        // ---------------------------------------------------------------- 文件导出

        [Fact]
        public void ExportToFile_CreatesFileWithHeaderAndRow_WithoutBom()
        {
            string path = Path.Combine(RootPath, "metrics.csv");
            var metrics = CreateMetrics();

            int rows = ConsoleMetricsExport.ExportToFile(metrics, path, append: false);

            Assert.True(File.Exists(path));
            Assert.Equal(1, rows);
            Assert.EndsWith("\n", File.ReadAllText(path), StringComparison.Ordinal);

            byte[] bytes = File.ReadAllBytes(path);
            // 无 BOM：BOM 会让 pandas / Java 导入器把第一列读成 "\ufeffdateUtc"，整张表列名对不上。
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal((byte)'d', bytes[0]);

            string[] lines = SplitLines(File.ReadAllText(path));
            Assert.Equal(2, lines.Length);
            Assert.Equal(ConsoleMetricsExport.CsvHeader, lines[0]);
        }

        [Fact]
        public void ExportToFile_SecondCall_AppendsWithoutDuplicatingHeader()
        {
            string path = Path.Combine(RootPath, "metrics.csv");
            var metrics = CreateMetrics();

            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);
            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);

            string[] lines = SplitLines(File.ReadAllText(path));
            Assert.Equal(3, lines.Length);
            Assert.Equal(ConsoleMetricsExport.CsvHeader, lines[0]);

            // 数据行不许再出现表头（否则导入脚本会把表头当成一行数据）。
            Assert.NotEqual(ConsoleMetricsExport.CsvHeader, lines[1]);
            Assert.NotEqual(ConsoleMetricsExport.CsvHeader, lines[2]);
        }

        [Fact]
        public void ExportToFile_AppendFalse_TruncatesPreviousContent()
        {
            string path = Path.Combine(RootPath, "metrics.csv");
            var metrics = CreateMetrics();

            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);
            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);
            ConsoleMetricsExport.ExportToFile(metrics, path, append: false);

            // 覆盖写 = "重建一份快照"：旧行必须消失，否则同一时间段的数字会被重复统计。
            Assert.Equal(2, SplitLines(File.ReadAllText(path)).Length);
        }

        [Fact]
        public void ExportToFile_CreatesMissingDirectories()
        {
            string path = Path.Combine(RootPath, "reports", "2026", "11", "metrics.csv");
            var metrics = CreateMetrics();

            ConsoleMetricsExport.ExportToFile(metrics, path, append: false);

            // 首次导出常常发生在新建目录之前（Console 宿主、CI 产物目录），导出不能因此失败。
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void ExportToFile_ExistingEmptyFile_GetsHeaderBeforeRow()
        {
            string path = Path.Combine(RootPath, "metrics.csv");
            File.WriteAllText(path, string.Empty);

            ConsoleMetricsExport.ExportToFile(CreateMetrics(), path, append: true);

            string[] lines = SplitLines(File.ReadAllText(path));
            Assert.Equal(2, lines.Length);
            Assert.Equal(ConsoleMetricsExport.CsvHeader, lines[0]);
        }

        [Fact]
        public void ExportToFile_FileWithoutTrailingNewline_DoesNotGlueRowsTogether()
        {
            string path = Path.Combine(RootPath, "metrics.csv");

            // 模拟"上一次写入被强杀/被编辑器保存成无尾换行"的文件：表头 + 一行，末尾无 '\n'。
            File.WriteAllText(
                path,
                ConsoleMetricsExport.CsvHeader + "\n2026-11-19,0,0,0,0,0,0,0,0,0,0,0");

            ConsoleMetricsExport.ExportToFile(CreateMetrics(), path, append: true);

            Assert.True(
                ConsoleMetricsExport.TryReadRows(path, out List<string[]> rows, out string error),
                error);

            // 若没补分隔符，新行会粘在上一行尾部，两行数据合成一行 24 列的垃圾。
            Assert.Equal(2, rows.Count);
            Assert.Equal("2026-11-19", rows[0][0]);
            Assert.Equal("2026-11-20", rows[1][0]);
        }

        [Fact]
        public void ExportToFile_NullMetrics_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => ConsoleMetricsExport.ExportToFile(null!, Path.Combine(RootPath, "m.csv")));
        }

        [Fact]
        public void ExportToFile_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => ConsoleMetricsExport.ExportToFile(CreateMetrics(), null!));
        }

        [Fact]
        public void ExportToFile_BlankPath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => ConsoleMetricsExport.ExportToFile(CreateMetrics(), "   "));
        }

        [Fact]
        public void ExportToFile_TwoDays_ProducesTwoRowsWithTheirOwnDates()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);
            string path = Path.Combine(RootPath, "metrics.csv");

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.Purchase, 900f, 10f, 0f, null));
            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);

            time.AdvanceDays(1);
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.Purchase, 900f, 15f, 0f, null));
            ConsoleMetricsExport.ExportToFile(metrics, path, append: true);

            Assert.True(ConsoleMetricsExport.TryReadRows(path, out List<string[]> rows, out string error), error);
            Assert.Equal(2, rows.Count);
            Assert.Equal("2026-11-20", rows[0][0]);
            Assert.Equal("2026-11-21", rows[1][0]);

            // 累计列只增（10 + 15），日期列各自是导出当天的日期。
            Assert.Equal("10", rows[0][7]);
            Assert.Equal("25", rows[1][7]);
        }

        // ---------------------------------------------------------------- 读回校验

        [Fact]
        public void TryReadRows_RoundTripsEveryColumn()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);
            metrics.BeginSession();
            time.AdvanceSeconds(120);
            metrics.EndSession();
            metrics.MarkActive();
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.Purchase, 900f, 40f, 0f, null));
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));

            string path = Path.Combine(RootPath, "metrics.csv");
            ConsoleMetricsExport.ExportToFile(metrics, path, append: false);

            Assert.True(ConsoleMetricsExport.TryReadRows(path, out List<string[]> rows, out string error), error);
            Assert.Single(rows);

            string[] fields = rows[0];
            Assert.Equal(ConsoleMetricsExport.ColumnCount, fields.Length);
            Assert.Equal("2026-11-20", fields[0]);
            Assert.Equal("1", fields[1]);   // sessions
            Assert.Equal("120", fields[2]); // playSeconds
            Assert.Equal("1", fields[5]);   // purchases
            Assert.Equal("1", fields[6]);   // adsWatched
            Assert.Equal("40", fields[7]);  // softSpent
            Assert.Equal("1", fields[10]);  // dailyActiveDays
        }

        [Fact]
        public void TryReadRows_AcceptsCrlfEditedFile()
        {
            string path = Path.Combine(RootPath, "metrics-crlf.csv");

            // 运营用 Excel/记事本另存后行尾会变成 CRLF：读回必须归一化，否则第一列末尾粘 '\r'。
            File.WriteAllText(
                path,
                ConsoleMetricsExport.CsvHeader + "\r\n2026-11-20,1,2,3,4,5,6,7,8,9,10,11\r\n");

            Assert.True(ConsoleMetricsExport.TryReadRows(path, out List<string[]> rows, out string error), error);
            Assert.Single(rows);
            Assert.Equal("dateUtc", ConsoleMetricsExport.CsvHeader.Split(',')[0]);
            Assert.Equal("2026-11-20", rows[0][0]);
            Assert.Equal("11", rows[0][11]);
        }

        [Fact]
        public void TryReadRows_WrongHeader_ReturnsFalse()
        {
            string path = Path.Combine(RootPath, "bad-header.csv");
            File.WriteAllText(path, "date,sessions\n2026-11-20,1\n");

            Assert.False(ConsoleMetricsExport.TryReadRows(path, out _, out string error));
            Assert.Contains("表头", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryReadRows_WrongColumnCount_ReturnsFalse()
        {
            string path = Path.Combine(RootPath, "short-row.csv");
            File.WriteAllText(
                path,
                ConsoleMetricsExport.CsvHeader + "\n2026-11-20,1,2,3,4,5,6,7,8,9,10\n");

            Assert.False(ConsoleMetricsExport.TryReadRows(path, out _, out string error));
            Assert.Contains("列数", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryReadRows_NonIntegerColumn_ReturnsFalse()
        {
            string path = Path.Combine(RootPath, "decimal.csv");

            // 小数点/千分位是"区域设置漏进导出"的典型症状：读回必须当格式错误拒绝。
            File.WriteAllText(
                path,
                ConsoleMetricsExport.CsvHeader + "\n2026-11-20,1,2.5,3,4,5,6,7,8,9,10,11\n");

            Assert.False(ConsoleMetricsExport.TryReadRows(path, out _, out string error));
            Assert.Contains("不是整数", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryReadRows_HeaderOnly_ReturnsFalse()
        {
            string path = Path.Combine(RootPath, "empty.csv");
            File.WriteAllText(path, ConsoleMetricsExport.CsvHeader + "\n");

            Assert.False(ConsoleMetricsExport.TryReadRows(path, out _, out string error));
            Assert.Contains("没有数据行", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryReadRows_MissingFile_ReturnsFalse()
        {
            string path = Path.Combine(RootPath, "nope.csv");

            Assert.False(ConsoleMetricsExport.TryReadRows(path, out _, out string error));
            Assert.Contains("不存在", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryReadRows_NullPath_ReturnsFalse()
        {
            Assert.False(ConsoleMetricsExport.TryReadRows(null!, out _, out string error));
            Assert.Contains("null", error, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>构造一个带固定日期的空指标聚合器。</summary>
        private static PlayerMetrics CreateMetrics()
        {
            return new PlayerMetrics(new FakeTimeProvider(StartUtc));
        }

        /// <summary>拆行并去掉末尾空行（导出文本以 '\n' 结尾）。</summary>
        private static string[] SplitLines(string text)
        {
            return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>可拨动的假时间源：本类只需要 <see cref="UtcNow"/>（日期列）与 <see cref="ElapsedSeconds"/>（会话时长）。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime startUtc)
            {
                _utcNow = startUtc;
            }

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            /// <summary>同时推进两个时钟（正常流逝的时间）。</summary>
            public void AdvanceSeconds(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }

            /// <summary>推进整天：跨天追加写用例的时间入口。</summary>
            public void AdvanceDays(int days)
            {
                AdvanceSeconds(days * 86400d);
            }
        }
    }
}

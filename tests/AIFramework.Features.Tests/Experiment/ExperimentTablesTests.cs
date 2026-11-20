using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.Experiment;
using Xunit;

namespace AIFramework.Features.Tests.Experiment
{
    /// <summary>
    /// 实验子表解析验收：合法字节逐字段回读；截断 / 尾部残留 / 流量越界 / enabled 非 0/1 /
    /// key 为空或重复 / experimentId 非正或重复一律显式抛 <see cref="InvalidDataException"/>；
    /// 0 行是合法配置；表缺失抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public sealed class ExperimentTablesTests
    {
        private static readonly ExperimentRowSpec[] ThreeRows =
        {
            new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 50, 1),
            new ExperimentRowSpec(2, "balance.gold_rate", 1, 2, 30, 1),
            new ExperimentRowSpec(3, "feature.battle_speed", 1, 2, 20, 0),
        };

        [Fact]
        public void Parse_ThreeRows_ExposesAllFieldsInRowOrder()
        {
            var tables = new ExperimentTables(ExperimentTableStub.WithRows(ThreeRows));

            Assert.Equal(3, tables.Count);

            ExperimentDef first = tables.Get(0);
            Assert.Equal(1, first.ExperimentId);
            Assert.Equal("ui.new_shop", first.Key);
            Assert.Equal(0, first.VariantA);
            Assert.Equal(1, first.VariantB);
            Assert.Equal(50, first.TrafficPercent);
            Assert.True(first.Enabled);

            // 行顺序 = 配置表顺序：解析器不排序、不按 id 重排。
            ExperimentDef second = tables.Get(1);
            Assert.Equal(2, second.ExperimentId);
            Assert.Equal("balance.gold_rate", second.Key);
            Assert.Equal(30, second.TrafficPercent);

            // enabled=0 必须在解析期折成 false（而不是留到使用方自己判 0/1）。
            ExperimentDef third = tables.Get(2);
            Assert.Equal(3, third.ExperimentId);
            Assert.Equal(20, third.TrafficPercent);
            Assert.False(third.Enabled);
        }

        [Fact]
        public void Parse_TrafficBoundariesZeroAndHundred_AreAccepted()
        {
            var tables = new ExperimentTables(ExperimentTableStub.WithRows(
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 0, 1),
                new ExperimentRowSpec(2, "balance.gold_rate", 1, 2, 100, 1)));

            Assert.Equal(0, tables.Get(0).TrafficPercent);
            Assert.Equal(100, tables.Get(1).TrafficPercent);
        }

        [Fact]
        public void Parse_EmptyTable_IsAllowed()
        {
            var tables = new ExperimentTables(ExperimentTableStub.WithRows());

            Assert.Equal(0, tables.Count);
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ExperimentTables(null!));
        }

        [Fact]
        public void Parse_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new ExperimentTables(ExperimentTableStub.WithoutTable()));

            Assert.Contains(ExperimentTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Get_OutOfRange_Throws()
        {
            var tables = new ExperimentTables(ExperimentTableStub.WithRows(ThreeRows));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(3));
        }

        [Fact]
        public void Parse_NegativeRowCount_Throws()
        {
            byte[] payload = { 0xFF, 0xFF, 0xFF, 0xFF };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("rowCount 为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_HeaderShorterThanFourBytes_Throws()
        {
            byte[] payload = { 1, 0 };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("头部长度不足", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DeclaredRowsExceedBytes_ThrowsTruncated()
        {
            // 头部声明 2 行，物理上只留 30 字节：应在分配前按"行数 × 最小行宽"挡住。
            byte[] payload = ExperimentTableStub.BuildPayload(ThreeRows, declaredRowCount: 2);
            Array.Resize(ref payload, 4 + 30);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TruncatedMidRow_Throws()
        {
            // 声明 1 行且剩余字节数过了前置校验（≥ 24），但读到 variantB 就没了：字段级越界检查兜底。
            byte[] payload = ExperimentTableStub.BuildPayload(ThreeRows, declaredRowCount: 1);
            Array.Resize(ref payload, 4 + 25);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrailingBytes_Throws()
        {
            byte[] payload = ExperimentTableStub.AppendTrailingBytes(
                ExperimentTableStub.BuildPayload(ThreeRows), 4);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("尾部残留", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_KeyLengthNegative_Throws()
        {
            // [rowCount=1][experimentId=1][keyLen=-1] + 补齐到最小行宽（否则会先被"行数 × 行宽"前置校验挡住）。
            byte[] payload = ExperimentTableStub.BuildRaw(1, 1, -1, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("长度为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_KeyDeclaredLongerThanPayload_Throws()
        {
            // [rowCount=1][experimentId=1][keyLen=999]：声明 999 字节 key，实际可读字节远不够。
            byte[] payload = ExperimentTableStub.BuildRaw(1, 1, 999, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithPayload(payload)));

            Assert.Contains("key", exception.Message, StringComparison.Ordinal);
            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrafficPercentOutOfRange_Throws()
        {
            var tooMuch = new[]
            {
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 101, 1),
            };

            InvalidDataException upper = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(tooMuch)));
            Assert.Contains("trafficPercent", upper.Message, StringComparison.Ordinal);

            var negative = new[]
            {
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, -1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(negative)));
        }

        [Fact]
        public void Parse_EnabledNotZeroOrOne_Throws()
        {
            var two = new[]
            {
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 50, 2),
            };

            InvalidDataException upper = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(two)));
            Assert.Contains("enabled 只能是 0/1", upper.Message, StringComparison.Ordinal);

            var negative = new[]
            {
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 50, -1),
            };

            Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(negative)));
        }

        [Fact]
        public void Parse_EmptyKey_Throws()
        {
            var emptyKey = new[]
            {
                new ExperimentRowSpec(1, string.Empty, 0, 1, 50, 1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(emptyKey)));

            Assert.Contains("key 不能为空", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DuplicateKey_Throws()
        {
            var duplicates = new[]
            {
                new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 50, 1),
                new ExperimentRowSpec(2, "ui.new_shop", 0, 1, 50, 1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(duplicates)));

            Assert.Contains("key 重复：ui.new_shop", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DuplicateExperimentId_Throws()
        {
            var duplicates = new[]
            {
                new ExperimentRowSpec(7, "ui.new_shop", 0, 1, 50, 1),
                new ExperimentRowSpec(7, "balance.gold_rate", 1, 2, 30, 1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(duplicates)));

            Assert.Contains("experimentId 重复：7", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_ExperimentIdNonPositive_Throws()
        {
            var zeroId = new[]
            {
                new ExperimentRowSpec(0, "ui.new_shop", 0, 1, 50, 1),
            };

            InvalidDataException zero = Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(zeroId)));
            Assert.Contains("experimentId 必须为正", zero.Message, StringComparison.Ordinal);

            // 负 id 同样不可用：主键必须能直接进曝光事件与埋点看板。
            var negativeId = new[]
            {
                new ExperimentRowSpec(-7, "ui.new_shop", 0, 1, 50, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new ExperimentTables(ExperimentTableStub.WithRows(negativeId)));
        }

        /// <summary>
        /// 端到端守卫：直接解析仓库已导出的 <c>TbExperiment.bin</c>，防止"tablegen 列顺序"与
        /// "切片解析器列顺序"各改各的。定长部分 4 字节表头 + 6 行（每行 24 字节 + key 字节）。
        /// </summary>
        [Fact]
        public void Parse_CommittedRepoBinary_MatchesSixRowContract()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(
                repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables", "TbExperiment.bin");
            byte[] payload = File.ReadAllBytes(path);

            var tables = new ExperimentTables(ExperimentTableStub.WithPayload(payload));

            // 4 + 6×24 + (11 + 17 + 20 + 19 + 14 + 23) = 4 + 144 + 104 = 252。
            Assert.Equal(252, payload.Length);
            Assert.Equal(6, tables.Count);

            ExperimentDef first = tables.Get(0);
            Assert.Equal(1, first.ExperimentId);
            Assert.Equal("ui.new_shop", first.Key);
            Assert.Equal(0, first.VariantA);
            Assert.Equal(1, first.VariantB);
            Assert.Equal(50, first.TrafficPercent);
            Assert.True(first.Enabled);

            // 仓库表刻意覆盖 0% 与 100% 两个边界：0% 全 A（对照组），100% 且 enabled=0（下线后整组回退 A）。
            ExperimentDef zeroTraffic = tables.Get(4);
            Assert.Equal("ui.hud_compact", zeroTraffic.Key);
            Assert.Equal(0, zeroTraffic.TrafficPercent);
            Assert.True(zeroTraffic.Enabled);

            ExperimentDef last = tables.Get(5);
            Assert.Equal(6, last.ExperimentId);
            Assert.Equal("event.anniversary_theme", last.Key);
            Assert.Equal(100, last.TrafficPercent);
            Assert.False(last.Enabled);

            for (int i = 0; i < tables.Count; i++)
            {
                ExperimentDef row = tables.Get(i);
                Assert.True(row.Key.Length > 0, "experimentId " + row.ExperimentId + " key 为空。");
                Assert.InRange(row.TrafficPercent, 0, 100);
            }
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.Features.Tests/Experiment/ExperimentTablesTests.cs 向上三级
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

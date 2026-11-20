using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.SeasonPass;
using Xunit;

namespace AIFramework.Features.Tests.SeasonPass
{
    /// <summary>
    /// 通行证子表解析验收：合法字节逐字段回读；截断/残留/等级不连续/门槛不递增/奖励列非法
    /// 一律显式抛 <see cref="InvalidDataException"/>；0 行是合法配置。
    /// </summary>
    public sealed class SeasonPassTablesTests
    {
        private static readonly SeasonPassRowSpec[] ThreeRows =
        {
            new SeasonPassRowSpec(1, 0, 7501, 10, 7502, 1),
            new SeasonPassRowSpec(2, 100, 7501, 20, 0, 0),
            new SeasonPassRowSpec(3, 250, 7501, 30, 7502, 1),
        };

        [Fact]
        public void Parse_ThreeRows_ExposesAllFieldsInOrder()
        {
            var tables = new SeasonPassTables(SeasonPassTableStub.WithPayload(
                SeasonPassTableStub.BuildPayload(ThreeRows)));

            Assert.Equal(3, tables.Count);

            SeasonPassDef first = tables.Get(0);
            Assert.Equal(1, first.Level);
            Assert.Equal(0, first.RequiredXp);
            Assert.Equal(7501, first.FreeItemId);
            Assert.Equal(10, first.FreeCount);
            Assert.Equal(7502, first.PaidItemId);
            Assert.Equal(1, first.PaidCount);

            SeasonPassDef second = tables.Get(1);
            Assert.Equal(2, second.Level);
            Assert.Equal(100, second.RequiredXp);
            Assert.Equal(20, second.FreeCount);
            // 付费列整对为 0：该级无付费奖励，是合法配置而不是错误。
            Assert.Equal(0, second.PaidItemId);
            Assert.Equal(0, second.PaidCount);

            SeasonPassDef third = tables.Get(2);
            Assert.Equal(3, third.Level);
            Assert.Equal(250, third.RequiredXp);
            Assert.Equal(30, third.FreeCount);
            Assert.Equal(7502, third.PaidItemId);
        }

        [Fact]
        public void Parse_EmptyTable_IsAllowed()
        {
            var tables = new SeasonPassTables(SeasonPassTableStub.WithRows());

            Assert.Equal(0, tables.Count);
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SeasonPassTables(null!));
        }

        [Fact]
        public void Parse_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithoutTable()));

            Assert.Contains(SeasonPassTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Get_OutOfRange_Throws()
        {
            var tables = new SeasonPassTables(SeasonPassTableStub.WithRows(ThreeRows));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(3));
        }

        [Fact]
        public void Parse_NegativeRowCount_Throws()
        {
            byte[] payload = { 0xFF, 0xFF, 0xFF, 0xFF };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithPayload(payload)));

            Assert.Contains("rowCount 为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_HeaderShorterThanFourBytes_Throws()
        {
            byte[] payload = { 1, 0 };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithPayload(payload)));

            Assert.Contains("头部长度不足", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DeclaredRowsExceedBytes_ThrowsTruncated()
        {
            // 头部声明 2 行，但物理上只写了 1 行：定长表应该在分配前挡住。
            byte[] payload = SeasonPassTableStub.BuildPayload(ThreeRows, declaredRowCount: 2);
            Array.Resize(ref payload, 4 + 24);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrailingBytes_Throws()
        {
            byte[] payload = SeasonPassTableStub.AppendTrailingBytes(
                SeasonPassTableStub.BuildPayload(ThreeRows), 4);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithPayload(payload)));

            Assert.Contains("尾部残留", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_LevelMustStartAtOne_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(2, 0, 7501, 10, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("level", exception.Message, StringComparison.Ordinal);
            Assert.Contains("第 0 行", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_LevelMustBeContiguous_Throws()
        {
            // 1 级后直接跳到 3 级：等级 = 行号 + 1 的寻址契约被破坏。
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 0, 0),
                new SeasonPassRowSpec(3, 100, 7501, 20, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("连续递增", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_FirstLevelRequiredXpMustBeZero_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 50, 7501, 10, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("1 级 requiredXp 必须为 0", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_RequiredXpMustStrictlyIncrease_Throws()
        {
            // 累计经验口径：相等意味着两级门槛重合，等级只能是其中一个，配置必须拒绝。
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 0, 0),
                new SeasonPassRowSpec(2, 100, 7501, 20, 0, 0),
                new SeasonPassRowSpec(3, 100, 7501, 30, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("严格递增", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_FreeItemIdNonPositive_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 0, 10, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("freeItemId", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_FreeCountNonPositive_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 0, 0, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("freeCount", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_PaidItemIdNegative_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, -1, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("paidItemId 不能为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_PaidCountNegative_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 7502, -1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("paidCount 不能为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NoPaidRewardWithNonZeroCount_Throws()
        {
            // 有数量没 ID：付费列只置 0 一半，按配置错误挡掉，避免"领了但发 0 个道具"。
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 0, 5),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("paidCount 必须为 0", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_PaidRewardWithZeroCount_Throws()
        {
            var rows = new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 7502, 0),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithRows(rows)));

            Assert.Contains("paidCount 为 0", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TruncatedMidRow_Throws()
        {
            // 头部声明 1 行，但物理字节只有 10 字节（不足一整行 24 字节）。
            byte[] payload = SeasonPassTableStub.BuildPayload(ThreeRows, declaredRowCount: 1);
            Array.Resize(ref payload, 14);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new SeasonPassTables(SeasonPassTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 端到端守卫：直接解析仓库已导出的 <c>TbSeasonPass.bin</c>，防止"tablegen 列顺序"与"切片解析器列顺序"各改各的。
        /// 与 <c>TableGenTests</c> 的字节金标准互补：那边验 JSON→字节，这边验字节→切片结构。
        /// </summary>
        [Fact]
        public void Parse_CommittedRepoBinary_MatchesThirtyLevelContract()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(
                repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables", "TbSeasonPass.bin");
            byte[] payload = File.ReadAllBytes(path);

            var tables = new SeasonPassTables(SeasonPassTableStub.WithPayload(payload));

            // 定长布局：4 字节表头 + 30 行 × 24 字节。
            Assert.Equal(724, payload.Length);
            Assert.Equal(30, tables.Count);

            SeasonPassDef first = tables.Get(0);
            Assert.Equal(1, first.Level);
            Assert.Equal(0, first.RequiredXp);
            Assert.Equal(7501, first.FreeItemId);
            Assert.Equal(10, first.FreeCount);
            Assert.Equal(7502, first.PaidItemId);

            SeasonPassDef last = tables.Get(29);
            Assert.Equal(30, last.Level);
            Assert.Equal(11020, last.RequiredXp);

            for (int i = 1; i < tables.Count; i++)
            {
                Assert.True(
                    tables.Get(i).RequiredXp > tables.Get(i - 1).RequiredXp,
                    "level " + tables.Get(i).Level + " 的 requiredXp 未严格递增。");
            }

            // 付费列整对为 0 的等级：5/10/15/20/25/30（1 基 → 0 基下标 4/9/14/19/24/29）。
            Assert.Equal(0, tables.Get(4).PaidItemId);
            Assert.Equal(0, tables.Get(4).PaidCount);
            Assert.Equal(0, tables.Get(9).PaidItemId);
            Assert.Equal(0, tables.Get(14).PaidItemId);
            Assert.Equal(0, tables.Get(19).PaidItemId);
            Assert.Equal(0, tables.Get(24).PaidItemId);
            Assert.Equal(0, tables.Get(29).PaidItemId);
            Assert.True(tables.Get(5).PaidItemId > 0);
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.Features.Tests/SeasonPass/SeasonPassTablesTests.cs 向上三级
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

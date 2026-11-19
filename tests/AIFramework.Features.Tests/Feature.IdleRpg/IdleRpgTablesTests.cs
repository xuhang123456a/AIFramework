using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Features.IdleRpg;
using Xunit;

namespace AIFramework.Features.Tests.IdleRpg
{
    /// <summary>
    /// <c>TbIdleStage</c> 子表加载与校验验收：合法夹具逐行对齐 CONTRACT §3，
    /// 每一类畸形配置都必须抛 <see cref="InvalidDataException"/>，而"表不存在"必须是
    /// <see cref="InvalidOperationException"/>——两者绝不混用，也不允许静默当空表。
    /// </summary>
    public sealed class IdleRpgTablesTests
    {
        [Fact]
        public void LoadsFixtureRowsInOrderAndOnlyAsksForItsOwnTable()
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows());

            IdleRpgTables tables = new IdleRpgTables(loader);

            Assert.Equal(5, tables.Count);
            Assert.Single(loader.RequestedTables);
            Assert.Equal(IdleRpgLoaderStub.TableName, loader.RequestedTables[0]);

            IdleStageDef first = tables.Get(0);
            Assert.Equal(1, first.StageId);
            Assert.Equal(30, first.EnemyHp);
            Assert.Equal(2, first.EnemyAttack);
            Assert.Equal(20, first.GoldReward);
            Assert.Equal(10, first.RequiredPower);

            IdleStageDef last = tables.Get(4);
            Assert.Equal(5, last.StageId);
            Assert.Equal(480, last.EnemyHp);
            Assert.Equal(12, last.EnemyAttack);
            Assert.Equal(320, last.GoldReward);
            Assert.Equal(70, last.RequiredPower);
        }

        [Fact]
        public void TryFind_MatchesByStageIdAndReportsMissWithoutThrowing()
        {
            IdleRpgTables tables = new IdleRpgTables(
                IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows()));

            IdleStageDef found;
            Assert.True(tables.TryFind(3, out found));
            Assert.Equal(3, found.StageId);
            Assert.Equal(120, found.EnemyHp);

            IdleStageDef missing;
            Assert.False(tables.TryFind(0, out missing));
            Assert.Equal(0, missing.StageId);
            Assert.False(tables.TryFind(6, out missing));
            Assert.False(tables.TryFind(-1, out missing));
        }

        [Fact]
        public void Get_OutOfRangeIndex_ThrowsArgumentOutOfRange()
        {
            IdleRpgTables tables = new IdleRpgTables(
                IdleRpgLoaderStub.WithRows(IdleRpgHarness.DefaultRows()));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(5));
        }

        [Fact]
        public void MissingTable_ThrowsInvalidOperation()
        {
            Assert.Throws<InvalidOperationException>(() => new IdleRpgTables(IdleRpgLoaderStub.Missing()));
        }

        [Fact]
        public void PayloadShorterThanHeader_ThrowsInvalidData()
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.Missing().AddTable(new byte[3]);

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void TruncatedPayload_ThrowsInvalidData()
        {
            // 头部声明 2 行，实际只写了 1 行（24 字节 ≠ 4 + 2 × 20）。
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.Missing().AddTable(
                IdleRpgLoaderStub.BuildPayloadDeclaringRows(
                    2,
                    new IdleStageRow(1, 30, 2, 20, 10)));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void OverlongPayload_ThrowsInvalidData()
        {
            // 头部声明 1 行，实际写了 2 行。
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.Missing().AddTable(
                IdleRpgLoaderStub.BuildPayloadDeclaringRows(
                    1,
                    new IdleStageRow(1, 30, 2, 20, 10),
                    new IdleStageRow(2, 60, 4, 40, 16)));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void NegativeDeclaredRowCount_ThrowsInvalidData()
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.Missing().AddTable(
                IdleRpgLoaderStub.BuildRawPayload(-1));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void DuplicateStageId_ThrowsInvalidData()
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.WithRows(
                new IdleStageRow(1, 30, 2, 20, 10),
                new IdleStageRow(1, 60, 4, 40, 16));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void NonContiguousStageId_ThrowsInvalidData()
        {
            // 跳号：第 2 行必须是 2，实际是 3。
            IdleRpgLoaderStub gap = IdleRpgLoaderStub.WithRows(
                new IdleStageRow(1, 30, 2, 20, 10),
                new IdleStageRow(3, 60, 4, 40, 16));
            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(gap));

            // 不从 1 起：第 1 行就是 2。
            IdleRpgLoaderStub offset = IdleRpgLoaderStub.WithRows(
                new IdleStageRow(2, 30, 2, 20, 10));
            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(offset));
        }

        [Theory]
        [InlineData(0, 2, 20, 10)]
        [InlineData(-5, 2, 20, 10)]
        [InlineData(30, -1, 20, 10)]
        [InlineData(30, 2, -1, 10)]
        [InlineData(30, 2, 20, -1)]
        public void IllegalRowValues_ThrowInvalidData(int enemyHp, int enemyAttack, int goldReward, int requiredPower)
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.WithRows(
                new IdleStageRow(1, enemyHp, enemyAttack, goldReward, requiredPower));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }

        [Fact]
        public void ZeroRowTable_IsLegalConfiguration()
        {
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.WithRows();

            IdleRpgTables tables = new IdleRpgTables(loader);

            Assert.Equal(0, tables.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(0));

            IdleStageDef any;
            Assert.False(tables.TryFind(1, out any));
        }

        [Fact]
        public void ZeroDeclaredRowsWithTrailingBytes_ThrowsInvalidData()
        {
            // 声明 0 行却多出 1 行字节：要么是导出脚本坏了，要么是截断，都不能当合法空表。
            IdleRpgLoaderStub loader = IdleRpgLoaderStub.Missing().AddTable(
                IdleRpgLoaderStub.BuildRawPayload(0, 1, 30, 2, 20, 10));

            Assert.Throws<InvalidDataException>(() => new IdleRpgTables(loader));
        }
    }
}

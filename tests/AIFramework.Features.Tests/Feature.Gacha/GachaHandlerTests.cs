using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.Gacha;
using Xunit;

namespace AIFramework.Features.Tests.Gacha
{
    /// <summary>
    /// 抽卡切片验收：真实 <see cref="CqeBus"/> + 本目录私有桩（可控随机源 / 子表字节夹具 / 背包与钱包桩），
    /// 断言只落在"切片发出了什么消息、按什么顺序"与"处理器状态（保底计数 / 最近结果）"两处。
    /// </summary>
    public sealed class GachaHandlerTests
    {
        // ---------- 费用与正常路径 ----------

        [Fact]
        public void SingleDraw_Consumes100GoldGrantsOneItemAndPublishesEvent()
        {
            SequenceRandomSource random = new SequenceRandomSource(0);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 500);

            // 构造期不发总线（装配层可能还没挂上背包处理器）。
            Assert.Empty(harness.Messages);
            Assert.Null(harness.Handler.LastResult);

            harness.Draw(1);

            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(MetaItemIds.Gold, harness.Inventory.QueriedItemIds[0]);
            Assert.Equal(400, harness.Inventory.GetCount(MetaItemIds.Gold));

            ConsumeItemCommand consume = Assert.Single(harness.MessagesOf<ConsumeItemCommand>());
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(GachaFixture.SingleDrawCost, consume.Count);
            Assert.Equal(GachaFixture.ConsumeReasonTagId, consume.ReasonTagId);

            GrantItemCommand grant = Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Equal(GachaFixture.CommonItemId, grant.ItemId);
            Assert.Equal(1, grant.Count);
            Assert.Equal(GachaFixture.GrantReasonTagId, grant.ReasonTagId);

            GachaDrawnEvent drawn = Assert.Single(harness.MessagesOf<GachaDrawnEvent>());
            Assert.Equal(GachaFixture.CommonItemId, drawn.ItemId);
            Assert.False(drawn.IsRare);
            Assert.False(drawn.PityTriggered);

            // 顺序契约：先扣费 → 再发货 → 最后广播。
            Assert.Equal(3, harness.Messages.Count);
            Assert.IsType<ConsumeItemCommand>(harness.Messages[0]);
            Assert.IsType<GrantItemCommand>(harness.Messages[1]);
            Assert.IsType<GachaDrawnEvent>(harness.Messages[2]);

            Assert.Equal(1, random.CallCount);
            Assert.Equal(0, random.MinInclusiveValues[0]);
            Assert.Equal(GachaFixture.TotalWeight, random.MaxExclusiveValues[0]);

            GachaResult result = RequireResult(harness.Handler);
            GachaResultItem item = Assert.Single(result.Items);
            Assert.Equal(GachaFixture.CommonItemId, item.ItemId);
            Assert.False(item.IsRare);
            Assert.Equal(0, result.PityBefore);
            Assert.False(result.PityTriggered);
            Assert.Equal(1, harness.Handler.PityCounter);
        }

        [Fact]
        public void TenDraw_Consumes900Gold()
        {
            SequenceRandomSource random = new SequenceRandomSource(0, 0, 0, 99, 0, 0, 0, 0, 0, 0);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 900);

            harness.Draw(10);

            Assert.Equal(0, harness.Inventory.GetCount(MetaItemIds.Gold));

            ConsumeItemCommand consume = Assert.Single(harness.MessagesOf<ConsumeItemCommand>());
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(GachaFixture.TenDrawCost, consume.Count);
            Assert.Equal(GachaFixture.ConsumeReasonTagId, consume.ReasonTagId);

            Assert.Equal(10, harness.MessagesOf<GrantItemCommand>().Count);
            Assert.Equal(10, harness.MessagesOf<GachaDrawnEvent>().Count);
            Assert.Equal(10, random.CallCount);
            Assert.Equal(10, RequireResult(harness.Handler).Items.Length);
        }

        [Fact]
        public void TenDraw_GrantsAndPublishesInSameOrderAsResult()
        {
            SequenceRandomSource random = new SequenceRandomSource(0, 0, 0, 99, 0, 0, 0, 0, 0, 0);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 900);

            harness.Draw(10);

            GachaResult result = RequireResult(harness.Handler);
            List<GrantItemCommand> grants = harness.MessagesOf<GrantItemCommand>();
            List<GachaDrawnEvent> events = harness.MessagesOf<GachaDrawnEvent>();
            Assert.Equal(10, result.Items.Length);
            Assert.Equal(10, grants.Count);
            Assert.Equal(10, events.Count);

            for (int i = 0; i < result.Items.Length; i++)
            {
                Assert.Equal(result.Items[i].ItemId, grants[i].ItemId);
                Assert.Equal(result.Items[i].ItemId, events[i].ItemId);
                Assert.Equal(result.Items[i].IsRare, events[i].IsRare);
                Assert.Equal(1, grants[i].Count);
                Assert.False(events[i].PityTriggered);
            }

            // 第 4 抽自然命中稀有项：结果与发货顺序都必须与随机序列一致。
            Assert.Equal(GachaFixture.RareItemId, grants[3].ItemId);
            Assert.True(events[3].IsRare);

            // 1 条扣费 + 10 条发货 + 10 条事件，且严格按"每抽 Grant → Event"交错。
            Assert.Equal(21, harness.Messages.Count);
            Assert.IsType<ConsumeItemCommand>(harness.Messages[0]);
            for (int i = 0; i < 10; i++)
            {
                Assert.IsType<GrantItemCommand>(harness.Messages[1 + (i * 2)]);
                Assert.IsType<GachaDrawnEvent>(harness.Messages[2 + (i * 2)]);
            }
        }

        // ---------- 静默拒绝 ----------

        [Fact]
        public void SingleDraw_WithInsufficientGold_IsSilentlyRejected()
        {
            SequenceRandomSource random = new SequenceRandomSource();
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, GachaFixture.SingleDrawCost - 1);

            harness.Draw(1);

            // 余额不足是"查过之后才发现"：查询发生一次，但没有扣费、没有随机、没有任何消息。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(GachaFixture.SingleDrawCost - 1, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Empty(harness.Messages);
            Assert.Null(harness.Handler.LastResult);
            Assert.Equal(0, harness.Handler.PityCounter);
            Assert.Equal(0, random.CallCount);
        }

        [Fact]
        public void TenDraw_WithInsufficientGold_IsSilentlyRejected()
        {
            SequenceRandomSource random = new SequenceRandomSource();
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, GachaFixture.TenDrawCost - 1);

            harness.Draw(10);

            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(GachaFixture.TenDrawCost - 1, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Empty(harness.MessagesOf<ConsumeItemCommand>());
            Assert.Empty(harness.MessagesOf<GrantItemCommand>());
            Assert.Empty(harness.MessagesOf<GachaDrawnEvent>());
            Assert.Null(harness.Handler.LastResult);
            Assert.Equal(0, random.CallCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(11)]
        public void InvalidCount_IsSilentlyRejectedWithoutCostOrAnyMessage(int count)
        {
            SequenceRandomSource random = new SequenceRandomSource();
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 10000);

            harness.Draw(count);

            // 非法次数连余额都不该查：不扣费、不取随机、不发指令、不发事件。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Equal(10000, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Empty(harness.Messages);
            Assert.Null(harness.Handler.LastResult);
            Assert.Equal(0, harness.Handler.PityCounter);
            Assert.Equal(0, random.CallCount);
        }

        // ---------- 权重边界 ----------

        [Fact]
        public void WeightRollZero_HitsFirstRow()
        {
            SequenceRandomSource random = new SequenceRandomSource(0);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, GachaFixture.SingleDrawCost);

            harness.Draw(1);

            GachaResultItem item = Assert.Single(RequireResult(harness.Handler).Items);
            Assert.Equal(GachaFixture.CommonItemId, item.ItemId);
            Assert.False(item.IsRare);
            Assert.Equal(0, random.MinInclusiveValues[0]);
            Assert.Equal(GachaFixture.TotalWeight, random.MaxExclusiveValues[0]);
        }

        [Fact]
        public void WeightRollTotalWeightMinusOne_HitsLastRow()
        {
            SequenceRandomSource random = new SequenceRandomSource(GachaFixture.TotalWeight - 1);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, GachaFixture.SingleDrawCost);

            harness.Draw(1);

            GachaResultItem item = Assert.Single(RequireResult(harness.Handler).Items);
            Assert.Equal(GachaFixture.RareItemId, item.ItemId);
            Assert.True(item.IsRare);
            Assert.Equal(GachaFixture.RareItemId, Assert.Single(harness.MessagesOf<GrantItemCommand>()).ItemId);

            // 自然命中稀有：保底计数归零，且不算保底触发。
            Assert.Equal(0, harness.Handler.PityCounter);
            Assert.False(Assert.Single(harness.MessagesOf<GachaDrawnEvent>()).PityTriggered);
        }

        [Fact]
        public void WeightRollAtCumulativeBoundaries_HitsExpectedRows()
        {
            // 累计区间：[0,70) 第一行、[70,90) 第二行、[90,99) 第三行、[99,100) 最后一行。
            SequenceRandomSource random = new SequenceRandomSource(69, 70, 89, 90, 98);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 500);

            harness.Draw(1);
            harness.Draw(1);
            harness.Draw(1);
            harness.Draw(1);
            harness.Draw(1);

            int[] expected =
            {
                GachaFixture.CommonItemId,
                GachaFixture.UncommonItemId,
                GachaFixture.UncommonItemId,
                GachaFixture.UncommonRareItemId,
                GachaFixture.UncommonRareItemId,
            };
            List<GrantItemCommand> grants = harness.MessagesOf<GrantItemCommand>();
            Assert.Equal(expected.Length, grants.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], grants[i].ItemId);
            }
        }

        // ---------- 保底 ----------

        [Fact]
        public void Pity_ForcesRareOnTenthConsecutiveNonRareDraw()
        {
            SequenceRandomSource random = new SequenceRandomSource(new int[11]);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 1000);

            for (int i = 0; i < 9; i++)
            {
                harness.Draw(1);
                GachaResult interim = RequireResult(harness.Handler);
                Assert.Equal(i, interim.PityBefore);
                Assert.False(interim.PityTriggered);
                Assert.Equal(GachaFixture.CommonItemId, Assert.Single(interim.Items).ItemId);
                Assert.Equal(i + 1, harness.Handler.PityCounter);
            }

            harness.Draw(1);

            GachaResult result = RequireResult(harness.Handler);
            Assert.Equal(9, result.PityBefore);
            Assert.True(result.PityTriggered);
            GachaResultItem forced = Assert.Single(result.Items);
            Assert.Equal(GachaFixture.RareItemId, forced.ItemId);
            Assert.True(forced.IsRare);
            Assert.Equal(0, harness.Handler.PityCounter);
            Assert.Equal(0, harness.Inventory.GetCount(MetaItemIds.Gold));

            List<GrantItemCommand> grants = harness.MessagesOf<GrantItemCommand>();
            Assert.Equal(10, grants.Count);
            Assert.Equal(GachaFixture.RareItemId, grants[9].ItemId);

            List<GachaDrawnEvent> events = harness.MessagesOf<GachaDrawnEvent>();
            Assert.Equal(10, events.Count);
            Assert.False(events[8].PityTriggered);
            Assert.True(events[9].PityTriggered);
            Assert.True(events[9].IsRare);

            // 前 10 次是自然权重取整，第 11 次是保底改判时在稀有池 [0, rareCount) 上取整。
            Assert.Equal(11, random.CallCount);
            Assert.Equal(GachaFixture.TotalWeight, random.MaxExclusiveValues[9]);
            Assert.Equal(1, random.MaxExclusiveValues[10]);
        }

        [Fact]
        public void Pity_ResetsCounterWhenRareDrawnNaturally()
        {
            SequenceRandomSource random = new SequenceRandomSource(0, 0, GachaFixture.TotalWeight - 1);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 300);

            harness.Draw(1);
            harness.Draw(1);
            Assert.Equal(2, harness.Handler.PityCounter);

            harness.Draw(1);

            Assert.Equal(0, harness.Handler.PityCounter);
            GachaResult result = RequireResult(harness.Handler);
            Assert.False(result.PityTriggered);
            Assert.Equal(GachaFixture.RareItemId, Assert.Single(result.Items).ItemId);
            Assert.False(harness.MessagesOf<GachaDrawnEvent>()[2].PityTriggered);
        }

        [Fact]
        public void TenDraw_AllNonRare_ForcesRareOnlyOnLastItem()
        {
            SequenceRandomSource random = new SequenceRandomSource(new int[11]);
            GachaHarness harness = GachaHarness.Create(GachaFixture.DefaultLoader(), random, 900);

            harness.Draw(10);

            GachaResult result = RequireResult(harness.Handler);
            Assert.Equal(0, result.PityBefore);
            Assert.True(result.PityTriggered);
            Assert.Equal(10, result.Items.Length);
            for (int i = 0; i < 9; i++)
            {
                Assert.Equal(GachaFixture.CommonItemId, result.Items[i].ItemId);
                Assert.False(result.Items[i].IsRare);
            }

            Assert.Equal(GachaFixture.RareItemId, result.Items[9].ItemId);
            Assert.True(result.Items[9].IsRare);
            Assert.Equal(0, harness.Handler.PityCounter);

            List<GachaDrawnEvent> events = harness.MessagesOf<GachaDrawnEvent>();
            Assert.Equal(10, events.Count);
            for (int i = 0; i < 9; i++)
            {
                Assert.False(events[i].PityTriggered);
            }

            Assert.True(events[9].PityTriggered);
            Assert.Equal(11, random.CallCount);
        }

        [Fact]
        public void Pity_ForcedRarePicksFromRarePoolOnly()
        {
            GachaRowFixture[] rows =
            {
                new GachaRowFixture(30001, 90, false),
                new GachaRowFixture(30002, 5, true),
                new GachaRowFixture(30003, 5, true),
            };
            int[] values = new int[11];
            values[10] = 1;
            SequenceRandomSource random = new SequenceRandomSource(values);
            GachaHarness harness = GachaHarness.Create(GachaLoaderStub.WithRows(rows), random, 900);

            harness.Draw(10);

            GachaResult result = RequireResult(harness.Handler);
            Assert.Equal(30001, result.Items[0].ItemId);

            // 稀有池下标 1 = 子表第 3 行（30003）：保底只在稀有行里取，不会落到非稀有行。
            Assert.Equal(30003, result.Items[9].ItemId);
            Assert.True(result.Items[9].IsRare);
            Assert.Equal(2, random.MaxExclusiveValues[10]);
        }

        // ---------- 子表加载校验 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            GachaLoaderStub loader = GachaLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains(GachaLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new GachaHandler(new CqeBus(), new SequenceRandomSource(), loader);
                });
            Assert.Contains(GachaLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(GachaLoaderStub.TableName, name));
        }

        [Fact]
        public void TableLengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 1 行，实际写了 2 行：长度 28，期望 4 + 1 * 12 = 16。
            byte[] payload = GachaLoaderStub.BuildPayloadDeclaringRows(
                1,
                new GachaRowFixture(GachaFixture.CommonItemId, 50, false),
                new GachaRowFixture(GachaFixture.RareItemId, 50, true));
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaHandler(new CqeBus(), new SequenceRandomSource(), loader);
                });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void MalformedHeader_ThrowsInvalidDataException(int payloadLength)
        {
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(new byte[payloadLength]);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
        }

        [Fact]
        public void NegativeDeclaredRowCount_ThrowsInvalidDataException()
        {
            byte[] payload = GachaLoaderStub.BuildPayloadDeclaringRows(-1);
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
        }

        [Fact]
        public void DuplicateItemId_ThrowsInvalidDataException()
        {
            byte[] payload = GachaLoaderStub.BuildPayload(
                new GachaRowFixture(GachaFixture.CommonItemId, 50, false),
                new GachaRowFixture(GachaFixture.CommonItemId, 50, true));
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }

        [Fact]
        public void NonPositiveWeight_ThrowsInvalidDataException()
        {
            byte[] payload = GachaLoaderStub.BuildPayload(
                new GachaRowFixture(GachaFixture.CommonItemId, 0, false),
                new GachaRowFixture(GachaFixture.RareItemId, 50, true));
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains("weight", exception.Message);
        }

        [Fact]
        public void IllegalIsRareValue_ThrowsInvalidDataException()
        {
            byte[] payload = GachaLoaderStub.BuildPayload(
                new GachaRowFixture(GachaFixture.CommonItemId, 50, 2),
                new GachaRowFixture(GachaFixture.RareItemId, 50, true));
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains("isRare", exception.Message);
        }

        [Fact]
        public void NoRareEntry_ThrowsInvalidDataException()
        {
            byte[] payload = GachaLoaderStub.BuildPayload(
                new GachaRowFixture(GachaFixture.CommonItemId, 50, false),
                new GachaRowFixture(GachaFixture.UncommonItemId, 50, false));
            GachaLoaderStub loader = GachaLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new GachaTables(loader);
                });
            Assert.Contains("稀有", exception.Message);
        }

        [Fact]
        public void Tables_ExposeRowCountTotalWeightAndRows()
        {
            GachaTables tables = new GachaTables(GachaFixture.DefaultLoader());

            Assert.Equal(4, tables.Count);
            Assert.Equal(GachaFixture.TotalWeight, tables.TotalWeight);

            GachaEntryDef first = tables.Get(0);
            Assert.Equal(GachaFixture.CommonItemId, first.ItemId);
            Assert.Equal(70, first.Weight);
            Assert.False(first.IsRare);

            GachaEntryDef last = tables.Get(3);
            Assert.Equal(GachaFixture.RareItemId, last.ItemId);
            Assert.Equal(1, last.Weight);
            Assert.True(last.IsRare);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(4);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(-1);
                });
        }

        [Fact]
        public void GachaTables_WithNullLoader_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new GachaTables(null!);
                });
        }

        // ---------- 装配隔离与确定性 ----------

        [Fact]
        public void UnknownConfigInLoader_DoesNotDisturbRegisteredHandlers()
        {
            // 加载器里混入一张本切片根本不认识的表：不得影响已注册的处理器，也不得被抽卡逻辑读走。
            GachaLoaderStub loader = GachaFixture.DefaultLoader().AddTable("TbUnknownConfig", new byte[] { 1, 2, 3, 4 });
            SentinelHandler sentinel = new SentinelHandler();
            GachaHarness harness = GachaHarness.Create(
                loader,
                new SequenceRandomSource(0),
                GachaFixture.SingleDrawCost,
                bus => bus.RegisterCommandHandler<SentinelCommand>(sentinel));

            harness.Bus.Send(new SentinelCommand(7));
            harness.Draw(1);

            Assert.Equal(1, sentinel.HandledCount);
            Assert.Equal(7, sentinel.LastValue);
            Assert.Equal(GachaFixture.CommonItemId, Assert.Single(harness.MessagesOf<GrantItemCommand>()).ItemId);
            Assert.Equal(GachaFixture.CommonItemId, Assert.Single(RequireResult(harness.Handler).Items).ItemId);
            Assert.All(loader.RequestedTables, name => Assert.Equal(GachaLoaderStub.TableName, name));
        }

        [Fact]
        public void SameSeed_ReplaysIdenticalDrawSequence()
        {
            GachaHarness first = GachaHarness.Create(GachaFixture.DefaultLoader(), new SeededRandomSource(20240101), 5000);
            GachaHarness second = GachaHarness.Create(GachaFixture.DefaultLoader(), new SeededRandomSource(20240101), 5000);

            for (int round = 0; round < 3; round++)
            {
                first.Draw(10);
                second.Draw(10);

                AssertResultsEqual(RequireResult(first.Handler), RequireResult(second.Handler));
                Assert.Equal(first.Handler.PityCounter, second.Handler.PityCounter);
            }
        }

        [Fact]
        public void NullArguments_ThrowArgumentNullException()
        {
            GachaLoaderStub loader = GachaFixture.DefaultLoader();
            SequenceRandomSource random = new SequenceRandomSource();

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new GachaHandler(null!, random, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new GachaHandler(new CqeBus(), null!, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new GachaHandler(new CqeBus(), random, null!);
                });

            GachaHandler handler = new GachaHandler(new CqeBus(), random, loader);
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }

        private static GachaResult RequireResult(GachaHandler handler)
        {
            GachaResult? result = handler.LastResult;
            Assert.NotNull(result);
            return result!;
        }

        private static void AssertResultsEqual(GachaResult expected, GachaResult actual)
        {
            Assert.Equal(expected.Items.Length, actual.Items.Length);
            Assert.Equal(expected.PityBefore, actual.PityBefore);
            Assert.Equal(expected.PityTriggered, actual.PityTriggered);
            for (int i = 0; i < expected.Items.Length; i++)
            {
                Assert.Equal(expected.Items[i].ItemId, actual.Items[i].ItemId);
                Assert.Equal(expected.Items[i].IsRare, actual.Items[i].IsRare);
            }
        }

        /// <summary>用于验证"未知配置不影响已注册处理器"的中性命令。</summary>
        private readonly struct SentinelCommand : ICommand
        {
            internal SentinelCommand(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        private sealed class SentinelHandler : ICommandHandler<SentinelCommand>
        {
            internal int HandledCount { get; private set; }

            public void Handle(in SentinelCommand command)
            {
                LastValue = command.Value;
                HandledCount++;
            }

            internal int LastValue { get; private set; }
        }
    }
}

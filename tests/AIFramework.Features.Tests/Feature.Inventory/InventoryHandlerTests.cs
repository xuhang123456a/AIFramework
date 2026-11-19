using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.Inventory;
using Xunit;

namespace AIFramework.Features.Tests.Inventory
{
    /// <summary>
    /// 背包业务规则验收：真实 <see cref="CqeBus"/> + 本目录内的录制订阅器，
    /// 断言只落在"总线收到什么消息"与"账本快照"两处，不对处理器做 mock。
    /// </summary>
    public sealed class InventoryHandlerTests
    {
        /// <summary>金币 ID（走契约常量，避免测试里写魔法数字）。</summary>
        private const int Gold = MetaItemIds.Gold;

        /// <summary>补签卡 ID。</summary>
        private const int Card = MetaItemIds.RetroactiveCard;

        /// <summary>签到奖励道具 ID 的第二个槽位。</summary>
        private const int Reward = MetaItemIds.SignInRewardBase + 2;

        /// <summary>发放/消耗原因标签（本切片不解析，仅验证透传不影响行为）。</summary>
        private const int ReasonTagId = 5001;

        [Fact]
        public void GrantToExistingLedger_AccumulatesAndPublishesItemGrantedOnce()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 2);

            harness.Bus.Send(new GrantItemCommand(Gold, 3, ReasonTagId));

            Assert.Equal(5, harness.Handler.GetCount(Gold));

            ItemGrantedEvent granted = Assert.Single(harness.Granted.Events);
            Assert.Equal(Gold, granted.ItemId);
            Assert.Equal(3, granted.Count);

            // 合法发放不得附带任何拒绝/消耗事实。
            Assert.Empty(harness.Rejected.Events);
            Assert.Empty(harness.Consumed.Events);
        }

        [Fact]
        public void ConsumeEnough_DeductsAndPublishesItemConsumedOnce()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 10);

            harness.Bus.Send(new ConsumeItemCommand(Gold, 4, ReasonTagId));

            Assert.Equal(6, harness.Handler.GetCount(Gold));

            ItemConsumedEvent consumed = Assert.Single(harness.Consumed.Events);
            Assert.Equal(Gold, consumed.ItemId);
            Assert.Equal(4, consumed.Count);

            Assert.Empty(harness.Granted.Events);
            Assert.Empty(harness.Rejected.Events);
        }

        [Fact]
        public void ConsumeExactBalance_DropsEntryFromSnapshotAndGetCountIsZero()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Card, 1);
            harness.Handler.Seed(Reward, 2);

            harness.Bus.Send(new ConsumeItemCommand(Reward, 2, ReasonTagId));

            Assert.Equal(0, harness.Handler.GetCount(Reward));

            // 扣到 0 的条目必须离开快照，只剩仍然持有的那一条。
            InventoryEntry entry = Assert.Single(harness.Handler.Snapshot());
            Assert.Equal(Card, entry.ItemId);
            Assert.Equal(1, entry.Count);

            Assert.Single(harness.Consumed.Events);
            Assert.Empty(harness.Rejected.Events);
        }

        [Fact]
        public void ConsumeMoreThanHeld_RejectsWithNotEnoughItemAndKeepsLedger()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Card, 2);

            harness.Bus.Send(new ConsumeItemCommand(Card, 3, ReasonTagId));

            // 账本一点不动，且绝不能出现"先扣再拒"。
            Assert.Equal(2, harness.Handler.GetCount(Card));
            InventoryEntry entry = Assert.Single(harness.Handler.Snapshot());
            Assert.Equal(Card, entry.ItemId);
            Assert.Equal(2, entry.Count);

            ItemChangeRejectedEvent rejected = Assert.Single(harness.Rejected.Events);
            Assert.Equal(Card, rejected.ItemId);
            Assert.Equal(3, rejected.Count);
            Assert.Equal(MetaReasonTags.NotEnoughItem, rejected.ReasonTagId);

            Assert.Empty(harness.Consumed.Events);
            Assert.Empty(harness.Granted.Events);
        }

        [Theory]
        [InlineData(0, 5)]
        [InlineData(-1, 3)]
        [InlineData(1, 0)]
        [InlineData(1, -1)]
        public void InvalidGrant_IsRejectedWithoutSideEffects(int itemId, int count)
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 7);
            harness.Handler.Seed(Card, 1);

            harness.Bus.Send(new GrantItemCommand(itemId, count, ReasonTagId));

            Assert.Equal(7, harness.Handler.GetCount(Gold));
            Assert.Equal(1, harness.Handler.GetCount(Card));
            Assert.Equal(2, harness.Handler.Snapshot().Count);
            Assert.Empty(harness.Granted.Events);
            Assert.Empty(harness.Consumed.Events);

            ItemChangeRejectedEvent rejected = Assert.Single(harness.Rejected.Events);
            Assert.Equal(itemId, rejected.ItemId);
            Assert.Equal(count, rejected.Count);
            Assert.Equal(MetaReasonTags.InvalidRequest, rejected.ReasonTagId);
        }

        [Theory]
        [InlineData(0, 5)]
        [InlineData(-5, 1)]
        [InlineData(1, 0)]
        [InlineData(1, -2)]
        public void InvalidConsume_IsRejectedWithoutSideEffects(int itemId, int count)
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 7);

            harness.Bus.Send(new ConsumeItemCommand(itemId, count, ReasonTagId));

            Assert.Equal(7, harness.Handler.GetCount(Gold));
            Assert.Single(harness.Handler.Snapshot());
            Assert.Empty(harness.Consumed.Events);
            Assert.Empty(harness.Granted.Events);

            // 参数非法必须走 InvalidRequest，不能退化成 NotEnoughItem。
            ItemChangeRejectedEvent rejected = Assert.Single(harness.Rejected.Events);
            Assert.Equal(itemId, rejected.ItemId);
            Assert.Equal(count, rejected.Count);
            Assert.Equal(MetaReasonTags.InvalidRequest, rejected.ReasonTagId);
        }

        [Fact]
        public void QueryOfUnheldItem_ReturnsZeroWithoutThrowing()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 3);

            InventorySnapshot snapshot = harness.Bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(Reward));
            Assert.Equal(Reward, snapshot.ItemId);
            Assert.Equal(0, snapshot.Count);

            // 非法 ID 同样只返回 0，不得抛异常、不得污染账本。
            InventorySnapshot negative = harness.Bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(-8));
            Assert.Equal(-8, negative.ItemId);
            Assert.Equal(0, negative.Count);
            Assert.Equal(3, harness.Handler.GetCount(Gold));
        }

        [Fact]
        public void Snapshot_IsAscendingStableAndOmitsZeroCountEntries()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(300, 1);
            harness.Handler.Seed(100, 2);
            harness.Handler.Seed(200, 5);
            harness.Handler.Seed(50, 4);

            // 一条扣到 0（走业务路径）、一条被 Seed 清 0：两条都必须离开快照。
            harness.Bus.Send(new ConsumeItemCommand(200, 5, ReasonTagId));
            harness.Handler.Seed(50, 0);

            IReadOnlyList<InventoryEntry> first = harness.Handler.Snapshot();
            IReadOnlyList<InventoryEntry> second = harness.Handler.Snapshot();

            Assert.Equal(2, first.Count);
            Assert.Equal(100, first[0].ItemId);
            Assert.Equal(2, first[0].Count);
            Assert.Equal(300, first[1].ItemId);
            Assert.Equal(1, first[1].Count);

            // 稳定：两次调用顺序一致，顺序只由 ItemId 决定、不泄漏字典枚举顺序。
            Assert.Equal(2, second.Count);
            Assert.Equal(100, second[0].ItemId);
            Assert.Equal(300, second[1].ItemId);

            Assert.Equal(0, harness.Handler.GetCount(200));
            Assert.Equal(0, harness.Handler.GetCount(50));
        }

        [Fact]
        public void Snapshot_ReturnsDetachedCopyThatCannotMutateLedger()
        {
            Harness harness = new Harness();
            harness.Handler.Seed(Gold, 2);

            InventoryEntry[] detached = (InventoryEntry[])harness.Handler.Snapshot();
            detached[0] = new InventoryEntry(Gold, 999);

            Assert.Equal(2, harness.Handler.GetCount(Gold));
            Assert.Equal(2, ((InventoryEntry[])harness.Handler.Snapshot())[0].Count);
        }

        [Fact]
        public void Register_RoutesCommandsAndQueriesThroughRealBus()
        {
            CqeBus bus = new CqeBus();
            InventoryHandler handler = new InventoryHandler();

            handler.Register(bus);

            // 2 条命令 + 1 条查询，全部落在真实总线上。
            Assert.Equal(2, bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, bus.RegisteredQueryHandlerCount);

            bus.Send(new GrantItemCommand(Gold, 4, ReasonTagId));
            bus.Send(new ConsumeItemCommand(Gold, 1, ReasonTagId));

            Assert.Equal(3, handler.GetCount(Gold));

            InventorySnapshot snapshot = bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(Gold));
            Assert.Equal(Gold, snapshot.ItemId);
            Assert.Equal(3, snapshot.Count);
        }

        [Fact]
        public void RegisteringSecondHandlerOnSameBus_ThrowsInvalidOperationException()
        {
            CqeBus bus = new CqeBus();
            InventoryHandler first = new InventoryHandler();
            first.Register(bus);

            InventoryHandler second = new InventoryHandler();
            Assert.Throws<InvalidOperationException>(() => second.Register(bus));

            // 冲突后第二个处理器仍未接入总线，账本互不可见。
            Assert.Equal(0, second.GetCount(Gold));
        }

        [Fact]
        public void HandleBeforeRegister_DoesNotThrowAndKeepsLedgerConsistent()
        {
            InventoryHandler handler = new InventoryHandler();

            handler.Handle(new GrantItemCommand(Gold, 2, ReasonTagId));
            handler.Handle(new ConsumeItemCommand(Card, 1, ReasonTagId));

            // 未接总线：事件静默丢弃、账本照常变更，但业务拒绝依旧不抛异常。
            Assert.Equal(2, handler.GetCount(Gold));
            Assert.Equal(0, handler.GetCount(Card));
            InventoryEntry entry = Assert.Single(handler.Snapshot());
            Assert.Equal(Gold, entry.ItemId);
        }

        /// <summary>每个用例一套全新真实总线 + 处理器 + 三路录制订阅器，用例之间零共享状态。</summary>
        private sealed class Harness
        {
            internal Harness()
            {
                Bus = new CqeBus();
                Handler = new InventoryHandler();
                Handler.Register(Bus);
                Bus.Subscribe(Granted);
                Bus.Subscribe(Consumed);
                Bus.Subscribe(Rejected);
            }

            internal CqeBus Bus { get; }

            internal InventoryHandler Handler { get; }

            internal EventRecorder<ItemGrantedEvent> Granted { get; } = new EventRecorder<ItemGrantedEvent>();

            internal EventRecorder<ItemConsumedEvent> Consumed { get; } = new EventRecorder<ItemConsumedEvent>();

            internal EventRecorder<ItemChangeRejectedEvent> Rejected { get; } = new EventRecorder<ItemChangeRejectedEvent>();
        }
    }
}

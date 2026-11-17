using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Core.Cqe;
using Xunit;
using Xunit.Abstractions;

namespace AIFramework.Core.Tests.Cqe
{
    /// <summary>
    /// <see cref="CqeBus"/> 行为契约：命令 / 查询路由、未注册异常、事件广播顺序、重复注册拒绝、
    /// 实例隔离（处理器引用落在实例字段而非静态槽位）以及发送路径的零托管堆分配。
    /// </summary>
    public sealed class CqeBusTests
    {
        private readonly ITestOutputHelper _output;

        public CqeBusTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ── 消息与处理器夹具 ─────────────────────────────────────────────

        private readonly struct DamageCommand : ICommand
        {
            public DamageCommand(int amount, int targetId)
            {
                Amount = amount;
                TargetId = targetId;
            }

            public int Amount { get; }

            public int TargetId { get; }
        }

        private readonly struct InventoryCountQuery : IQuery<int>
        {
            public InventoryCountQuery(int itemId)
            {
                ItemId = itemId;
            }

            public int ItemId { get; }
        }

        private readonly struct ScreenChangedEvent : IEvent
        {
            public ScreenChangedEvent(int screenId)
            {
                ScreenId = screenId;
            }

            public int ScreenId { get; }
        }

        private sealed class CapturingCommandHandler : ICommandHandler<DamageCommand>
        {
            public int CallCount { get; set; }

            public int LastAmount { get; private set; }

            public int LastTargetId { get; private set; }

            public void Handle(in DamageCommand command)
            {
                CallCount++;
                LastAmount = command.Amount;
                LastTargetId = command.TargetId;
            }
        }

        /// <summary>只累加一个外部数组的处理器：不分配，用于热路径与实例隔离用例。</summary>
        private sealed class ArrayCountingCommandHandler : ICommandHandler<DamageCommand>
        {
            private readonly int[] _calls;

            public ArrayCountingCommandHandler(int[] calls)
            {
                _calls = calls;
            }

            public void Handle(in DamageCommand command)
            {
                _calls[0]++;
            }
        }

        private sealed class CountingCommandHandler : ICommandHandler<DamageCommand>
        {
            public int CallCount;

            public void Handle(in DamageCommand command)
            {
                CallCount++;
            }
        }

        private sealed class FixedInventoryQueryHandler : IQueryHandler<InventoryCountQuery, int>
        {
            private readonly int _result;

            public FixedInventoryQueryHandler(int result)
            {
                _result = result;
            }

            public int CallCount;

            public int LastItemId;

            public int Handle(in InventoryCountQuery query)
            {
                CallCount++;
                LastItemId = query.ItemId;
                return _result;
            }
        }

        private sealed class OrderingEventHandler : IEventHandler<ScreenChangedEvent>
        {
            private readonly List<int> _order;

            private readonly int _id;

            public OrderingEventHandler(List<int> order, int id)
            {
                _order = order;
                _id = id;
            }

            public int CallCount;

            public void Handle(in ScreenChangedEvent evt)
            {
                CallCount++;
                _order.Add(_id);
            }
        }

        // ── 用例 ────────────────────────────────────────────────────────

        [Fact]
        public void Send_RoutesCommandToRegisteredHandlerExactlyOnce()
        {
            CqeBus bus = new CqeBus();
            CapturingCommandHandler handler = new CapturingCommandHandler();
            bus.RegisterCommandHandler(handler);

            DamageCommand command = new DamageCommand(7, 42);
            bus.Send(in command);

            Assert.Equal(1, handler.CallCount);
            Assert.Equal(7, handler.LastAmount);
            Assert.Equal(42, handler.LastTargetId);
            Assert.Equal(1, bus.RegisteredCommandHandlerCount);
        }

        [Fact]
        public void Query_ReturnsValueProducedByRegisteredHandler()
        {
            CqeBus bus = new CqeBus();
            FixedInventoryQueryHandler handler = new FixedInventoryQueryHandler(11);
            bus.RegisterQueryHandler(handler);

            InventoryCountQuery query = new InventoryCountQuery(9001);
            int result = bus.Query<InventoryCountQuery, int>(in query);

            Assert.Equal(11, result);
            Assert.Equal(1, handler.CallCount);
            Assert.Equal(9001, handler.LastItemId);
            Assert.Equal(1, bus.RegisteredQueryHandlerCount);
        }

        [Fact]
        public void Send_WithoutRegisteredHandler_ThrowsAndMentionsMessageType()
        {
            CqeBus bus = new CqeBus();
            DamageCommand command = new DamageCommand(1, 1);

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(() => SendUnregistered(bus, command));

            Assert.Contains(typeof(DamageCommand).FullName!, exception.Message);
        }

        [Fact]
        public void Query_WithoutRegisteredHandler_ThrowsAndMentionsMessageType()
        {
            CqeBus bus = new CqeBus();
            InventoryCountQuery query = new InventoryCountQuery(1);

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(() => QueryUnregistered(bus, query));

            Assert.Contains(typeof(InventoryCountQuery).FullName!, exception.Message);
        }

        [Fact]
        public void Publish_WithNoSubscribers_IsSilentAndRegistersNothing()
        {
            CqeBus bus = new CqeBus();
            ScreenChangedEvent evt = new ScreenChangedEvent(3);

            // 零订阅者必须静默成功：不抛异常，也不凭空造出订阅者。
            bus.Publish(in evt);
            bus.Publish(in evt);

            Assert.Equal(0, bus.SubscriberCount);
        }

        [Fact]
        public void Publish_DeliversToEverySubscriberOnceInRegistrationOrder()
        {
            CqeBus bus = new CqeBus();
            List<int> order = new List<int>(3);
            OrderingEventHandler first = new OrderingEventHandler(order, 1);
            OrderingEventHandler second = new OrderingEventHandler(order, 2);
            OrderingEventHandler third = new OrderingEventHandler(order, 3);

            bus.Subscribe(first);
            bus.Subscribe(second);
            bus.Subscribe(third);
            Assert.Equal(3, bus.SubscriberCount);

            ScreenChangedEvent evt = new ScreenChangedEvent(9);
            bus.Publish(in evt);

            Assert.Equal(new[] { 1, 2, 3 }, order);
            Assert.Equal(1, first.CallCount);
            Assert.Equal(1, second.CallCount);
            Assert.Equal(1, third.CallCount);
        }

        [Fact]
        public void RegisterCommandHandler_TwiceForSameType_Throws()
        {
            CqeBus bus = new CqeBus();
            bus.RegisterCommandHandler(new CapturingCommandHandler());

            Assert.Throws<InvalidOperationException>(
                () => bus.RegisterCommandHandler(new CapturingCommandHandler()));

            Assert.Equal(1, bus.RegisteredCommandHandlerCount);
        }

        [Fact]
        public void RegisterQueryHandler_TwiceForSameType_Throws()
        {
            CqeBus bus = new CqeBus();
            bus.RegisterQueryHandler(new FixedInventoryQueryHandler(1));

            Assert.Throws<InvalidOperationException>(
                () => bus.RegisterQueryHandler(new FixedInventoryQueryHandler(2)));

            Assert.Equal(1, bus.RegisteredQueryHandlerCount);
        }

        [Fact]
        public void TwoBuses_RegisteringSameMessageType_AreIsolatedAndSurviveCollection()
        {
            int[] firstCalls = new int[1];

            CqeBus second = new CqeBus();
            CountingCommandHandler secondHandler = new CountingCommandHandler();
            second.RegisterCommandHandler(secondHandler);

            // 第一个总线只在这个辅助方法里存在，返回后除了 WeakReference 再无强引用。
            WeakReference firstHandler = CreateIsolatedBus(firstCalls);

            DamageCommand command = new DamageCommand(5, 5);
            second.Send(in command);

            Assert.Equal(0, firstCalls[0]);
            Assert.Equal(1, secondHandler.CallCount);

            // 处理器若被放在静态槽位里，这里回收不掉；实例字段则可以被回收。
            for (int i = 0; i < 3 && firstHandler.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(firstHandler.IsAlive);

            // 回收后第二个总线行为不变：仍然只命中自己的处理器。
            second.Send(in command);
            Assert.Equal(2, secondHandler.CallCount);
            Assert.Equal(0, firstCalls[0]);
        }

        [Fact]
        public void Send_WarmedUp_AllocatesNothingOnHeap()
        {
            CqeBus bus = new CqeBus();
            CountingCommandHandler handler = new CountingCommandHandler();
            bus.RegisterCommandHandler(handler);

            DamageCommand command = new DamageCommand(3, 4);

            // 预热：跑通槽位查找、接口分派与 JIT 分层编译。
            for (int i = 0; i < 100; i++)
            {
                bus.Send(in command);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                bus.Send(in command);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            _output.WriteLine("CqeBus.Send x1000 allocated bytes = " + allocated);

            Assert.Equal(0L, allocated);
            Assert.Equal(1100, handler.CallCount);
        }

        // ── 辅助方法 ────────────────────────────────────────────────────

        private static void SendUnregistered(CqeBus bus, in DamageCommand command)
        {
            bus.Send(in command);
        }

        private static void QueryUnregistered(CqeBus bus, in InventoryCountQuery query)
        {
            bus.Query<InventoryCountQuery, int>(in query);
        }

        /// <summary>建一个只在本方法内可见的总线，并只返回其处理器的弱引用。</summary>
        private static WeakReference CreateIsolatedBus(int[] calls)
        {
            CqeBus bus = new CqeBus();
            ArrayCountingCommandHandler handler = new ArrayCountingCommandHandler(calls);
            bus.RegisterCommandHandler(handler);
            return new WeakReference(handler);
        }
    }
}

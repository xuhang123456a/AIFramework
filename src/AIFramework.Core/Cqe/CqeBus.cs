using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Core.Cqe
{
    /// <summary>
    /// CQE 总线（Phase 1 实现）。
    /// <para>
    /// 命令 / 查询 / 事件各持一条"全局槽位数组"，用 <see cref="MessageTypeId{T}.Id"/> 直接下标访问，
    /// 不做 <c>Dictionary&lt;Type, object&gt;</c> 查找、不装箱、不反射。数组扩容只发生在注册期
    /// （<see cref="RegisterCommandHandler{TCommand}"/> / <see cref="RegisterQueryHandler{TQuery,TResult}"/> / <see cref="Subscribe{TEvent}"/>）；
    /// 发送与广播路径只读数组与长度，不触发扩容、不产生堆分配。
    /// </para>
    /// <para>
    /// 线程安全不在 Phase 1 范围内：装配期单线程注册，运行期单线程发送。
    /// 所有处理器引用都存在实例字段里，因此不同 <see cref="CqeBus"/> 实例之间完全隔离。
    /// </para>
    /// </summary>
    public sealed class CqeBus : ICqeBus
    {
        private const int InitialSlotCapacity = 16;

        private object?[] _commandSlots;
        private object?[] _querySlots;
        private object?[] _eventSlots;

        private int _commandHandlerCount;
        private int _queryHandlerCount;
        private int _subscriberCount;

        /// <summary>创建一个空的、与其他总线实例完全隔离的总线。</summary>
        public CqeBus()
        {
            _commandSlots = new object?[InitialSlotCapacity];
            _querySlots = new object?[InitialSlotCapacity];
            _eventSlots = new object?[InitialSlotCapacity];
        }

        /// <summary>已注册的命令处理器数量（即已占用的命令槽位数）。</summary>
        public int RegisteredCommandHandlerCount
        {
            get { return _commandHandlerCount; }
        }

        /// <summary>已注册的查询处理器数量（即已占用的查询槽位数）。</summary>
        public int RegisteredQueryHandlerCount
        {
            get { return _queryHandlerCount; }
        }

        /// <summary>累计订阅者数量：所有事件类型下的回调实例合计，重复订阅同一个实例也各计一次。</summary>
        public int SubscriberCount
        {
            get { return _subscriberCount; }
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">该命令类型未注册处理器。</exception>
        public void Send<TCommand>(in TCommand command)
            where TCommand : struct, ICommand
        {
            int slot = MessageTypeId<TCommand>.Id;
            object?[] slots = _commandSlots;

            // 发送路径只读：槽位越界等价于"未注册"，不算异常扩容时机。
            if (slot < slots.Length)
            {
                ICommandHandler<TCommand>? handler = (ICommandHandler<TCommand>?)slots[slot];
                if (handler != null)
                {
                    handler.Handle(in command);
                    return;
                }
            }

            throw new InvalidOperationException(
                "No command handler registered for " + DescribeMessage(typeof(TCommand)) + ".");
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">该查询类型未注册处理器。</exception>
        public TResult Query<TQuery, TResult>(in TQuery query)
            where TQuery : struct, IQuery<TResult>
        {
            int slot = MessageTypeId<TQuery>.Id;
            object?[] slots = _querySlots;

            if (slot < slots.Length)
            {
                IQueryHandler<TQuery, TResult>? handler = (IQueryHandler<TQuery, TResult>?)slots[slot];
                if (handler != null)
                {
                    return handler.Handle(in query);
                }
            }

            throw new InvalidOperationException(
                "No query handler registered for " + DescribeMessage(typeof(TQuery)) + ".");
        }

        /// <inheritdoc />
        /// <remarks>零订阅者时静默返回；多订阅者按注册顺序逐个回调，用 <c>for</c> 下标遍历预分配数组。</remarks>
        public void Publish<TEvent>(in TEvent evt)
            where TEvent : struct, IEvent
        {
            int slot = MessageTypeId<TEvent>.Id;
            object?[] slots = _eventSlots;

            if (slot < slots.Length)
            {
                EventSubscribers<TEvent>? subscribers = (EventSubscribers<TEvent>?)slots[slot];
                if (subscribers != null)
                {
                    subscribers.Dispatch(in evt);
                }
            }
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">该命令类型已注册过处理器。</exception>
        public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
            where TCommand : struct, ICommand
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            int slot = MessageTypeId<TCommand>.Id;
            EnsureSlotCapacity(ref _commandSlots, slot);

            if (_commandSlots[slot] != null)
            {
                throw new InvalidOperationException(
                    "Command handler already registered for " + DescribeMessage(typeof(TCommand)) + ".");
            }

            _commandSlots[slot] = handler;
            _commandHandlerCount++;
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">该查询类型已注册过处理器。</exception>
        public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            int slot = MessageTypeId<TQuery>.Id;
            EnsureSlotCapacity(ref _querySlots, slot);

            if (_querySlots[slot] != null)
            {
                throw new InvalidOperationException(
                    "Query handler already registered for " + DescribeMessage(typeof(TQuery)) + ".");
            }

            _querySlots[slot] = handler;
            _queryHandlerCount++;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 同一事件类型允许任意多个订阅者；<b>重复订阅同一个 handler 实例被允许</b>，
        /// 每次订阅都会在广播时按注册顺序各自回调一次（不去重、不合并）。
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 <c>null</c>。</exception>
        public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : struct, IEvent
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            int slot = MessageTypeId<TEvent>.Id;
            EnsureSlotCapacity(ref _eventSlots, slot);

            EventSubscribers<TEvent>? subscribers = (EventSubscribers<TEvent>?)_eventSlots[slot];
            if (subscribers == null)
            {
                subscribers = new EventSubscribers<TEvent>();
                _eventSlots[slot] = subscribers;
            }

            subscribers.Add(handler);
            _subscriberCount++;
        }

        /// <summary>只在注册期调用的槽位扩容：按 2 倍增长，保证发送路径永远不需要扩容。</summary>
        /// <param name="slots">目标槽位数组。</param>
        /// <param name="slot">本次要写入的槽位号。</param>
        private static void EnsureSlotCapacity(ref object?[] slots, int slot)
        {
            if (slot < slots.Length)
            {
                return;
            }

            int capacity = slots.Length;
            while (capacity <= slot)
            {
                capacity = capacity == 0 ? InitialSlotCapacity : capacity * 2;
            }

            object?[] grown = new object?[capacity];
            Array.Copy(slots, grown, slots.Length);
            slots = grown;
        }

        /// <summary>异常路径专用：取消息类型的可读名（只在抛异常时分配字符串）。</summary>
        /// <param name="type">消息类型。</param>
        /// <returns>类型的完整名，取不到时退回短名。</returns>
        private static string DescribeMessage(Type type)
        {
            string? fullName = type.FullName;
            return fullName ?? type.Name;
        }

        /// <summary>
        /// 单个事件类型的订阅者容器：预分配处理器数组 + 顺序追加，广播时用 <c>for</c> 下标遍历。
        /// 实例挂在 <see cref="CqeBus"/> 的槽位数组上，不是静态字段，因此总线实例之间互不可见。
        /// </summary>
        /// <typeparam name="TEvent">事件类型。</typeparam>
        private sealed class EventSubscribers<TEvent>
            where TEvent : struct, IEvent
        {
            private const int InitialHandlerCapacity = 4;

            private IEventHandler<TEvent>[] _handlers;
            private int _count;

            internal EventSubscribers()
            {
                _handlers = new IEventHandler<TEvent>[InitialHandlerCapacity];
                _count = 0;
            }

            /// <summary>追加一个订阅者（注册期调用，可能扩容）。</summary>
            /// <param name="handler">订阅者实例。</param>
            internal void Add(IEventHandler<TEvent> handler)
            {
                if (_count == _handlers.Length)
                {
                    IEventHandler<TEvent>[] grown = new IEventHandler<TEvent>[_handlers.Length * 2];
                    Array.Copy(_handlers, grown, _count);
                    _handlers = grown;
                }

                _handlers[_count] = handler;
                _count++;
            }

            /// <summary>广播：按下标顺序逐个回调，只读本地数组快照，零分配。</summary>
            /// <param name="evt">事件内容（<c>in</c> 只读引用）。</param>
            internal void Dispatch(in TEvent evt)
            {
                IEventHandler<TEvent>[] handlers = _handlers;
                int count = _count;
                for (int i = 0; i < count; i++)
                {
                    handlers[i].Handle(in evt);
                }
            }
        }
    }
}

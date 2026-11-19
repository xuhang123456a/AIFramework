using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.AsyncPvp;

namespace AIFramework.Features.Tests.AsyncPvp
{
    /// <summary>
    /// 异步 PVP 本目录私有的记录型总线：先记录，再原样转发给真实 <c>CqeBus</c>。
    /// 断言取本类记录 = 断言"切片真实发出了什么"；注册/订阅仍由真实总线承担。
    /// </summary>
    internal sealed class AsyncPvpRecordingBus : ICqeBus
    {
        private readonly CqeBus _inner;
        private readonly List<object> _sentCommands;
        private readonly List<object> _publishedEvents;

        public AsyncPvpRecordingBus()
        {
            _inner = new CqeBus();
            _sentCommands = new List<object>();
            _publishedEvents = new List<object>();
        }

        /// <summary>按发送顺序记录的命令（含切片内部发出的发奖指令）。</summary>
        public IReadOnlyList<object> SentCommands
        {
            get { return _sentCommands; }
        }

        /// <summary>按广播顺序记录的事件。</summary>
        public IReadOnlyList<object> PublishedEvents
        {
            get { return _publishedEvents; }
        }

        /// <inheritdoc />
        public void Send<TCommand>(in TCommand command)
            where TCommand : struct, ICommand
        {
            _sentCommands.Add(command);
            _inner.Send(in command);
        }

        /// <inheritdoc />
        public TResult Query<TQuery, TResult>(in TQuery query)
            where TQuery : struct, IQuery<TResult>
        {
            return _inner.Query<TQuery, TResult>(in query);
        }

        /// <inheritdoc />
        public void Publish<TEvent>(in TEvent evt)
            where TEvent : struct, IEvent
        {
            _publishedEvents.Add(evt);
            _inner.Publish(in evt);
        }

        /// <inheritdoc />
        public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
            where TCommand : struct, ICommand
        {
            _inner.RegisterCommandHandler(handler);
        }

        /// <inheritdoc />
        public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            _inner.RegisterQueryHandler<TQuery, TResult>(handler);
        }

        /// <inheritdoc />
        public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : struct, IEvent
        {
            _inner.Subscribe(handler);
        }

        /// <summary>取指定命令类型的记录（按提交顺序）。</summary>
        public List<T> SentOf<T>()
            where T : struct
        {
            List<T> result = new List<T>();
            for (int i = 0; i < _sentCommands.Count; i++)
            {
                if (_sentCommands[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }

        /// <summary>取指定事件类型的记录（按广播顺序）。</summary>
        public List<T> PublishedOf<T>()
            where T : struct
        {
            List<T> result = new List<T>();
            for (int i = 0; i < _publishedEvents.Count; i++)
            {
                if (_publishedEvents[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 发奖接收桩：真实总线要求 <c>GrantItemCommand</c> 有处理器，否则切片发奖会直接抛。
    /// 这里只记账，不做规则——发不发、发多少全部由 <see cref="AsyncPvpHandler"/> 决定。
    /// </summary>
    internal sealed class AsyncPvpRewardSink : ICommandHandler<GrantItemCommand>
    {
        private readonly List<GrantItemCommand> _grants;

        public AsyncPvpRewardSink()
        {
            _grants = new List<GrantItemCommand>();
        }

        /// <summary>按发送顺序记录的发奖指令。</summary>
        public IReadOnlyList<GrantItemCommand> Grants
        {
            get { return _grants; }
        }

        /// <inheritdoc />
        public void Handle(in GrantItemCommand command)
        {
            _grants.Add(command);
        }
    }

    /// <summary>事件订阅桩：只记录结算结果，用于断言"事件确实经总线广播"。</summary>
    internal sealed class AsyncPvpEventSink : IEventHandler<AsyncDuelResolvedEvent>
    {
        private readonly List<AsyncDuelResult> _results;

        public AsyncPvpEventSink()
        {
            _results = new List<AsyncDuelResult>();
        }

        /// <summary>按广播顺序记录的结算结果。</summary>
        public IReadOnlyList<AsyncDuelResult> Results
        {
            get { return _results; }
        }

        /// <inheritdoc />
        public void Handle(in AsyncDuelResolvedEvent evt)
        {
            _results.Add(evt.Result);
        }
    }

    /// <summary>
    /// 异步 PVP 的一次性装配：真实 <see cref="AsyncPvpHandler"/> + 真实 <c>CqeBus</c>
    /// （经本目录私有的 <see cref="AsyncPvpRecordingBus"/> 记录）+ 发奖桩。
    /// </summary>
    internal sealed class AsyncPvpHarness
    {
        private AsyncPvpHarness(AsyncPvpRecordingBus bus, AsyncPvpRewardSink rewards, AsyncPvpHandler handler)
        {
            Bus = bus;
            Rewards = rewards;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public AsyncPvpRecordingBus Bus { get; }

        /// <summary>发奖接收桩。</summary>
        public AsyncPvpRewardSink Rewards { get; }

        /// <summary>被测处理器。</summary>
        public AsyncPvpHandler Handler { get; }

        /// <summary>装配一套夹具：处理器经 <c>Register</c> 注册到真实总线，后续命令走总线。</summary>
        public static AsyncPvpHarness Create(int initialRating = 1000, int playerId = 1)
        {
            AsyncPvpRecordingBus bus = new AsyncPvpRecordingBus();
            AsyncPvpRewardSink rewards = new AsyncPvpRewardSink();
            bus.RegisterCommandHandler<GrantItemCommand>(rewards);

            AsyncPvpHandler handler = new AsyncPvpHandler(bus, playerId, initialRating);
            handler.Register(bus);

            return new AsyncPvpHarness(bus, rewards, handler);
        }

        /// <summary>经真实总线发起一场挑战，返回切片给出的结算结果。</summary>
        public AsyncDuelResult Duel(int attackerPower, int defenderPower, int randomSeed, int defenderId = 300)
        {
            Bus.Send(new AsyncDuelCommand(
                new DefenseSnapshot(defenderId, defenderPower, randomSeed, 0),
                attackerPower));
            return Handler.LastResult;
        }
    }
}

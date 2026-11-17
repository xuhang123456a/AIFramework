using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Core.Cqe;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 先记录、再原样转发给真实 <see cref="CqeBus"/> 的总线。
    /// 断言取本类记录 = 断言"切片真实发出了什么"；注册/订阅行为仍由真实总线承担（重复注册照旧抛异常）。
    /// </summary>
    public sealed class RecordingBus : ICqeBus
    {
        private readonly CqeBus _inner;
        private readonly List<object> _sentCommands;
        private readonly List<object> _publishedEvents;
        private readonly List<object> _queries;

        public RecordingBus()
        {
            _inner = new CqeBus();
            _sentCommands = new List<object>();
            _publishedEvents = new List<object>();
            _queries = new List<object>();
        }

        /// <summary>被包裹的真实总线。</summary>
        public CqeBus Inner
        {
            get { return _inner; }
        }

        /// <summary>按发送顺序记录的命令（含切片内部发出的发奖/扣道具指令）。</summary>
        public IReadOnlyList<object> SentCommands
        {
            get { return _sentCommands; }
        }

        /// <summary>按广播顺序记录的事件。</summary>
        public IReadOnlyList<object> PublishedEvents
        {
            get { return _publishedEvents; }
        }

        /// <summary>按调用顺序记录的查询。</summary>
        public IReadOnlyList<object> Queries
        {
            get { return _queries; }
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
            _queries.Add(query);
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
            return Collect<T>(_sentCommands);
        }

        /// <summary>取指定事件类型的记录（按广播顺序）。</summary>
        public List<T> PublishedOf<T>()
            where T : struct
        {
            return Collect<T>(_publishedEvents);
        }

        /// <summary>取指定查询类型的记录（按调用顺序）。</summary>
        public List<T> QueriesOf<T>()
            where T : struct
        {
            return Collect<T>(_queries);
        }

        /// <summary>某类命令的发送条数。</summary>
        public int CountSent<T>()
            where T : struct
        {
            return SentOf<T>().Count;
        }

        /// <summary>某类事件的广播条数。</summary>
        public int CountPublished<T>()
            where T : struct
        {
            return PublishedOf<T>().Count;
        }

        /// <summary>某类查询的调用条数。</summary>
        public int CountQueried<T>()
            where T : struct
        {
            return QueriesOf<T>().Count;
        }

        private static List<T> Collect<T>(List<object> source)
            where T : struct
        {
            List<T> result = new List<T>();
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }
    }
}

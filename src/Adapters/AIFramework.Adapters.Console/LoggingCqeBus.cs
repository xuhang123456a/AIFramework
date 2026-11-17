using AIFramework.Contracts.Cqe;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 因果日志装饰器：把每条总线消息记成一条数值记录（消息 Tag + 发起源 + 结果码），
    /// 热路径零字符串——类型名只在首次见到该消息类型时经注册表取一次 id。
    /// </summary>
    public sealed class LoggingCqeBus : ICqeBus
    {
        private readonly ICqeBus _inner;
        private readonly ICausalLog _log;
        private readonly ICausalTagRegistry _registry;
        private readonly int _sourceTagId;

        public LoggingCqeBus(ICqeBus inner, ICausalLog log, ICausalTagRegistry registry, int sourceTagId)
        {
            _inner = inner;
            _log = log;
            _registry = registry;
            _sourceTagId = sourceTagId;
        }

        public void Send<TCommand>(in TCommand command)
            where TCommand : struct, ICommand
        {
            // 进入即记，保证"因"排在"果"之前（处理器内部再发的指令序号会更大）。
            Record(typeof(TCommand).Name, CausalOutcome.Success);
            _inner.Send(in command);
        }

        public TResult Query<TQuery, TResult>(in TQuery query)
            where TQuery : struct, IQuery<TResult>
        {
            Record(typeof(TQuery).Name, CausalOutcome.Success);
            return _inner.Query<TQuery, TResult>(in query);
        }

        public void Publish<TEvent>(in TEvent evt)
            where TEvent : struct, IEvent
        {
            Record(typeof(TEvent).Name, CausalOutcome.Success);
            _inner.Publish(in evt);
        }

        public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
            where TCommand : struct, ICommand
        {
            _inner.RegisterCommandHandler(handler);
        }

        public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            _inner.RegisterQueryHandler(handler);
        }

        public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : struct, IEvent
        {
            _inner.Subscribe(handler);
        }

        private void Record(string tagName, int outcomeCode)
        {
            _log.Record(_registry.Register(tagName), _sourceTagId, outcomeCode);
        }
    }
}

using System;

namespace AIFramework.Contracts.Cqe
{
    /// <summary>显式变更意图的标记接口。实现方必须是 <c>readonly struct</c>，避免发送时装箱。</summary>
    public interface ICommand
    {
    }

    /// <summary>只读查询的标记接口，禁止带写副作用。</summary>
    public interface IQuery<TResult>
    {
    }

    /// <summary>已发生事实的标记接口，发出方不得假设订阅方存在。</summary>
    public interface IEvent
    {
    }

    /// <summary>命令处理器：同一消息类型在同一总线实例上只允许一个实现。</summary>
    public interface ICommandHandler<TCommand>
        where TCommand : struct, ICommand
    {
        void Handle(in TCommand command);
    }

    /// <summary>查询处理器：只读，禁止修改状态。</summary>
    public interface IQueryHandler<TQuery, TResult>
        where TQuery : struct, IQuery<TResult>
    {
        TResult Handle(in TQuery query);
    }

    /// <summary>事件处理器：同一事件类型允许 0..N 个订阅者。</summary>
    public interface IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        void Handle(in TEvent evt);
    }

    /// <summary>
    /// CQE 总线。签名固定为泛型 + <c>in</c> 只读引用，禁止出现 <c>Send(ICommand)</c> 这类接口参数写法（会让 struct 消息装箱）。
    /// </summary>
    public interface ICqeBus
    {
        /// <summary>发送命令。该消息类型未注册处理器时抛 <see cref="InvalidOperationException"/>。</summary>
        void Send<TCommand>(in TCommand command)
            where TCommand : struct, ICommand;

        /// <summary>执行查询。该消息类型未注册处理器时抛 <see cref="InvalidOperationException"/>。</summary>
        TResult Query<TQuery, TResult>(in TQuery query)
            where TQuery : struct, IQuery<TResult>;

        /// <summary>广播事件。零订阅者时静默成功。</summary>
        void Publish<TEvent>(in TEvent evt)
            where TEvent : struct, IEvent;

        /// <summary>注册命令处理器。同一消息类型重复注册抛 <see cref="InvalidOperationException"/>。</summary>
        void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
            where TCommand : struct, ICommand;

        /// <summary>注册查询处理器。同一消息类型重复注册抛 <see cref="InvalidOperationException"/>。</summary>
        void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>;

        /// <summary>订阅事件。允许多个订阅者，按注册顺序回调。</summary>
        void Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : struct, IEvent;
    }
}

using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Tests.Inventory
{
    /// <summary>
    /// 事件录制订阅器：一个事件类型一个实例，按广播顺序记录收到的全部回调。
    /// 只做记录、不做断言；刻意放在本目录而不是共享 <c>Fakes/</c>，避免与其他切片的并行作业互相干扰。
    /// </summary>
    /// <typeparam name="TEvent">被订阅的事件类型。</typeparam>
    internal sealed class EventRecorder<TEvent> : IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        private readonly List<TEvent> _events = new List<TEvent>();

        /// <summary>已收到的回调次数。</summary>
        internal int Count
        {
            get { return _events.Count; }
        }

        /// <summary>按广播顺序排列的事件（活引用，只读）。</summary>
        internal IReadOnlyList<TEvent> Events
        {
            get { return _events; }
        }

        /// <inheritdoc />
        public void Handle(in TEvent evt)
        {
            _events.Add(evt);
        }
    }
}

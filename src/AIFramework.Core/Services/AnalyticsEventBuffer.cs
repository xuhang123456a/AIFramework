using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 定长环形埋点缓冲：**容量固定、写满丢最老、热路径零分配**。
    /// <para>
    /// 单条事件是 24 字节左右的 struct（int + 3 float + 引用），放进预分配数组后
    /// <see cref="Track"/> 只有一次下标写与计数自增，不碰 List、不装箱、不拼字符串。
    /// </para>
    /// <para>
    /// 为什么"丢最老"而不是"丢最新"：游戏崩溃前的最后几十条事件最有诊断价值，
    /// 缓冲写满时应优先保住新事件；被挤掉的计入 <see cref="OverwrittenCount"/>，
    /// 便于判断"看板数据有缺口"是否由缓冲过小造成。
    /// </para>
    /// </summary>
    public sealed class AnalyticsEventBuffer
    {
        /// <summary>默认容量：256 条（按 24B/条约 6KB，足够覆盖一次战斗的事件量）。</summary>
        public const int DefaultCapacity = 256;

        private readonly AnalyticsEvent[] _events;
        private int _head;
        private int _count;
        private long _accepted;
        private long _dropped;
        private long _overwritten;

        public AnalyticsEventBuffer(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正。");
            }

            _events = new AnalyticsEvent[capacity];
        }

        public int Capacity
        {
            get { return _events.Length; }
        }

        /// <summary>当前缓存条数（≤ 容量）。</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>被接受（进入缓冲）的累计条数。</summary>
        public long AcceptedCount
        {
            get { return _accepted; }
        }

        /// <summary>因非法被丢弃的累计条数。</summary>
        public long DroppedCount
        {
            get { return _dropped; }
        }

        /// <summary>缓冲满时被挤掉的最老事件累计条数。</summary>
        public long OverwrittenCount
        {
            get { return _overwritten; }
        }

        /// <summary>写入一条事件；非法事件丢弃并计数。返回是否被接受。</summary>
        public bool TryTrack(in AnalyticsEvent analyticsEvent)
        {
            if (!analyticsEvent.IsValid)
            {
                _dropped++;
                return false;
            }

            int index = (_head + _count) % _events.Length;
            if (_count == _events.Length)
            {
                // 满：覆盖最老的一条（_head 指向最老），窗口后移一位。
                index = _head;
                _head = (_head + 1) % _events.Length;
                _overwritten++;
            }
            else
            {
                _count++;
            }

            _events[index] = analyticsEvent;
            _accepted++;
            return true;
        }

        /// <summary>按写入顺序取出全部事件（最老 → 最新）并清空缓冲。计数不清。</summary>
        public int Drain(Span<AnalyticsEvent> destination)
        {
            int copy = _count < destination.Length ? _count : destination.Length;
            for (int i = 0; i < copy; i++)
            {
                destination[i] = _events[(_head + i) % _events.Length];
            }

            if (copy == _count)
            {
                _head = 0;
                _count = 0;
            }
            else
            {
                // 只取走前 copy 条：窗口前移，剩余事件保持原有顺序留给下一次 Drain。
                _head = (_head + copy) % _events.Length;
                _count -= copy;
            }

            return copy;
        }

        /// <summary>按下标读第 i 条（0 = 最老）；越界返回 <see cref="AnalyticsEventIds.None"/> 的无效事件。</summary>
        public AnalyticsEvent Get(int index)
        {
            if (index < 0 || index >= _count)
            {
                return default;
            }

            return _events[(_head + index) % _events.Length];
        }

        /// <summary>清空缓冲（不动累计计数）。</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}

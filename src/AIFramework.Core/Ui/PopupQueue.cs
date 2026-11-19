#nullable enable
using System;
using AIFramework.Contracts.Ui;

namespace AIFramework.Core.Ui
{
    /// <summary>
    /// 弹窗队列（主板）：业务切片只负责"我要弹这个窗"，排队、优先级与去重由本类统一管。
    /// <para>
    /// 顺序规则：<b>priority 大者先出；同 priority 先进先出</b>。实现是"按优先级降序、同优先级按入队顺序"
    /// 排好的定长数组，出队永远取下标 0，插入时用稳定插入点（跳过所有 <c>priority &gt;= 新 priority</c> 的项），
    /// 因此同优先级之间自然保持 FIFO，不需要额外的序号字段。
    /// </para>
    /// <para>
    /// 容量规则：容量在构造期锁定，<b>永不扩容</b>。满了仍可入队，此时淘汰当前队尾
    /// （即最低优先级里最晚入队的那一项）并累加 <see cref="DroppedCount"/>，不抛异常——
    /// UI 队列被挤爆是预期内的降级，不该让业务切片崩掉。
    /// </para>
    /// <para>
    /// 分配说明：队列操作不在帧内热路径（弹窗是低频交互），允许线性搬移；但全程不使用 LINQ、不产生中间集合。
    /// 线程安全不在 Phase 1 范围内：与 <c>CqeBus</c> 一致，单线程使用。
    /// </para>
    /// </summary>
    public sealed class PopupQueue : IPopupQueue
    {
        /// <summary>构造时不给容量则用这个值（足够覆盖 Phase 1 的弹窗量级）。</summary>
        private const int DefaultCapacity = 64;

        /// <summary>排队项：定长数组按 "priority 降序 + 同优先级入队顺序" 排列，出队只取下标 0。</summary>
        private readonly Entry[] _entries;

        /// <summary>当前排队项数（含正在展示的那一项），永远不超过 <see cref="_entries"/> 的长度。</summary>
        private int _count;

        /// <summary>自构造（或最近一次 <see cref="Clear"/>）以来因容量不足被淘汰的累计条数。</summary>
        private int _droppedCount;

        /// <summary>创建一个容量固定的弹窗队列。</summary>
        /// <param name="capacity">容量上限，必须大于 0。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于等于 0。</exception>
        public PopupQueue(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Popup queue capacity must be greater than zero.");
            }

            _entries = new Entry[capacity];
            _count = 0;
            _droppedCount = 0;
        }

        /// <summary>容量上限（构造后不再变化，队列永不扩容）。</summary>
        public int Capacity
        {
            get { return _entries.Length; }
        }

        /// <summary>排队数（含正在展示的）。</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// 因容量不足被淘汰的累计条数（诊断用）。正常排队流程里非 0 即表示有弹窗被挤掉了；
        /// <see cref="Clear"/> 会把队列连同这个计数一起归零。
        /// </summary>
        public int DroppedCount
        {
            get { return _droppedCount; }
        }

        /// <summary>
        /// 入队。同一 <paramref name="popupId"/> 已在队列里时<b>直接忽略</b>（保留原优先级、原排队位置），
        /// 避免玩家狂点把队列塞满同一个窗。
        /// <para>
        /// 容量已满时：淘汰当前队尾（最低优先级；同优先级取最晚入队的那一项），并把新项插入——
        /// <b>新项必然进入</b>，不抛异常、不扩容。
        /// </para>
        /// </summary>
        /// <param name="popupId">弹窗 ID，由业务切片自定义。</param>
        /// <param name="priority">优先级，大者先出；允许任意 <see cref="int"/>（含负数）。</param>
        public void Enqueue(int popupId, int priority)
        {
            if (Contains(popupId))
            {
                return;
            }

            if (_count == _entries.Length)
            {
                // 队尾即"最低优先级里最晚入队"的那一项：砍掉它，随后正常插入。
                _count--;
                _droppedCount++;
            }

            // 稳定插入点：跳过所有 priority 不小于新项的排队项，新项落在同优先级队列的最末尾。
            int insertIndex = 0;
            while (insertIndex < _count && _entries[insertIndex].Priority >= priority)
            {
                insertIndex++;
            }

            for (int i = _count; i > insertIndex; i--)
            {
                _entries[i] = _entries[i - 1];
            }

            _entries[insertIndex] = new Entry(popupId, priority);
            _count++;
        }

        /// <summary>取出优先级最高的弹窗；空队列返回 <c>false</c> 且 <paramref name="popupId"/> 置 0。</summary>
        /// <param name="popupId">取出的弹窗 ID；空队列时为 0。</param>
        /// <returns>是否取到弹窗。</returns>
        public bool TryDequeue(out int popupId)
        {
            if (_count == 0)
            {
                popupId = 0;
                return false;
            }

            popupId = _entries[0].PopupId;

            for (int i = 1; i < _count; i++)
            {
                _entries[i - 1] = _entries[i];
            }

            _count--;
            return true;
        }

        /// <summary>是否在队列里（含正在展示的）。</summary>
        /// <param name="popupId">弹窗 ID。</param>
        /// <returns>在队列里返回 <c>true</c>。</returns>
        public bool Contains(int popupId)
        {
            return IndexOf(popupId) >= 0;
        }

        /// <summary>移除指定弹窗（玩家已处理或条件失效）；不在队列里则静默返回。</summary>
        /// <param name="popupId">弹窗 ID。</param>
        public void Remove(int popupId)
        {
            int index = IndexOf(popupId);
            if (index < 0)
            {
                return;
            }

            for (int i = index + 1; i < _count; i++)
            {
                _entries[i - 1] = _entries[i];
            }

            _count--;
        }

        /// <summary>清空队列并把 <see cref="DroppedCount"/> 归零（切场景 / 登出 / 热更换账号时用）。</summary>
        public void Clear()
        {
            _count = 0;
            _droppedCount = 0;
        }

        /// <summary>线性查找弹窗下标；找不到返回 -1。</summary>
        /// <param name="popupId">弹窗 ID。</param>
        /// <returns>数组下标，未找到为 -1。</returns>
        private int IndexOf(int popupId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].PopupId == popupId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>排队项：只有 ID 与优先级两个数值字段，无引用、无装箱。</summary>
        private struct Entry
        {
            internal Entry(int popupId, int priority)
            {
                PopupId = popupId;
                Priority = priority;
            }

            /// <summary>弹窗 ID。</summary>
            internal int PopupId { get; }

            /// <summary>优先级，大者先出。</summary>
            internal int Priority { get; }
        }
    }
}

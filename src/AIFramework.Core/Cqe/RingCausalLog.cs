#nullable enable
using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Core.Cqe
{
    /// <summary>
    /// 定长环形因果日志。
    /// <para>
    /// 热路径只做数值写入：<see cref="Record"/> 不扩容、不堆分配、不拼字符串、不做运行时类型反射，
    /// 字符串映射由 <see cref="CausalTagRegistry"/> 在导出或断言阶段按需完成。
    /// </para>
    /// </summary>
    public sealed class RingCausalLog : ICausalLog
    {
        private readonly CausalRecord[] _records;

        /// <summary>下一条记录的写入位置；等于容量时回绕到 0。</summary>
        private int _writeIndex;

        /// <summary>当前缓冲里的有效记录条数（不超过容量）。</summary>
        private int _validCount;

        /// <summary>自构造（或最近一次 <see cref="Clear"/>）以来已写入的总条数，可超过容量。</summary>
        private int _totalCount;

        /// <summary>已分配过的最大序号，单调递增，<see cref="Clear"/> 也不回退。</summary>
        private long _sequenceId;

        /// <summary>创建一个容量固定的环形日志。</summary>
        /// <param name="capacity">缓冲容量，必须大于 0。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于等于 0。</exception>
        public RingCausalLog(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Causal log capacity must be greater than zero.");
            }

            _records = new CausalRecord[capacity];
        }

        /// <summary>缓冲容量（定长，构造后不再变化）。</summary>
        public int Capacity
        {
            get { return _records.Length; }
        }

        /// <summary>
        /// 自构造（或最近一次 <see cref="Clear"/>）以来写入的总条数。
        /// 超过 <see cref="Capacity"/> 即表示发生过环形覆盖；当前有效记录数为其与容量的较小值。
        /// </summary>
        public int Count
        {
            get { return _totalCount; }
        }

        /// <summary>追加一条纯数值记录；环形缓冲满时覆盖最旧记录，不扩容、不抛异常。</summary>
        /// <param name="messageTagId">消息类型数值 ID。</param>
        /// <param name="sourceTagId">发起源数值 ID。</param>
        /// <param name="outcomeCode">结果码，见 <see cref="CausalOutcome"/>。</param>
        public void Record(int messageTagId, int sourceTagId, int outcomeCode)
        {
            _sequenceId++;
            _records[_writeIndex] = new CausalRecord(_sequenceId, messageTagId, sourceTagId, outcomeCode);

            _writeIndex++;
            if (_writeIndex == _records.Length)
            {
                _writeIndex = 0;
            }

            if (_validCount < _records.Length)
            {
                _validCount++;
            }

            _totalCount++;
        }

        /// <summary>按逻辑顺序读取：索引 0 为当前缓冲里最旧的记录。</summary>
        /// <param name="logicalIndex">逻辑下标，有效范围是 [0, 当前有效记录数)。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalIndex"/> 越界。</exception>
        public CausalRecord this[int logicalIndex]
        {
            get
            {
                if (logicalIndex < 0 || logicalIndex >= _validCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(logicalIndex),
                        logicalIndex,
                        "Logical index is outside the currently valid record range.");
                }

                // 最旧记录 = 写指针往回退 validCount 格（负数就回绕到缓冲区尾部）。
                int physicalIndex = _writeIndex - _validCount;
                if (physicalIndex < 0)
                {
                    physicalIndex += _records.Length;
                }

                physicalIndex += logicalIndex;
                if (physicalIndex >= _records.Length)
                {
                    physicalIndex -= _records.Length;
                }

                return _records[physicalIndex];
            }
        }

        /// <summary>
        /// 清空缓冲：有效记录数归零、写指针回到 0。
        /// 序列号不回退（<c>SequenceId</c> 仍然全局单调递增），避免清空后新旧记录序号重复。
        /// </summary>
        public void Clear()
        {
            _writeIndex = 0;
            _validCount = 0;
            _totalCount = 0;
        }
    }
}

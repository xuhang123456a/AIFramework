#nullable enable
using System;
using AIFramework.Contracts.Replay;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 内存内输入录制器：构造期一次预分配，<see cref="Record"/> 只写不扩容。
    /// <para>
    /// <b>溢出语义</b>：容量写满后继续调用 <see cref="Record"/> 只累加 <see cref="DroppedCount"/>，
    /// 不抛异常、不替换已有记录——录像尾部被截断是"可诊断的降级"，绝不能把整局录制炸掉。
    /// </para>
    /// <para>录制器不依赖任何内核类型：<see cref="IReplaySink"/> 由宿主在提交意图时顺带调用。</para>
    /// </summary>
    public sealed class ReplayRecorder : IReplaySink
    {
        /// <summary>默认容量：8192 条输入，够 1200 帧上限的强实时战局按每帧数条输入录制。</summary>
        private const int DefaultCapacity = 8192;

        private readonly RecordedIntent[] _items;

        private int _count;

        private int _droppedCount;

        /// <summary>创建录制器；容量在构造期一次性分配，运行期不再扩容。</summary>
        /// <param name="capacity">最大可记录输入条数，必须为正数。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于等于 0。</exception>
        public ReplayRecorder(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "录制容量必须为正数。");
            }

            _items = new RecordedIntent[capacity];
            _count = 0;
            _droppedCount = 0;
        }

        /// <summary>已成功记录的输入条数。</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>构造期确定的录制容量（固定不变）。</summary>
        public int Capacity
        {
            get { return _items.Length; }
        }

        /// <summary>因容量耗尽被丢弃的输入累计条数。</summary>
        public int DroppedCount
        {
            get { return _droppedCount; }
        }

        /// <inheritdoc />
        public void Record(int frame, int kind, int a, int b, int c, int d)
        {
            if (_count >= _items.Length)
            {
                _droppedCount++;
                return;
            }

            _items[_count] = new RecordedIntent(frame, kind, a, b, c, d);
            _count++;
        }

        /// <summary>
        /// 生成录像头。只做字段装箱，不隐式推导任何东西：
        /// 种子 / 关卡 / 布场数量由宿主给出，内核不参与。
        /// </summary>
        public ReplayHeader BuildHeader(int kernelType, int levelId, int randomSeed, int unitCount, int frameCount)
        {
            return new ReplayHeader(kernelType, levelId, randomSeed, unitCount, frameCount);
        }

        /// <summary>按录制顺序取第 <paramref name="index"/> 条输入。</summary>
        /// <exception cref="ArgumentOutOfRangeException">下标越界（合法范围是 <c>0..Count-1</c>）。</exception>
        public RecordedIntent Get(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "录制下标越界。");
            }

            return _items[index];
        }

        /// <summary>清空本次录制（记录与丢弃计数一并归零），预分配数组保持不变，可直接复用。</summary>
        public void Clear()
        {
            _count = 0;
            _droppedCount = 0;
        }
    }
}

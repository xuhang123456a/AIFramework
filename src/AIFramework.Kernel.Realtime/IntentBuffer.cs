#nullable enable
namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 定长意图缓冲：帧内唯一的"意图落点"。容量在构造期写死，
    /// <see cref="Add"/> 满则丢弃并累加 <see cref="OverflowCount"/>，<b>禁止扩容、禁止抛异常</b>。
    /// <para>
    /// 用 <c>ref T At(int)</c> 取元素引用，遍历时直接读字段，不做结构体拷贝、不做装箱。
    /// </para>
    /// </summary>
    /// <typeparam name="T">意图值类型。</typeparam>
    internal struct IntentBuffer<T>
        where T : struct
    {
        private readonly T[] _items;

        private int _count;

        private int _overflowCount;

        internal IntentBuffer(int capacity)
        {
            int size = capacity > 0 ? capacity : 1;
            _items = new T[size];
            _count = 0;
            _overflowCount = 0;
        }

        /// <summary>当前帧已写入的意图数。</summary>
        internal int Count
        {
            get { return _count; }
        }

        /// <summary>因缓冲已满被丢弃的意图累计数。</summary>
        internal int OverflowCount
        {
            get { return _overflowCount; }
        }

        /// <summary>写入一条意图；满则丢弃并计溢出。</summary>
        internal void Add(in T intent)
        {
            if (_count >= _items.Length)
            {
                _overflowCount++;
                return;
            }

            _items[_count] = intent;
            _count++;
        }

        /// <summary>取指定意图的只读引用（调用方保证下标在 <see cref="Count"/> 内）。</summary>
        internal ref T At(int index)
        {
            return ref _items[index];
        }

        /// <summary>清空计数，为下一帧复用同一块内存。</summary>
        internal void Clear()
        {
            _count = 0;
        }
    }
}

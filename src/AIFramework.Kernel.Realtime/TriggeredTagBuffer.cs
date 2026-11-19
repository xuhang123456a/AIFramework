#nullable enable
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 本局触发标签表：定长、去重、零分配追加。
    /// 超过容量直接丢弃新标签（禁止扩容），保证帧内写入永不分配。
    /// </summary>
    internal struct TriggeredTagBuffer
    {
        private readonly GameplayTag[] _tags;

        private int _count;

        internal TriggeredTagBuffer(int capacity)
        {
            int size = capacity > 0 ? capacity : 1;
            _tags = new GameplayTag[size];
            _count = 0;
        }

        internal int Count
        {
            get { return _count; }
        }

        /// <summary>按写入顺序取标签（不做拷贝，值类型直接返回）。</summary>
        internal GameplayTag At(int index)
        {
            return _tags[index];
        }

        /// <summary>追加一条标签：None 忽略、重复忽略、超容量丢弃。</summary>
        internal void Add(GameplayTag tag)
        {
            if (tag.IsNone)
            {
                return;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_tags[i].Id == tag.Id)
                {
                    return;
                }
            }

            if (_count >= _tags.Length)
            {
                return;
            }

            _tags[_count] = tag;
            _count++;
        }
    }
}

namespace AIFramework.Kernel.Turn.Match3
{
    /// <summary>
    /// 三消专用确定性随机源（xorshift32，状态为 32 位无符号整数）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：AIF003 禁止无参构造；且 <c>System.Random</c> 的内部算法
    /// 跨 .NET 版本不保证一致（.NET Framework 与 .NET 6+ 的默认算法不同）。内核要求
    /// 「同种子 + 同操作序列 ⇒ 同结局」，因此自带一个跨运行期（.NET / Unity / HybridCLR）逐位一致的算法。
    /// </para>
    /// <para>
    /// 状态通过 <see cref="Match3World.RandomState"/> 以 <c>int</c> 暴露，供测试断言可复现性。
    /// </para>
    /// </summary>
    internal struct Match3Random
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的全零状态是吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal Match3Random(int seed)
        {
            uint converted = unchecked((uint)seed);
            _state = converted != 0u ? converted : ZeroSeedReplacement;
        }

        /// <summary>当前状态的 <c>int</c> 视图（可能为负，只用于相等性断言）。</summary>
        internal int State
        {
            get { return unchecked((int)_state); }
        }

        /// <summary>推进一次 xorshift32 并返回 32 位结果。</summary>
        internal uint NextUInt()
        {
            uint state = _state;
            state ^= state << ShiftLeftA;
            state ^= state >> ShiftRightB;
            state ^= state << ShiftLeftC;
            _state = state;
            return state;
        }

        /// <summary>
        /// 返回 <c>[0, exclusiveMax)</c> 区间的整数。取 32 位乘法的高位而非取模，
        /// 避免低位取模偏置；<paramref name="exclusiveMax"/> 为 0/1 时仍消耗一次随机，保持取样节奏固定。
        /// </summary>
        internal int NextIndex(int exclusiveMax)
        {
            if (exclusiveMax <= 1)
            {
                NextUInt();
                return 0;
            }

            ulong scaled = (ulong)NextUInt() * (ulong)(uint)exclusiveMax;
            return (int)(scaled >> 32);
        }

        /// <summary>返回 <c>[1, colorCount]</c> 的宝石颜色 ID。</summary>
        internal int NextColor(int colorCount)
        {
            return NextIndex(colorCount) + 1;
        }
    }
}

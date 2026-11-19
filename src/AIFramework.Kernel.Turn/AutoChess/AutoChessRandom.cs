#nullable enable
namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// 自走棋专用确定性随机源（xorshift32，状态为 32 位无符号整数）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：AIF003 禁止无参构造；且 <c>System.Random</c> 的内部算法
    /// 跨 .NET 版本不保证一致（.NET Framework 与 .NET 6+ 默认算法不同）。自走棋要求
    /// 「同种子 + 同布局 ⇒ 同战斗结果」，因此自带一个在 .NET / Unity / HybridCLR 上逐位一致的算法。
    /// </para>
    /// <para>
    /// <b>消耗节奏</b>：只有"两轴都能缩短与目标距离"的走位才抽一次随机数（见 <see cref="AutoChessBattle"/>），
    /// 单轴走位与攻击不抽。节奏固定，才能保证同一操作序列下随机流完全一致。
    /// </para>
    /// </summary>
    internal struct AutoChessRandom
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的全零状态是吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal AutoChessRandom(int seed)
        {
            uint converted = unchecked((uint)seed);
            _state = converted != 0u ? converted : ZeroSeedReplacement;
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
        /// 返回 <c>[0, exclusiveMax)</c> 区间的整数。取 32 位乘法的高位而非取模，避免低位取模偏置；
        /// <paramref name="exclusiveMax"/> 为 0/1 时仍消耗一次随机，保持取样节奏固定。
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
    }
}

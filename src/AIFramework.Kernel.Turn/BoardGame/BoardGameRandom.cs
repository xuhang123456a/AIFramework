#nullable enable
namespace AIFramework.Kernel.Turn.BoardGame
{
    /// <summary>
    /// 黑白棋专用确定性随机源（xorshift32），只服务于 <see cref="BoardGameDifficulty.Easy"/>。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：AIF003 禁止无参构造，且它的内部算法跨 .NET 版本不保证一致；
    /// 内核要求"同种子 + 同落子序列 ⇒ 同结局"，Easy 的每一步都必须可复现，因此自带逐位一致的算法。
    /// </para>
    /// <para>
    /// 取值用 32 位乘法取高位而非取模，避免低位取模偏置；<see cref="NextIndex"/> 在
    /// <paramref name="exclusiveMax"/> 为 0/1 时仍消耗一次随机，保持取样节奏固定（重开会回到同一条随机流）。
    /// </para>
    /// </summary>
    internal struct BoardGameRandom
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的全零状态是吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal BoardGameRandom(int seed)
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

        /// <summary>返回 <c>[0, exclusiveMax)</c> 的整数；<paramref name="exclusiveMax"/> ≤ 1 时恒为 0。</summary>
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

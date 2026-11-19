#nullable enable
namespace AIFramework.Kernel.Turn.Rhythm
{
    /// <summary>
    /// 音游专用的确定性随机源（xorshift32，状态为 32 位无符号整数），只用来给谱面的
    /// "固定骨架"抹上一点种子相关的微调（音符间隔抖动、轨道偏移）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：AIF003 禁止无参构造，而且它的内部算法跨 .NET 版本
    /// 不保证一致（.NET Framework 与 .NET 6+ 默认算法不同）。谱面是"同种子 ⇒ 同曲谱"的可复现性根，
    /// 必须来自一个在 .NET / Unity / HybridCLR 上逐位一致的算法，因此内核自带一份。
    /// </para>
    /// <para>
    /// <b>为什么构造期还要做一次种子混合</b>：xorshift32 由小种子起步时首个输出量级很小（高位全 0），
    /// 于是 <see cref="NextIndex(int)"/> 的高位乘法会连续返回 0，谱面退化成几乎没有抖动。这里用
    /// splitmix32 终结器把任意种子打散成高低位都随机的非零状态，仍然全是整数位运算。
    /// </para>
    /// <para>
    /// 本结构只被 <see cref="RhythmChart"/> 持有，不对外暴露状态：可复现性由"同种子 ⇒ 同谱面"
    /// 从测试外部验证，避免为了断言把内部状态泄漏成公开 API。
    /// </para>
    /// </summary>
    internal struct RhythmRandom
    {
        /// <summary>种子混合后的兜底值：全零状态是 xorshift32 的吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal RhythmRandom(int seed)
        {
            _state = MixSeed(unchecked((uint)seed));
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

        /// <summary>splitmix32 终结器：把连续/极小的种子打散成高低位都随机的非零状态。</summary>
        private static uint MixSeed(uint seed)
        {
            uint mixed = seed + 0x9E3779B9u;
            mixed ^= mixed >> 16;
            mixed *= 0x7FEB352Du;
            mixed ^= mixed >> 15;
            mixed *= 0x846CA68Bu;
            mixed ^= mixed >> 16;
            return mixed != 0u ? mixed : ZeroSeedReplacement;
        }
    }
}

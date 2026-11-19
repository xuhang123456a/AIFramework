#nullable enable
namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// 跑酷专用确定性随机源（xorshift32，状态为 32 位无符号整数）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：AIF003 禁止无参构造；且 <c>System.Random</c> 的内部算法
    /// 跨 .NET 版本不保证一致（.NET Framework 与 .NET 6+ 的默认算法不同）。障碍种类序列是玩法可复现性的根，
    /// 必须来自一个在 .NET / Unity / HybridCLR 上逐位一致的算法，因此内核自带一份。
    /// </para>
    /// <para>
    /// <b>为什么构造期还要做一次种子混合</b>：xorshift32 由小种子起步时首个输出只有很小的量级（高位全 0），
    /// 直接按高位取百分比会让 Low/High 分布严重偏向一侧。<see cref="MixSeed"/> 用 splitmix32 终结器
    /// 把任意种子打散成高低位都随机的状态，仍然是纯整数位运算，跨运行期逐位一致。
    /// </para>
    /// <para>
    /// 本结构只被 <see cref="RunnerWorld"/> 持有，不对外暴露状态：可复现性由"同种子 ⇒ 同障碍序列"
    /// 在测试里从外部验证，避免把内部状态泄漏成公开 API。
    /// </para>
    /// </summary>
    internal struct RunnerRandom
    {
        /// <summary>种子混合后的兜底值：全零状态是 xorshift32 的吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal RunnerRandom(int seed)
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
        /// 返回 <c>[0, 100)</c> 的整数百分比。取 32 位乘法的高位而非取模，避免低位取模偏置：
        /// 判定 <c>percent &lt; 70</c> 即 Low，其余为 High。
        /// </summary>
        internal int NextPercent()
        {
            ulong scaled = (ulong)NextUInt() * 100UL;
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

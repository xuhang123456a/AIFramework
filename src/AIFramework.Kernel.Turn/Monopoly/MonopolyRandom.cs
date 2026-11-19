#nullable enable
namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// 大富翁专用确定性随机源（xorshift32，13/17/5 位移，周期 2^32-1）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：纯逻辑层禁用无参随机（AIF003），且"同种子 + 同 TryRoll 序列
    /// ⇒ 同棋盘/同现金/同事件序列"是本卡带的硬要求；只有自管状态的 RNG 才能保证跨 .NET 版本逐位一致。
    /// </para>
    /// <para>
    /// <b>取样口径</b>：<see cref="NextIndex"/> 用 32 位乘法取高位而非取模，避免低位取模偏置；
    /// <paramref name="exclusiveMax"/> ≤ 1 时仍消耗一次随机，保持取样节奏固定（将来加 Restart 时序列可完整重放）。
    /// </para>
    /// <para>
    /// <b>消耗顺序</b>（确定性契约的一部分）：构造期不抽随机；每次 <c>TryRoll</c> 先抽骰子，
    /// 只有真正落在机会格才再抽一张机会卡（"前进 3 格"若再落到机会格会继续抽）。
    /// 改动这个顺序会改变同种子的整局走向，属于破坏性变更。
    /// </para>
    /// </summary>
    internal struct MonopolyRandom
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的全零状态是吸收态（永远输出 0），必须避开。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private const int ShiftLeftA = 13;

        private const int ShiftRightB = 17;

        private const int ShiftLeftC = 5;

        /// <summary>32 位状态，保证非 0。</summary>
        private uint _state;

        internal MonopolyRandom(int seed)
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

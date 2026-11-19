#nullable enable
using System;
using AIFramework.Contracts.Ports;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 命令行侧的确定性随机源（xorshift32）。
    /// <para>
    /// <b>为什么不用 <c>System.Random</c></b>：一来 <c>AIF003</c> 禁止无参 <c>new Random()</c> 的隐蔽随机；
    /// 二来抽卡/扭蛋要能在冒烟里复现，固定种子 + 自实现算法才跨 .NET 版本稳定
    /// （<c>System.Random</c> 的算法在 .NET Core 3.0 前后换过）。
    /// </para>
    /// </summary>
    public sealed class XorShiftRandomSource : IRandomSource
    {
        private uint _state;

        /// <summary>以种子构造；种子为 0 时映射到非零常量（0 是 xorshift 的不动点）。</summary>
        /// <param name="seed">随机种子。</param>
        public XorShiftRandomSource(int seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : (uint)seed;
        }

        /// <summary>当前内部状态（断言/日志用）。</summary>
        public int State
        {
            get { return (int)_state; }
        }

        /// <inheritdoc />
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "上界必须大于下界。");
            }

            uint next = NextUInt32();
            uint range = (uint)(maxExclusive - minInclusive);
            return (int)(minInclusive + (long)(next % range));
        }

        private uint NextUInt32()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }
    }
}

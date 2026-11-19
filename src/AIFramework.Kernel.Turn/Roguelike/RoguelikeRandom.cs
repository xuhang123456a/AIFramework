#nullable enable
namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 确定性随机源：xorshift32（13/17/5 位移，周期 2^32-1）。
    /// <para>
    /// <b>为什么不用 <see cref="System.Random"/></b>：纯逻辑层禁用无参随机（AIF003），且肉鸽 run 必须
    /// "同种子 = 同地图 + 同奖励序列"，只有自管状态的 RNG 才能把 <see cref="Reset"/> 做成严格重放。
    /// </para>
    /// <para>
    /// 两份实例各管一条流：<see cref="RoguelikeMap"/> 用"地图流"生成节点种类，
    /// <see cref="RoguelikeWorld"/> 用"战斗流"洗牌与抽奖励卡。两条流互不影响，
    /// 因此改战斗侧逻辑不会挪动地图，反之亦然（这是可复现的最小代价）。
    /// </para>
    /// </summary>
    internal sealed class RoguelikeRandom
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的 0 是不动点，必须换一个非零种子。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private readonly uint _initialState;

        private uint _state;

        internal RoguelikeRandom(int seed)
        {
            _initialState = seed == 0 ? ZeroSeedReplacement : unchecked((uint)seed);
            _state = _initialState;
        }

        /// <summary>回到构造时的状态：同种子的 run 重开后序列完全一致。</summary>
        internal void Reset()
        {
            _state = _initialState;
        }

        /// <summary>下一个 32 位随机数。</summary>
        internal uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>[0, maxExclusive) 的整数。取模有极小偏差，洗牌/抽卡的统计性质不受影响。</summary>
        internal int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 1)
            {
                return 0;
            }

            return (int)(NextUInt() % (uint)maxExclusive);
        }
    }
}

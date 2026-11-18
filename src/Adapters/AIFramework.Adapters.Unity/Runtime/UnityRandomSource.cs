#nullable enable
using System;
using AIFramework.Contracts.Ports;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 局外随机源：内部包一个 <see cref="System.Random"/> 实例（适配层允许直接 new 它，
    /// <c>AIF003</c> 只约束纯逻辑程序集）。
    /// <para>
    /// <b>局内（Realtime / Turn 沙盒）绝对不要走这个端口</b>：局内随机必须由内核用
    /// <c>GameplayInitData.RandomSeed</c> 自己持有一份确定性随机流，否则回放、断线重连与服务端重算
    /// 三方结果会发散。这个端口只服务抽奖、外观随机、日志采样一类"局外、丢了也无所谓"的场景。
    /// </para>
    /// <para>
    /// 另有一条硬理由：<see cref="System.Random"/> 的算法在不同 .NET 运行时（Mono / IL2CPP 各版本）
    /// 之间<b>不保证一致</b>，同一种子在不同平台可能产出不同序列。
    /// </para>
    /// </summary>
    public sealed class UnityRandomSource : IRandomSource
    {
        /// <summary>
        /// 未显式传入种子时的默认种子。<b>为什么是固定值而不是 <c>Environment.TickCount</c></b>：
        /// 编辑器里连续两次 Play 得到同一串局外随机，排障时才有可比性；需要真随机请显式传种子。
        /// </summary>
        public const int DefaultSeed = 20261118;

        /// <summary>实际产出随机数的实例；单线程使用（Unity 主线程写存档/摇奖），不做并发保护。</summary>
        private readonly System.Random _random;

        /// <summary>使用 <see cref="DefaultSeed"/> 构造。</summary>
        public UnityRandomSource()
            : this(DefaultSeed)
        {
        }

        /// <summary>以指定种子构造；同一种子在同一运行时上序列可复现。</summary>
        /// <param name="seed">随机种子。</param>
        public UnityRandomSource(int seed)
        {
            _random = new System.Random(seed);
            Seed = seed;
        }

        /// <summary>构造时使用的种子，供日志排查"这一局用的是哪串随机"。</summary>
        public int Seed { get; }

        /// <inheritdoc />
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="minInclusive"/> 大于等于 <paramref name="maxExclusive"/>（区间为空）。
        /// </exception>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            // 空区间直接拒绝而不是静默返回 min：调用方把 [n, n) 当成"总是命中"是典型的边界写错，
            // 静默返回会让这个 bug 一直躺在业务里。
            if (minInclusive >= maxExclusive)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minInclusive),
                    minInclusive,
                    "minInclusive 必须小于 maxExclusive（区间必须非空）。");
            }

            return _random.Next(minInclusive, maxExclusive);
        }
    }
}

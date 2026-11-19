#nullable enable
using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Core.Gameplay
{
    /// <summary>
    /// 玩法会话宿主（主板）。
    /// <para>
    /// <c>Core</c> 绝不 <c>new</c> 任何内核：进出门只走 <see cref="IGameplayKernelFactory"/>，
    /// 由适配壳（Console / Unity Bootstrap）决定装配哪一套卡带。
    /// </para>
    /// </summary>
    public sealed class GameplaySessionHost
    {
        private readonly IGameplayKernelFactory _factory;
        private IGameplaySession? _session;

        /// <summary>用装配层提供的工厂创建宿主；此时还没有活跃会话。</summary>
        /// <param name="factory">内核工厂，由适配壳注入。</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> 为 <c>null</c>。</exception>
        public GameplaySessionHost(IGameplayKernelFactory factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            _factory = factory;
            _session = null;
        }

        /// <summary>当前是否存在未结算的活跃会话。</summary>
        public bool HasActiveSession
        {
            get { return _session != null; }
        }

        /// <summary>
        /// 当前活跃会话；无会话时为 <c>null</c>。
        /// <para>
        /// <b>为什么必须有这个属性</b>：宿主/关卡脚本常常需要"拿到刚开的这局"（例如转成
        /// <see cref="IRealtimeSandbox"/> 布场）。若只暴露 <see cref="HasActiveSession"/>，调用方只能自己
        /// 镜像一份引用，两侧一旦不同步就会重复 <see cref="Begin"/> 抛异常。
        /// </para>
        /// <para>只读；所有权仍在宿主，释放只能走 <see cref="EndAndCollect"/> 或 <see cref="Release"/>。</para>
        /// </summary>
        public IGameplaySession? Current
        {
            get { return _session; }
        }

        /// <summary>开启一局：向工厂申请会话并接管所有权。</summary>
        /// <param name="type">要装配的玩法卡带类型。</param>
        /// <param name="init">战前数据快照（<c>in</c> 只读引用，不复制）。</param>
        /// <returns>新建的会话，调用方只允许 <c>Step</c> 推进；结算必须走 <see cref="EndAndCollect"/>。</returns>
        /// <exception cref="InvalidOperationException">已有活跃会话未结算。</exception>
        public IGameplaySession Begin(GameplayKernelType type, in GameplayInitData init)
        {
            if (_session != null)
            {
                throw new InvalidOperationException(
                    "A gameplay session is already active; call EndAndCollect or Release before Begin.");
            }

            IGameplaySession session = _factory.Create(type, in init);
            _session = session;
            return session;
        }

        /// <summary>
        /// 结算当前会话：调用 <see cref="IGameplaySession.CollectResult"/> 后立即释放引用。
        /// <para>
        /// <c>CollectResult</c> 自身抛出的异常（未结束就结算、二次结算等）原样冒泡，不包装、不吞；
        /// 抛出时引用保持不动，由调用方决定 <see cref="Release"/> 还是继续等待结束。
        /// </para>
        /// </summary>
        /// <returns>本局结算报告。</returns>
        /// <exception cref="InvalidOperationException">当前没有活跃会话。</exception>
        public GameplayResult EndAndCollect()
        {
            IGameplaySession session = _session
                ?? throw new InvalidOperationException("No active gameplay session to end.");

            GameplayResult result = session.CollectResult();
            _session = null;
            return result;
        }

        /// <summary>丢弃当前会话引用（未结束时强制释放，仅供测试与异常兜底使用）。</summary>
        public void Release()
        {
            _session = null;
        }
    }
}

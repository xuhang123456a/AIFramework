#nullable enable
using UnityEngine;
// 注意：Unity 自己有一个 UnityEngine.ILogger（见 PlayerSettings 相关 API），
// 一旦同时 using 两个命名空间，裸写 ILogger 会撞成 CS0104。这里用别名把归属钉死。
using ILogger = AIFramework.Contracts.Ports.ILogger;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 日志端口转发到 Unity 控制台。
    /// <para>
    /// <b>为什么统一加前缀</b>：Unity Console 是全体插件共用的垃圾桶，没有前缀就没法过滤；
    /// 前缀也让人一眼看出"这行来自框架而不是业务代码"。
    /// </para>
    /// <para>
    /// 本类刻意不做时间戳、不拼调用栈：因果追溯走 <c>RingCausalLog</c>（数值、零分配），
    /// 日志只负责给人看的一次性信息，两者不混用。
    /// </para>
    /// </summary>
    public sealed class UnityLogger : ILogger
    {
        /// <summary>默认前缀。</summary>
        public const string DefaultPrefix = "[AIFramework] ";

        /// <summary>
        /// 实际使用的前缀。<b>为什么允许覆盖</b>：一个 Unity 工程里可能并存框架与多个适配壳实例，
        /// 加语境前缀（如 <c>[AIFramework/Battle] </c>）才能分辨来源，且不必让每个调用点自己拼。
        /// </summary>
        private readonly string _prefix;

        /// <summary>使用 <see cref="DefaultPrefix"/> 构造。</summary>
        public UnityLogger()
            : this(DefaultPrefix)
        {
        }

        /// <summary>以自定义前缀构造；传 <c>null</c> 或空串时回退 <see cref="DefaultPrefix"/>。</summary>
        /// <param name="prefix">日志前缀，通常以空格结尾。</param>
        public UnityLogger(string? prefix)
        {
            string? requested = prefix;
            _prefix = string.IsNullOrEmpty(requested) ? DefaultPrefix : requested;
        }

        /// <inheritdoc />
        public void Info(string message)
        {
            Debug.Log(_prefix + message);
        }

        /// <inheritdoc />
        public void Warn(string message)
        {
            Debug.LogWarning(_prefix + message);
        }

        /// <inheritdoc />
        public void Error(string message)
        {
            Debug.LogError(_prefix + message);
        }
    }
}

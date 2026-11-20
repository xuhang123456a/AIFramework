#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Ui
{
    /// <summary>Toast（浮层提示）的严重级别：决定颜色/图标/停留时长策略。</summary>
    public enum ToastSeverity : byte
    {
        /// <summary>中性信息（例如"已保存"）。</summary>
        Info = 0,

        /// <summary>成功（例如"购买成功"）。</summary>
        Success = 1,

        /// <summary>警告（例如"体力不足，已用钻石补足"）。</summary>
        Warning = 2,

        /// <summary>错误（例如"网络异常，请稍后重试"）。</summary>
        Error = 3,
    }

    /// <summary>
    /// 请求展示一条 Toast（事实事件，不关心谁在听）。
    /// <para>
    /// <b>为什么是事件而不是直接调 UI 端口</b>：业务切片（商店/任务/签到）只该说"这件事发生了、该提示什么"，
    /// 不该知道 Toast 长什么样、在哪个层、显示多久；表现层订阅本事件自行决定样式。
    /// 这样 Console 可以把它打日志、Unity 可以接 UI、测试可以断言文案。
    /// </para>
    /// </summary>
    public readonly struct ToastRequestedEvent : IEvent
    {
        /// <summary>构造一条 Toast 请求。</summary>
        /// <param name="text">最终展示文案（应当已经过本地化解析；空串会被表现层忽略）。</param>
        /// <param name="severity">严重级别。</param>
        /// <param name="durationMs">建议展示时长（毫秒，&lt;=0 表示由表现层决定默认值）。</param>
        public ToastRequestedEvent(string text, ToastSeverity severity, int durationMs)
        {
            Text = text;
            Severity = severity;
            DurationMs = durationMs;
        }

        /// <summary>最终展示文案。</summary>
        public string Text { get; }

        /// <summary>严重级别。</summary>
        public ToastSeverity Severity { get; }

        /// <summary>建议展示时长（毫秒；&lt;=0 = 表现层默认）。</summary>
        public int DurationMs { get; }

        public override string ToString()
        {
            return "[" + Severity + "] " + Text;
        }
    }

    /// <summary>
    /// Toast 表现端口：由引擎适配层实现（Console 打日志、Unity 接 UI 层）。
    /// <para>
    /// <b>为什么还要端口</b>：事件负责"通知"，端口负责"兜底直连"。装配早期（事件总线上还没有表现层订阅者）
    /// 或某些必须立即展示的路径（登录失败弹窗被拒绝后）需要直接调用；两者并存，但业务代码优先走事件。
    /// </para>
    /// </summary>
    public interface IToastPresenter
    {
        /// <summary>展示一条 Toast；实现方不得抛异常（表现层失败不能反向影响业务）。</summary>
        void Show(in ToastRequestedEvent toast);
    }
}

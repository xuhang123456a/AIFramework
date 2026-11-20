#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Ui;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 的 Toast 表现层：Phase 1 先落到日志端口（真实 UI 层接入前，提示至少不会丢）。
    /// <para>
    /// <b>为什么不直接上 UI</b>：Toast 视觉（字体/动画/层级）需要真实 UI 资源与设计，且当前工程没有统一 UI 栈；
    /// 先把"文案 + 级别 + 时长"送达表现层，UI 接入时只替换本类，业务与路由零改动。
    /// </para>
    /// </summary>
    public sealed class UnityToastPresenter : IToastPresenter
    {
        private readonly ILogger _logger;

        /// <summary>构造。</summary>
        /// <param name="logger">日志端口。</param>
        public UnityToastPresenter(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>已展示条数（测试断言"请求确实到达表现层"）。</summary>
        public int ShownCount { get; private set; }

        /// <summary>记录一条提示日志。</summary>
        /// <param name="toast">提示。</param>
        public void Show(in ToastRequestedEvent toast)
        {
            if (string.IsNullOrEmpty(toast.Text))
            {
                return;
            }

            ShownCount++;
            string line = "[Toast][" + toast.Severity + "] " + toast.Text;
            if (toast.Severity == ToastSeverity.Error)
            {
                _logger.Error(line);
            }
            else if (toast.Severity == ToastSeverity.Warning)
            {
                _logger.Warn(line);
            }
            else
            {
                _logger.Info(line);
            }
        }
    }
}

#nullable enable
using System;

using AIFramework.Contracts.Ui;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// Console 的 Toast 表现层：把提示按统一格式打到输出流，便于无头验收与日志排查。
    /// <para>
    /// <b>为什么 Console 也要实现</b>：错误提示链路（错误码 → 文案 → 展示）在没有 UI 的环境里同样要有证据；
    /// 只做 Unity 版本会让"提示到底显示了什么"在 CI 里完全不可见。
    /// </para>
    /// </summary>
    public sealed class ConsoleToastPresenter : IToastPresenter
    {
        private readonly IIOWriter _writer;

        /// <summary>构造。</summary>
        /// <param name="writer">输出流。</param>
        public ConsoleToastPresenter(IIOWriter writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        /// <summary>已展示条数。</summary>
        public int ShownCount { get; private set; }

        /// <summary>按 <c>[Toast][级别] 文案</c> 输出一行；空文案直接忽略（与表现层口径一致）。</summary>
        /// <param name="toast">提示。</param>
        public void Show(in ToastRequestedEvent toast)
        {
            if (string.IsNullOrEmpty(toast.Text))
            {
                return;
            }

            ShownCount++;
            _writer.WriteLine("[Toast][" + toast.Severity + "] " + toast.Text);
        }
    }
}

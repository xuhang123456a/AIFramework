using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Ui;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 错误码 → 本地化文案 → Toast 的统一路由。
    /// <para>
    /// <b>它解决的问题</b>：以前每个入口各自拼一句中文（"购买失败"/"网络错误"），
    /// 玩家看到的是拼装串，翻译无法覆盖，改文案要翻遍代码。现在业务只上报错误码，
    /// 由本类完成：错误码 → 目录 → 本地化 key → 文案（缺失回退兜底）→ 广播 <see cref="ToastRequestedEvent"/>。
    /// </para>
    /// <para>
    /// <b>为什么要吞掉本地化查询异常</b>：本地化表缺失/查询处理器没注册时，错误提示本身绝不能再抛异常
    /// （否则"提示失败"会盖掉真正要处理的业务失败）；此时退回兜底文案并计数，便于观测。
    /// </para>
    /// </summary>
    public sealed class ErrorToastRouter
    {
        /// <summary>未知错误码的兜底 key（与 <see cref="ErrorCatalog.UnknownKey"/> 同值）。</summary>
        public const string UnknownKey = ErrorCatalog.UnknownKey;

        private readonly ICqeBus _bus;
        private readonly IToastPresenter? _presenter;
        private int _routedCount;
        private int _fallbackCount;
        private int _unknownCodeCount;

        /// <summary>构造路由器。</summary>
        /// <param name="bus">CQE 总线（必须已注册本地化查询处理器，否则全部走兜底文案）。</param>
        /// <param name="presenter">可选直连表现层；为 <c>null</c> 时只广播事件。</param>
        public ErrorToastRouter(ICqeBus bus, IToastPresenter? presenter = null)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _presenter = presenter;
        }

        /// <summary>已路由的 Toast 条数（生命周期累计）。</summary>
        public int RoutedCount
        {
            get { return _routedCount; }
        }

        /// <summary>因本地化缺失而使用兜底文案的条数。</summary>
        public int FallbackCount
        {
            get { return _fallbackCount; }
        }

        /// <summary>未收录错误码的条数（走"未知错误"兜底）。</summary>
        public int UnknownCodeCount
        {
            get { return _unknownCodeCount; }
        }

        /// <summary>
        /// 路由一个错误码：解析文案 → 广播 <see cref="ToastRequestedEvent"/> →（可选）直连表现层。
        /// </summary>
        /// <param name="errorCode">错误码。</param>
        /// <param name="durationMs">建议展示时长（&lt;=0 由表现层决定）。</param>
        /// <returns>实际展示文案（便于调用方/测试断言）。</returns>
        public string Route(int errorCode, int durationMs = 0)
        {
            if (!ErrorCatalog.TryGet(errorCode, out ErrorDescriptor descriptor))
            {
                _unknownCodeCount++;
                descriptor = new ErrorDescriptor(errorCode, UnknownKey, ErrorCatalog.UnknownMessage, ToastSeverity.Error);
            }

            string text = ResolveText(descriptor);
            var toast = new ToastRequestedEvent(text, descriptor.Severity, durationMs);

            _routedCount++;
            _bus.Publish(in toast);
            _presenter?.Show(in toast);

            return text;
        }

        /// <summary>按本地化 key 直接路由（用于没有错误码的特殊提示，例如活动文案）。</summary>
        /// <param name="localizationKey">本地化 key。</param>
        /// <param name="fallbackMessage">本地化缺失时的兜底文案。</param>
        /// <param name="severity">级别。</param>
        /// <param name="durationMs">建议展示时长。</param>
        /// <returns>实际展示文案。</returns>
        public string RouteLocalized(string localizationKey, string fallbackMessage, ToastSeverity severity, int durationMs = 0)
        {
            if (localizationKey == null)
            {
                throw new ArgumentNullException(nameof(localizationKey));
            }

            string text = ResolveText(new ErrorDescriptor(0, localizationKey, fallbackMessage ?? string.Empty, severity));
            var toast = new ToastRequestedEvent(text, severity, durationMs);

            _routedCount++;
            _bus.Publish(in toast);
            _presenter?.Show(in toast);

            return text;
        }

        private string ResolveText(in ErrorDescriptor descriptor)
        {
            string key = descriptor.LocalizationKey;
            string fallback = descriptor.FallbackMessage;

            if (string.IsNullOrEmpty(key))
            {
                _fallbackCount++;
                return fallback;
            }

            try
            {
                var request = new LocalizationQueryRequest(key);
                LocalizedText localized = _bus.Query<LocalizationQueryRequest, LocalizedText>(in request);
                if (localized.Found && !string.IsNullOrEmpty(localized.Text))
                {
                    return localized.Text;
                }
            }
            catch (InvalidOperationException)
            {
                // 本地化处理器未注册：错误提示本身不能再抛，退回兜底文案并计数。
            }

            _fallbackCount++;
            return fallback;
        }
    }
}

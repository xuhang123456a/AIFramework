using AIFramework.Contracts.Ui;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 错误码目录：错误码 → 本地化 key + 兜底文案 + 严重级别。
    /// <para>
    /// <b>为什么需要它</b>：错误提示散落在各切片里会立刻出现三个问题——
    /// ①同一错误在不同入口文案不一致；②改文案要翻代码；③没人知道某个错误码还在不在用。
    /// 收口到一张表后：业务只上报错误码，文案与级别由目录决定，缺翻译时还有兜底文案（不会露出 <c>err.1001</c>）。
    /// </para>
    /// <para>
    /// <b>为什么先用代码常量而不是配置表</b>：错误码与契约里的枚举/常量强关联，过早放进配置表会形成
    /// "表里有、代码里没有"的幽灵码。等错误码稳定后再迁到 <c>TbErrorCode</c> 并把本目录改成表驱动。
    /// </para>
    /// </summary>
    public static class ErrorCatalog
    {
        /// <summary>未知错误码的兜底本地化 key。</summary>
        public const string UnknownKey = "err.unknown";

        /// <summary>未知错误码的兜底文案（本地化缺失时直接展示）。</summary>
        public const string UnknownMessage = "操作失败，请稍后重试";

        private static readonly ErrorDescriptor[] Descriptors =
        {
            // 网络/服务端
            new ErrorDescriptor(1001, "err.network", "网络异常，请检查网络后重试", ToastSeverity.Error),
            new ErrorDescriptor(1002, "err.timeout", "请求超时，请稍后重试", ToastSeverity.Error),
            new ErrorDescriptor(1003, "err.server_busy", "服务器繁忙，请稍后重试", ToastSeverity.Warning),

            // 账号/会话
            new ErrorDescriptor(2001, "err.session_expired", "登录已过期，请重新登录", ToastSeverity.Warning),
            new ErrorDescriptor(2002, "err.account_banned", "账号已被封禁", ToastSeverity.Error),

            // 商店/购买
            new ErrorDescriptor(3001, "err.purchase_unavailable", "商店暂不可用，请稍后重试", ToastSeverity.Error),
            new ErrorDescriptor(3002, "err.purchase_cancelled", "已取消购买", ToastSeverity.Info),
            new ErrorDescriptor(3003, "err.already_owned", "已拥有该商品", ToastSeverity.Info),
            new ErrorDescriptor(3004, "err.insufficient_funds", "余额不足", ToastSeverity.Warning),
            new ErrorDescriptor(3005, "err.purchase_failed", "购买失败，请稍后重试", ToastSeverity.Error),

            // 背包/资源
            new ErrorDescriptor(4001, "err.inventory_full", "背包已满，请先清理", ToastSeverity.Warning),
            new ErrorDescriptor(4002, "err.not_enough_items", "道具不足", ToastSeverity.Warning),

            // 玩法/关卡
            new ErrorDescriptor(5001, "err.level_locked", "关卡尚未解锁", ToastSeverity.Info),
            new ErrorDescriptor(5002, "err.energy_empty", "体力不足", ToastSeverity.Warning),
            new ErrorDescriptor(5003, "err.already_claimed", "已领取", ToastSeverity.Info),
        };

        /// <summary>错误码是否在目录里（未收录的码会走兜底文案，并在日志里提示）。</summary>
        /// <param name="errorCode">错误码。</param>
        /// <returns>收录返回 <c>true</c>。</returns>
        public static bool Contains(int errorCode)
        {
            return TryGet(errorCode, out _);
        }

        /// <summary>按错误码取描述；未收录返回 <c>false</c>。</summary>
        /// <param name="errorCode">错误码。</param>
        /// <param name="descriptor">描述（未收录时为 <c>default</c>）。</param>
        /// <returns>收录返回 <c>true</c>。</returns>
        public static bool TryGet(int errorCode, out ErrorDescriptor descriptor)
        {
            for (int i = 0; i < Descriptors.Length; i++)
            {
                if (Descriptors[i].Code == errorCode)
                {
                    descriptor = Descriptors[i];
                    return true;
                }
            }

            descriptor = default;
            return false;
        }

        /// <summary>目录快照（测试与工具用；返回副本，调用方改不动内部表）。</summary>
        /// <returns>全部错误描述。</returns>
        public static ErrorDescriptor[] Snapshot()
        {
            var copy = new ErrorDescriptor[Descriptors.Length];
            Descriptors.CopyTo(copy, 0);
            return copy;
        }
    }

    /// <summary>一条错误码描述：码 + 本地化 key + 兜底文案 + 严重级别。</summary>
    public readonly struct ErrorDescriptor
    {
        /// <summary>构造描述。</summary>
        /// <param name="code">错误码（正数）。</param>
        /// <param name="localizationKey">本地化 key（缺失时用兜底文案）。</param>
        /// <param name="fallbackMessage">兜底文案（保证玩家永远不会看到 key 本身）。</param>
        /// <param name="severity">Toast 级别。</param>
        public ErrorDescriptor(int code, string localizationKey, string fallbackMessage, ToastSeverity severity)
        {
            Code = code;
            LocalizationKey = localizationKey;
            FallbackMessage = fallbackMessage;
            Severity = severity;
        }

        /// <summary>错误码。</summary>
        public int Code { get; }

        /// <summary>本地化 key。</summary>
        public string LocalizationKey { get; }

        /// <summary>兜底文案。</summary>
        public string FallbackMessage { get; }

        /// <summary>Toast 级别。</summary>
        public ToastSeverity Severity { get; }
    }
}

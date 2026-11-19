#nullable enable
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Localization;

namespace AIFramework.Features.Localization
{
    /// <summary>
    /// 切换当前语言。<b>只接受已定义语言</b>：负数值、越界值一律静默拒绝；
    /// 与当前语言相同也静默忽略，两种情况都不发事件、不改状态。
    /// </summary>
    public readonly struct SetLanguageCommand : ICommand
    {
        /// <summary>请求切换到目标语言。</summary>
        /// <param name="language">目标语言；未定义数值会在 <c>Handle</c> 内被静默拒绝。</param>
        public SetLanguageCommand(GameLanguage language)
        {
            Language = language;
        }

        /// <summary>目标语言。</summary>
        public GameLanguage Language { get; }
    }

    /// <summary>
    /// 语言真实变更事实。只有"合法且与当前不同"的切换才广播，
    /// 同语言请求与非法数值不会发本事件（订阅方不必自己再判重）。
    /// </summary>
    public readonly struct LanguageChangedEvent : IEvent
    {
        /// <summary>广播一次语言变更。</summary>
        /// <param name="language">变更后的语言。</param>
        public LanguageChangedEvent(GameLanguage language)
        {
            Language = language;
        }

        /// <summary>变更后的语言。</summary>
        public GameLanguage Language { get; }
    }
}

#nullable enable
using AIFramework.Contracts.Localization;

namespace AIFramework.Features.Localization
{
    /// <summary>
    /// 语言数值合法性白名单：表解析与 <c>SetLanguageCommand</c> 共用同一份判定，
    /// 避免"表里能读、命令里能设"两套规则漂移。
    /// <para>
    /// 用显式 <c>switch</c> 而不是 <c>Enum.IsDefined</c>：后者走反射且装箱，
    /// 也被"数值稳定"的契约明确排除；新增语言必须先在这里开口子。
    /// </para>
    /// </summary>
    internal static class LocalizationLanguage
    {
        /// <summary>该数值是否是已定义的语言（0..2，非负数、不越界）。</summary>
        /// <param name="language">待判定的枚举值（可能来自不可信字节或外部输入）。</param>
        /// <returns>已定义返回 <c>true</c>，否则返回 <c>false</c>。</returns>
        internal static bool IsSupported(GameLanguage language)
        {
            switch (language)
            {
                case GameLanguage.ChineseSimplified:
                case GameLanguage.English:
                case GameLanguage.Japanese:
                    return true;
                default:
                    return false;
            }
        }
    }
}

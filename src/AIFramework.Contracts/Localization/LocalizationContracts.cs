#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Localization
{
    /// <summary>语言枚举：数值稳定，存档/配置里按数值存。</summary>
    /// <remarks>禁止重排或复用数值：一旦发布，旧存档里的数值必须仍解释为同一语言。</remarks>
    public enum GameLanguage
    {
        /// <summary>简体中文（0，默认语言）。</summary>
        ChineseSimplified = 0,

        /// <summary>英语（1）。</summary>
        English = 1,

        /// <summary>日语（2）。</summary>
        Japanese = 2,
    }

    /// <summary>本地化查询端口：UI 只问 key 对应文案，不关心表结构。</summary>
    public interface ILocalizationQuery : Cqe.IQuery<LocalizedText>
    {
        /// <summary>文案键（与子表 <c>TbLocalization.key</c> 同义）。</summary>
        string Key { get; }
    }

    /// <summary>
    /// 文案查询请求体。放在 Contracts 而不是切片里，因为**任何切片与适配层都可能查文案**；
    /// 若定义在切片内，其它切片一引用就触发 AIF001（跨切片互引）。
    /// </summary>
    public readonly struct LocalizationQueryRequest : ILocalizationQuery
    {
        /// <summary>用文案键构造请求。</summary>
        /// <param name="key">文案键；<c>null</c> 按空键处理，查询侧仍返回非 null 文本。</param>
        public LocalizationQueryRequest(string key)
        {
            Key = key;
        }

        /// <inheritdoc />
        public string Key { get; }
    }

    /// <summary>文案结果：Found=false 时回退到 Key 本身（永不返回 null/空串）。</summary>
    public readonly struct LocalizedText
    {
        /// <summary>构造一条查询结果。</summary>
        /// <param name="key">查询用的文案键。</param>
        /// <param name="text">命中文案；未命中时与 <paramref name="key"/> 相同。</param>
        /// <param name="found">是否真正在表里命中（回退到 key 时为 <c>false</c>）。</param>
        public LocalizedText(string key, string text, bool found)
        {
            Key = key;
            Text = text;
            Found = found;
        }

        /// <summary>查询用的文案键，原样回显。</summary>
        public string Key { get; }

        /// <summary>展示文案：命中即表内文本，未命中即 <see cref="Key"/> 本身。</summary>
        public string Text { get; }

        /// <summary>是否命中表（含回退到默认语言命中）。</summary>
        public bool Found { get; }
    }
}

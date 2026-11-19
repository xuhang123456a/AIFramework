#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Localization
{
    /// <summary>
    /// 本地化业务规则：当前语言、回退链与"什么时候算真切换"全部收口在这里。
    /// <para>
    /// 查询回退链固定三步：<see cref="CurrentLanguage"/> → <c>defaultLanguage</c> → key 本身。
    /// 任何情况下 <c>Text</c> 都不返回 null（空 key 输入原样回显空串），因此调用方不需要再判空。
    /// </para>
    /// <para>
    /// 构造里同时完成子表解析（表缺失直接抛 <see cref="InvalidOperationException"/>），
    /// 但<b>不</b>自动注册总线：装配入口必须先调用 <see cref="Register"/>。
    /// </para>
    /// </summary>
    public sealed class LocalizationHandler : IQueryHandler<LocalizationQueryRequest, LocalizedText>,
                                              ICommandHandler<SetLanguageCommand>
    {
        private readonly ICqeBus _bus;
        private readonly LocalizationTables _tables;
        private readonly GameLanguage _defaultLanguage;
        private GameLanguage _currentLanguage;

        /// <summary>加载子表并设定当前语言初值。</summary>
        /// <param name="bus">装配入口注入的总线（只用于广播语言变更）。</param>
        /// <param name="loader">配置表加载端口，只按固定表名取本切片子表。</param>
        /// <param name="defaultLanguage">默认语言，同时作为回退语言与 <see cref="CurrentLanguage"/> 初值。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">子表不存在。</exception>
        /// <exception cref="System.IO.InvalidDataException">子表字节布局不合法。</exception>
        public LocalizationHandler(
            ICqeBus bus,
            IBinaryTableLoader loader,
            GameLanguage defaultLanguage = GameLanguage.ChineseSimplified)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new LocalizationTables(loader);

            // 当前语言初值就是默认语言：避免"启动瞬间当前语言缺文案、兜底又兜回自己"的空转。
            _defaultLanguage = defaultLanguage;
            _currentLanguage = defaultLanguage;
        }

        /// <summary>当前语言（查询回退链的起点）。</summary>
        public GameLanguage CurrentLanguage
        {
            get { return _currentLanguage; }
        }

        /// <summary>注册 1 条查询处理器与 1 条命令处理器；重复注册由总线抛异常，本切片不吞。</summary>
        /// <param name="bus">装配入口注入的总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterQueryHandler<LocalizationQueryRequest, LocalizedText>(this);
            bus.RegisterCommandHandler<SetLanguageCommand>(this);
        }

        /// <summary>
        /// 文案查询：当前语言命中即返回；否则回退默认语言；仍没有则把 key 当文案返回
        /// （<see cref="LocalizedText.Found"/> = <c>false</c>），永不返回 null。
        /// </summary>
        /// <param name="query">查询请求，<c>Key</c> 为 <c>null</c> 时按空键处理。</param>
        /// <returns>文案结果。</returns>
        public LocalizedText Handle(in LocalizationQueryRequest query)
        {
            string key = query.Key ?? string.Empty;

            string? text = _tables.Find(key, _currentLanguage);
            if (text == null && _currentLanguage != _defaultLanguage)
            {
                text = _tables.Find(key, _defaultLanguage);
            }

            return text == null
                ? new LocalizedText(key, key, false)
                : new LocalizedText(key, text, true);
        }

        /// <summary>
        /// 切换语言：未定义数值 → 静默拒绝；与当前相同 → 静默忽略；
        /// 合法且不同 → 先改状态再 <c>Publish(LanguageChangedEvent)</c>。
        /// </summary>
        /// <param name="command">切换请求。</param>
        public void Handle(in SetLanguageCommand command)
        {
            if (!LocalizationLanguage.IsSupported(command.Language))
            {
                return;
            }

            if (command.Language == _currentLanguage)
            {
                return;
            }

            _currentLanguage = command.Language;
            _bus.Publish(new LanguageChangedEvent(command.Language));
        }
    }
}

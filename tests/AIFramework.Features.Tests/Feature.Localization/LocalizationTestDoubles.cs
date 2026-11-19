#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Localization;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.Localization
{
    /// <summary>一条"语言 + 文案"夹具。</summary>
    public readonly struct LanguageTextFixture
    {
        public LanguageTextFixture(GameLanguage language, string text)
        {
            Language = language;
            Text = text;
        }

        /// <summary>该文案的语言。</summary>
        public GameLanguage Language { get; }

        /// <summary>该语言下的文案。</summary>
        public string Text { get; }
    }

    /// <summary>
    /// <c>TbLocalization</c> 字节构造器（布局见切片 <c>CONTRACT.md</c> §3）。
    /// 合法表走 <see cref="AddKey"/>；畸形表走 <see cref="AddRawEntry"/> + <see cref="RawEntry"/> /
    /// <see cref="RawLanguageText"/>，以便逐字段造出负长度、重复语言、未知语言等字节流。
    /// </summary>
    public sealed class LocalizationPayloadBuilder
    {
        private const int Int32Bytes = 4;

        private readonly List<byte[]> _entries = new List<byte[]>();

        /// <summary>追加一条合法 key 条目（key + 若干语言文案）。</summary>
        public LocalizationPayloadBuilder AddKey(string key, params LanguageTextFixture[] texts)
        {
            _entries.Add(Entry(key, texts));
            return this;
        }

        /// <summary>追加一段已经编码好的 key 条目原始字节，用于构造畸形表。</summary>
        public LocalizationPayloadBuilder AddRawEntry(byte[] entryBytes)
        {
            if (entryBytes == null)
            {
                throw new ArgumentNullException(nameof(entryBytes));
            }

            _entries.Add(entryBytes);
            return this;
        }

        /// <summary>生成头部 <c>keyCount</c> 等于实际条目数的合法字节。</summary>
        public byte[] Build()
        {
            return BuildDeclaringKeys(_entries.Count);
        }

        /// <summary>生成头部声明 <paramref name="declaredKeyCount"/>、实际仍写入全部已添加条目的字节。</summary>
        public byte[] BuildDeclaringKeys(int declaredKeyCount)
        {
            int length = Int32Bytes;
            for (int i = 0; i < _entries.Count; i++)
            {
                length += _entries[i].Length;
            }

            byte[] payload = new byte[length];
            WriteInt32(payload, 0, declaredKeyCount);

            int offset = Int32Bytes;
            for (int i = 0; i < _entries.Count; i++)
            {
                Array.Copy(_entries[i], 0, payload, offset, _entries[i].Length);
                offset += _entries[i].Length;
            }

            return payload;
        }

        /// <summary>造一条合法 key 条目：<c>[keyLen][key][langCount][lang][textLen][text]...</c>。</summary>
        public static byte[] Entry(string key, params LanguageTextFixture[] texts)
        {
            byte[] keyBytes = Utf8(key);

            int length = Int32Bytes + keyBytes.Length + Int32Bytes;
            for (int i = 0; i < texts.Length; i++)
            {
                length += Int32Bytes + Int32Bytes + Utf8(texts[i].Text).Length;
            }

            byte[] entry = new byte[length];
            WriteInt32(entry, 0, keyBytes.Length);
            Array.Copy(keyBytes, 0, entry, Int32Bytes, keyBytes.Length);

            int offset = Int32Bytes + keyBytes.Length;
            WriteInt32(entry, offset, texts.Length);
            offset += Int32Bytes;

            for (int i = 0; i < texts.Length; i++)
            {
                byte[] textBytes = Utf8(texts[i].Text);
                WriteInt32(entry, offset, (int)texts[i].Language);
                WriteInt32(entry, offset + Int32Bytes, textBytes.Length);
                Array.Copy(textBytes, 0, entry, offset + (Int32Bytes * 2), textBytes.Length);
                offset += (Int32Bytes * 2) + textBytes.Length;
            }

            return entry;
        }

        /// <summary>造一条 key 条目骨架：字段值由调用方指定，可与真实字节长度不一致。</summary>
        public static byte[] RawEntry(int keyLength, byte[] keyBytes, int languageCount, params byte[][] languageEntries)
        {
            int length = Int32Bytes + keyBytes.Length + Int32Bytes;
            for (int i = 0; i < languageEntries.Length; i++)
            {
                length += languageEntries[i].Length;
            }

            byte[] entry = new byte[length];
            WriteInt32(entry, 0, keyLength);
            Array.Copy(keyBytes, 0, entry, Int32Bytes, keyBytes.Length);
            WriteInt32(entry, Int32Bytes + keyBytes.Length, languageCount);

            int offset = (Int32Bytes * 2) + keyBytes.Length;
            for (int i = 0; i < languageEntries.Length; i++)
            {
                Array.Copy(languageEntries[i], 0, entry, offset, languageEntries[i].Length);
                offset += languageEntries[i].Length;
            }

            return entry;
        }

        /// <summary>造一条语言条目：<c>[lang][textLen][text]</c>，两个 int32 由调用方指定。</summary>
        public static byte[] RawLanguageText(int languageValue, int textLength, byte[] textBytes)
        {
            byte[] entry = new byte[(Int32Bytes * 2) + textBytes.Length];
            WriteInt32(entry, 0, languageValue);
            WriteInt32(entry, Int32Bytes, textLength);
            Array.Copy(textBytes, 0, entry, Int32Bytes * 2, textBytes.Length);
            return entry;
        }

        /// <summary>UTF-8 编码（与切片解析端同一编码）。</summary>
        public static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        /// <summary>截断到前 <paramref name="keptBytes"/> 字节，用于造"字节流被截断"。</summary>
        public static byte[] Truncate(byte[] payload, int keptBytes)
        {
            if (keptBytes < 0 || keptBytes > payload.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(keptBytes), keptBytes, "截断长度必须落在 0..payload.Length。");
            }

            byte[] truncated = new byte[keptBytes];
            Array.Copy(payload, truncated, keptBytes);
            return truncated;
        }

        /// <summary>在尾部追加 <paramref name="extraBytes"/> 个 0 字节，用于造"尾部残留"。</summary>
        public static byte[] AppendPadding(byte[] payload, int extraBytes)
        {
            if (extraBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extraBytes), extraBytes, "追加字节数不能为负。");
            }

            byte[] padded = new byte[payload.Length + extraBytes];
            Array.Copy(payload, padded, payload.Length);
            return padded;
        }

        private static void WriteInt32(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// 本地化子表加载桩：按表名回放夹具字节，未登记的表返回 <c>null</c>（模拟"表没打进包"）。
    /// 同时记录被请求的表名，用于断言切片确实按名字取 <c>TbLocalization</c>。只属于本切片测试。
    /// </summary>
    public sealed class LocalizationLoaderStub : IBinaryTableLoader
    {
        /// <summary>子表名（与切片内常量一致）。</summary>
        public const string TableName = "TbLocalization";

        /// <summary>标准夹具里有三种语言的 key。</summary>
        public const string StartKey = "ui.start";

        public const string StartChinese = "开始";

        public const string StartEnglish = "Start";

        public const string StartJapanese = "スタート";

        /// <summary>标准夹具里缺日语行的 key（用于验证回退默认语言）。</summary>
        public const string SettingsKey = "ui.settings";

        public const string SettingsChinese = "设置";

        public const string SettingsEnglish = "Settings";

        /// <summary>标准夹具里只有中文的 key（用于验证语言回退链）。</summary>
        public const string QuitKey = "ui.quit";

        public const string QuitChinese = "退出";

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public LocalizationLoaderStub()
        {
            _tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            _requestedTables = new List<string>();
        }

        /// <summary>被请求过的表名（按调用顺序）。</summary>
        public IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        /// <summary>空加载器：任何表都返回 <c>null</c>。</summary>
        public static LocalizationLoaderStub Missing()
        {
            return new LocalizationLoaderStub();
        }

        /// <summary>登记指定字节；传 <c>null</c> 表示该表缺失。</summary>
        public static LocalizationLoaderStub WithPayload(byte[]? payload)
        {
            return new LocalizationLoaderStub().AddTable(payload);
        }

        /// <summary>标准 3 key 夹具：`ui.start`(中/英/日)、`ui.settings`(中/英)、`ui.quit`(中)。</summary>
        public static LocalizationLoaderStub WithDefaultTable()
        {
            return WithPayload(DefaultPayload());
        }

        /// <summary>标准 3 key 夹具的字节。</summary>
        public static byte[] DefaultPayload()
        {
            LocalizationPayloadBuilder builder = new LocalizationPayloadBuilder();
            builder.AddKey(
                StartKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, StartChinese),
                new LanguageTextFixture(GameLanguage.English, StartEnglish),
                new LanguageTextFixture(GameLanguage.Japanese, StartJapanese));
            builder.AddKey(
                SettingsKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, SettingsChinese),
                new LanguageTextFixture(GameLanguage.English, SettingsEnglish));
            builder.AddKey(
                QuitKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, QuitChinese));
            return builder.Build();
        }

        /// <summary>登记（或覆盖）子表字节。</summary>
        public LocalizationLoaderStub AddTable(byte[]? payload)
        {
            _tables[TableName] = payload;
            return this;
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }
    }

    /// <summary>语言变更事件记录器：断言"到底有没有发事件、发了几条、最后一条是什么语言"。</summary>
    public sealed class LanguageEventRecorder : IEventHandler<LanguageChangedEvent>
    {
        private readonly List<GameLanguage> _languages;

        public LanguageEventRecorder()
        {
            _languages = new List<GameLanguage>();
        }

        /// <summary>按广播顺序记录的语言。</summary>
        public IReadOnlyList<GameLanguage> Languages
        {
            get { return _languages; }
        }

        /// <summary>记录条数。</summary>
        public int Count
        {
            get { return _languages.Count; }
        }

        /// <summary>最后一条事件的语言；无事件时抛 <see cref="InvalidOperationException"/>。</summary>
        public GameLanguage Last
        {
            get { return _languages[_languages.Count - 1]; }
        }

        /// <inheritdoc />
        public void Handle(in LanguageChangedEvent evt)
        {
            _languages.Add(evt.Language);
        }
    }

    /// <summary>
    /// 切片一次性装配：真实 <see cref="LocalizationHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本目录私有桩端口。
    /// </summary>
    public sealed class LocalizationTestHarness
    {
        private LocalizationTestHarness(
            RecordingBus bus,
            IBinaryTableLoader loader,
            LanguageEventRecorder events,
            LocalizationHandler handler)
        {
            Bus = bus;
            Loader = loader;
            Events = events;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>装配用的加载器（空表/畸形表场景需要再取一次字节）。</summary>
        public IBinaryTableLoader Loader { get; }

        /// <summary>语言变更事件记录器。</summary>
        public LanguageEventRecorder Events { get; }

        /// <summary>被测处理器。</summary>
        public LocalizationHandler Handler { get; }

        /// <summary>用标准 3 key 夹具 + 默认语言（简体中文）装配。</summary>
        public static LocalizationTestHarness CreateWithDefaultTable()
        {
            return Create(LocalizationLoaderStub.WithDefaultTable(), GameLanguage.ChineseSimplified);
        }

        /// <summary>用指定加载器 + 默认语言（简体中文）装配。</summary>
        public static LocalizationTestHarness Create(IBinaryTableLoader loader)
        {
            return Create(loader, GameLanguage.ChineseSimplified);
        }

        /// <summary>用指定加载器与显式默认语言装配一套夹具。</summary>
        public static LocalizationTestHarness Create(IBinaryTableLoader loader, GameLanguage defaultLanguage)
        {
            RecordingBus bus = new RecordingBus();
            LanguageEventRecorder events = new LanguageEventRecorder();
            bus.Subscribe(events);

            LocalizationHandler handler = new LocalizationHandler(bus, loader, defaultLanguage);
            handler.Register(bus);

            return new LocalizationTestHarness(bus, loader, events, handler);
        }

        /// <summary>走真实总线发一条文案查询。</summary>
        public LocalizedText Query(string key)
        {
            return Bus.Query<LocalizationQueryRequest, LocalizedText>(new LocalizationQueryRequest(key));
        }

        /// <summary>走真实总线发一条语言切换命令。</summary>
        public void SetLanguage(GameLanguage language)
        {
            Bus.Send(new SetLanguageCommand(language));
        }
    }
}

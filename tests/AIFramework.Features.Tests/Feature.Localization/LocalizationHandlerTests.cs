#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Localization;
using AIFramework.Features.Localization;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.Localization
{
    /// <summary>
    /// 本地化切片验收：真实 <c>CqeBus</c>（经 <see cref="RecordingBus"/> 记录）+ 本目录私有桩端口，
    /// 断言只落在"查询回了什么文案""当前语言""发了哪些事件"与"子表校验抛了什么异常"四处。
    /// </summary>
    public sealed class LocalizationHandlerTests
    {
        // ---------- 查询命中与回退链 ----------

        [Fact]
        public void QueryKnownKey_ReturnsTextOfCurrentLanguage()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            LocalizedText text = harness.Query(LocalizationLoaderStub.StartKey);

            Assert.True(text.Found);
            Assert.Equal(LocalizationLoaderStub.StartKey, text.Key);
            Assert.Equal(LocalizationLoaderStub.StartChinese, text.Text);

            // 查询是只读的：不得顺手发指令或事件。
            Assert.Empty(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        [Fact]
        public void QueryKeyMissingCurrentLanguage_FallsBackToDefaultLanguage()
        {
            // 默认语言 = 英语，当前语言切到日语：ui.settings 没有日语行，必须回退英语而不是退回 key。
            LocalizationTestHarness harness = LocalizationTestHarness.Create(
                LocalizationLoaderStub.WithDefaultTable(),
                GameLanguage.English);
            harness.SetLanguage(GameLanguage.Japanese);

            LocalizedText text = harness.Query(LocalizationLoaderStub.SettingsKey);

            Assert.True(text.Found);
            Assert.Equal(LocalizationLoaderStub.SettingsKey, text.Key);
            Assert.Equal(LocalizationLoaderStub.SettingsEnglish, text.Text);

            // 语言回退只查表，不额外广播任何事件（唯一的变更事件来自上面的切换）。
            Assert.Single(harness.Bus.PublishedOf<LanguageChangedEvent>());
        }

        [Fact]
        public void QueryKeyMissingInBothCurrentAndDefaultLanguage_FallsBackToKeyItself()
        {
            // 默认语言 = 英语、当前 = 日语，而 ui.quit 只有中文：两步回退都落空 → 回退 key。
            LocalizationTestHarness harness = LocalizationTestHarness.Create(
                LocalizationLoaderStub.WithDefaultTable(),
                GameLanguage.English);
            harness.SetLanguage(GameLanguage.Japanese);

            LocalizedText text = harness.Query(LocalizationLoaderStub.QuitKey);

            Assert.False(text.Found);
            Assert.Equal(LocalizationLoaderStub.QuitKey, text.Key);
            Assert.Equal(LocalizationLoaderStub.QuitKey, text.Text);
            Assert.NotEmpty(text.Text);
        }

        [Fact]
        public void QueryUnknownKey_FallsBackToKeyItself()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            LocalizedText text = harness.Query("ui.missing");

            Assert.False(text.Found);
            Assert.Equal("ui.missing", text.Key);
            Assert.Equal("ui.missing", text.Text);
            Assert.NotEmpty(text.Text);
        }

        [Fact]
        public void QueryNullKey_StillReturnsNonNullResult()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            LocalizedText text = harness.Query(null!);

            // 病态输入也不允许把 null 泄给 UI；命中标志必须为 false。
            Assert.False(text.Found);
            Assert.NotNull(text.Key);
            Assert.NotNull(text.Text);
        }

        // ---------- 当前语言 ----------

        [Fact]
        public void CurrentLanguage_StartsAsChineseSimplifiedByDefault()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            Assert.Equal(GameLanguage.ChineseSimplified, harness.Handler.CurrentLanguage);

            // 初值即查询起点：不做任何切换就该命中中文行，且启动不发事件。
            Assert.Equal(LocalizationLoaderStub.StartChinese, harness.Query(LocalizationLoaderStub.StartKey).Text);
            Assert.Empty(harness.Events.Languages);
        }

        [Fact]
        public void CurrentLanguage_StartsAsExplicitDefaultLanguage()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.Create(
                LocalizationLoaderStub.WithDefaultTable(),
                GameLanguage.Japanese);

            Assert.Equal(GameLanguage.Japanese, harness.Handler.CurrentLanguage);
            Assert.Equal(LocalizationLoaderStub.StartJapanese, harness.Query(LocalizationLoaderStub.StartKey).Text);
        }

        [Fact]
        public void SetLanguage_ChangesQueryResultAndPublishesEvent()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            harness.SetLanguage(GameLanguage.Japanese);

            LanguageChangedEvent published = Assert.Single(harness.Bus.PublishedOf<LanguageChangedEvent>());
            Assert.Equal(GameLanguage.Japanese, published.Language);
            Assert.Equal(GameLanguage.Japanese, harness.Handler.CurrentLanguage);
            Assert.Equal(GameLanguage.Japanese, harness.Events.Last);

            // 切换后同一 key 回日语行；没有日语行的 key 回退默认语言（中文）。
            Assert.Equal(LocalizationLoaderStub.StartJapanese, harness.Query(LocalizationLoaderStub.StartKey).Text);
            Assert.Equal(LocalizationLoaderStub.SettingsChinese, harness.Query(LocalizationLoaderStub.SettingsKey).Text);
        }

        [Fact]
        public void SetLanguageToCurrentLanguageAtStartup_IsSilent()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            harness.SetLanguage(GameLanguage.ChineseSimplified);

            Assert.Empty(harness.Bus.PublishedOf<LanguageChangedEvent>());
            Assert.Empty(harness.Events.Languages);
            Assert.Equal(GameLanguage.ChineseSimplified, harness.Handler.CurrentLanguage);
        }

        [Fact]
        public void SetLanguageToSameLanguageAfterSwitch_IsSilent()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();
            harness.SetLanguage(GameLanguage.English);
            int eventsAfterRealSwitch = harness.Events.Count;

            harness.SetLanguage(GameLanguage.English);

            Assert.Equal(1, eventsAfterRealSwitch);
            Assert.Equal(eventsAfterRealSwitch, harness.Events.Count);
            Assert.Single(harness.Bus.PublishedOf<LanguageChangedEvent>());
            Assert.Equal(GameLanguage.English, harness.Handler.CurrentLanguage);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(int.MaxValue)]
        public void SetLanguageWithUndefinedValue_IsSilentlyRejected(int rawLanguage)
        {
            LocalizationTestHarness harness = LocalizationTestHarness.CreateWithDefaultTable();

            harness.SetLanguage((GameLanguage)rawLanguage);

            // 静默拒绝：不发事件、不改状态、查询结果不变。
            Assert.Empty(harness.Bus.PublishedOf<LanguageChangedEvent>());
            Assert.Empty(harness.Events.Languages);
            Assert.Equal(GameLanguage.ChineseSimplified, harness.Handler.CurrentLanguage);
            Assert.Equal(LocalizationLoaderStub.StartChinese, harness.Query(LocalizationLoaderStub.StartKey).Text);
        }

        // ---------- 总线注册 ----------

        [Fact]
        public void Register_MakesQueryAndCommandReachableThroughRealBus()
        {
            RecordingBus bus = new RecordingBus();
            LanguageEventRecorder recorder = new LanguageEventRecorder();
            bus.Subscribe(recorder);

            LocalizationHandler handler = new LocalizationHandler(bus, LocalizationLoaderStub.WithDefaultTable());
            handler.Register(bus);

            LocalizedText text = bus.Query<LocalizationQueryRequest, LocalizedText>(
                new LocalizationQueryRequest(LocalizationLoaderStub.StartKey));
            Assert.Equal(LocalizationLoaderStub.StartChinese, text.Text);

            bus.Send(new SetLanguageCommand(GameLanguage.English));

            Assert.Equal(GameLanguage.English, handler.CurrentLanguage);
            Assert.Single(recorder.Languages);

            // 重复注册照旧由总线抛异常：切片不得吞掉。
            Assert.Throws<InvalidOperationException>(
                () =>
                {
                    handler.Register(bus);
                });
        }

        // ---------- 子表加载校验 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            LocalizationLoaderStub loader = LocalizationLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains(LocalizationLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new LocalizationHandler(new RecordingBus(), loader);
                });
            Assert.Contains(LocalizationLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按同一个表名取子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(LocalizationLoaderStub.TableName, name));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(9)]
        [InlineData(20)]
        public void TruncatedPayload_ThrowsInvalidDataException(int keptBytes)
        {
            byte[] payload = LocalizationLoaderStub.DefaultPayload();
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                LocalizationPayloadBuilder.Truncate(payload, keptBytes));

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationHandler(new RecordingBus(), loader);
                });
        }

        [Fact]
        public void NegativeKeyLength_ThrowsInvalidDataException()
        {
            byte[] entry = LocalizationPayloadBuilder.RawEntry(
                -1,
                LocalizationPayloadBuilder.Utf8(LocalizationLoaderStub.StartKey),
                1,
                LocalizationPayloadBuilder.RawLanguageText(
                    (int)GameLanguage.ChineseSimplified,
                    0,
                    Array.Empty<byte>()));
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                new LocalizationPayloadBuilder().AddRawEntry(entry).Build());

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains("key 长度为负", exception.Message);
        }

        [Fact]
        public void NegativeTextLength_ThrowsInvalidDataException()
        {
            // 声明 textLen = -5，实际只写 1 字节；解析必须先拒绝负长度，而不是把它当"读到 1 字节"。
            byte[] entry = LocalizationPayloadBuilder.RawEntry(
                LocalizationPayloadBuilder.Utf8(LocalizationLoaderStub.StartKey).Length,
                LocalizationPayloadBuilder.Utf8(LocalizationLoaderStub.StartKey),
                1,
                LocalizationPayloadBuilder.RawLanguageText(
                    (int)GameLanguage.English,
                    -5,
                    LocalizationPayloadBuilder.Utf8("S")));
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                new LocalizationPayloadBuilder().AddRawEntry(entry).Build());

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains("text", exception.Message);
        }

        [Fact]
        public void DuplicateKeyAndLanguage_ThrowsInvalidDataException()
        {
            LocalizationPayloadBuilder builder = new LocalizationPayloadBuilder();
            builder.AddKey(
                LocalizationLoaderStub.StartKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, LocalizationLoaderStub.StartChinese));
            builder.AddKey(
                LocalizationLoaderStub.StartKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, "开始（重复）"));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(LocalizationLoaderStub.WithPayload(builder.Build()));
                });
            Assert.Contains("重复", exception.Message);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        [InlineData(99)]
        public void UnknownLanguageValue_ThrowsInvalidDataException(int rawLanguage)
        {
            byte[] entry = LocalizationPayloadBuilder.Entry(
                LocalizationLoaderStub.StartKey,
                new LanguageTextFixture((GameLanguage)rawLanguage, "x"));
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                new LocalizationPayloadBuilder().AddRawEntry(entry).Build());

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains("未知语言", exception.Message);
        }

        [Fact]
        public void KeyWithoutAnyLanguage_ThrowsInvalidDataException()
        {
            byte[] entry = LocalizationPayloadBuilder.RawEntry(
                4,
                LocalizationPayloadBuilder.Utf8("ui.x"),
                0);
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                new LocalizationPayloadBuilder().AddRawEntry(entry).Build());

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains("langCount", exception.Message);
        }

        [Fact]
        public void TrailingBytes_ThrowInvalidDataException()
        {
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithPayload(
                LocalizationPayloadBuilder.AppendPadding(LocalizationLoaderStub.DefaultPayload(), 3));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new LocalizationTables(loader);
                });
            Assert.Contains("尾部", exception.Message);
        }

        // ---------- 空表与归并语义 ----------

        [Fact]
        public void EmptyTable_IsValidAndEveryQueryFallsBackToKey()
        {
            LocalizationTestHarness harness = LocalizationTestHarness.Create(
                LocalizationLoaderStub.WithPayload(new LocalizationPayloadBuilder().Build()));
            LocalizationTables tables = new LocalizationTables(harness.Loader);

            Assert.Equal(0, tables.Count);
            Assert.Equal(0, tables.KeyCount);
            Assert.Null(tables.Find(LocalizationLoaderStub.StartKey, GameLanguage.ChineseSimplified));

            LocalizedText text = harness.Query(LocalizationLoaderStub.StartKey);

            Assert.False(text.Found);
            Assert.Equal(LocalizationLoaderStub.StartKey, text.Text);

            // 空表下语言切换照旧合法：状态与事件不依赖表内容。
            harness.SetLanguage(GameLanguage.English);
            Assert.Single(harness.Bus.PublishedOf<LanguageChangedEvent>());
        }

        [Fact]
        public void RepeatedKeyWithDistinctLanguages_IsMergedIntoSingleKey()
        {
            LocalizationPayloadBuilder builder = new LocalizationPayloadBuilder();
            builder.AddKey(
                LocalizationLoaderStub.StartKey,
                new LanguageTextFixture(GameLanguage.ChineseSimplified, LocalizationLoaderStub.StartChinese));
            builder.AddKey(
                LocalizationLoaderStub.StartKey,
                new LanguageTextFixture(GameLanguage.English, LocalizationLoaderStub.StartEnglish));

            LocalizationTables tables = new LocalizationTables(
                LocalizationLoaderStub.WithPayload(builder.Build()));

            Assert.Equal(2, tables.Count);
            Assert.Equal(1, tables.KeyCount);
            Assert.Equal(
                LocalizationLoaderStub.StartChinese,
                tables.Find(LocalizationLoaderStub.StartKey, GameLanguage.ChineseSimplified));
            Assert.Equal(
                LocalizationLoaderStub.StartEnglish,
                tables.Find(LocalizationLoaderStub.StartKey, GameLanguage.English));
            Assert.Null(tables.Find(LocalizationLoaderStub.StartKey, GameLanguage.Japanese));
        }

        [Fact]
        public void Tables_ExposeEntryAndKeyCounts_AndFindReturnsNullOnMiss()
        {
            LocalizationTables tables = new LocalizationTables(LocalizationLoaderStub.WithDefaultTable());

            Assert.Equal(3, tables.Count);
            Assert.Equal(3, tables.KeyCount);
            Assert.Equal(
                LocalizationLoaderStub.StartJapanese,
                tables.Find(LocalizationLoaderStub.StartKey, GameLanguage.Japanese));

            // 缺 key、缺该语言、null key 一律返回 null（表只报"有没有"，回退由 Handler 负责）。
            Assert.Null(tables.Find("ui.missing", GameLanguage.ChineseSimplified));
            Assert.Null(tables.Find(LocalizationLoaderStub.QuitKey, GameLanguage.English));
            Assert.Null(tables.Find(null!, GameLanguage.ChineseSimplified));
        }

        // ---------- 构造参数守卫 ----------

        [Fact]
        public void NullArguments_ThrowArgumentNullException()
        {
            LocalizationLoaderStub loader = LocalizationLoaderStub.WithDefaultTable();

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new LocalizationTables(null!);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new LocalizationHandler(null!, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new LocalizationHandler(new RecordingBus(), null!);
                });

            LocalizationHandler handler = new LocalizationHandler(new RecordingBus(), loader);
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }
    }
}

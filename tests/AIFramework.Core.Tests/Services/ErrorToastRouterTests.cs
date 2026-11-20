using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Ui;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 错误码 → 本地化 → Toast 的统一路由验收。
    /// <para>
    /// 口径：错误提示本身**绝不能再抛异常**（提示失败不能盖掉业务失败），
    /// 所以本地化缺失、处理器未注册都必须退回兜底文案并计数，而不是把异常抛给调用方。
    /// </para>
    /// </summary>
    public sealed class ErrorToastRouterTests
    {
        [Fact]
        public void Route_KnownCode_UsesLocalizedText()
        {
            var bus = new FakeBus();
            bus.Localization["err.network"] = "网络连接失败";
            var router = new ErrorToastRouter(bus);

            string text = router.Route(1001);

            Assert.Equal("网络连接失败", text);
            Assert.Equal(1, router.RoutedCount);
            Assert.Equal(0, router.FallbackCount);
        }

        [Fact]
        public void Route_BroadcastsToastEventWithSeverity()
        {
            var bus = new FakeBus();
            bus.Localization["err.purchase_cancelled"] = "已取消购买";
            var router = new ErrorToastRouter(bus);
            var sink = new ToastSink();
            bus.Subscribe(sink);

            router.Route(3002, durationMs: 1200);

            ToastRequestedEvent toast = Assert.Single(sink.Toasts);
            Assert.Equal("已取消购买", toast.Text);
            Assert.Equal(ToastSeverity.Info, toast.Severity);
            Assert.Equal(1200, toast.DurationMs);
        }

        [Fact]
        public void Route_PresenterIsAlsoCalled()
        {
            var bus = new FakeBus();
            bus.Localization["err.level_locked"] = "关卡尚未解锁";
            var presenter = new RecordingPresenter();
            var router = new ErrorToastRouter(bus, presenter);

            router.Route(5001);

            Assert.Equal("关卡尚未解锁", Assert.Single(presenter.Texts));
        }

        [Fact]
        public void Route_MissingLocalization_FallsBackToCatalogMessage()
        {
            var bus = new FakeBus();
            var router = new ErrorToastRouter(bus);

            string text = router.Route(1001);

            Assert.Equal("网络异常，请检查网络后重试", text);
            Assert.Equal(1, router.FallbackCount);
        }

        [Fact]
        public void Route_LocalizationHandlerMissing_FallsBackWithoutThrowing()
        {
            var bus = new FakeBus { ThrowOnQuery = true };
            var router = new ErrorToastRouter(bus);

            string text = router.Route(1001);

            Assert.Equal("网络异常，请检查网络后重试", text);
            Assert.Equal(1, router.FallbackCount);
            Assert.Equal(1, router.RoutedCount);
        }

        [Fact]
        public void Route_UnknownCode_UsesUnknownFallbackAndCounts()
        {
            var bus = new FakeBus();
            var router = new ErrorToastRouter(bus);

            string text = router.Route(999999);

            Assert.Equal(ErrorCatalog.UnknownMessage, text);
            Assert.Equal(1, router.UnknownCodeCount);
            Assert.Equal(1, router.FallbackCount);
        }

        [Fact]
        public void Route_UnknownCode_SeverityIsError()
        {
            var bus = new FakeBus();
            var router = new ErrorToastRouter(bus);
            var sink = new ToastSink();
            bus.Subscribe(sink);

            router.Route(999999);

            Assert.Equal(ToastSeverity.Error, Assert.Single(sink.Toasts).Severity);
        }

        [Fact]
        public void Route_EmptyLocalizedText_FallsBack()
        {
            var bus = new FakeBus();
            bus.Localization["err.timeout"] = string.Empty; // Found=true 但空文本
            var router = new ErrorToastRouter(bus);

            string text = router.Route(1002);

            Assert.Equal("请求超时，请稍后重试", text);
            Assert.Equal(1, router.FallbackCount);
        }

        [Fact]
        public void RouteLocalized_UsesKeyText()
        {
            var bus = new FakeBus();
            bus.Localization["activity.opened"] = "活动已开启";
            var router = new ErrorToastRouter(bus);

            string text = router.RouteLocalized("activity.opened", "活动开启", ToastSeverity.Success);

            Assert.Equal("活动已开启", text);
        }

        [Fact]
        public void RouteLocalized_MissingKey_UsesProvidedFallback()
        {
            var bus = new FakeBus();
            var router = new ErrorToastRouter(bus);

            string text = router.RouteLocalized("activity.missing", "活动开启", ToastSeverity.Success);

            Assert.Equal("活动开启", text);
        }

        [Fact]
        public void RouteLocalized_NullKey_Throws()
        {
            var bus = new FakeBus();
            var router = new ErrorToastRouter(bus);

            Assert.Throws<ArgumentNullException>(() => router.RouteLocalized(null!, "x", ToastSeverity.Info));
        }

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ErrorToastRouter(null!));
        }

        [Fact]
        public void Counters_AccumulateAcrossCalls()
        {
            var bus = new FakeBus();
            bus.Localization["err.network"] = "网络连接失败";
            var router = new ErrorToastRouter(bus);

            router.Route(1001);   // 命中
            router.Route(1001);   // 命中
            router.Route(77777);  // 未知

            Assert.Equal(3, router.RoutedCount);
            Assert.Equal(1, router.UnknownCodeCount);
        }

        private sealed class ToastSink : IEventHandler<ToastRequestedEvent>
        {
            public List<ToastRequestedEvent> Toasts { get; } = new List<ToastRequestedEvent>();

            public void Handle(in ToastRequestedEvent evt)
            {
                Toasts.Add(evt);
            }
        }

        private sealed class RecordingPresenter : IToastPresenter
        {
            public List<string> Texts { get; } = new List<string>();

            public void Show(in ToastRequestedEvent toast)
            {
                Texts.Add(toast.Text);
            }
        }

        /// <summary>本地化查询用假总线：只实现本测试需要的查询与事件，其余抛"未实现"。</summary>
        private sealed class FakeBus : ICqeBus
        {
            private readonly List<IEventHandler<ToastRequestedEvent>> _toastHandlers =
                new List<IEventHandler<ToastRequestedEvent>>();

            public Dictionary<string, string> Localization { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public bool ThrowOnQuery { get; set; }

            public void Publish<TEvent>(in TEvent evt)
                where TEvent : struct, IEvent
            {
                if (evt is ToastRequestedEvent toast)
                {
                    for (int i = 0; i < _toastHandlers.Count; i++)
                    {
                        _toastHandlers[i].Handle(in toast);
                    }
                }
            }

            public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
                where TEvent : struct, IEvent
            {
                if (handler is IEventHandler<ToastRequestedEvent> toastHandler)
                {
                    _toastHandlers.Add(toastHandler);
                }
            }

            public TResult Query<TQuery, TResult>(in TQuery query)
                where TQuery : struct, IQuery<TResult>
            {
                if (ThrowOnQuery)
                {
                    throw new InvalidOperationException("本地化查询处理器未注册。");
                }

                if (query is LocalizationQueryRequest request)
                {
                    string key = request.Key ?? string.Empty;
                    bool found = Localization.TryGetValue(key, out string? text);
                    return (TResult)(object)new LocalizedText(key, text ?? string.Empty, found);
                }

                throw new NotImplementedException();
            }

            public void Send<TCommand>(in TCommand command)
                where TCommand : struct, ICommand
            {
                throw new NotImplementedException();
            }

            public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
                where TCommand : struct, ICommand
            {
                throw new NotImplementedException();
            }

            public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
                where TQuery : struct, IQuery<TResult>
            {
                throw new NotImplementedException();
            }
        }
    }

    /// <summary>错误码目录本身的约束：码唯一、正数、key 唯一、兜底文案非空。</summary>
    public sealed class ErrorCatalogTests
    {
        [Fact]
        public void Catalog_HasNoDuplicateCodes()
        {
            ErrorDescriptor[] all = ErrorCatalog.Snapshot();
            var seen = new HashSet<int>();

            foreach (ErrorDescriptor descriptor in all)
            {
                Assert.True(seen.Add(descriptor.Code), "错误码重复：" + descriptor.Code);
            }
        }

        [Fact]
        public void Catalog_HasNoDuplicateLocalizationKeys()
        {
            ErrorDescriptor[] all = ErrorCatalog.Snapshot();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (ErrorDescriptor descriptor in all)
            {
                Assert.True(seen.Add(descriptor.LocalizationKey), "本地化 key 重复：" + descriptor.LocalizationKey);
            }
        }

        [Fact]
        public void Catalog_CodesArePositive()
        {
            foreach (ErrorDescriptor descriptor in ErrorCatalog.Snapshot())
            {
                Assert.True(descriptor.Code > 0);
            }
        }

        [Fact]
        public void Catalog_KeysUseErrPrefix()
        {
            foreach (ErrorDescriptor descriptor in ErrorCatalog.Snapshot())
            {
                Assert.StartsWith("err.", descriptor.LocalizationKey, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Catalog_FallbackMessagesAreNonEmpty()
        {
            foreach (ErrorDescriptor descriptor in ErrorCatalog.Snapshot())
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.FallbackMessage));
            }
        }

        [Fact]
        public void Catalog_ContainsCoreErrorCodes()
        {
            Assert.True(ErrorCatalog.Contains(1001));
            Assert.True(ErrorCatalog.Contains(2001));
            Assert.True(ErrorCatalog.Contains(3004));
            Assert.True(ErrorCatalog.Contains(4001));
            Assert.True(ErrorCatalog.Contains(5002));
        }

        [Fact]
        public void Catalog_UnknownCodeReturnsFalse()
        {
            Assert.False(ErrorCatalog.TryGet(424242, out _));
        }

        [Fact]
        public void Catalog_SnapshotIsCopy()
        {
            ErrorDescriptor[] first = ErrorCatalog.Snapshot();
            first[0] = new ErrorDescriptor(1, "err.hacked", "x", ToastSeverity.Info);

            ErrorDescriptor[] second = ErrorCatalog.Snapshot();

            Assert.NotEqual(1, second[0].Code);
        }

        [Fact]
        public void Catalog_NeverReturnsKeyAsMessage()
        {
            foreach (ErrorDescriptor descriptor in ErrorCatalog.Snapshot())
            {
                Assert.NotEqual(descriptor.LocalizationKey, descriptor.FallbackMessage);
            }
        }
    }
}

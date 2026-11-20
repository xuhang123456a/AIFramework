using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;
using AIFramework.Core.Cqe;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="PurchaseReconciler"/> 的行为契约验收：收据批次 → 判定（掉单/已发/重复/退款/孤儿）
    /// → 只对掉单补发 → 写台账。每条规则都被单独钉死。
    /// <para>
    /// <b>这些用例守的是钱和玩家资产</b>：绿一次不代表逻辑漂亮，只代表"掉单会补""补过的不会再补"
    /// "退款不会再发""台账写失败也不会双发""孤儿只报不动手"这五条在改动后依然成立。
    /// </para>
    /// <para>
    /// 替身只用真实现（<see cref="CqeBus"/> + <see cref="IapPurchaseLedger"/> + <see cref="IapPurchaseLedgerAdapter"/>）：
    /// 总线必须是真的，因为"未注册处理器会抛异常"正是要被验收的补发失败路径；台账也必须是真的，
    /// 因为"收据已消耗"是补发幂等的唯一外部证据。只有"台账写失败"这一条用可编程假台账注入。
    /// </para>
    /// </summary>
    public sealed class PurchaseReconcilerTests
    {
        /// <summary>测试用商品 ID。</summary>
        private const string ProductId = "com.demo.gold60";

        /// <summary>测试用到账道具 ID。</summary>
        private const int GoldItemId = 1001;

        /// <summary>测试用到账数量。</summary>
        private const int GoldCount = 60;

        /// <summary>测试用购买时间（Unix 秒；只用于诊断字段，不参与判定）。</summary>
        private const long PurchasedAt = 1_700_000_000;

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            // 台账与总线都是硬依赖：没有台账无法判断"发过没有"，没有总线无法补发，属于装配期编程错误。
            var ledger = new RejectingLedger();
            var bus = new CqeBus();

            Assert.Throws<ArgumentNullException>(() => new PurchaseReconciler(null!, bus));
            Assert.Throws<ArgumentNullException>(() => new PurchaseReconciler(ledger, null!));
        }

        [Fact]
        public void Reconcile_NullReceipts_Throws()
        {
            var harness = new Harness();

            Assert.Throws<ArgumentNullException>(() => harness.Reconciler.Reconcile(null!));
        }

        [Fact]
        public void Reconcile_EmptyBatch_ReturnsEmptyEvenWithTrackedGrants()
        {
            // 空批次在真实链路里通常意味着"商店没拉到 / 接口失败"。
            // 若把它当成"商店一张收据都没有"，每台设备一断网就会批量误报孤儿。
            var harness = new Harness();
            harness.SeedLedger("receipt-paid");
            Assert.Single(harness.Reconciler.Reconcile(new[] { Granted("receipt-paid") }));

            ReconcileAction[] actions = harness.Reconciler.Reconcile(Array.Empty<PurchaseReceipt>());

            Assert.Empty(actions);
        }

        [Fact]
        public void Reconcile_GrantedWithoutLocalRecord_ReportsMissing()
        {
            // 收据说"已发货"、台账却没有这张收据：典型的掉单（玩家付了钱没到账）。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal("receipt-1", action.ReceiptId);
            Assert.Equal(ReconcileState.Missing, action.State);
            Assert.Equal(GoldItemId, action.ItemId);
            Assert.Equal(GoldCount, action.Count);
            Assert.Equal(PurchaseReconciler.ReasonMissing, action.Reason);

            // 对账本身绝不出手：掉单只是"待补发"，补发必须显式调用 TryGrantMissing。
            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(1, harness.Reconciler.MissingCount);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
        }

        [Fact]
        public void Reconcile_GrantedWithLocalRecord_ReportsGrantedAndIsIdempotent()
        {
            // 收据与台账一致：判 Granted，不出任何后续动作。
            var harness = new Harness();
            harness.SeedLedger("receipt-1");

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal(ReconcileState.Granted, action.State);
            Assert.Equal(PurchaseReconciler.ReasonGranted, action.Reason);
            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Empty(harness.Handler.Commands);

            // 同一结论重复喂 → 不再产出动作（幂等），也不会变成 Duplicated 乱报。
            Assert.Empty(harness.Reconciler.Reconcile(new[] { Granted("receipt-1") }));
        }

        [Theory]
        [InlineData("granted")]
        [InlineData("GRANTED")]
        [InlineData("  GrAnTeD  ")]
        [InlineData("\tgranted\t")]
        public void Reconcile_GrantedState_IsTrimmedAndCaseInsensitive(string state)
        {
            // 状态文本来自商店/服务端，大小写与首尾空白都不能当成"未知状态"——
            // 误判成 Unknown 的后果是掉单永远不补，玩家永远收不到货。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", ProductId, GoldItemId, GoldCount, PurchasedAt, state)
            });

            Assert.Equal(ReconcileState.Missing, Assert.Single(actions).State);
            Assert.Equal(1, harness.Reconciler.MissingCount);
        }

        [Theory]
        [InlineData("refunded")]
        [InlineData("REFUNDED")]
        [InlineData(" Refunded ")]
        public void Reconcile_RefundedState_ReportsRefundWithoutGranting(string state)
        {
            // 退款：不发货、只上报与计数。回收（扣道具/收回权益）由装配层走服务端裁决，本类不越权。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", ProductId, GoldItemId, GoldCount, PurchasedAt, state)
            });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal(ReconcileState.Refunded, action.State);
            Assert.Equal(PurchaseReconciler.ReasonRefunded, action.Reason);
            Assert.Equal(1, harness.Reconciler.RefundedCount);
            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Empty(harness.Handler.Commands);
        }

        [Theory]
        [InlineData("pending")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("grantedx")]
        [InlineData("refund")]
        public void Reconcile_UnknownState_ReportsUnknownWithoutChangingCounters(string state)
        {
            // 状态既不是 granted 也不是 refunded：无从裁决，只报 Unknown 一次。
            // 宁可不动（玩家可能已经付了钱），也不能猜成 Missing 去补发。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", ProductId, GoldItemId, GoldCount, PurchasedAt, state)
            });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal(ReconcileState.Unknown, action.State);
            Assert.Equal(PurchaseReconciler.ReasonUnknownState, action.Reason);
            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
            Assert.Equal(0, harness.Reconciler.RefundedCount);
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Reconcile_NullStateText_IsTreatedAsUnknown()
        {
            // 结构体可能被 default 出来（State 为 null）：绝不能因为空引用把对账整批打断。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", ProductId, GoldItemId, GoldCount, PurchasedAt, null!)
            });

            Assert.Equal(ReconcileState.Unknown, Assert.Single(actions).State);
        }

        [Fact]
        public void Reconcile_DuplicateReceiptInSameBatch_ProducesSingleAction()
        {
            // 商店回调重复/服务端分页重叠都会让同一条收据在一批里出现多次：
            // 同一收据只产出一条动作，否则补发队列会被同一条掉单刷屏。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                Granted("receipt-1"), Granted("receipt-1")
            });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal("receipt-1", action.ReceiptId);
            Assert.Equal(ReconcileState.Missing, action.State);
            Assert.Equal(1, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void Reconcile_RepeatedBatch_IsIdempotent()
        {
            // 对账通常会定时重跑：同一批喂两次，第二次必须什么都没有，计数器也不许涨。
            var harness = new Harness();
            var batch = new[] { Granted("receipt-1"), Refunded("receipt-2") };

            Assert.Equal(2, harness.Reconciler.Reconcile(batch).Length);
            Assert.Empty(harness.Reconciler.Reconcile(batch));

            Assert.Equal(1, harness.Reconciler.MissingCount);
            Assert.Equal(1, harness.Reconciler.RefundedCount);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
        }

        [Fact]
        public void Reconcile_ActionsFollowInputOrder()
        {
            // 返回数组按输入顺序，不看状态、不看时间戳：调用方可以按下标一一对应展示/处理。
            var harness = new Harness();
            harness.SeedLedger("receipt-2");

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                Granted("receipt-1"),
                Refunded("receipt-3"),
                Granted("receipt-2"),
                new PurchaseReceipt("receipt-4", ProductId, GoldItemId, GoldCount, PurchasedAt, "pending")
            });

            Assert.Equal(4, actions.Length);
            Assert.Equal("receipt-1", actions[0].ReceiptId);
            Assert.Equal(ReconcileState.Missing, actions[0].State);
            Assert.Equal("receipt-3", actions[1].ReceiptId);
            Assert.Equal(ReconcileState.Refunded, actions[1].State);
            Assert.Equal("receipt-2", actions[2].ReceiptId);
            Assert.Equal(ReconcileState.Granted, actions[2].State);
            Assert.Equal("receipt-4", actions[3].ReceiptId);
            Assert.Equal(ReconcileState.Unknown, actions[3].State);
        }

        [Fact]
        public void Reconcile_OutOfOrderTimestamps_KeepInputOrder()
        {
            // 时间戳来自客户端/商店，设备时间会被改：永远不以它排序（否则"谁先谁后"可被伪造）。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                Granted("receipt-late", at: PurchasedAt + 3600),
                Granted("receipt-early", at: PurchasedAt - 3600)
            });

            Assert.Equal(2, actions.Length);
            Assert.Equal("receipt-late", actions[0].ReceiptId);
            Assert.Equal("receipt-early", actions[1].ReceiptId);
        }

        [Fact]
        public void Reconcile_ReceiptWithoutId_IsIgnored()
        {
            // 没有主键的条目无法查台账、无法去重、也无法补发：直接丢弃，但同批里的合法条目照常处理。
            var harness = new Harness();

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt(string.Empty, ProductId, GoldItemId, GoldCount, PurchasedAt, "granted"),
                new PurchaseReceipt(null!, ProductId, GoldItemId, GoldCount, PurchasedAt, "granted"),
                Granted("receipt-1")
            });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal("receipt-1", action.ReceiptId);
            Assert.Equal(1, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void Reconcile_Orphan_DetectedWhenLedgerHasReceiptButStoreDoesNot()
        {
            // 孤儿：台账已经发过货，但这批收据里没有它（刷单/台账异常/服务端丢了交易）。
            // 本类只报状态，绝不自动撤销玩家资产——撤销是资损动作，必须人工/服务端裁决。
            var harness = new Harness();
            harness.SeedLedger("receipt-1");
            harness.SeedLedger("receipt-2");

            ReconcileAction[] first = harness.Reconciler.Reconcile(new[]
            {
                Granted("receipt-1"), Granted("receipt-2"), Granted("receipt-3")
            });
            Assert.Equal(3, first.Length);

            // 第二批只带着掉单那张：前两张本地已发的收据从商店清单里消失了。
            ReconcileAction[] second = harness.Reconciler.Reconcile(new[] { Granted("receipt-3") });

            Assert.Equal(2, second.Length);
            Assert.Equal("receipt-1", second[0].ReceiptId);
            Assert.Equal(ReconcileState.Orphan, second[0].State);
            Assert.Equal(PurchaseReconciler.ReasonOrphan, second[0].Reason);
            Assert.Equal(GoldItemId, second[0].ItemId);
            Assert.Equal(GoldCount, second[0].Count);
            Assert.Equal("receipt-2", second[1].ReceiptId);
            Assert.Equal(ReconcileState.Orphan, second[1].State);

            // 孤儿只上报不动手：没有补发、没有撤销、没有额外指令。
            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(1, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void Reconcile_Orphan_IsReportedOnlyOnce()
        {
            // 定时对账会反复跑：同一条孤儿不能每批刷一次（否则看板报警永远不收敛）。
            var harness = new Harness();
            harness.SeedLedger("receipt-1");
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1"), Granted("receipt-lost") });

            Assert.NotEmpty(harness.Reconciler.Reconcile(new[] { Granted("receipt-lost") }));
            Assert.Empty(harness.Reconciler.Reconcile(new[] { Granted("receipt-lost") }));
        }

        [Fact]
        public void Reconcile_EmptyBatchNeverReportsOrphanButNextBatchDoes()
        {
            // 空批次不触发孤儿扫描（可能是拉取失败）；下一批非空时才如实上报。
            var harness = new Harness();
            harness.SeedLedger("receipt-1");
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            Assert.Empty(harness.Reconciler.Reconcile(Array.Empty<PurchaseReceipt>()));

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[] { Granted("receipt-2") });

            Assert.Equal(2, actions.Length);
            Assert.Equal("receipt-2", actions[0].ReceiptId);
            Assert.Equal(ReconcileState.Missing, actions[0].State);
            Assert.Equal("receipt-1", actions[1].ReceiptId);
            Assert.Equal(ReconcileState.Orphan, actions[1].State);
        }

        [Fact]
        public void Reconcile_RefundClearsPendingMissing()
        {
            // 先判掉单、随后商店又报退款：钱退回去了，这张收据不该再补发。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });
            Assert.Equal(1, harness.Reconciler.MissingCount);

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[] { Refunded("receipt-1") });

            Assert.Equal(ReconcileState.Refunded, Assert.Single(actions).State);
            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Equal(1, harness.Reconciler.RefundedCount);

            // 补发入口同步拒绝：已退款的收据永远拿不到 GrantItemCommand。
            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Reconcile_RefundAfterGrant_ReportsRefundAndStopsOrphanTracking()
        {
            // 已发货的收据退款：只上报（回收由装配层裁决），且此后商店不再列它属于预期行为，不该再刷孤儿。
            var harness = new Harness();
            harness.SeedLedger("receipt-1");
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            ReconcileAction refund = Assert.Single(harness.Reconciler.Reconcile(new[] { Refunded("receipt-1") }));

            Assert.Equal(ReconcileState.Refunded, refund.State);
            Assert.Equal(1, harness.Reconciler.RefundedCount);

            ReconcileAction[] next = harness.Reconciler.Reconcile(new[] { Granted("receipt-2") });

            ReconcileAction only = Assert.Single(next);
            Assert.Equal("receipt-2", only.ReceiptId);
            Assert.Equal(ReconcileState.Missing, only.State);
        }

        [Fact]
        public void TryGrantMissing_UnknownOrAlreadyGrantedReceipt_ReturnsFalse()
        {
            // 只对"已判定掉单"的收据生效：未知、已发货、空 ID 一律不发（防误发/双发）。
            var harness = new Harness();
            harness.SeedLedger("receipt-paid");
            harness.Reconciler.Reconcile(new[] { Granted("receipt-paid") });

            Assert.False(harness.Reconciler.TryGrantMissing("receipt-never-seen"));
            Assert.False(harness.Reconciler.TryGrantMissing("receipt-paid"));
            Assert.False(harness.Reconciler.TryGrantMissing(string.Empty));
            Assert.False(harness.Reconciler.TryGrantMissing(null!));

            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
        }

        [Fact]
        public void TryGrantMissing_SendsGrantCommandAndRecordsLedger()
        {
            // 补发闭环：一条带正确内容的 GrantItemCommand + 台账登记 + 计数迁移，且只发一次。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            Assert.True(harness.Reconciler.TryGrantMissing("receipt-1"));

            GrantItemCommand command = Assert.Single(harness.Handler.Commands);
            Assert.Equal(GoldItemId, command.ItemId);
            Assert.Equal(GoldCount, command.Count);
            Assert.Equal(MetaReasonTags.ShopGrant, command.ReasonTagId);
            Assert.Equal(6102, PurchaseReconciler.GrantReasonTagId);

            Assert.True(harness.Ledger.IsReceiptConsumed("receipt-1"));
            Assert.Equal(1, harness.Reconciler.GrantedCount);
            Assert.Equal(0, harness.Reconciler.MissingCount);

            // 第二次调用不得再发（这是最核心的防双发断言）。
            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(harness.Handler.Commands);
            Assert.Equal(1, harness.Reconciler.GrantedCount);
        }

        [Fact]
        public void TryGrantMissing_LedgerRecordFails_ReturnsFalseAndNeverResends()
        {
            // 台账写失败：指令已经发出去了，本类必须拒绝任何重发（否则重试一次就是真双发），
            // 如实返回 false 并让补发成功计数保持 0，缺口交给上层补偿。
            var ledger = new RejectingLedger();
            var bus = new CqeBus();
            var handler = new RecordingCommandHandler();
            bus.RegisterCommandHandler<GrantItemCommand>(handler);
            var reconciler = new PurchaseReconciler(ledger, bus);
            reconciler.Reconcile(new[] { Granted("receipt-1") });

            Assert.False(reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(handler.Commands);
            Assert.False(reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(handler.Commands);
            Assert.Equal(0, reconciler.GrantedCount);
            Assert.Equal(0, reconciler.MissingCount);

            // 下一轮对账看到的是"本类补发过、商店又报了一次"＝Duplicated，不会重新判成掉单。
            ReconcileAction[] actions = reconciler.Reconcile(new[] { Granted("receipt-1") });
            Assert.Equal(ReconcileState.Duplicated, Assert.Single(actions).State);
        }

        [Theory]
        [InlineData(0, GoldCount)]
        [InlineData(GoldItemId, 0)]
        [InlineData(-1, -1)]
        public void TryGrantMissing_WithoutItemPayload_RecordsWithoutSendingCommand(int itemId, int count)
        {
            // 订阅/功能解锁这类收据没有道具可发：不发 0 数量指令（只会制造噪音），但台账照登记，
            // 否则每次对账都会把它重新判成掉单。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", ProductId, itemId, count, PurchasedAt, "granted")
            });

            Assert.True(harness.Reconciler.TryGrantMissing("receipt-1"));

            Assert.Empty(harness.Handler.Commands);
            Assert.True(harness.Ledger.IsReceiptConsumed("receipt-1"));
            Assert.Equal(1, harness.Reconciler.GrantedCount);
            Assert.Equal(0, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void TryGrantMissing_EmptyProductId_SendsButLedgerRejectsAndNeverResends()
        {
            // 服务端收据只带交易号与到账内容、没带 SKU：玩家已经付钱，必须补发；
            // 但台账会以"商品 ID 为空"拒收，于是否返回 false 并留下台账缺口（由上层补偿），且绝不重发。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[]
            {
                new PurchaseReceipt("receipt-1", string.Empty, GoldItemId, GoldCount, PurchasedAt, "granted")
            });

            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(harness.Handler.Commands);
            Assert.False(harness.Ledger.IsReceiptConsumed("receipt-1"));

            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(harness.Handler.Commands);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
            Assert.Equal(0, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void TryGrantMissing_BusWithoutHandler_ReturnsFalseAndKeepsPendingForRetry()
        {
            // 未注册处理器（装配遗漏）：指令没送达，台账不登记、待补状态保留，接线修好后可以重试成功。
            var harness = new Harness(registerHandler: false);
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Equal(1, harness.Reconciler.MissingCount);
            Assert.False(harness.Ledger.IsReceiptConsumed("receipt-1"));
            Assert.Empty(harness.Handler.Commands);

            harness.Bus.RegisterCommandHandler<GrantItemCommand>(harness.Handler);

            Assert.True(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(harness.Handler.Commands);
            Assert.True(harness.Ledger.IsReceiptConsumed("receipt-1"));
        }

        [Fact]
        public void TryGrantMissing_HandlerThrows_ReturnsFalseAndKeepsPending()
        {
            // 处理器自己抛异常（代码缺陷）：同样按"指令没有确认落地"处理——状态不变，允许重试。
            var harness = new Harness(handlerThrows: true);
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Equal(1, harness.Reconciler.MissingCount);
            Assert.False(harness.Ledger.IsReceiptConsumed("receipt-1"));

            harness.Handler.ThrowOnHandle = false;

            Assert.True(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Single(harness.Handler.Commands);
        }

        [Fact]
        public void TryGrantMissing_ThenStoreReportsGranted_ReportsDuplicated()
        {
            // 补发之后商店又报同一条收据：判 Duplicated（只上报一次），绝不再发第二份。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });
            Assert.True(harness.Reconciler.TryGrantMissing("receipt-1"));

            ReconcileAction[] actions = harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });

            ReconcileAction action = Assert.Single(actions);
            Assert.Equal(ReconcileState.Duplicated, action.State);
            Assert.Equal(PurchaseReconciler.ReasonDuplicated, action.Reason);
            Assert.Equal(1, harness.Reconciler.GrantedCount);
            Assert.Single(harness.Handler.Commands);

            // 第三批同样的输入：状态没变，不再出动作。
            Assert.Empty(harness.Reconciler.Reconcile(new[] { Granted("receipt-1") }));
        }

        [Fact]
        public void Reconcile_StateChange_IsNotSwallowedByIdempotency()
        {
            // 幂等只吞"同一结论"：Unknown → granted 必须产出新动作，掉单才能真正被发现。
            var harness = new Harness();
            var unknown = new PurchaseReceipt("receipt-1", ProductId, GoldItemId, GoldCount, PurchasedAt, "pending");

            Assert.Equal(ReconcileState.Unknown, Assert.Single(harness.Reconciler.Reconcile(new[] { unknown })).State);
            Assert.Equal(ReconcileState.Missing, Assert.Single(harness.Reconciler.Reconcile(new[] { Granted("receipt-1") })).State);
            Assert.Equal(1, harness.Reconciler.MissingCount);
        }

        [Fact]
        public void Reconcile_LedgerGrantedElsewhere_ClearsPendingMissing()
        {
            // 上一轮判掉单，这一轮台账已经有记录了（另一台设备/上一进程补发过）：判 Granted 并从待补里摘掉。
            var harness = new Harness();
            harness.Reconciler.Reconcile(new[] { Granted("receipt-1") });
            Assert.Equal(1, harness.Reconciler.MissingCount);

            harness.SeedLedger("receipt-1");

            ReconcileAction action = Assert.Single(harness.Reconciler.Reconcile(new[] { Granted("receipt-1") }));
            Assert.Equal(ReconcileState.Granted, action.State);
            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.False(harness.Reconciler.TryGrantMissing("receipt-1"));
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Counters_StartAtZero()
        {
            var harness = new Harness();

            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
            Assert.Equal(0, harness.Reconciler.RefundedCount);
        }

        [Fact]
        public void Counters_TrackMixedScenario()
        {
            // 一次真实对账里三种状态同时出现：计数器各自独立，补发后只迁移掉单那一份。
            var harness = new Harness();
            harness.SeedLedger("receipt-paid");

            harness.Reconciler.Reconcile(new[]
            {
                Granted("receipt-paid"), Granted("receipt-lost"), Refunded("receipt-refunded")
            });

            Assert.Equal(1, harness.Reconciler.MissingCount);
            Assert.Equal(0, harness.Reconciler.GrantedCount);
            Assert.Equal(1, harness.Reconciler.RefundedCount);

            Assert.True(harness.Reconciler.TryGrantMissing("receipt-lost"));

            Assert.Equal(0, harness.Reconciler.MissingCount);
            Assert.Equal(1, harness.Reconciler.GrantedCount);
            Assert.Equal(1, harness.Reconciler.RefundedCount);
        }

        [Fact]
        public void LedgerAdapter_WritesThroughToWrappedLedger()
        {
            // 适配器必须只是"薄桥"：去重、受理、道具/数量口径全部沿用被包装台账的既有规则。
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            var adapter = new IapPurchaseLedgerAdapter(ledger);

            Assert.False(adapter.IsReceiptConsumed("receipt-1"));

            Assert.True(adapter.TryRecord(ProductId, "receipt-1", GoldItemId, GoldCount, out string reason));
            Assert.Equal(IapPurchaseLedger.ReasonRecorded, reason);
            Assert.True(adapter.IsReceiptConsumed("receipt-1"));
            Assert.True(ledger.IsReceiptConsumed("receipt-1"));
            Assert.Equal(1, ledger.RecordCount);
            Assert.Equal(GoldItemId, ledger.GrantedItemId);
            Assert.Equal(GoldCount, ledger.GrantedCount);

            // 同一收据第二次：台账自己的去重闸门生效。
            Assert.False(adapter.TryRecord(ProductId, "receipt-1", GoldItemId, GoldCount, out reason));
            Assert.Equal(IapPurchaseLedger.ReasonReceiptConsumed, reason);

            // 空商品 ID：台账拒收（对账侧会表现为"发了货但台账没写成功"，由补发返回值暴露）。
            Assert.False(adapter.TryRecord(string.Empty, "receipt-2", GoldItemId, GoldCount, out reason));
            Assert.Equal(IapPurchaseLedger.ReasonInvalidProduct, reason);
        }

        [Fact]
        public void LedgerAdapter_ValidatorRejects_ReturnsFalse()
        {
            // 收据验签失败（伪造/过期）：台账不放行，适配器如实透传。
            var ledger = new IapPurchaseLedger(new ToggleValidator { Accept = false });
            var adapter = new IapPurchaseLedgerAdapter(ledger);

            Assert.False(adapter.TryRecord(ProductId, "receipt-1", GoldItemId, GoldCount, out string reason));
            Assert.Equal(IapPurchaseLedger.ReasonReceiptRejected, reason);
            Assert.False(adapter.IsReceiptConsumed("receipt-1"));
        }

        [Fact]
        public void LedgerAdapter_NullLedger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IapPurchaseLedgerAdapter(null!));
        }

        /// <summary>构造一条"商店说已发货"的收据（是否掉单取决于台账）。</summary>
        private static PurchaseReceipt Granted(
            string receiptId, int itemId = GoldItemId, int count = GoldCount, long at = PurchasedAt)
        {
            return new PurchaseReceipt(receiptId, ProductId, itemId, count, at, "granted");
        }

        /// <summary>构造一条"商店说已退款"的收据。</summary>
        private static PurchaseReceipt Refunded(
            string receiptId, int itemId = GoldItemId, int count = GoldCount, long at = PurchasedAt)
        {
            return new PurchaseReceipt(receiptId, ProductId, itemId, count, at, "refunded");
        }

        /// <summary>
        /// 一次对账的测试装配：真 <see cref="CqeBus"/> + 真 <see cref="IapPurchaseLedger"/>（经适配器）
        /// + 记录型发奖处理器 + 可切换行为的收据校验器。
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                bool registerHandler = true,
                bool handlerThrows = false,
                bool ledgerAccepts = true)
            {
                Bus = new CqeBus();
                Handler = new RecordingCommandHandler { ThrowOnHandle = handlerThrows };
                if (registerHandler)
                {
                    Bus.RegisterCommandHandler<GrantItemCommand>(Handler);
                }

                Ledger = new IapPurchaseLedger(new ToggleValidator { Accept = ledgerAccepts });
                Reconciler = new PurchaseReconciler(new IapPurchaseLedgerAdapter(Ledger), Bus);
            }

            internal CqeBus Bus { get; }

            internal RecordingCommandHandler Handler { get; }

            internal IapPurchaseLedger Ledger { get; }

            internal PurchaseReconciler Reconciler { get; }

            /// <summary>往真台账里预置一条发放记录（制造"本地已发"的历史）。</summary>
            internal void SeedLedger(string receiptId)
            {
                var product = new IapProduct(ProductId, IapProductKind.Consumable, "$0.99", GoldItemId, GoldCount);
                Assert.True(
                    Ledger.TryRecord(in product, receiptId, out string reason),
                    "预置台账失败：" + reason);
            }
        }

        /// <summary>记录型发奖处理器：留下一份 <see cref="GrantItemCommand"/> 清单供断言。</summary>
        private sealed class RecordingCommandHandler : ICommandHandler<GrantItemCommand>
        {
            /// <summary>收到的指令（按顺序）。</summary>
            internal List<GrantItemCommand> Commands { get; } = new List<GrantItemCommand>();

            /// <summary>处理时抛异常（模拟背包切片的代码缺陷）。</summary>
            internal bool ThrowOnHandle { get; set; }

            /// <inheritdoc />
            public void Handle(in GrantItemCommand command)
            {
                if (ThrowOnHandle)
                {
                    throw new InvalidOperationException("inventory exploded");
                }

                Commands.Add(command);
            }
        }

        /// <summary>
        /// 可编程台账端口：默认<b>拒绝</b>一切写入（验收"台账登记失败绝不允许重发"），
        /// 只有显式打开 <see cref="Accept"/> 才真正登记。
        /// </summary>
        private sealed class RejectingLedger : IIapPurchaseLedger
        {
            /// <summary>已登记收据（仅在 <see cref="Accept"/> 为 <c>true</c> 时增长）。</summary>
            private readonly HashSet<string> _consumed = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>是否接受写入。</summary>
            internal bool Accept { get; set; }

            /// <inheritdoc />
            public bool IsReceiptConsumed(string receiptId)
            {
                return !string.IsNullOrEmpty(receiptId) && _consumed.Contains(receiptId);
            }

            /// <inheritdoc />
            public bool TryRecord(string productId, string receiptId, int itemId, int count, out string reason)
            {
                if (!Accept)
                {
                    reason = "ledger offline";
                    return false;
                }

                _consumed.Add(receiptId);
                reason = "recorded";
                return true;
            }
        }

        /// <summary>可切换行为的收据校验器（验收"校验失败 → 台账拒收"）。</summary>
        private sealed class ToggleValidator : IReceiptValidator
        {
            /// <summary>是否通过校验。</summary>
            internal bool Accept { get; set; } = true;

            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return Accept;
            }
        }
    }
}

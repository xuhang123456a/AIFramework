#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台商店闭环接线：把"假商店 + 游戏侧台账 + CQE 总线 + 埋点"装成一条可直接调用的链路，
    /// 让 <b>商店 → IAP → 发货 → 埋点</b> 在无头环境里端到端跑通（T5-3）。
    /// <para>
    /// <b>为什么需要这个类</b>：<see cref="ConsoleStorePurchaseFlow"/> 之前的四个端口各自都被单测覆盖，
    /// 但没有任何地方把它们串起来——真机上"扣了钱没到账"正是发生在接缝处，不是发生在任何一个端口内部。
    /// 这里同时是 <c>ConsoleAppBootstrap</c>（本任务不允许改动）将来接线的**参考实现**：
    /// 装配代码照抄构造函数里的四行即可。
    /// </para>
    /// <para>
    /// <b>两份台账是刻意的，不是重复</b>：
    /// <list type="bullet">
    /// <item><see cref="ConsoleIapService"/> 持有的那份是**商店侧已购清单**（模拟 App Store / Play Billing 的记录），
    /// 它在 <c>Purchase</c> 内部就登记收据，用来回答"这件非消耗品我买过没有"。</item>
    /// <item>协调器持有的 <see cref="Ledger"/> 是**游戏侧发放台账**（回答"这份权益我发过没有"），
    /// 跨商店实现、跨平台都不变。</item>
    /// </list>
    /// 把同一份台账同时交给两边，会让每笔购买都被判成 <c>receipt already consumed</c>——这个坑在
    /// <see cref="StorePurchaseCoordinator"/> 的类型注释里有完整说明，这里用两份实例把正确形态钉死。
    /// </para>
    /// <para>
    /// <b>它不做的事</b>：不注册任何 CQE 处理器（那是装配入口的职责，本类只要求传入的总线已经注册了
    /// <see cref="AIFramework.Contracts.Meta.GrantItemCommand"/> 处理器），不落盘，不连网络。
    /// 因此它既可以给冒烟自检用，也可以在测试里当"真实接线"的替身。
    /// </para>
    /// <para>线程模型：非线程安全。Console 主线程串行调用（与 <see cref="ConsoleIapService"/> 一致）。</para>
    /// </summary>
    public sealed class ConsoleStorePurchaseFlow
    {
        /// <summary>
        /// 创建控制台商店闭环。
        /// </summary>
        /// <param name="bus">CQE 总线（非 <c>null</c>；必须已注册 <c>GrantItemCommand</c> 处理器）。</param>
        /// <param name="analytics">埋点端口（<c>null</c> = 不埋点，链路行为不变）。</param>
        /// <param name="storeAvailable">假商店是否可用（<c>false</c> 用来验收离线降级路径）。</param>
        /// <param name="catalog">商品目录（<c>null</c> = <see cref="ConsoleIapService.CreateDefaultCatalog"/>）。</param>
        /// <param name="validator">收据校验器（<c>null</c> = "非空商品 + 非空收据即通过"的本地 mock）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>（由协调器抛出，装配期错误）。</exception>
        public ConsoleStorePurchaseFlow(
            ICqeBus bus,
            IAnalyticsService? analytics = null,
            bool storeAvailable = true,
            IapProduct[]? catalog = null,
            IReceiptValidator? validator = null)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            IReceiptValidator receiptValidator = validator ?? new NonEmptyReceiptValidator();
            IapProduct[] storeCatalog = catalog ?? ConsoleIapService.CreateDefaultCatalog();

            // 商店侧台账：模拟"平台记住了哪些已购商品"。真实商店由 SDK 自己记，客户端拿不到也不需要。
            var storeLedger = new IapPurchaseLedger(receiptValidator);
            Store = new ConsoleIapService(storeLedger, storeCatalog, storeAvailable);

            // 游戏侧台账：回答"这份权益有没有发过"，与商店实现无关。
            Ledger = new IapPurchaseLedger(receiptValidator);

            Coordinator = new StorePurchaseCoordinator(Store, Ledger, bus, analytics);
        }

        /// <summary>底层假商店（需要看 <see cref="ConsoleIapService.DrainLog"/> 的排障日志时用）。</summary>
        public ConsoleIapService Store { get; }

        /// <summary>游戏侧发放台账（收据去重 + 拥有型一次性发放）；跨启动持久化属于装配层后续工作。</summary>
        public IapPurchaseLedger Ledger { get; }

        /// <summary>购买协调器（闭环主入口；需要更细的结果时直接用它）。</summary>
        public StorePurchaseCoordinator Coordinator { get; }

        /// <summary>当前商品目录（每次取商店最新，返回副本）。</summary>
        public IapProduct[] Catalog
        {
            get { return Coordinator.Catalog; }
        }

        /// <summary>购买一件商品并完成"发货 + 埋点"（失败不发物品，见 <see cref="StorePurchaseOutcome"/>）。</summary>
        /// <param name="productId">商店商品 ID。</param>
        /// <returns>闭环结果（永不抛异常）。</returns>
        public StorePurchaseOutcome Buy(string productId)
        {
            return Coordinator.Purchase(productId);
        }

        /// <summary>
        /// 购买一件商品，并用调用方给的道具补足目录里没有配置的到账内容
        /// （目录只列 SKU 与价格、到账由调用点决定的场景）。
        /// </summary>
        /// <param name="productId">商店商品 ID。</param>
        /// <param name="overrideItemId">兜底到账道具 ID（<c>&lt;= 0</c> 时用协调器的 goldItemId）。</param>
        /// <param name="overrideCount">兜底到账数量（<c>&lt;= 0</c> 时本重载不起作用）。</param>
        /// <returns>闭环结果（永不抛异常）。</returns>
        public StorePurchaseOutcome Buy(string productId, int overrideItemId, int overrideCount)
        {
            return Coordinator.Purchase(productId, overrideItemId, overrideCount);
        }

        /// <summary>恢复购买（只补本地缺失，不重复发；返回本次恢复确认的件数）。</summary>
        /// <returns>恢复确认件数（<c>0</c> = 没有可恢复项 / 商店不可用）。</returns>
        public int Restore()
        {
            return Coordinator.RestorePurchases();
        }

        /// <summary>取走假商店的购买日志（冒烟/排障用；取走即清空）。</summary>
        /// <returns>按发生顺序排列的日志条目。</returns>
        public IReadOnlyList<string> DrainStoreLog()
        {
            return Store.DrainLog();
        }

        /// <summary>
        /// 本地收据校验器：商品与收据都非空即通过。
        /// <para>
        /// 真实实现必须是服务端验签（App Store Server API / Play Developer API）；这个 mock 的用途只是
        /// 让无头链路跑通，且它<b>仍然会拒</b>坏形状的收据（空商品/空收据），因此"坏收据不发货"这条
        /// 在适配层也能被验到。
        /// </para>
        /// </summary>
        private sealed class NonEmptyReceiptValidator : IReceiptValidator
        {
            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return !string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(receipt);
            }
        }
    }
}

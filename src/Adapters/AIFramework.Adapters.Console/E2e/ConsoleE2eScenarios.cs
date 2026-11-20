#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Gameplay;
using AIFramework.Core.Save;
using AIFramework.Core.Services;
using AIFramework.Features.Inventory;

namespace AIFramework.Adapters.Console.E2e
{
    /// <summary>
    /// 控制台 E2E 场景库：把仓库里<b>已有的真实能力</b>（账号会话、商店闭环、背包账本、
    /// 回合内核、存档包络、掉单对账）按玩家真实路径串成一条可断言的无头场景。
    /// <para>
    /// <b>为什么这层必须存在</b>：上面每个组件都有自己的单测，但"真机上扣了钱没到账"这类事故
    /// 从不发生在组件内部，而是发生在接缝处——购买闭环把 <c>GrantItemCommand</c> 发到总线，
    /// 背包切片是否真的挂在那根总线上、战斗结算的战利品是否真的走同一条发放路径、
    /// 对账补发是否落到同一个账本，只有把整条链跑一遍才能验到。这里不新增业务规则，
    /// 只做<b>编排与断言</b>。
    /// </para>
    /// <para>
    /// <b>不伪造内部状态</b>：每一步只调用公开 API（登录、购买、推进会话、收发命令、写读存档、对账），
    /// 断言只读公开快照（账号、购买结果、背包数量、结算报告、存档字节、对账计数）。
    /// 因此场景通过 ⟺ 真实接线成立；场景失败时报告能直接指向断掉的接缝。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步</b>。场景由 <see cref="E2eRunner"/> 在调用线程上串行执行；
    /// 步骤闭包共享本方法内的局部状态，禁止并发跑同一条场景。
    /// </para>
    /// </summary>
    public static class ConsoleE2eScenarios
    {
        /// <summary>主流程场景名（进 <c>[E2E] 场景=</c>，CI 用它做聚合键）。</summary>
        public const string MainFlowName = "console-main-flow";

        /// <summary>战斗步骤的推演步数上限：防止内核不结束导致冒烟挂死（TurnWorld 正常 6 步内结束）。</summary>
        public const int BattleStepLimit = 64;

        /// <summary>存档槽位号（场景独占，避免覆盖调用方的业务槽位）。</summary>
        public const int SaveSlot = 0;

        /// <summary>对账收据前缀：与真实商店收据区分，避免误判"这张收据我发过"。</summary>
        public const string ReceiptPrefix = "e2e-receipt-";

        /// <summary>对账补发的金币数量（与收据声明一致，断言按它算期望增量）。</summary>
        public const int ReconcileGoldCount = 60;

        /// <summary>
        /// 主流程场景：<b>登录 → 购买 → 重复购买拒绝 → 背包到账 → 战斗 → 结算 → 存档 → 对账</b>。
        /// <para>
        /// <b>参数全部是真实依赖，不在这里 <c>new</c> 任何业务实现</b>（除了两个纯适配层的假后端：
        /// <see cref="ConsoleAccountService"/> 与它上面的 <see cref="AccountSessionManager"/>，
        /// 它们本身就是"无头环境下的账号后端"）。这样同一条场景既能给
        /// <c>ConsoleAppBootstrap</c> 冒烟用，也能在单测里对真实总线/真实背包断言。
        /// </para>
        /// <para>
        /// <b>对参数的要求</b>：<paramref name="bus"/> 必须已注册 <see cref="GrantItemCommand"/> 处理器
        /// （即 <see cref="InventoryHandler.Register"/> 已调用）；否则购买与补发都无法到账，
        /// 场景会以"购买未发货/背包无道具"失败——这正是装配遗漏该有的表现，而不是被悄悄吞掉。
        /// </para>
        /// </summary>
        /// <param name="time">时间源（账号会话 TTL 与 E2E 计时共用）。</param>
        /// <param name="bus">CQE 总线（购买发货、战利品结算、对账补发都走它）。</param>
        /// <param name="purchaseFlow">已接线的商店闭环（自带商店侧与游戏侧两份台账）。</param>
        /// <param name="inventory">背包账本（真实切片；断言读它的 <see cref="InventoryHandler.GetCount"/>）。</param>
        /// <param name="host">玩法会话宿主（战斗步骤从它取回合会话）。</param>
        /// <param name="save">存档服务（写读同一槽位，验包络往返）。</param>
        /// <returns>可交给 <see cref="E2eRunner"/> 的场景（步骤顺序即玩家路径顺序）。</returns>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>（装配期编程错误）。</exception>
        public static E2eScenario MainFlow(
            ITimeProvider time,
            ICqeBus bus,
            ConsoleStorePurchaseFlow purchaseFlow,
            InventoryHandler inventory,
            GameplaySessionHost host,
            SaveService save)
        {
            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (purchaseFlow == null)
            {
                throw new ArgumentNullException(nameof(purchaseFlow));
            }

            if (inventory == null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }

            // 账号会话：真实 Console 账号后端 + Core 的会话状态机（TTL 走传入的时间源）。
            var accounts = new ConsoleAccountService(time);
            var session = new AccountSessionManager(accounts, time);

            // 对账器：包装商店闭环持有的"游戏侧发放台账"（不是商店侧那份，见 ConsoleStorePurchaseFlow 注释）。
            var reconciler = new PurchaseReconciler(new IapPurchaseLedgerAdapter(purchaseFlow.Ledger), bus);

            // ---- 步骤间共享的状态槽（全部由步骤自己写入，verify 只读）----
            AccountLoginOutcome login = default;
            AccountInfo account = default;
            bool hasAccount = false;

            StorePurchaseOutcome goldPurchase = default;
            StorePurchaseOutcome noAdsPurchase = default;
            StorePurchaseOutcome noAdsRepeat = default;

            int busGoldCount = 0;
            int busNoAdsCount = 0;

            GameplayResult? battle = null;

            int goldBeforeSettlement = 0;
            int settlementDelta = 0;

            int goldBeforeReconcile = 0;
            int missingGranted = 0;

            var steps = new E2eStep[]
            {
                // 1) 登录：静默游客登录必须真的建立起会话（不是"没报错就算过"）。
                new E2eStep(
                    "账号-游客静默登录",
                    () =>
                    {
                        login = session.EnsureSignedIn();
                        hasAccount = session.TryGetAccount(out account);
                    },
                    () => login.IsSuccess
                        && hasAccount
                        && session.State == AccountState.SignedIn
                        && account.Kind == AccountKind.Guest
                        && account.PlayerId.Length > 0),

                // 2) 购买：消耗品必须真的发货（Granted + 到账内容与目录一致）。
                new E2eStep(
                    "商店-购买消耗品",
                    () => { goldPurchase = purchaseFlow.Buy(ConsoleIapService.ProductGold60); },
                    () => goldPurchase.Granted
                        && goldPurchase.ItemId == ConsoleIapService.GoldItemId
                        && goldPurchase.Count == 60),

                // 3) 重复购买：拥有型商品第二次必须被拒（AlreadyOwned），且不发第二份。
                new E2eStep(
                    "商店-重复购买拥有型被拒",
                    () =>
                    {
                        noAdsPurchase = purchaseFlow.Buy(ConsoleIapService.ProductNoAds);
                        noAdsRepeat = purchaseFlow.Buy(ConsoleIapService.ProductNoAds);
                    },
                    () => noAdsPurchase.Granted
                        && !noAdsRepeat.Granted
                        && string.Equals(
                            noAdsRepeat.Reason,
                            IapPurchaseState.AlreadyOwned.ToString(),
                            StringComparison.Ordinal)),

                // 4) 背包：购买闭环真的把道具写进了背包账本，且**总线查询**能读到同一份数字
                //    （既验到账、也验查询处理器挂上了；只看商店返回值这两件事都验不到）。
                new E2eStep(
                    "背包-购买到账",
                    () =>
                    {
                        busGoldCount = bus.Query<InventoryQueryRequest, InventorySnapshot>(
                            new InventoryQueryRequest(ConsoleIapService.GoldItemId)).Count;
                        busNoAdsCount = bus.Query<InventoryQueryRequest, InventorySnapshot>(
                            new InventoryQueryRequest(ConsoleIapService.NoAdsItemId)).Count;
                    },
                    () => busGoldCount >= 60
                        && busNoAdsCount >= 1
                        && inventory.GetCount(ConsoleIapService.GoldItemId) == busGoldCount
                        && inventory.GetCount(ConsoleIapService.NoAdsItemId) == busNoAdsCount),

                // 5) 战斗：起一个回合会话推演到结束，并取回真实结算报告。
                new E2eStep(
                    "战斗-回合会话",
                    () =>
                    {
                        var init = new GameplayInitData(1, 20261120, 3);
                        IGameplaySession battleSession = host.Begin(GameplayKernelType.Turn, in init);

                        int steps = 0;
                        while (!battleSession.IsFinished && steps < BattleStepLimit)
                        {
                            battleSession.Step(1f / 60f);
                            steps++;
                        }

                        battle = host.EndAndCollect();
                    },
                    () => battle != null
                        && battle.KernelType == GameplayKernelType.Turn
                        && battle.IsVictory
                        && battle.StepCount > 0),

                // 6) 结算：战利品走真实发放链路（GrantItemCommand → 背包），并核对增量。
                new E2eStep(
                    "结算-战利品入包",
                    () =>
                    {
                        goldBeforeSettlement = inventory.GetCount(ConsoleIapService.GoldItemId);
                        settlementDelta = 0;

                        if (battle == null)
                        {
                            // 上游战斗失败时（非停止模式）没有结算可发：保持增量 0，由断言报失败。
                            return;
                        }

                        IReadOnlyList<LootItem> loots = battle.Loots;
                        for (int i = 0; i < loots.Count; i++)
                        {
                            LootItem loot = loots[i];
                            if (loot.ItemId == ConsoleIapService.GoldItemId)
                            {
                                settlementDelta += loot.Count;
                            }

                            bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
                        }
                    },
                    () => battle != null
                        && settlementDelta > 0
                        && inventory.GetCount(ConsoleIapService.GoldItemId) == goldBeforeSettlement + settlementDelta),

                // 7) 存档：真实 SaveService 写 AISV 包络，再 TryLoad 解回来与背包逐条比对。
                new E2eStep(
                    "存档-写入读回一致",
                    () =>
                    {
                        IReadOnlyList<InventoryEntry> snapshot = inventory.Snapshot();
                        var rows = new InventoryEntrySnapshot[snapshot.Count];
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            rows[i] = new InventoryEntrySnapshot(snapshot[i].ItemId, snapshot[i].Count);
                        }

                        save.Save(SaveSlot, new SaveEnvelope(SaveService.CurrentVersion, InventorySaveCodec.Encode(rows)));
                    },
                    () => VerifySaveRoundTrip(save, inventory)),

                // 8) 对账：一批"商店已成交"的收据里，掉单（Missing）的那张补发一次，退款的只计数不发货。
                new E2eStep(
                    "对账-掉单补发",
                    () =>
                    {
                        goldBeforeReconcile = inventory.GetCount(ConsoleIapService.GoldItemId);
                        missingGranted = 0;

                        ReconcileAction[] actions = reconciler.Reconcile(new[]
                        {
                            new PurchaseReceipt(
                                ReceiptPrefix + "gold60",
                                ConsoleIapService.ProductGold60,
                                ConsoleIapService.GoldItemId,
                                ReconcileGoldCount,
                                0L,
                                "granted"),
                            new PurchaseReceipt(
                                ReceiptPrefix + "no-ads",
                                ConsoleIapService.ProductNoAds,
                                ConsoleIapService.NoAdsItemId,
                                1,
                                0L,
                                "refunded"),
                        });

                        for (int i = 0; i < actions.Length; i++)
                        {
                            if (actions[i].State == ReconcileState.Missing
                                && reconciler.TryGrantMissing(actions[i].ReceiptId))
                            {
                                missingGranted++;
                            }
                        }
                    },
                    () => missingGranted == 1
                        && reconciler.MissingCount == 0
                        && reconciler.GrantedCount == 1
                        && reconciler.RefundedCount == 1
                        && inventory.GetCount(ConsoleIapService.GoldItemId) == goldBeforeReconcile + ReconcileGoldCount),
            };

            return new E2eScenario(MainFlowName, steps);
        }

        /// <summary>
        /// 校验存档往返：<see cref="SaveService.TryLoad"/> 读回 → <see cref="InventorySaveCodec"/> 解码 →
        /// 与背包当前快照逐条比对（数量、ID、顺序）。
        /// <para>
        /// <b>为什么比"读回来了"更严</b>：包络 CRC 通过只证明字节完整，不证明<b>内容</b>是背包——
        /// 编码器把 itemId/count 写反、存档层错位一个字段，CRC 都会照样通过。只有逐条比对才能抓住这类错位。
        /// </para>
        /// </summary>
        /// <param name="save">存档服务。</param>
        /// <param name="inventory">背包账本（快照已按 itemId 升序，编码顺序与它一致）。</param>
        /// <returns>是否完全一致。</returns>
        private static bool VerifySaveRoundTrip(SaveService save, InventoryHandler inventory)
        {
            if (!save.TryLoad(SaveSlot, out SaveEnvelope envelope, out _))
            {
                return false;
            }

            if (!InventorySaveCodec.TryDecode(
                    envelope.Payload, InventorySaveCodec.FormatVersion1, out InventorySaveEntry[] decoded))
            {
                return false;
            }

            IReadOnlyList<InventoryEntry> snapshot = inventory.Snapshot();
            if (decoded.Length != snapshot.Count)
            {
                return false;
            }

            for (int i = 0; i < decoded.Length; i++)
            {
                if (decoded[i].ItemId != snapshot[i].ItemId || decoded[i].Count != snapshot[i].Count)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

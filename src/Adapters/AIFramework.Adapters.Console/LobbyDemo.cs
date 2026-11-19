#nullable enable
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Save;
using AIFramework.Features.Gacha;
using AIFramework.Features.IdleTycoon;
using AIFramework.Features.Inventory;
using AIFramework.Features.Mail;
using AIFramework.Features.Merge;
using AIFramework.Features.Quest;
using AIFramework.Features.Shop;
using AIFramework.Features.TowerDefense;
using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 大厅冒烟：把 Meta 类切片（背包 / 商店 / 任务 / 邮件 / 红点 / 弹窗队列）串成一条真实链路，
    /// 并把背包快照走一遍 <see cref="SaveService"/> 的存档往返。
    /// <para>
    /// 这里只做编排与打印，**不含业务规则**；规则的唯一归属是各切片自身的 Handler。
    /// </para>
    /// </summary>
    public sealed class LobbyDemo
    {
        /// <summary>演示用初始金币。</summary>
        private const int InitialGold = 2000;

        private readonly IIOWriter _writer;
        private readonly ICqeBus _bus;
        private readonly SystemTimeProvider _time;
        private readonly IBinaryTableLoader _tables;
        private readonly IStoragePort _storage;

        private readonly InventoryHandler _inventory;
        private readonly Core.Meta.RedDotTree _redDots;
        private readonly Core.Ui.PopupQueue _popups;

        private MergeHandler? _merge;
        private GachaHandler? _gacha;
        private IdleTycoonHandler? _idle;
        private TowerDefenseHandler? _towerDefense;

        /// <summary>
        /// 背包与红点由装配入口创建并注入：它们是多切片共享的底座，
        /// 不能由大厅再 new 一份（重复注册会直接抛异常，这条约束由总线强制）。
        /// </summary>
        public LobbyDemo(
            IIOWriter writer,
            ICqeBus bus,
            SystemTimeProvider time,
            IBinaryTableLoader tables,
            IStoragePort storage,
            InventoryHandler inventory,
            Core.Meta.RedDotTree redDots)
        {
            _writer = writer;
            _bus = bus;
            _time = time;
            _tables = tables;
            _storage = storage;

            _inventory = inventory;
            _redDots = redDots;
            _popups = new Core.Ui.PopupQueue();
        }

        /// <summary>装配大厅自有切片（背包与红点已由装配入口注册，这里不再重复注册）。</summary>
        public void Wire()
        {
            var shop = new ShopHandler(_bus, _tables);
            shop.Register(_bus);

            var quest = new QuestHandler(_bus, _tables);
            quest.Register(_bus);

            var mail = new MailHandler(_bus, _time, _tables);
            mail.Register(_bus);
            mail.SeedRedDot(_bus);

            // 其余玩法切片：注册进同一总线，证明"多玩法共存于同一主板"不是口号。
            _merge = new MergeHandler(_bus, _tables);
            _merge.Register(_bus);

            _gacha = new GachaHandler(_bus, new XorShiftRandomSource(20261120), _tables);
            _gacha.Register(_bus);

            _idle = new IdleTycoonHandler(_bus, _time, _tables);
            _idle.Register(_bus);

            _towerDefense = new TowerDefenseHandler(_bus, _tables);
            _towerDefense.Register(_bus);

            _inventory.Seed(MetaItemIds.Gold, InitialGold);
            _inventory.Seed(MetaItemIds.RetroactiveCard, 0);
        }

        /// <summary>跑一遍完整大厅链路并打印每步结果。</summary>
        public void Run()
        {
            _writer.WriteLine("--- 大厅（Meta 切片）---");
            _writer.WriteLine("初始背包：" + DescribeInventory());

            SimulateShopPurchase();
            SimulateQuestFlow();
            SimulateMailFlow();
            SimulatePopupQueue();
            ReportRedDots();
            SimulateOtherGenres();
            RunSaveRoundTrip();
        }

        private void SimulateShopPurchase()
        {
            var catalog = new ShopCatalogQueryRequest();
            ShopCatalogSnapshot catalogSnapshot = _bus.Query<ShopCatalogQueryRequest, ShopCatalogSnapshot>(in catalog);
            _writer.WriteLine("[Shop] 目录：" + DescribeCatalog(catalogSnapshot));

            // 买第一件（100 金币）：验证"先扣后给"的整条链路。
            int targetItemId = catalogSnapshot.Items.Count > 0 ? catalogSnapshot.Items[0].ItemId : 0;
            if (targetItemId == 0)
            {
                _writer.WriteLine("[Shop] 目录为空，跳过购买冒烟。");
                return;
            }

            var buy = new BuyShopItemCommand(targetItemId, 1);
            _bus.Send(in buy);
            _writer.WriteLine("[Shop] 购买 item=" + targetItemId + " → 金币=" + _inventory.GetCount(MetaItemIds.Gold)
                + "，背包=" + DescribeInventory());
        }

        private void SimulateQuestFlow()
        {
            // 打三局：任务切片只看到"战斗结束"这个事实，不需要知道打的是什么。
            for (int i = 2; i <= 4; i++)
            {
                var finished = new GameFinishedEvent(GameplayKernelType.Turn, i, isVictory: true);
                _bus.Publish(in finished);
            }

            var listQuery = new QuestListQueryRequest();
            QuestListSnapshot quests = _bus.Query<QuestListQueryRequest, QuestListSnapshot>(in listQuery);
            _writer.WriteLine("[Quest] 进度：" + DescribeQuests(quests));

            for (int i = 0; i < quests.Quests.Count; i++)
            {
                QuestViewData quest = quests.Quests[i];
                if (quest.Claimed || quest.Progress < quest.TargetCount)
                {
                    continue;
                }

                var claim = new ClaimQuestRewardCommand(quest.QuestId);
                _bus.Send(in claim);
                _writer.WriteLine("[Quest] 领取任务 " + quest.QuestId + " → 背包=" + DescribeInventory());
            }
        }

        private void SimulateMailFlow()
        {
            var listQuery = new MailListQueryRequest();
            MailListSnapshot mails = _bus.Query<MailListQueryRequest, MailListSnapshot>(in listQuery);
            _writer.WriteLine("[Mail] 收件箱：" + DescribeMails(mails));

            for (int i = 0; i < mails.Mails.Count; i++)
            {
                MailViewData mail = mails.Mails[i];
                if (mail.State != MailState.Unread)
                {
                    continue;
                }

                var claim = new ClaimMailCommand(mail.MailId);
                _bus.Send(in claim);
                _writer.WriteLine("[Mail] 领取邮件 " + mail.MailId + " → 金币=" + _inventory.GetCount(MetaItemIds.Gold));
            }

            // 再查一次：确认状态变成已领取，且过期判定不影响未读。
            MailListSnapshot after = _bus.Query<MailListQueryRequest, MailListSnapshot>(in listQuery);
            _writer.WriteLine("[Mail] 领取后：" + DescribeMails(after));
        }

        private void SimulatePopupQueue()
        {
            _popups.Enqueue(101, priority: 10);
            _popups.Enqueue(102, priority: 50);
            _popups.Enqueue(103, priority: 10);
            _popups.Enqueue(102, priority: 99); // 同 id 重复入队应被忽略（保持首次优先级）

            var order = new List<int>();
            while (_popups.TryDequeue(out int popupId))
            {
                order.Add(popupId);
            }

            _writer.WriteLine("[Popup] 出队顺序=" + string.Join(" → ", order) + "，剩余=" + _popups.Count
                + "，丢弃=" + _popups.DroppedCount);
        }

        /// <summary>
        /// 其余玩法切片冒烟：合成 / 抽卡 / 放置经营 / 塔防建造。
        /// <b>为什么都放在大厅里跑</b>：它们只依赖 CQE 总线与 Contracts 端口、不需要单独的内核会话，
        /// 正好验证"Meta 类玩法能零成本叠在同一个后台上"。
        /// </summary>
        private void SimulateOtherGenres()
        {
            SimulateMerge();
            SimulateIdle();
            SimulateTowerDefense();
            SimulateGacha();
        }

        private void SimulateMerge()
        {
            MergeHandler? merge = _merge;
            if (merge == null)
            {
                _writer.WriteLine("[Merge] 切片未装配，跳过。");
                return;
            }

            _bus.Send(new SpawnItemCommand(0, 0, 1));
            _bus.Send(new SpawnItemCommand(1, 0, 1));
            _bus.Send(new MergeCellsCommand(0, 0, 1, 0));

            var query = new MergeGridQueryRequest();
            MergeGridSnapshot grid = _bus.Query<MergeGridQueryRequest, MergeGridSnapshot>(in query);
            _writer.WriteLine("[Merge] (1,0) 等级=" + grid.LevelAt(1, 0)
                + "，已用格=" + grid.UsedCellCount + "，合成次数=" + merge.MergeCount
                + "，金币=" + _inventory.GetCount(MetaItemIds.Gold));
        }

        private void SimulateGacha()
        {
            GachaHandler? gacha = _gacha;
            if (gacha == null)
            {
                _writer.WriteLine("[Gacha] 切片未装配，跳过。");
                return;
            }

            _bus.Send(new DrawGachaCommand(1));
            _writer.WriteLine("[Gacha] 单抽=" + DescribeGachaResult(gacha.LastResult)
                + "，保底计数=" + gacha.PityCounter + "，金币=" + _inventory.GetCount(MetaItemIds.Gold));

            _bus.Send(new DrawGachaCommand(10));
            _writer.WriteLine("[Gacha] 十连=" + DescribeGachaResult(gacha.LastResult)
                + "，保底计数=" + gacha.PityCounter + "，金币=" + _inventory.GetCount(MetaItemIds.Gold));
        }

        private void SimulateIdle()
        {
            IdleTycoonHandler? idle = _idle;
            if (idle == null)
            {
                _writer.WriteLine("[Idle] 切片未装配，跳过。");
                return;
            }

            _bus.Send(new UpgradeBuildingCommand(1));
            int level = idle.GetLevel(1);
            int income = idle.IncomePerSecond;

            // 推进 30 秒后结算离线收益（时间源是注入的，测试/演示都可控）。
            _time.Advance(30d);
            int collected = idle.SettleOfflineIncome();

            _writer.WriteLine("[Idle] 建筑1 等级=" + level + "，收入/秒=" + income
                + "，结算 30 秒收益=" + collected + "，金币=" + _inventory.GetCount(MetaItemIds.Gold));
        }

        private void SimulateTowerDefense()
        {
            TowerDefenseHandler? towers = _towerDefense;
            if (towers == null)
            {
                _writer.WriteLine("[TowerDefense] 切片未装配，跳过。");
                return;
            }

            _bus.Send(new BuildTowerCommand(0, 0, 1));
            _bus.Send(new BuildTowerCommand(0, 0, 2)); // 同一格重复建造应被静默拒绝

            var query = new TowerLayoutQueryRequest();
            TowerLayoutSnapshot layout = _bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(in query);
            _writer.WriteLine("[TowerDefense] (0,0)=" + layout.TowerIdAt(0, 0)
                + "，塔数=" + layout.TowerCount + "，金币=" + _inventory.GetCount(MetaItemIds.Gold));
        }

        private static string DescribeGachaResult(GachaResult? result)
        {
            if (result == null)
            {
                return "无";
            }

            var builder = new System.Text.StringBuilder();
            GachaResultItem[] items = result.Items;
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(items[i].ItemId);
                if (items[i].IsRare)
                {
                    builder.Append("(稀有)");
                }
            }

            return builder.ToString();
        }

        private void ReportRedDots()
        {
            _writer.WriteLine("[RedDot] 签到=" + QueryRedDot(RedDotNodeIds.SignIn)
                + " 任务=" + QueryRedDot(RedDotNodeIds.Quest)
                + " 邮件=" + QueryRedDot(RedDotNodeIds.Mail)
                + " 商店=" + QueryRedDot(RedDotNodeIds.Shop));
        }

        private int QueryRedDot(int nodeId)
        {
            var query = new RedDotQueryRequest(nodeId);
            return _bus.Query<RedDotQueryRequest, RedDotSnapshot>(in query).Count;
        }

        /// <summary>把背包快照写进 SaveService 再读回：验证"存档能真的落盘再接回业务"。 </summary>
        private void RunSaveRoundTrip()
        {
            IReadOnlyList<InventoryEntry> entries = _inventory.Snapshot();

            // 用 codec 写 v2（带 updatedTicks），读档走自动识别——v1 旧档也能被读，且不会再出现
            // "手写 BinaryReader 按 v1 行距解析 v2 字节"这类静默错位。
            var saveEntries = new InventorySaveEntry[entries.Count];
            for (int i = 0; i < saveEntries.Length; i++)
            {
                saveEntries[i] = new InventorySaveEntry(entries[i].ItemId, entries[i].Count, 0L);
            }

            var save = new SaveService(_storage, "lobby");
            save.Save(0, new SaveEnvelope(SaveService.CurrentVersion, InventorySaveCodec.EncodeV2(saveEntries)));

            if (!save.TryLoad(0, out SaveEnvelope loaded))
            {
                _writer.WriteLine("[Save] 存档往返失败：读回为空。");
                return;
            }

            if (!InventorySaveCodec.TryDecodeAuto(loaded.Payload, out InventorySaveEntry[] decoded))
            {
                _writer.WriteLine("[Save] 存档往返失败：负载无法解析。");
                return;
            }

            var parts = new List<string>(decoded.Length);
            for (int i = 0; i < decoded.Length; i++)
            {
                parts.Add(decoded[i].ItemId + "=" + decoded[i].Count);
            }

            _writer.WriteLine("[Save] 背包存档往返（schema v" + loaded.SchemaVersion + "，" + loaded.Payload.Length
                + " 字节）：" + string.Join(", ", parts));
        }

        private string DescribeInventory()
        {
            IReadOnlyList<InventoryEntry> snapshot = _inventory.Snapshot();
            if (snapshot.Count == 0)
            {
                return "空";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(snapshot[i].ItemId).Append('=').Append(snapshot[i].Count);
            }

            return builder.ToString();
        }

        private static string DescribeCatalog(ShopCatalogSnapshot snapshot)
        {
            if (snapshot.Items.Count == 0)
            {
                return "空";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                ShopItemDef item = snapshot.Items[i];
                builder.Append(item.ItemId).Append("(价格 ").Append(item.PriceCount).Append('×').Append(item.PriceItemId).Append(')');
            }

            return builder.ToString();
        }

        private static string DescribeQuests(QuestListSnapshot snapshot)
        {
            if (snapshot.Quests.Count == 0)
            {
                return "空";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Quests.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                QuestViewData quest = snapshot.Quests[i];
                builder.Append('Q').Append(quest.QuestId).Append(' ')
                    .Append(quest.Progress).Append('/').Append(quest.TargetCount);
                if (quest.Claimed)
                {
                    builder.Append("(已领)");
                }
            }

            return builder.ToString();
        }

        private static string DescribeMails(MailListSnapshot snapshot)
        {
            if (snapshot.Mails.Count == 0)
            {
                return "空";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Mails.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                MailViewData mail = snapshot.Mails[i];
                builder.Append('M').Append(mail.MailId).Append('(').Append(mail.State)
                    .Append(' ').Append(mail.RewardItemId).Append('×').Append(mail.RewardCount).Append(')');
            }

            return builder.ToString();
        }
    }
}

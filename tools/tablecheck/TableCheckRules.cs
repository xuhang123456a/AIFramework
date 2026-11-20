using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFramework.TableGen;

namespace AIFramework.TableCheck
{
    /// <summary>一条体检问题：定位到"表 / 行 / 列"，并给出可执行的修复建议。</summary>
    public readonly struct TableIssue
    {
        public TableIssue(string table, int rowIndex, string column, string message)
        {
            Table = table;
            RowIndex = rowIndex;
            Column = column;
            Message = message;
        }

        /// <summary>表名（= JSON 的 table 字段，也是 .bin 文件名）。</summary>
        public string Table { get; }

        /// <summary>行号（0 起；-1 表示表级问题，如列缺失/排序错误）。</summary>
        public int RowIndex { get; }

        /// <summary>列名（空串表示整行/整表问题）。</summary>
        public string Column { get; }

        /// <summary>问题描述 + 修复建议（人读）。</summary>
        public string Message { get; }

        public override string ToString()
        {
            string where = RowIndex >= 0 ? "第 " + (RowIndex + 1) + " 行" : "表级";
            string column = string.IsNullOrEmpty(Column) ? string.Empty : " [" + Column + "]";
            return Table + " " + where + column + "：" + Message;
        }
    }

    /// <summary>
    /// 跨表/单表不变量校验器。
    /// <para>
    /// 为什么需要它：`tablegen --check` 只保证"JSON 与 .bin 一致"，**不保证数据本身合理**。
    /// 数值策划改一行 JSON（打折把价格改成 0、赛季经验写反、成就目标写 0）时，
    /// 二进制照样一致、客户照样启动，问题要到玩家点下去才暴露。这里把"明显不合理"的规则
    /// 收成一份可执行的体检清单，进 CI 前先挡掉。
    /// </para>
    /// <para>
    /// 规则是**白名单式**的：只校验我们明确知道语义的表/列；未知表一律跳过（不假装懂）。
    /// </para>
    /// </summary>
    public static class TableCheckRules
    {
        /// <summary>对全部表跑一遍规则，返回问题列表（空 = 通过）。</summary>
        public static List<TableIssue> Validate(IReadOnlyDictionary<string, TableData> tables)
        {
            var issues = new List<TableIssue>(16);

            foreach (KeyValuePair<string, TableData> pair in tables)
            {
                switch (pair.Key)
                {
                    case "TbShopItem":
                        CheckShopItem(pair.Value, issues);
                        break;
                    case "TbSignInReward":
                        CheckSignInReward(pair.Value, issues);
                        break;
                    case "TbAchievement":
                        CheckAchievement(pair.Value, issues);
                        break;
                    case "TbSeasonPass":
                        CheckSeasonPass(pair.Value, issues);
                        break;
                    case "TbGuideStep":
                        CheckGuideStep(pair.Value, issues);
                        break;
                    case "TbLocalization":
                        CheckLocalization(pair.Value, issues);
                        break;
                    case "TbCard":
                        CheckCard(pair.Value, issues);
                        break;
                    case "TbMergeItem":
                        CheckMergeItem(pair.Value, issues);
                        break;
                    case "TbGachaPool":
                        CheckGachaPool(pair.Value, issues);
                        break;
                    case "TbQuest":
                        CheckQuest(pair.Value, issues);
                        break;
                    case "TbMail":
                        CheckMail(pair.Value, issues);
                        break;
                    case "TbTower":
                        CheckTower(pair.Value, issues);
                        break;
                    case "TbBuilding":
                    case "TbTycoonBuilding":
                        CheckBuilding(pair.Key, pair.Value, issues);
                        break;
                    case "TbActivitySchedule":
                    CheckActivitySchedule(pair.Value, issues);
                    break;
                case "TbLimitedOffer":
                    CheckLimitedOffer(pair.Value, issues);
                    break;
                case "TbExperiment":
                    CheckExperiment(pair.Value, issues);
                    break;
                case "TbIdleStage":
                        CheckIdleStage(pair.Value, issues);
                        break;
                    default:
                        // 未知表：只做"主键列存在 + 主键唯一"这类通用检查（有 id 列才做）。
                        CheckGenericIdentity(pair.Key, pair.Value, issues);
                        break;
                }
            }

            return issues;
        }

        private static void CheckShopItem(TableData table, List<TableIssue> issues)
        {
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? priceId = GetInt(table, i, "priceItemId");
                int? priceCount = GetInt(table, i, "priceCount");
                int? itemId = GetInt(table, i, "itemId");

                if (priceCount.HasValue && priceCount.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "priceCount", "价格必须 > 0（0 元购会让商店变成无限刷）"));
                }

                if (priceId.HasValue && priceId.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "priceItemId", "货币道具 id 必须 > 0"));
                }

                if (itemId.HasValue && itemId.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "itemId", "商品道具 id 必须 > 0"));
                }
            }

            CheckUniqueIntColumn(table, "itemId", issues);
        }

        private static void CheckSignInReward(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "dayIndex", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? day = GetInt(table, i, "dayIndex");
                if (day.HasValue && day.Value < 1)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "dayIndex", "签到天序号必须从 1 开始（0/负数是配置笔误）"));
                }

                int? count = GetInt(table, i, "count");
                if (count.HasValue && count.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "count", "奖励数量必须 > 0（0 个的奖励只会让玩家以为没发）"));
                }
            }
        }

        private static void CheckAchievement(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "achievementId", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? metric = GetInt(table, i, "metric");
                int? target = GetInt(table, i, "target");

                if (metric.HasValue && (metric.Value < 0 || metric.Value > 4))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "metric", "metric 只定义了 0..4，越界值会让成就永远不解锁"));
                }

                if (target.HasValue && target.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "target", "目标值必须 > 0（0 目标会在事件到达前就完成）"));
                }
            }
        }

        private static void CheckSeasonPass(TableData table, List<TableIssue> issues)
        {
            int previousLevel = 0;
            int previousXp = -1;

            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? level = GetInt(table, i, "level");
                int? requiredXp = GetInt(table, i, "requiredXp");
                int? freeItemId = GetInt(table, i, "freeItemId");
                int? paidItemId = GetInt(table, i, "paidItemId");
                int? paidCount = GetInt(table, i, "paidCount");

                if (level.HasValue)
                {
                    if (level.Value != previousLevel + 1)
                    {
                        issues.Add(new TableIssue(table.Schema.TableName, i, "level",
                            "等级必须从 1 连续递增（当前 " + level.Value + "，期望 " + (previousLevel + 1) + "）"));
                    }

                    previousLevel = level.Value;
                }

                if (requiredXp.HasValue)
                {
                    if (requiredXp.Value <= previousXp)
                    {
                        issues.Add(new TableIssue(table.Schema.TableName, i, "requiredXp",
                            "累计经验必须严格递增（当前 " + requiredXp.Value + "，上一条 " + previousXp + "），否则升级判定会卡住"));
                    }

                    previousXp = requiredXp.Value;
                }

                if (freeItemId.HasValue && freeItemId.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "freeItemId", "免费轨奖励道具 id 必须 > 0"));
                }

                bool hasPaidItem = paidItemId.HasValue && paidItemId.Value > 0;
                bool hasPaidCount = paidCount.HasValue && paidCount.Value > 0;
                if (hasPaidItem != hasPaidCount)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "paidItemId",
                        "付费轨奖励必须\"道具 id 与数量\"成对出现（只填一半会让领取逻辑拿到半个奖励）"));
                }
            }
        }

        private static void CheckGuideStep(TableData table, List<TableIssue> issues)
        {
            int expected = 1;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? stepId = GetInt(table, i, "stepId");
                int? skippable = GetInt(table, i, "skippable");
                int? trigger = GetInt(table, i, "trigger");

                if (stepId.HasValue)
                {
                    if (stepId.Value != expected)
                    {
                        issues.Add(new TableIssue(table.Schema.TableName, i, "stepId",
                            "stepId 必须从 1 连续（当前 " + stepId.Value + "，期望 " + expected + "）"));
                    }

                    expected = stepId.Value + 1;
                }

                if (skippable.HasValue && skippable.Value != 0 && skippable.Value != 1)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "skippable", "skippable 只能是 0/1"));
                }

                if (trigger.HasValue && (trigger.Value < 0 || trigger.Value > 5))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "trigger", "trigger 只定义了 0..5"));
                }

                string? title = GetString(table, i, "title");
                if (title != null && title.Length == 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "title", "引导标题不能为空（空标题会让引导浮层变成一片空白）"));
                }
            }
        }

        /// <summary>
        /// 活动排期：时间窗口正序、reset 小时 0..23、轮换组非负。
        /// <para>
        /// <b>为什么窗口必须查</b>：start == end 时活动永远不开启（左闭右开），运营会以为"配了却看不到"；
        /// 反向窗口同样静默失效。这两类错误在 JSON 里完全不显眼，只能靠体检拦。
        /// </para>
        /// </summary>
        private static void CheckActivitySchedule(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "activityId", issues);

            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? start = GetInt(table, i, "startUnixSeconds");
                int? end = GetInt(table, i, "endUnixSeconds");
                if (start.HasValue && end.HasValue && end.Value <= start.Value)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "endUnixSeconds",
                        "结束时间必须晚于开始时间（相等时活动永远不开启）"));
                }

                CheckUnixSecond(table, i, "startUnixSeconds", start, issues);
                CheckUnixSecond(table, i, "endUnixSeconds", end, issues);

                int? resetHour = GetInt(table, i, "dailyResetHourUtc");
                if (resetHour.HasValue && (resetHour.Value < 0 || resetHour.Value > 23))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "dailyResetHourUtc",
                        "每日重置小时必须落在 0..23"));
                }
            }
        }

        /// <summary>
        /// 限时礼包：时间窗口正序、折扣 1..100、限购非负、售价为正。
        /// <para>
        /// 折扣 0 会让 finalPrice 恒为 1（玩家花 1 金币买光），>100 则变成加价；
        /// 两类都应该在导表阶段红灯，而不是上线后靠客服退款。
        /// </para>
        /// </summary>
        private static void CheckLimitedOffer(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "offerId", issues);

            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? discount = GetInt(table, i, "discountPercent");
                if (discount.HasValue && (discount.Value < 1 || discount.Value > 100))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "discountPercent",
                        "折扣必须在 1..100（0 会让售价恒为 1，>100 变成加价）"));
                }

                int? original = GetInt(table, i, "originalPrice");
                if (original.HasValue && original.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "originalPrice", "原价必须为正"));
                }

                int? grant = GetInt(table, i, "grantCount");
                if (grant.HasValue && grant.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "grantCount", "发放数量必须为正"));
                }

                int? daily = GetInt(table, i, "dailyLimit");
                int? total = GetInt(table, i, "totalLimit");
                if (daily.HasValue && daily.Value < 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "dailyLimit", "每日限购不能为负"));
                }

                if (total.HasValue && total.Value < 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "totalLimit", "总限购不能为负"));
                }

                int? start = GetInt(table, i, "startUnixSeconds");
                int? end = GetInt(table, i, "endUnixSeconds");
                if (start.HasValue && end.HasValue && end.Value <= start.Value)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "endUnixSeconds",
                        "结束时间必须晚于开始时间（相等时礼包永远不可购）"));
                }

                // 折后价 floor 后为 0：客户端会夹到 1，等于白送——这是最容易上线后才发现的一类配置事故。
                if (original.HasValue && discount.HasValue && (long)original.Value * discount.Value < 100L)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "discountPercent",
                        "折后价向下取整后为 0（客户端会夹到 1，等于白送）"));
                }

                // 日限 > 总限：日限购形同虚设（总限先耗尽）。
                if (daily.HasValue && total.HasValue && total.Value > 0 && daily.Value > total.Value)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "dailyLimit",
                        "每日限购大于总限购（总限会先耗尽，日限设置失去意义）"));
                }

                // int32 Unix 秒上限：2038-01-19 之后溢出为负，窗口会静默失效。
                CheckUnixSecond(table, i, "startUnixSeconds", start, issues);
                CheckUnixSecond(table, i, "endUnixSeconds", end, issues);
            }
        }
        /// <summary>
        /// A/B 实验：主键唯一、key 唯一非空、流量 0..100、enabled 只能 0/1。
        /// <para>
        /// <b>为什么这些必须拦</b>：trafficPercent 越界会让分桶静默退化（比如 150 变成"全 B"、
        /// -1 变成"全 A"），enabled 写成 2 会被客户端当"启用"而服务端当"禁用"，两边分组直接分叉。
        /// </para>
        /// </summary>
        private static void CheckExperiment(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "experimentId", issues);
            CheckUniqueStringColumn(table, "key", issues);

            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? traffic = GetInt(table, i, "trafficPercent");
                if (traffic.HasValue && (traffic.Value < 0 || traffic.Value > 100))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "trafficPercent",
                        "B 组流量必须在 0..100（越界会让分桶静默退化）"));
                }

                int? enabled = GetInt(table, i, "enabled");
                if (enabled.HasValue && enabled.Value != 0 && enabled.Value != 1)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "enabled", "enabled 只能是 0/1"));
                }

                int? variantA = GetInt(table, i, "variantA");
                int? variantB = GetInt(table, i, "variantB");
                if (variantA.HasValue && variantB.HasValue && variantA.Value == variantB.Value)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "variantB",
                        "variantA 与 variantB 相同（实验无差异，确认是否有意为之）"));
                }

                string? key = GetString(table, i, "key");
                if (key != null && key.Length == 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "key", "key 不能为空（分桶哈希依赖它）"));
                }
            }
        }
        /// <summary>int32 Unix 秒必须为正且在 2038 年前（溢出为负会让时间窗口静默失效）。</summary>
        private static void CheckUnixSecond(TableData table, int rowIndex, string columnName, int? value, List<TableIssue> issues)
        {
            if (value.HasValue && value.Value <= 0)
            {
                issues.Add(new TableIssue(table.Schema.TableName, rowIndex, columnName,
                    "Unix 秒必须为正（int32 溢出/未配置都会让时间窗口静默失效）"));
            }
        }
        /// <summary>字符串列唯一性（空值跳过，空值由各表自己的规则判）。</summary>
        private static void CheckUniqueStringColumn(TableData table, string columnName, List<TableIssue> issues)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                string? value = GetString(table, i, columnName);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (!seen.Add(value!))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, columnName, "值重复：" + value));
                }
            }
        }
        private static void CheckLocalization(TableData table, List<TableIssue> issues)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                string? key = GetString(table, i, "key");
                if (key == null)
                {
                    continue;
                }

                if (key.Length == 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "key", "本地化 key 不能为空"));
                    continue;
                }

                if (!seen.Add(key))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "key", "本地化 key 重复：" + key + "（后一条会覆盖前一条，翻译会丢）"));
                }
            }
        }

        private static void CheckCard(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "cardId", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? cost = GetInt(table, i, "cost");
                if (cost.HasValue && (cost.Value < 0 || cost.Value > 10))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "cost", "费用应在 0..10（越界多半是填错列）"));
                }
            }
        }

        private static void CheckMergeItem(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "itemId", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? level = GetInt(table, i, "level");
                if (level.HasValue && level.Value < 1)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "level", "合成等级必须 ≥1"));
                }
            }
        }

        private static void CheckGachaPool(TableData table, List<TableIssue> issues)
        {
            int total = 0;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? weight = GetInt(table, i, "weight");
                if (weight.HasValue)
                {
                    if (weight.Value <= 0)
                    {
                        issues.Add(new TableIssue(table.Schema.TableName, i, "weight", "抽卡权重必须 > 0（0 权重行等于永不产出）"));
                    }

                    total += weight.Value;
                }
            }

            if (table.Rows.Count > 0 && total == 0)
            {
                issues.Add(new TableIssue(table.Schema.TableName, -1, "weight", "整表权重和为 0，抽卡会无法出结果"));
            }
        }

        private static void CheckQuest(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "questId", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? target = GetInt(table, i, "target");
                if (target.HasValue && target.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "target", "任务目标值必须 > 0"));
                }
            }
        }

        private static void CheckMail(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "mailId", issues);
        }

        private static void CheckTower(TableData table, List<TableIssue> issues)
        {
            CheckUniqueIntColumn(table, "towerId", issues);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? cost = GetInt(table, i, "cost");
                if (cost.HasValue && cost.Value <= 0)
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, "cost", "塔价必须 > 0"));
                }
            }
        }

        private static void CheckBuilding(string tableName, TableData table, List<TableIssue> issues)
        {
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? cost = GetInt(table, i, "cost");
                int? income = GetInt(table, i, "incomePerMinute");
                if (cost.HasValue && cost.Value <= 0)
                {
                    issues.Add(new TableIssue(tableName, i, "cost", "建造成本必须 > 0"));
                }

                if (income.HasValue && income.Value < 0)
                {
                    issues.Add(new TableIssue(tableName, i, "incomePerMinute", "收益不能为负（负收益会让玩家越玩越穷）"));
                }
            }
        }

        private static void CheckIdleStage(TableData table, List<TableIssue> issues)
        {
            int previousStage = 0;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int? stage = GetInt(table, i, "stageId");
                if (stage.HasValue)
                {
                    if (stage.Value <= previousStage)
                    {
                        issues.Add(new TableIssue(table.Schema.TableName, i, "stageId", "关卡 id 必须严格递增"));
                    }

                    previousStage = stage.Value;
                }
            }
        }

        /// <summary>通用兜底：有 id 类列时只查唯一性，没有就跳过。</summary>
        private static void CheckGenericIdentity(string tableName, TableData table, List<TableIssue> issues)
        {
            for (int c = 0; c < table.Schema.Columns.Count; c++)
            {
                string columnName = table.Schema.Columns[c].Name;
                if (string.Equals(columnName, "id", StringComparison.Ordinal)
                    || columnName.EndsWith("Id", StringComparison.Ordinal))
                {
                    CheckUniqueIntColumn(table, columnName, issues);
                    return;
                }
            }
        }

        private static void CheckUniqueIntColumn(TableData table, string columnName, List<TableIssue> issues)
        {
            int index = FindColumn(table, columnName);
            if (index < 0)
            {
                return;
            }

            var seen = new HashSet<int>();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                JsonElement element = table.Rows[i][index];
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
                {
                    continue;
                }

                if (!seen.Add(value))
                {
                    issues.Add(new TableIssue(table.Schema.TableName, i, columnName, "主键重复：" + value + "（后一行会覆盖前一行）"));
                }
            }
        }

        private static int FindColumn(TableData table, string columnName)
        {
            for (int c = 0; c < table.Schema.Columns.Count; c++)
            {
                if (string.Equals(table.Schema.Columns[c].Name, columnName, StringComparison.Ordinal))
                {
                    return c;
                }
            }

            return -1;
        }

        private static int? GetInt(TableData table, int rowIndex, string columnName)
        {
            int index = FindColumn(table, columnName);
            if (index < 0)
            {
                return null;
            }

            JsonElement element = table.Rows[rowIndex][index];
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            {
                return null;
            }

            return value;
        }

        private static string? GetString(TableData table, int rowIndex, string columnName)
        {
            int index = FindColumn(table, columnName);
            if (index < 0)
            {
                return null;
            }

            JsonElement element = table.Rows[rowIndex][index];
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
    }
}

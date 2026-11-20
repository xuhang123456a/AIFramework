using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFramework.TableGen;

namespace AIFramework.OpCheck
{
    /// <summary>一条运营配置问题：级别 + 涉及的活动/礼包 + 说明。</summary>
    public readonly struct OpsIssue
    {
        public OpsIssue(string scope, string subject, string message, bool isError)
        {
            Scope = scope;
            Subject = subject;
            Message = message;
            IsError = isError;
        }

        /// <summary>问题类别（"activity"/"offer" 等）。</summary>
        public string Scope { get; }

        /// <summary>涉及对象（id 或名字）。</summary>
        public string Subject { get; }

        /// <summary>说明。</summary>
        public string Message { get; }

        /// <summary>true = 必须改（运营事故级）；false = 提示。</summary>
        public bool IsError { get; }

        public override string ToString()
        {
            return (IsError ? "✗ " : "⚠ ") + "[" + Scope + "] " + Subject + "：" + Message;
        }
    }

    /// <summary>
    /// 运营排期体检（跨表/跨活动）：单表规则在 <c>tablecheck</c> 里，这里只查"表和表、活动和活动之间"的冲突。
    /// <para>
    /// <b>为什么单表规则不够</b>：tablecheck 每张表独立看，能发现"结束早于开始"，但发现不了
    /// "两个同组活动同时开着（互斥组失效）"、"同一道具两个礼包同时卖（玩家看到两个价格）"。
    /// 这类问题只有把表放在同一时间轴上才暴露，而它们恰恰是运营最常出的错。
    /// </para>
    /// </summary>
    public static class OpsRules
    {
        /// <summary>同一时刻允许同时开启的活动数上限（超过多半是排期失误）。</summary>
        public const int MaxConcurrentActivities = 3;

        /// <summary>检查活动排期冲突：同轮换组窗口重叠 = 错误；同时开启数超上限 = 提示。</summary>
        /// <param name="activities">活动行（已解析）。</param>
        /// <param name="nowUnixSeconds">用于"当前是否有活动"的观测时刻。</param>
        /// <param name="issues">输出列表。</param>
        public static void CheckActivities(IReadOnlyList<ActivityRow> activities, long nowUnixSeconds, List<OpsIssue> issues)
        {
            if (activities == null)
            {
                throw new ArgumentNullException(nameof(activities));
            }

            if (issues == null)
            {
                throw new ArgumentNullException(nameof(issues));
            }

            for (int i = 0; i < activities.Count; i++)
            {
                for (int j = i + 1; j < activities.Count; j++)
                {
                    ActivityRow left = activities[i];
                    ActivityRow right = activities[j];

                    if (left.RotationGroup != right.RotationGroup)
                    {
                        continue;
                    }

                    if (!Overlaps(left.Start, left.End, right.Start, right.End))
                    {
                        continue;
                    }

                    // 同组重叠本身是设计允许的（按 priority 裁决谁在开，用于"常驻 + 限时"叠加场景）；
                    // 只有"同优先级还重叠"才是运营事故——这时没有任何规则能裁决，两端实现可能各选一个。
                    bool samePriority = left.Priority == right.Priority;
                    issues.Add(new OpsIssue(
                        "activity",
                        left.Name + " ↔ " + right.Name,
                        samePriority
                            ? "同轮换组（group=" + left.RotationGroup + "）且同优先级（priority=" + left.Priority
                                + "）窗口重叠：无法裁决谁在开，运营必须错开或改优先级"
                            : "同轮换组（group=" + left.RotationGroup + "）窗口重叠，按 priority 裁决（"
                                + left.Name + "=" + left.Priority + "，" + right.Name + "=" + right.Priority + "）",
                        isError: samePriority));
                }
            }

            int concurrent = 0;
            string firstOpen = string.Empty;
            for (int i = 0; i < activities.Count; i++)
            {
                if (nowUnixSeconds >= activities[i].Start && nowUnixSeconds < activities[i].End)
                {
                    concurrent++;
                    if (firstOpen.Length == 0)
                    {
                        firstOpen = activities[i].Name;
                    }
                }
            }

            if (concurrent == 0)
            {
                issues.Add(new OpsIssue("activity", "(当前时刻)", "没有任何活动在开：玩家进游戏看不到活动入口",
                    isError: false));
            }
            else if (concurrent > MaxConcurrentActivities)
            {
                issues.Add(new OpsIssue("activity", "(当前时刻)",
                    "同时开启 " + concurrent + " 个活动（上限 " + MaxConcurrentActivities + "），入口/红点会挤爆",
                    isError: false));
            }
        }

        /// <summary>检查礼包冲突：同一道具的两个礼包窗口重叠 = 错误（同一时刻两个价格）。</summary>
        /// <param name="offers">礼包行。</param>
        /// <param name="issues">输出列表。</param>
        public static void CheckOffers(IReadOnlyList<OfferRow> offers, List<OpsIssue> issues)
        {
            if (offers == null)
            {
                throw new ArgumentNullException(nameof(offers));
            }

            if (issues == null)
            {
                throw new ArgumentNullException(nameof(issues));
            }

            for (int i = 0; i < offers.Count; i++)
            {
                for (int j = i + 1; j < offers.Count; j++)
                {
                    OfferRow left = offers[i];
                    OfferRow right = offers[j];

                    if (left.ItemId != right.ItemId)
                    {
                        continue;
                    }

                    if (Overlaps(left.Start, left.End, right.Start, right.End))
                    {
                        issues.Add(new OpsIssue(
                            "offer",
                            left.Name + " ↔ " + right.Name,
                            "同一道具（itemId=" + left.ItemId + "）两个礼包窗口重叠：玩家会同时看到两个价格，客服口径无法统一",
                            isError: true));
                    }
                }
            }
        }

        /// <summary>左闭右开区间相交判定。</summary>
        public static bool Overlaps(long startA, long endA, long startB, long endB)
        {
            return startA < endB && startB < endA;
        }
    }

    /// <summary>活动表的一行（opcheck 只需时间轴、分组与优先级）。</summary>
    public readonly struct ActivityRow
    {
        public ActivityRow(int activityId, string name, long start, long end, int rotationGroup, int priority)
        {
            ActivityId = activityId;
            Name = name ?? string.Empty;
            Start = start;
            End = end;
            RotationGroup = rotationGroup;
            Priority = priority;
        }

        public int ActivityId { get; }

        public string Name { get; }

        public long Start { get; }

        public long End { get; }

        public int RotationGroup { get; }

        /// <summary>同组重叠时用来裁决谁在开的优先级（大者优先）。</summary>
        public int Priority { get; }
    }

    /// <summary>礼包表的一行。</summary>
    public readonly struct OfferRow
    {
        public OfferRow(int offerId, string name, int itemId, long start, long end)
        {
            OfferId = offerId;
            Name = name ?? string.Empty;
            ItemId = itemId;
            Start = start;
            End = end;
        }

        public int OfferId { get; }

        public string Name { get; }

        public int ItemId { get; }

        public long Start { get; }

        public long End { get; }
    }

    /// <summary>从 JSON 表数据读行（列名固定，与 tables/*.json 一致）。</summary>
    public static class OpsTableReader
    {
        /// <summary>读取活动表。</summary>
        public static List<ActivityRow> ReadActivities(TableData table)
        {
            int idColumn = FindColumn(table, "activityId");
            int nameColumn = FindColumn(table, "name");
            int startColumn = FindColumn(table, "startUnixSeconds");
            int endColumn = FindColumn(table, "endUnixSeconds");
            int groupColumn = FindColumn(table, "rotationGroup");
            int priorityColumn = FindColumn(table, "priority");
            RequireColumns(table, idColumn, nameColumn, startColumn, endColumn, groupColumn, priorityColumn);

            var rows = new List<ActivityRow>(table.Rows.Count);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                rows.Add(new ActivityRow(
                    ReadInt(table.Rows[i][idColumn]),
                    ReadString(table.Rows[i][nameColumn]),
                    ReadLong(table.Rows[i][startColumn]),
                    ReadLong(table.Rows[i][endColumn]),
                    ReadInt(table.Rows[i][groupColumn]),
                    ReadInt(table.Rows[i][priorityColumn])));
            }

            return rows;
        }

        /// <summary>读取礼包表。</summary>
        public static List<OfferRow> ReadOffers(TableData table)
        {
            int idColumn = FindColumn(table, "offerId");
            int nameColumn = FindColumn(table, "name");
            int itemColumn = FindColumn(table, "itemId");
            int startColumn = FindColumn(table, "startUnixSeconds");
            int endColumn = FindColumn(table, "endUnixSeconds");
            RequireColumns(table, idColumn, nameColumn, itemColumn, startColumn, endColumn);

            var rows = new List<OfferRow>(table.Rows.Count);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                rows.Add(new OfferRow(
                    ReadInt(table.Rows[i][idColumn]),
                    ReadString(table.Rows[i][nameColumn]),
                    ReadInt(table.Rows[i][itemColumn]),
                    ReadLong(table.Rows[i][startColumn]),
                    ReadLong(table.Rows[i][endColumn])));
            }

            return rows;
        }

        private static void RequireColumns(TableData table, params int[] columns)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] < 0)
                {
                    throw new TableGenException("表 " + table.Schema.TableName + " 缺少 opcheck 需要的列（列序或列名被改了？）。");
                }
            }
        }

        private static int FindColumn(TableData table, string name)
        {
            for (int i = 0; i < table.Schema.Columns.Count; i++)
            {
                if (string.Equals(table.Schema.Columns[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ReadInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value) ? value : 0;
        }

        private static long ReadLong(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value) ? value : 0L;
        }

        private static string ReadString(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        }
    }
}

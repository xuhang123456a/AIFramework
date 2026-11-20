#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// Console 的假排行榜服务端：内存表 + 分数降序 / 同分 playerId 升序 + 两季切换。
    /// <para>
    /// <b>为什么把假服务端放适配层</b>：排序与分页是服务端职责，Core 的 <c>LeaderboardPager</c> 只做参数归一化；
    /// 把假服务端放进测试或适配层，能保证 pager 不被"本地实现"绑架（真实服务端换实现，pager 零改动）。
    /// </para>
    /// </summary>
    public sealed class ConsoleLeaderboardServer : ILeaderboardServer
    {
        private readonly Dictionary<int, List<LeaderboardRow>> _current = new Dictionary<int, List<LeaderboardRow>>();
        private readonly Dictionary<int, List<LeaderboardRow>> _previous = new Dictionary<int, List<LeaderboardRow>>();
        private LeaderboardSeason _season = LeaderboardSeason.Current;

        /// <summary>是否可用（false 时全部请求返回 unavailable）。</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>当前赛季。</summary>
        public LeaderboardSeason CurrentSeason
        {
            get { return _season; }
        }

        /// <summary>提交分数：只接受更高分（同分不覆盖）。</summary>
        public bool TrySubmit(int leaderboardId, long playerId, int score)
        {
            if (!IsAvailable || leaderboardId <= 0 || score < 0)
            {
                return false;
            }

            List<LeaderboardRow> rows = GetRows(_current, leaderboardId);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].PlayerId == playerId)
                {
                    if (score <= rows[i].Score)
                    {
                        return false;
                    }

                    rows[i] = new LeaderboardRow(playerId, rows[i].DisplayName, score, 0, 1764000000L);
                    Sort(rows);
                    return true;
                }
            }

            rows.Add(new LeaderboardRow(playerId, "玩家" + playerId, score, 0, 1764000000L));
            Sort(rows);
            return true;
        }

        /// <summary>拉一页：offset/limit 由 pager 归一化，这里只做切片与 selfRow 定位。</summary>
        public LeaderboardPage FetchPage(in LeaderboardPageRequest request)
        {
            if (!IsAvailable)
            {
                return new LeaderboardPage(request.LeaderboardId, request.Season, 0, 0, 0,
                    Array.Empty<LeaderboardRow>(), null, "unavailable");
            }

            Dictionary<int, List<LeaderboardRow>> source = request.Season == LeaderboardSeason.Previous ? _previous : _current;
            if (!source.TryGetValue(request.LeaderboardId, out List<LeaderboardRow>? rows) || rows == null)
            {
                return new LeaderboardPage(request.LeaderboardId, request.Season, 0, request.Offset, request.Limit,
                    Array.Empty<LeaderboardRow>(), null, string.Empty);
            }

            Sort(rows);

            int total = rows.Count;
            int start = request.Offset < 0 ? 0 : request.Offset;
            int count = request.Limit < 0 ? 0 : request.Limit;
            if (start > total)
            {
                start = total;
            }

            if (start + count > total)
            {
                count = total - start;
            }

            var page = new LeaderboardRow[count];
            for (int i = 0; i < count; i++)
            {
                LeaderboardRow row = rows[start + i];
                page[i] = new LeaderboardRow(row.PlayerId, row.DisplayName, row.Score, start + i + 1, row.UpdatedAtUnixSeconds);
            }

            LeaderboardRow? selfRow = null;
            for (int i = 0; i < total; i++)
            {
                if (rows[i].PlayerId == request.PlayerId)
                {
                    selfRow = new LeaderboardRow(rows[i].PlayerId, rows[i].DisplayName, rows[i].Score, i + 1, rows[i].UpdatedAtUnixSeconds);
                    break;
                }
            }

            // Limit 回显"应用的上限"而不是实际条数：调用方用 page.Offset + page.Limit 翻页，
            // 若末页回显实际条数，下一次 offset 会错位（静默跳行/重复行）。
            return new LeaderboardPage(request.LeaderboardId, request.Season, total, start, request.Limit, page, selfRow, string.Empty);
        }

        /// <summary>赛季切换：当前季打包到上一季，当前季清空。</summary>
        public void RotateSeason()
        {
            _previous.Clear();
            foreach (KeyValuePair<int, List<LeaderboardRow>> pair in _current)
            {
                _previous[pair.Key] = new List<LeaderboardRow>(pair.Value);
            }

            _current.Clear();

            // 契约口径：提交永远进 Current 桶、CurrentSeason 恒为 Current（上一季只读）。
            // 之前这里把 _season 翻成 Previous 会让"按 CurrentSeason 取赛季"的客户端切季后读到归档榜。
            _season = LeaderboardSeason.Current;
        }

        private static List<LeaderboardRow> GetRows(Dictionary<int, List<LeaderboardRow>> table, int leaderboardId)
        {
            if (!table.TryGetValue(leaderboardId, out List<LeaderboardRow>? rows) || rows == null)
            {
                rows = new List<LeaderboardRow>();
                table[leaderboardId] = rows;
            }

            return rows;
        }

        private static void Sort(List<LeaderboardRow> rows)
        {
            // 同分规则复用 pager 的唯一实现（分数降序、同分 playerId 升序），避免两处排序慢慢漂移。
            rows.Sort((left, right) => LeaderboardPager.CompareRows(left, right));
        }
    }
}

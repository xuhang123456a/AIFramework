using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 本地排行榜台账：定长容量、按 (score desc, submittedAt asc, playerId asc) 排序，
    /// 同分先到先排——**排序必须完全确定**，否则"同分但两次查询顺序不同"会被当成玄学 bug。
    /// <para>
    /// 容量固定：超出时保留前 N（即淘汰末尾）。写满后才做一次插入排序的移动，
    /// 提交路径是 O(N) 数组移动，不做 List/排序委托分配。
    /// </para>
    /// </summary>
    public sealed class LeaderboardTable
    {
        /// <summary>默认容量：256 名（本地假榜足够；真实服务端不占客户端内存）。</summary>
        public const int DefaultCapacity = 256;

        private readonly LeaderboardEntry[] _entries;
        private int _count;

        public LeaderboardTable(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须为正。");
            }

            _entries = new LeaderboardEntry[capacity];
        }

        public int Capacity
        {
            get { return _entries.Length; }
        }

        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// 提交一条记录：同玩家已有更高/同分记录时忽略；新高分则覆盖并重排。
        /// 返回是否真的改变了榜单。
        /// </summary>
        public bool Submit(in LeaderboardEntry entry)
        {
            if (entry.PlayerId <= 0)
            {
                return false;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].PlayerId != entry.PlayerId)
                {
                    continue;
                }

                if (_entries[i].Score >= entry.Score)
                {
                    return false;
                }

                RemoveAt(i);
                break;
            }

            int insertAt = FindInsertIndex(in entry);

            // 比满榜的最后一名还低：直接丢弃（榜单满时才可能发生）。
            if (insertAt >= _entries.Length)
            {
                return false;
            }

            // 满榜时插入会覆盖最后一名（count 不变）；未满时整体右移一格并 count++。
            int limit = _count < _entries.Length ? _count : _entries.Length - 1;
            for (int i = limit; i > insertAt; i--)
            {
                _entries[i] = _entries[i - 1];
            }

            _entries[insertAt] = entry;
            if (_count < _entries.Length)
            {
                _count++;
            }

            return true;
        }

        /// <summary>按下标读第 i 名（0 = 第一）；越界返回 default。</summary>
        public LeaderboardEntry Get(int index)
        {
            if (index < 0 || index >= _count)
            {
                return default;
            }

            return _entries[index];
        }

        /// <summary>某玩家的名次（1 起）；未上榜返回 0。</summary>
        public int GetRank(int playerId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].PlayerId == playerId)
                {
                    return i + 1;
                }
            }

            return 0;
        }

        /// <summary>取前 count 名快照；count ≤ 0 返回空榜（但仍带上自己的名次与总人数）。</summary>
        public LeaderboardSnapshot QueryTop(int count, int playerId)
        {
            int take = count <= 0 ? 0 : (count < _count ? count : _count);
            var top = new LeaderboardEntry[take];
            for (int i = 0; i < take; i++)
            {
                top[i] = _entries[i];
            }

            return new LeaderboardSnapshot(top, GetRank(playerId), _count);
        }

        /// <summary>清空榜单（纯本地操作）。</summary>
        public void Clear()
        {
            Array.Clear(_entries, 0, _entries.Length);
            _count = 0;
        }

        private int FindInsertIndex(in LeaderboardEntry entry)
        {
            for (int i = 0; i < _count; i++)
            {
                if (ComesBefore(in entry, in _entries[i]))
                {
                    return i;
                }
            }

            return _count;
        }

        private static bool ComesBefore(in LeaderboardEntry left, in LeaderboardEntry right)
        {
            if (left.Score != right.Score)
            {
                return left.Score > right.Score;
            }

            if (left.SubmittedAtUnixSeconds != right.SubmittedAtUnixSeconds)
            {
                return left.SubmittedAtUnixSeconds < right.SubmittedAtUnixSeconds;
            }

            return left.PlayerId < right.PlayerId;
        }

        private void RemoveAt(int index)
        {
            for (int i = index; i < _count - 1; i++)
            {
                _entries[i] = _entries[i + 1];
            }

            _count--;
            _entries[_count] = default;
        }

    }
}

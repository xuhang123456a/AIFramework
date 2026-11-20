#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧排行榜实现：**不联网**，用 <see cref="LeaderboardTable"/> 做本地榜单，
    /// 可选把前 N 名落盘到 <see cref="IStoragePort"/>（便于演示与人工核对）。
    /// <para>
    /// 落地文本格式：每行 <c>playerId|score|submittedAt</c>；解析失败的行直接跳过并计数，
    /// 不让一条脏行毁掉整张榜（真实服务端数据也可能被手改）。
    /// </para>
    /// </summary>
    public sealed class UnityLeaderboardService : ILeaderboardService
    {
        private readonly LeaderboardTable _table;
        private readonly IStoragePort? _storage;
        private readonly string _slotKey;
        private readonly bool _available;
        private int _dirtyLines;

        public UnityLeaderboardService(
            IStoragePort? storage = null,
            string slotKey = "leaderboard/top.txt",
            int capacity = LeaderboardTable.DefaultCapacity,
            bool available = false)
        {
            _storage = storage;
            _slotKey = slotKey;
            _table = new LeaderboardTable(capacity);
            _available = available && storage != null;
            if (_available)
            {
                Load();
            }
        }

        public bool IsAvailable
        {
            get { return _available; }
        }

        public int Count
        {
            get { return _table.Count; }
        }

        /// <summary>加载时被跳过的脏行数（诊断用）。</summary>
        public int DirtyLines
        {
            get { return _dirtyLines; }
        }

        public bool Submit(in LeaderboardEntry entry)
        {
            if (!_available)
            {
                return false;
            }

            bool changed = _table.Submit(in entry);
            if (changed)
            {
                Save();
            }

            return changed;
        }

        public LeaderboardSnapshot QueryTop(int count, int playerId)
        {
            if (!_available)
            {
                return new LeaderboardSnapshot(Array.Empty<LeaderboardEntry>(), 0, 0);
            }

            return _table.QueryTop(count, playerId);
        }

        public bool Reset()
        {
            if (!_available)
            {
                return false;
            }

            _table.Clear();
            Save();
            return true;
        }

        private void Load()
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            byte[]? data = storage.Read(_slotKey);
            if (data == null || data.Length == 0)
            {
                return;
            }

            string text = System.Text.Encoding.UTF8.GetString(data);
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split('|');
                if (parts.Length != 3
                    || !int.TryParse(parts[0], out int playerId)
                    || !long.TryParse(parts[1], out long score)
                    || !long.TryParse(parts[2], out long submittedAt))
                {
                    _dirtyLines++;
                    continue;
                }

                _table.Submit(new LeaderboardEntry(playerId, score, submittedAt));
            }
        }

        private void Save()
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            var builder = new System.Text.StringBuilder(_table.Count * 24);
            for (int i = 0; i < _table.Count; i++)
            {
                LeaderboardEntry entry = _table.Get(i);
                builder.Append(entry.PlayerId);
                builder.Append('|');
                builder.Append(entry.Score);
                builder.Append('|');
                builder.Append(entry.SubmittedAtUnixSeconds);
                builder.Append('\n');
            }

            storage.Write(_slotKey, System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
        }
    }
}

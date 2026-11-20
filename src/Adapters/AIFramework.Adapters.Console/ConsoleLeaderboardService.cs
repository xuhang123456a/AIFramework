using System;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台排行榜实现：本地台账 + 假"已同步"标记。
    /// <para>
    /// 它存在的意义是让上层（成绩结算/活动奖励）在**没有服务端**时也能跑通整条链路，
    /// 同时把"同分怎么排"这种规则钉在可测的本地实现里，而不是等接服务端时才现想。
    /// </para>
    /// </summary>
    public sealed class ConsoleLeaderboardService : ILeaderboardService
    {
        private readonly LeaderboardTable _table;
        private readonly bool _available;

        public ConsoleLeaderboardService(int capacity = LeaderboardTable.DefaultCapacity, bool available = true)
        {
            _table = new LeaderboardTable(capacity);
            _available = available;
        }

        public bool IsAvailable
        {
            get { return _available; }
        }

        public int Count
        {
            get { return _table.Count; }
        }

        public bool Submit(in LeaderboardEntry entry)
        {
            if (!_available)
            {
                return false;
            }

            return _table.Submit(in entry);
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
            return true;
        }
    }
}

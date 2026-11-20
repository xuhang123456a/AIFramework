using System;

namespace AIFramework.Contracts.Services
{
    /// <summary>一条排行榜记录：玩家 + 分数 + 时间戳（秒）。</summary>
    public readonly struct LeaderboardEntry
    {
        public LeaderboardEntry(int playerId, long score, long submittedAtUnixSeconds)
        {
            PlayerId = playerId;
            Score = score;
            SubmittedAtUnixSeconds = submittedAtUnixSeconds;
        }

        public int PlayerId { get; }

        public long Score { get; }

        /// <summary>提交时间（Unix 秒）；排序时用于同分裁决（先到先排）。</summary>
        public long SubmittedAtUnixSeconds { get; }
    }

    /// <summary>
    /// 排行榜快照：Top N + 自己的名次。
    /// <para>
    /// <see cref="OwnRank"/> 从 1 开始；未上榜为 0（用 0 而不是 -1，避免调用方写成 <c>&gt;= 0</c> 误判）。
    /// </para>
    /// </summary>
    public readonly struct LeaderboardSnapshot
    {
        public LeaderboardSnapshot(LeaderboardEntry[] top, int ownRank, int totalEntries)
        {
            Top = top;
            OwnRank = ownRank;
            TotalEntries = totalEntries;
        }

        public LeaderboardEntry[] Top { get; }

        public int OwnRank { get; }

        public int TotalEntries { get; }
    }

    /// <summary>
    /// 排行榜端口：真实实现走服务端；Phase 1 由适配层用本地台账模拟。
    /// <para>
    /// <b>为什么用 long 而不是 int</b>：排行榜分数天然容易溢出（累计金币/战力），
    /// int 在长线运营里是定时炸弹；序列化时也统一 64 位。
    /// </para>
    /// </summary>
    public interface ILeaderboardService
    {
        /// <summary>提交分数；同玩家只保留最高分（新高分覆盖，低分忽略）。返回是否更新了记录。</summary>
        bool Submit(in LeaderboardEntry entry);

        /// <summary>取前 <paramref name="count"/> 名（count ≤ 0 视为空）；不到 count 条就返回实际条数。</summary>
        LeaderboardSnapshot QueryTop(int count, int playerId);

        /// <summary>清空（仅本地假实现支持；服务端实现应返回 false）。</summary>
        bool Reset();
    }
}

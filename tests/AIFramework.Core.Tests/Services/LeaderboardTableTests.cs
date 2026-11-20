using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 排行榜台账的排序与去重契约。重点不是"能排序"，而是**同分裁决必须完全确定**：
    /// 同分按提交时间先到先排，再按玩家 id，任何人重算都得到同一结果。
    /// </summary>
    public sealed class LeaderboardTableTests
    {
        [Fact]
        public void Submit_FirstEntry_IsRankedFirst()
        {
            var table = new LeaderboardTable(8);

            Assert.True(table.Submit(new LeaderboardEntry(1, 100, 10)));
            Assert.Equal(1, table.Count);
            Assert.Equal(1, table.GetRank(1));
            Assert.Equal(100, table.Get(0).Score);
        }

        [Fact]
        public void Submit_HigherScore_RanksAbove()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 100, 10));
            table.Submit(new LeaderboardEntry(2, 300, 20));

            Assert.Equal(1, table.GetRank(2));
            Assert.Equal(2, table.GetRank(1));
            Assert.Equal(300, table.Get(0).Score);
        }

        [Fact]
        public void Submit_SamePlayerLowerScore_IsIgnored()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 300, 10));

            Assert.False(table.Submit(new LeaderboardEntry(1, 100, 20)));
            Assert.Equal(1, table.Count);
            Assert.Equal(300, table.Get(0).Score);
        }

        [Fact]
        public void Submit_SamePlayerSameScore_IsIgnoredButKeepsOriginalTime()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 300, 10));

            Assert.False(table.Submit(new LeaderboardEntry(1, 300, 99)));
            Assert.Equal(10, table.Get(0).SubmittedAtUnixSeconds);
        }

        [Fact]
        public void Submit_SamePlayerHigherScore_ReplacesAndReranks()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 100, 10));
            table.Submit(new LeaderboardEntry(2, 200, 10));

            Assert.True(table.Submit(new LeaderboardEntry(1, 500, 30)));

            Assert.Equal(2, table.Count);
            Assert.Equal(1, table.GetRank(1));
            Assert.Equal(500, table.Get(0).Score);
            Assert.Equal(2, table.GetRank(2));
        }

        [Fact]
        public void TieBreak_EarlierSubmissionRanksFirst()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(2, 100, 20));
            table.Submit(new LeaderboardEntry(1, 100, 10));

            Assert.Equal(1, table.Get(0).PlayerId);
            Assert.Equal(2, table.Get(1).PlayerId);
        }

        [Fact]
        public void TieBreak_SameTimeSmallerPlayerIdRanksFirst()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(9, 100, 50));
            table.Submit(new LeaderboardEntry(3, 100, 50));

            Assert.Equal(3, table.Get(0).PlayerId);
            Assert.Equal(9, table.Get(1).PlayerId);
        }

        [Fact]
        public void Submit_BeyondCapacity_DropsLowest()
        {
            var table = new LeaderboardTable(3);
            table.Submit(new LeaderboardEntry(1, 10, 1));
            table.Submit(new LeaderboardEntry(2, 20, 1));
            table.Submit(new LeaderboardEntry(3, 30, 1));

            Assert.False(table.Submit(new LeaderboardEntry(4, 5, 1)));

            Assert.Equal(3, table.Count);
            Assert.Equal(0, table.GetRank(4));
            Assert.Equal(1, table.GetRank(3));
        }

        [Fact]
        public void Submit_BeyondCapacityButHighEnough_EvictsLast()
        {
            var table = new LeaderboardTable(3);
            table.Submit(new LeaderboardEntry(1, 10, 1));
            table.Submit(new LeaderboardEntry(2, 20, 1));
            table.Submit(new LeaderboardEntry(3, 30, 1));

            Assert.True(table.Submit(new LeaderboardEntry(4, 25, 1)));

            Assert.Equal(3, table.Count);
            Assert.Equal(0, table.GetRank(1));
            Assert.Equal(1, table.GetRank(3));
            Assert.Equal(2, table.GetRank(4));
            Assert.Equal(3, table.GetRank(2));
        }

        [Fact]
        public void Submit_InvalidPlayerId_IsRejected()
        {
            var table = new LeaderboardTable(4);

            Assert.False(table.Submit(new LeaderboardEntry(0, 100, 1)));
            Assert.False(table.Submit(new LeaderboardEntry(-5, 100, 1)));
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void QueryTop_ReturnsRequestedCountWithOwnRank()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 100, 1));
            table.Submit(new LeaderboardEntry(2, 200, 1));
            table.Submit(new LeaderboardEntry(3, 300, 1));

            LeaderboardSnapshot snapshot = table.QueryTop(2, playerId: 1);

            Assert.Equal(2, snapshot.Top.Length);
            Assert.Equal(3, snapshot.Top[0].PlayerId);
            Assert.Equal(3, snapshot.OwnRank);
            Assert.Equal(3, snapshot.TotalEntries);
        }

        [Fact]
        public void QueryTop_NonPositiveCount_ReturnsEmptyButKeepsRank()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(7, 500, 1));

            LeaderboardSnapshot snapshot = table.QueryTop(0, playerId: 7);

            Assert.Empty(snapshot.Top);
            Assert.Equal(1, snapshot.OwnRank);
            Assert.Equal(1, snapshot.TotalEntries);
        }

        [Fact]
        public void QueryTop_PlayerNotRanked_OwnRankIsZero()
        {
            var table = new LeaderboardTable(8);
            table.Submit(new LeaderboardEntry(1, 100, 1));

            Assert.Equal(0, table.QueryTop(10, playerId: 2).OwnRank);
        }

        [Fact]
        public void Get_OutOfRange_ReturnsDefault()
        {
            var table = new LeaderboardTable(4);

            Assert.Equal(0, table.Get(-1).PlayerId);
            Assert.Equal(0, table.Get(0).PlayerId);
        }

        [Fact]
        public void Clear_EmptiesTable()
        {
            var table = new LeaderboardTable(4);
            table.Submit(new LeaderboardEntry(1, 100, 1));

            table.Clear();

            Assert.Equal(0, table.Count);
            Assert.Equal(0, table.GetRank(1));
        }

        [Fact]
        public void Constructor_InvalidCapacity_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new LeaderboardTable(0));
        }

        [Fact]
        public void Submit_IsDeterministicForSameInputSequence()
        {
            var first = new LeaderboardTable(8);
            var second = new LeaderboardTable(8);
            var entries = new[]
            {
                new LeaderboardEntry(3, 100, 30),
                new LeaderboardEntry(1, 100, 10),
                new LeaderboardEntry(2, 200, 20),
                new LeaderboardEntry(5, 100, 10),
            };

            for (int i = 0; i < entries.Length; i++)
            {
                first.Submit(in entries[i]);
                second.Submit(in entries[i]);
            }

            for (int i = 0; i < first.Count; i++)
            {
                Assert.Equal(first.Get(i).PlayerId, second.Get(i).PlayerId);
                Assert.Equal(first.Get(i).Score, second.Get(i).Score);
            }
        }
    }
}

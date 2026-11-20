using AIFramework.Adapters.Console;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>控制台排行榜：可用性、提交去重、Top 查询、Reset 的真实行为。</summary>
    public sealed class ConsoleLeaderboardServiceTests
    {
        [Fact]
        public void IsAvailable_DefaultTrue()
        {
            Assert.True(new ConsoleLeaderboardService().IsAvailable);
        }

        [Fact]
        public void Unavailable_SubmitAndQueryAreNoOps()
        {
            var service = new ConsoleLeaderboardService(available: false);

            Assert.False(service.Submit(new LeaderboardEntry(1, 100, 1)));
            LeaderboardSnapshot snapshot = service.QueryTop(10, 1);
            Assert.Empty(snapshot.Top);
            Assert.Equal(0, snapshot.TotalEntries);
            Assert.False(service.Reset());
        }

        [Fact]
        public void Submit_TracksCountAndRank()
        {
            var service = new ConsoleLeaderboardService();
            service.Submit(new LeaderboardEntry(1, 100, 1));
            service.Submit(new LeaderboardEntry(2, 300, 2));

            Assert.Equal(2, service.Count);
            Assert.Equal(2, service.QueryTop(10, 1).OwnRank);
        }

        [Fact]
        public void Submit_LowerScoreForSamePlayer_ReturnsFalse()
        {
            var service = new ConsoleLeaderboardService();
            service.Submit(new LeaderboardEntry(1, 300, 1));

            Assert.False(service.Submit(new LeaderboardEntry(1, 100, 2)));
            Assert.Equal(300, service.QueryTop(1, 1).Top[0].Score);
        }

        [Fact]
        public void QueryTop_RespectsCountAndOrder()
        {
            var service = new ConsoleLeaderboardService();
            service.Submit(new LeaderboardEntry(1, 100, 1));
            service.Submit(new LeaderboardEntry(2, 400, 1));
            service.Submit(new LeaderboardEntry(3, 250, 1));

            LeaderboardSnapshot snapshot = service.QueryTop(2, 1);

            Assert.Equal(2, snapshot.Top.Length);
            Assert.Equal(2, snapshot.Top[0].PlayerId);
            Assert.Equal(3, snapshot.Top[1].PlayerId);
            Assert.Equal(3, snapshot.OwnRank);
        }

        [Fact]
        public void Reset_ClearsEntries()
        {
            var service = new ConsoleLeaderboardService();
            service.Submit(new LeaderboardEntry(1, 100, 1));

            Assert.True(service.Reset());

            Assert.Equal(0, service.Count);
            Assert.Equal(0, service.QueryTop(10, 1).OwnRank);
        }

        [Fact]
        public void CapacityLimit_DropsLowestScore()
        {
            var service = new ConsoleLeaderboardService(capacity: 2);
            service.Submit(new LeaderboardEntry(1, 100, 1));
            service.Submit(new LeaderboardEntry(2, 200, 1));

            Assert.False(service.Submit(new LeaderboardEntry(3, 50, 1)));
            Assert.Equal(2, service.Count);
        }

        [Fact]
        public void SameScores_TieBreakByTimeThenPlayerId()
        {
            var service = new ConsoleLeaderboardService();
            service.Submit(new LeaderboardEntry(2, 100, 20));
            service.Submit(new LeaderboardEntry(1, 100, 10));

            LeaderboardSnapshot snapshot = service.QueryTop(2, playerId: 0);

            Assert.Equal(1, snapshot.Top[0].PlayerId);
            Assert.Equal(2, snapshot.Top[1].PlayerId);
        }
    }
}

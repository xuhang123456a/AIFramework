using System;
using AIFramework.Features.Experiment;
using Xunit;

namespace AIFramework.Features.Tests.Experiment
{
    /// <summary>
    /// 分桶确定性验收：FNV-1a32 的已知值硬编码钉死（防止有人换成 <c>string.GetHashCode()</c>
    /// 这类每进程随机的实现）、同输入多次调用稳定、A/B 边界 0%/100%、分布量级合理。
    /// <para>
    /// 这里的分桶期望值全部由测试侧独立实现算出后硬编码，不复用被测代码的中间结果，
    /// 所以"实现改了口径"会当场变红，而不是自证自洽。
    /// </para>
    /// </summary>
    public sealed class ExperimentBucketingTests
    {
        [Fact]
        public void Hash_KnownInput_MatchesHardcodedFnvValue()
        {
            // FNV-1a32 over UTF-8("ui.new_shop") + ':' + "10001" = 0x667CC7B6。
            Assert.Equal(1719453622u, ExperimentBucketing.Hash("ui.new_shop", 10001));

            // FNV-1a32 over UTF-8("balance.gold_rate") + ':' + "10001" = 0x25FEBC0E。
            Assert.Equal(637451278u, ExperimentBucketing.Hash("balance.gold_rate", 10001));
        }

        [Fact]
        public void Hash_NegativePlayerId_MatchesHardcodedFnvValue()
        {
            // 负数玩家 id 走的是 '-' + 十进制，同样必须逐字节钉住（联调/匿名账号会用到）。
            // FNV-1a32 over UTF-8("feature.battle_speed") + ':' + "-5" = 0x6595DC21。
            Assert.Equal(1704320033u, ExperimentBucketing.Hash("feature.battle_speed", -5));

            // FNV-1a32 over UTF-8("ui.new_shop") + ':' + "-5" = 0xBFDA5F48。
            Assert.Equal(3218759496u, ExperimentBucketing.Hash("ui.new_shop", -5));
        }

        [Fact]
        public void OffsetBasisAndPrime_AreTheStandardFnvParameters()
        {
            // 参数被改掉时，上面的已知值会一起变——这里再单独钉一次，报错更直白。
            Assert.Equal(2166136261u, ExperimentBucketing.OffsetBasis);
            Assert.Equal(16777619u, ExperimentBucketing.Prime);
            Assert.Equal(100, ExperimentBucketing.BucketCount);
        }

        [Fact]
        public void Bucket_KnownInputs_AreWithinZeroToNinetyNine()
        {
            Assert.Equal(22, ExperimentBucketing.Bucket("ui.new_shop", 10001));
            Assert.Equal(78, ExperimentBucketing.Bucket("balance.gold_rate", 10001));
            Assert.Equal(45, ExperimentBucketing.Bucket("feature.battle_speed", 10001));
        }

        [Fact]
        public void Bucket_SameKeyAndPlayer_IsStableAcrossRepeatedCalls()
        {
            int first = ExperimentBucketing.Bucket("ui.new_shop", 10001);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(first, ExperimentBucketing.Bucket("ui.new_shop", 10001));
            }
        }

        [Fact]
        public void Bucket_AllPlayers_StaysWithinRange()
        {
            for (int playerId = 1; playerId <= 1000; playerId++)
            {
                int bucket = ExperimentBucketing.Bucket("ui.new_shop", playerId);
                Assert.InRange(bucket, 0, ExperimentBucketing.BucketCount - 1);
            }
        }

        [Fact]
        public void Bucket_DifferentPlayers_SpanManyBuckets()
        {
            // 100 个玩家至少落在 60 个不同桶里：分桶如果退化成"取模玩家 id 的低位"或常量，
            // 这里会立刻塌陷（独立实现算出的真实值是 77）。
            var seen = new bool[ExperimentBucketing.BucketCount];
            int distinct = 0;
            for (int playerId = 1; playerId <= 100; playerId++)
            {
                int bucket = ExperimentBucketing.Bucket("feature.battle_speed", playerId);
                if (!seen[bucket])
                {
                    seen[bucket] = true;
                    distinct++;
                }
            }

            Assert.True(distinct >= 60, "100 个玩家只落到 " + distinct + " 个桶，分布可疑。");
        }

        [Fact]
        public void Distribution_FiftyPercent_IsNearHalf()
        {
            // 1..10000 号玩家在 50% 流量下应落在 50% 附近（独立实现算出的真实值：5007）。
            int bCount = CountB("balance.gold_rate", 50, 10000);

            Assert.InRange(bCount, 4700, 5300);
        }

        [Fact]
        public void Distribution_TenPercent_IsNearOneTenth()
        {
            // 真实值 1009/10000：10% 档位不能因为整数量化而明显偏航。
            int bCount = CountB("feature.battle_speed", 10, 10000);

            Assert.InRange(bCount, 900, 1100);
        }

        [Fact]
        public void IsB_ZeroTraffic_IsAlwaysFalse()
        {
            // 0% = 全 A：bucket(0..99) < 0 恒不成立。
            for (int playerId = 1; playerId <= 1000; playerId++)
            {
                Assert.False(ExperimentBucketing.IsB("ui.new_shop", playerId, 0));
            }
        }

        [Fact]
        public void IsB_FullTraffic_IsAlwaysTrue()
        {
            // 100% = 全 B：bucket(0..99) < 100 恒成立。
            for (int playerId = 1; playerId <= 1000; playerId++)
            {
                Assert.True(ExperimentBucketing.IsB("ui.new_shop", playerId, 100));
            }
        }

        [Fact]
        public void IsB_OutOfRangeTraffic_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentBucketing.IsB("ui.new_shop", 1, 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentBucketing.IsB("ui.new_shop", 1, -1));
        }

        [Fact]
        public void Hash_NullKey_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ExperimentBucketing.Hash(null!, 1));
            Assert.Throws<ArgumentNullException>(() => ExperimentBucketing.Bucket(null!, 1));
        }

        /// <summary>独立统计：玩家 1..<paramref name="playerCount"/> 中落在 B 组的人数。</summary>
        private static int CountB(string key, int trafficPercent, int playerCount)
        {
            int count = 0;
            for (int playerId = 1; playerId <= playerCount; playerId++)
            {
                if (ExperimentBucketing.IsB(key, playerId, trafficPercent))
                {
                    count++;
                }
            }

            return count;
        }
    }
}

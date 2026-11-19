using AIFramework.Adapters.Console;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleCloudSaveService"/> 的行为契约验收：可用性开关、下载副本隔离、
    /// 版本号单调性、上传成功/失败与排空日志。
    /// <para>
    /// 这些用例同时是"未来 HTTP 云实现"的验收清单——假云在这里被允许的行为，
    /// 就是真云必须做到的最小语义；任何一条在这里放松，真机同步就会跟着出错。
    /// </para>
    /// </summary>
    public sealed class ConsoleCloudSaveServiceTests
    {
        /// <summary>测试用合法键。</summary>
        private const string SlotKey = "inventory.0";

        [Fact]
        public void IsAvailable_ReflectsConstructorConfiguration()
        {
            Assert.True(new ConsoleCloudSaveService().IsAvailable);
            Assert.True(new ConsoleCloudSaveService(available: true).IsAvailable);
            Assert.False(new ConsoleCloudSaveService(available: false).IsAvailable);
        }

        [Fact]
        public void TryDownload_MissingKey_ReturnsNullAndLogsMiss()
        {
            var cloud = new ConsoleCloudSaveService();

            CloudSaveEnvelope? downloaded = cloud.TryDownload(SlotKey);

            Assert.Null(downloaded);
            Assert.Equal(0, cloud.Count);
            Assert.Contains("download key=inventory.0 -> miss", cloud.DrainLog());
        }

        [Fact]
        public void Upload_ThenTryDownload_RoundTripsEnvelope()
        {
            var cloud = new ConsoleCloudSaveService();
            byte[] payload = { 1, 2, 3, 4 };

            CloudSaveSyncOutcome outcome = cloud.Upload(new CloudSaveEnvelope(SlotKey, payload, 3, 1700000000));

            Assert.Equal(CloudSaveSyncResult.Uploaded, outcome.Result);
            Assert.Equal(3, outcome.Revision);
            Assert.Equal(ConsoleCloudSaveService.ReasonStored, outcome.Reason);
            Assert.Equal(1, cloud.Count);

            CloudSaveEnvelope? downloaded = cloud.TryDownload(SlotKey);
            Assert.True(downloaded.HasValue);

            CloudSaveEnvelope envelope = downloaded.GetValueOrDefault();
            Assert.Equal(SlotKey, envelope.Key);
            Assert.Equal(3, envelope.Revision);
            Assert.Equal(1700000000, envelope.UpdatedAtUnixSeconds);
            Assert.Equal(payload, envelope.Payload);
        }

        [Fact]
        public void TryDownload_ReturnsCopy_MutatingItDoesNotAffectStoredEnvelope()
        {
            var cloud = new ConsoleCloudSaveService();
            cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 10, 20, 30 }, 1, 100));

            CloudSaveEnvelope first = Require(cloud.TryDownload(SlotKey));
            first.Payload[0] = 99; // 试图越过契约改云端数据（真实云端不该被本地缓冲影响）

            CloudSaveEnvelope second = Require(cloud.TryDownload(SlotKey));
            Assert.Equal(10, second.Payload[0]);
            Assert.NotSame(first.Payload, second.Payload);
        }

        [Fact]
        public void Upload_StoresDefensiveCopyOfPayload()
        {
            var cloud = new ConsoleCloudSaveService();
            byte[] payload = { 7, 8, 9 };
            cloud.Upload(new CloudSaveEnvelope(SlotKey, payload, 1, 100));

            payload[0] = 0; // 包络构造不复制 payload，实现必须自己复制

            Assert.Equal(7, Require(cloud.TryDownload(SlotKey)).Payload[0]);
        }

        [Fact]
        public void Upload_WithEqualOrLowerRevision_RejectedAndKeepsStoredValue()
        {
            var cloud = new ConsoleCloudSaveService();
            cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 1, 1 }, 5, 500));

            CloudSaveSyncOutcome sameRevision = cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 2, 2 }, 5, 600));
            CloudSaveSyncOutcome lowerRevision = cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 3, 3 }, 4, 700));

            Assert.Equal(CloudSaveSyncResult.Unavailable, sameRevision.Result);
            Assert.Equal(ConsoleCloudSaveService.ReasonRevisionNotIncreasing, sameRevision.Reason);
            Assert.Equal(5, sameRevision.Revision); // 返回云端已有的权威版本

            Assert.Equal(CloudSaveSyncResult.Unavailable, lowerRevision.Result);
            Assert.Equal(ConsoleCloudSaveService.ReasonRevisionNotIncreasing, lowerRevision.Reason);

            // 被拒的写入绝不能污染台账。
            CloudSaveEnvelope stored = Require(cloud.TryDownload(SlotKey));
            Assert.Equal(5, stored.Revision);
            Assert.Equal(new byte[] { 1, 1 }, stored.Payload);
        }

        [Fact]
        public void Upload_WithHigherRevision_Overwrites()
        {
            var cloud = new ConsoleCloudSaveService();
            cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 1 }, 1, 100));

            CloudSaveSyncOutcome outcome = cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 2 }, 2, 200));

            Assert.Equal(CloudSaveSyncResult.Uploaded, outcome.Result);
            Assert.Equal(2, Require(cloud.TryDownload(SlotKey)).Revision);
            Assert.Equal(new byte[] { 2 }, Require(cloud.TryDownload(SlotKey)).Payload);
        }

        [Fact]
        public void UnavailableInstance_AllOperationsFailWithoutTouchingStore()
        {
            var cloud = new ConsoleCloudSaveService(available: false);

            Assert.Null(cloud.TryDownload(SlotKey));

            CloudSaveSyncOutcome outcome = cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 1 }, 1, 1));

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(ConsoleCloudSaveService.ReasonUnavailable, outcome.Reason);
            Assert.Equal(0, cloud.Count);
        }

        [Fact]
        public void InvalidKey_DownloadReturnsNullAndUploadRejected()
        {
            var cloud = new ConsoleCloudSaveService();

            Assert.Null(cloud.TryDownload("bad/key"));
            Assert.Null(cloud.TryDownload(".."));
            Assert.Null(cloud.TryDownload(string.Empty));

            CloudSaveSyncOutcome outcome = cloud.Upload(new CloudSaveEnvelope("bad key", new byte[] { 1 }, 1, 1));

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(ConsoleCloudSaveService.ReasonInvalidKey, outcome.Reason);
            Assert.Equal(0, cloud.Count);
        }

        [Fact]
        public void DrainLog_ReturnsEventsInOrderAndClears()
        {
            var cloud = new ConsoleCloudSaveService();
            cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 1 }, 1, 100));
            cloud.TryDownload(SlotKey);

            var log = cloud.DrainLog();

            Assert.Equal(2, log.Count);
            Assert.Contains("upload", log[0]);
            Assert.Contains("rev=1", log[0]);
            Assert.Contains("download", log[1]);

            // 排空语义：同一批事件不允许被重复打印。
            Assert.Empty(cloud.DrainLog());
        }

        [Fact]
        public void DrainLog_KeepsOnlyLatestEntries()
        {
            var cloud = new ConsoleCloudSaveService();
            for (int revision = 1; revision <= 40; revision++)
            {
                cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 1 }, revision, revision));
            }

            var log = cloud.DrainLog();

            // 环形缓冲只保留最近 32 条：40 次上传后第一条应是第 9 次（rev=9），最后一条是 rev=40。
            Assert.Equal(ConsoleCloudSaveService.LogCapacity, log.Count);
            Assert.Contains("rev=9 ", log[0]);
            Assert.Contains("rev=40", log[log.Count - 1]);
        }

        [Fact]
        public void Merger_AgainstConsoleCloud_ResolvesConflictAndBumpsRevisionPastRemote()
        {
            // 端到端：merger 的"覆盖云端必须严格递增"与 Console 假云的单调性校验必须是同一套规则，
            // 否则真机上会出现"策略判本地赢、上传却被拒"的死循环。
            var cloud = new ConsoleCloudSaveService();
            cloud.Upload(new CloudSaveEnvelope(SlotKey, new byte[] { 9, 9 }, 7, 700));

            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.NewerWins);
            CloudSaveSyncOutcome outcome = merger.Sync(
                SlotKey, new byte[] { 1, 1 }, localRevision: 2, localUpdatedAtUnixSeconds: 800, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(8, outcome.Revision);
            Assert.Equal(new byte[] { 1, 1 }, resolved);

            CloudSaveEnvelope stored = Require(cloud.TryDownload(SlotKey));
            Assert.Equal(8, stored.Revision);
            Assert.Equal(new byte[] { 1, 1 }, stored.Payload);
            Assert.Equal(800, stored.UpdatedAtUnixSeconds);
        }

        /// <summary>取回"必然存在"的云端包络：缺失即用例失败，避免用 <c>!</c> 掩盖 null 路径。</summary>
        private static CloudSaveEnvelope Require(CloudSaveEnvelope? envelope)
        {
            Assert.True(envelope.HasValue, "云端没有该 key");
            return envelope.GetValueOrDefault();
        }
    }
}

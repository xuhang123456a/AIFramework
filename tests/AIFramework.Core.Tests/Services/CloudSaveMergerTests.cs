using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;
using AIFramework.Core.Save;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="CloudSaveMerger"/> 的行为契约验收：六态结果、三种显式策略、MergeByKey 的合并/回退、
    /// <see cref="CloudSaveMerger.Decide"/> 纯判定矩阵，以及最硬的那条红线——
    /// <b>任何失败分支都不得丢掉本地内容</b>。
    /// <para>
    /// 这些用例全部跑在内存假云上（无网络、无文件、无 Unity），因此可以在毫秒级重复执行，
    /// 也正是 <c>ICloudSaveService</c> 故意做成同步契约的目的。
    /// </para>
    /// </summary>
    public sealed class CloudSaveMergerTests
    {
        /// <summary>测试用存档键（合法键：字母数字下划线点短横线）。</summary>
        private const string SlotKey = "inventory.0";

        // ------------------------------------------------------------------ 规则 1：云端不可用不丢本地

        [Fact]
        public void Sync_CloudUnavailable_ReturnsUnavailableAndKeepsLocalPayload()
        {
            var cloud = new FakeCloud { Available = false };
            byte[] local = Bytes(1, 2, 3);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 7, 1000, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.False(outcome.IsSuccess);
            Assert.Equal(CloudSaveMerger.ReasonCloudUnavailable, outcome.Reason);
            Assert.Equal(7, outcome.Revision);

            // 红线：本地内容原封不动（连引用都不换，调用方可以直接继续存盘）。
            Assert.Same(local, resolved);
            Assert.Equal(0, cloud.DownloadCallCount);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void Sync_DownloadThrows_ReturnsUnavailableAndKeepsLocalPayload()
        {
            var cloud = new FakeCloud { ThrowOnDownload = true };
            byte[] local = Bytes(9, 9);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 3, 500, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonDownloadFailed, outcome.Reason);
            Assert.Same(local, resolved);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void Sync_AvailabilityProbeThrows_ReturnsUnavailableAndKeepsLocalPayload()
        {
            var cloud = new FakeCloud { ThrowOnAvailability = true };
            byte[] local = Bytes(4, 5, 6);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 1, 10, out byte[] resolved);

            // IsAvailable 抛异常与"下载抛异常"同属"探不到云端"，必须同样降级而不是穿透。
            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonDownloadFailed, outcome.Reason);
            Assert.Same(local, resolved);
        }

        [Fact]
        public void Sync_EmptyKey_ReturnsUnavailableWithoutTouchingCloud()
        {
            var cloud = new FakeCloud();
            byte[] local = Bytes(1);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(string.Empty, local, 1, 1, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonInvalidKey, outcome.Reason);
            Assert.Same(local, resolved);
            Assert.Equal(0, cloud.DownloadCallCount);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        // ------------------------------------------------------------------ 规则 2：云端无该 key → 上传

        [Fact]
        public void Sync_NoRemote_UploadsLocalAsNewCloudSave()
        {
            var cloud = new FakeCloud();
            byte[] local = Inventory((7, 3));
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 4, 1234, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Uploaded, outcome.Result);
            Assert.True(outcome.IsSuccess);
            Assert.Equal(CloudSaveMerger.ReasonUploadedNew, outcome.Reason);
            Assert.Equal(4, outcome.Revision);
            Assert.Same(local, resolved);
            Assert.Equal(1, cloud.UploadCallCount);
            Assert.Equal(4, cloud.Require(SlotKey).Revision);
            Assert.Equal(1234, cloud.Require(SlotKey).UpdatedAtUnixSeconds);
            Assert.Equal(local, cloud.Require(SlotKey).Payload);
        }

        [Fact]
        public void Sync_UploadFailsWithoutRemote_ReturnsUnavailableAndKeepsLocalPayload()
        {
            var cloud = new FakeCloud { FailUpload = true, FailUploadReason = "quota exceeded" };
            byte[] local = Bytes(2, 2, 2);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 2, 20, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal("quota exceeded", outcome.Reason); // 云端原因优先透传，便于排障
            Assert.Same(local, resolved);
            Assert.Equal(2, outcome.Revision);
            Assert.False(cloud.HasStored(SlotKey));
        }

        [Fact]
        public void Sync_UploadThrows_ReturnsUnavailableAndKeepsLocalPayload()
        {
            var cloud = new FakeCloud { ThrowOnUpload = true };
            byte[] local = Bytes(8);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 1, 1, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonUploadFailed, outcome.Reason);
            Assert.Same(local, resolved);
        }

        [Fact]
        public void Sync_ConflictUploadFails_ReportsLocalRevisionNotTheCandidateBump()
        {
            var cloud = new FakeCloud { FailUpload = true, FailUploadReason = "server rejected" };
            cloud.Seed(SlotKey, Bytes(1), 9, 100);
            byte[] local = Bytes(2);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.LocalWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 2, 50, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.Unavailable, outcome.Result);
            Assert.Equal("server rejected", outcome.Reason);

            // 冲突分支本来会写 max(2,9)+1 = 10，但写入被拒 → 10 不能当成既成事实回报。
            Assert.Equal(2, outcome.Revision);
            Assert.Same(local, resolved);
            Assert.Equal(9, cloud.Require(SlotKey).Revision); // 云端原值未被改写
        }

        // ------------------------------------------------------------------ 规则 3：完全一致 → InSync

        [Fact]
        public void Sync_SameRevisionAndSamePayload_ReturnsInSyncWithoutWriting()
        {
            var cloud = new FakeCloud();
            byte[] payload = Inventory((1, 5));
            cloud.Seed(SlotKey, payload, 6, 600);
            var merger = new CloudSaveMerger(cloud);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, payload, 6, 600, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.InSync, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonInSync, outcome.Reason);
            Assert.Equal(6, outcome.Revision);
            Assert.Equal(payload, resolved);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void Sync_SameRevisionButDifferentPayload_TreatedAsConflictNotInSync()
        {
            var cloud = new FakeCloud();
            cloud.Seed(SlotKey, Inventory((1, 1)), 6, 600);
            byte[] local = Inventory((1, 2));
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.NewerWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 6, 600, out byte[] resolved);

            // revision 相同但字节不同：不能判"已同步"，否则本地这次改动会被永久吞掉。
            Assert.NotEqual(CloudSaveSyncResult.InSync, outcome.Result);
            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(7, outcome.Revision); // max(6,6)+1
            Assert.Equal(local, resolved);
        }

        // ------------------------------------------------------------------ 规则 4：各策略的冲突解决

        [Fact]
        public void Sync_LocalWins_OverwritesRemoteAndBumpsRevision()
        {
            var cloud = new FakeCloud();
            byte[] remotePayload = Bytes(1, 1, 1);
            byte[] localPayload = Bytes(2, 2, 2);
            cloud.Seed(SlotKey, remotePayload, 9, 9000);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.LocalWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, localPayload, 2, 100, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonLocalWins, outcome.Reason);
            Assert.Equal(10, outcome.Revision);
            Assert.Equal(localPayload, resolved);
            Assert.Equal(1, cloud.UploadCallCount);
            Assert.Equal(10, cloud.Require(SlotKey).Revision); // 必须严格大于云端旧的 9
            Assert.Equal(localPayload, cloud.Require(SlotKey).Payload);
        }

        [Fact]
        public void Sync_RemoteWins_AdoptsRemotePayloadWithoutUploading()
        {
            var cloud = new FakeCloud();
            byte[] remotePayload = Bytes(3, 3, 3);
            cloud.Seed(SlotKey, remotePayload, 9, 9000);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.RemoteWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, Bytes(4), 2, 20000, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedRemote, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonRemoteWins, outcome.Reason);
            Assert.Equal(9, outcome.Revision);
            Assert.Equal(remotePayload, resolved);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void Sync_NewerWins_LocalNewer_UploadsLocal()
        {
            var cloud = new FakeCloud();
            cloud.Seed(SlotKey, Bytes(1), 5, 100);
            byte[] local = Bytes(2);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.NewerWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 1, 200, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonLocalNewer, outcome.Reason);
            Assert.Equal(6, outcome.Revision);
            Assert.Equal(local, resolved);
            Assert.Equal(local, cloud.Require(SlotKey).Payload);
        }

        [Fact]
        public void Sync_NewerWins_RemoteNewer_DownloadsRemote()
        {
            var cloud = new FakeCloud();
            byte[] remote = Bytes(5, 5);
            cloud.Seed(SlotKey, remote, 5, 900);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.NewerWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, Bytes(6), 1, 100, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedRemote, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonRemoteNewer, outcome.Reason);
            Assert.Equal(5, outcome.Revision);
            Assert.Equal(remote, resolved);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void Sync_NewerWins_SameTimestamp_LocalWins()
        {
            var cloud = new FakeCloud();
            cloud.Seed(SlotKey, Bytes(7), 5, 777);
            byte[] local = Bytes(8);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.NewerWins);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 5, 777, out byte[] resolved);

            // 同刻本地优先（注释里写明的原因）：否则两台设备在同一秒会反复互相覆盖（横跳）。
            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonLocalNewer, outcome.Reason);
            Assert.Equal(6, outcome.Revision);
            Assert.Equal(local, resolved);
        }

        [Fact]
        public void Sync_RemoteWins_ResolvedPayloadIsDefensiveCopy()
        {
            var cloud = new FakeCloud();
            byte[] remote = Bytes(10, 20, 30);
            cloud.Seed(SlotKey, remote, 3, 300);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.RemoteWins);

            merger.Sync(SlotKey, Bytes(1), 1, 100, out byte[] resolved);

            // 假云故意把内部缓冲直接交出来；merger 必须复制，否则调用方改 resolved 就改坏了"云端"。
            Assert.NotSame(remote, resolved);
            resolved[0] = 99;
            Assert.Equal(10, cloud.Require(SlotKey).Payload[0]);
        }

        // ------------------------------------------------------------------ 规则 5：MergeByKey

        [Fact]
        public void Sync_MergeByKey_MergesInventoryAndUploadsMergedResult()
        {
            var cloud = new FakeCloud();
            byte[] remote = Inventory((2, 4), (3, 7));
            cloud.Seed(SlotKey, remote, 8, 900);
            byte[] local = Inventory((1, 3), (2, 5));
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.MergeByKey);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, local, 3, 100, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonMerged, outcome.Reason);
            Assert.Equal(9, outcome.Revision); // max(3,8)+1

            // 合并语义：同 itemId 取 max（2 → 5），键取并集，输出按 itemId 升序。
            AssertInventory(resolved, (1, 3), (2, 5), (3, 7));
            Assert.Equal(1, cloud.UploadCallCount);
            Assert.Equal(resolved, cloud.Require(SlotKey).Payload);
            Assert.Equal(9, cloud.Require(SlotKey).Revision);
        }

        [Fact]
        public void Sync_MergeByKey_BadPayloadFallsBackToNewerWins_LocalNewer()
        {
            var cloud = new FakeCloud();
            cloud.Seed(SlotKey, Inventory((1, 1)), 5, 100);
            byte[] localJunk = Bytes(1, 2, 3, 4, 5);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.MergeByKey);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, localJunk, 5, 500, out byte[] resolved);

            // 合并不了不许抛、也不许丢本地：退回 NewerWins，本地较新 → 覆盖云端。
            Assert.Equal(CloudSaveSyncResult.ConflictResolvedLocal, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonMergeFallbackLocalNewer, outcome.Reason);
            Assert.Equal(6, outcome.Revision);
            Assert.Equal(localJunk, resolved);
            Assert.Equal(localJunk, cloud.Require(SlotKey).Payload);
        }

        [Fact]
        public void Sync_MergeByKey_BadPayloadFallsBackToNewerWins_RemoteNewer()
        {
            var cloud = new FakeCloud();
            byte[] remoteJunk = Bytes(9, 9, 9, 9, 9, 9);
            cloud.Seed(SlotKey, remoteJunk, 5, 900);
            var merger = new CloudSaveMerger(cloud, CloudSaveConflictPolicy.MergeByKey);

            CloudSaveSyncOutcome outcome = merger.Sync(SlotKey, Inventory((1, 2)), 5, 100, out byte[] resolved);

            Assert.Equal(CloudSaveSyncResult.ConflictResolvedRemote, outcome.Result);
            Assert.Equal(CloudSaveMerger.ReasonRemoteNewer, outcome.Reason);
            Assert.Equal(remoteJunk, resolved);
            Assert.Equal(0, cloud.UploadCallCount);
        }

        [Fact]
        public void TryMerge_UnionTakesMaxAndSortsByItemId()
        {
            bool merged = CloudSaveMerger.TryMerge(
                Inventory((5, 1), (1, 2)),
                Inventory((1, 9), (3, 4)),
                out byte[] payload);

            Assert.True(merged);
            AssertInventory(payload, (1, 9), (3, 4), (5, 1));
        }

        [Fact]
        public void TryMerge_TwoEmptyInventories_ProducesEmptyInventory()
        {
            bool merged = CloudSaveMerger.TryMerge(
                Inventory(),
                Inventory(),
                out byte[] payload);

            Assert.True(merged);
            Assert.Equal(4, payload.Length); // 只有 count=0 的头
            AssertInventory(payload);
        }

        [Fact]
        public void TryMerge_InvalidPayload_ReturnsFalseWithoutThrowing()
        {
            // 坏档形态：null / 过短 / 截断 / count 与长度不符 / 字段非正。
            Assert.False(CloudSaveMerger.TryMerge(null, Inventory((1, 1)), out byte[] nullLocal));
            Assert.Empty(nullLocal);

            Assert.False(CloudSaveMerger.TryMerge(Inventory((1, 1)), null, out byte[] nullRemote));
            Assert.Empty(nullRemote);

            Assert.False(CloudSaveMerger.TryMerge(Bytes(1, 2, 3), Inventory((1, 1)), out byte[] truncated));
            Assert.Empty(truncated);

            Assert.False(CloudSaveMerger.TryMerge(Inventory((1, 1)), Bytes(0, 0, 0, 1, 0, 0, 0, 1), out byte[] badCount));
            Assert.Empty(badCount);

            Assert.False(CloudSaveMerger.TryMerge(Bytes(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Inventory((1, 1)), out byte[] zeroCountEntry));
            Assert.Empty(zeroCountEntry);
        }

        // ------------------------------------------------------------------ 规则 6：Decide 纯函数矩阵

        [Fact]
        public void Decide_NoRemote_ReturnsUploaded()
        {
            Assert.Equal(
                CloudSaveSyncResult.Uploaded,
                CloudSaveMerger.Decide(CloudSaveConflictPolicy.RemoteWins, null, 3, 300));
        }

        [Theory]
        [InlineData(CloudSaveConflictPolicy.LocalWins, 1L, 200L, CloudSaveSyncResult.ConflictResolvedLocal)]
        [InlineData(CloudSaveConflictPolicy.LocalWins, 1L, 100L, CloudSaveSyncResult.ConflictResolvedLocal)]
        [InlineData(CloudSaveConflictPolicy.RemoteWins, 1L, 200L, CloudSaveSyncResult.ConflictResolvedRemote)]
        [InlineData(CloudSaveConflictPolicy.RemoteWins, 1L, 100L, CloudSaveSyncResult.ConflictResolvedRemote)]
        [InlineData(CloudSaveConflictPolicy.NewerWins, 1L, 200L, CloudSaveSyncResult.ConflictResolvedLocal)]
        [InlineData(CloudSaveConflictPolicy.NewerWins, 1L, 50L, CloudSaveSyncResult.ConflictResolvedRemote)]
        [InlineData(CloudSaveConflictPolicy.NewerWins, 1L, 100L, CloudSaveSyncResult.ConflictResolvedLocal)]
        [InlineData(CloudSaveConflictPolicy.MergeByKey, 1L, 50L, CloudSaveSyncResult.ConflictResolvedLocal)]
        [InlineData((CloudSaveConflictPolicy)99, 1L, 50L, CloudSaveSyncResult.ConflictResolvedLocal)]
        public void Decide_RemoteConflict_MatchesPolicyMatrix(
            CloudSaveConflictPolicy policy,
            long localRevision,
            long localUpdatedAtUnixSeconds,
            CloudSaveSyncResult expected)
        {
            // 远端固定 rev=5 / ts=100；本地 rev=1（不同版本）时按策略判定。
            var remote = new CloudSaveEnvelope(SlotKey, Bytes(1, 2, 3), 5, 100);

            CloudSaveSyncResult actual = CloudSaveMerger.Decide(
                policy, remote, localRevision, localUpdatedAtUnixSeconds);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Decide_SameRevision_ReturnsInSync()
        {
            // Decide 拿不到本地 payload，因此只按 revision 判一致（Sync 会再比对字节）。
            var remote = new CloudSaveEnvelope(SlotKey, Bytes(1, 2, 3), 5, 100);

            Assert.Equal(
                CloudSaveSyncResult.InSync,
                CloudSaveMerger.Decide(CloudSaveConflictPolicy.NewerWins, remote, 5, 0));
        }

        [Fact]
        public void Constructor_NullCloud_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CloudSaveMerger(null!));
        }

        // ------------------------------------------------------------------ 夹具

        /// <summary>把若干条目编码成 <see cref="InventorySaveCodec"/> 约定格式（合并测试的输入）。</summary>
        private static byte[] Inventory(params (int ItemId, int Count)[] entries)
        {
            var list = new List<InventoryEntrySnapshot>(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                list.Add(new InventoryEntrySnapshot(entries[i].ItemId, entries[i].Count));
            }

            return InventorySaveCodec.Encode(list);
        }

        /// <summary>字面量负载（非背包结构，用来验证"不可合并"路径）。</summary>
        private static byte[] Bytes(params byte[] values)
        {
            return values;
        }

        /// <summary>解码并逐项断言（顺序敏感：合并输出必须按 itemId 升序）。</summary>
        private static void AssertInventory(byte[] payload, params (int ItemId, int Count)[] expected)
        {
            Assert.True(InventorySaveCodec.TryDecode(payload, out InventoryEntrySnapshot[] entries), "负载不是合法背包编码");
            Assert.Equal(expected.Length, entries.Length);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].ItemId, entries[i].ItemId);
                Assert.Equal(expected[i].Count, entries[i].Count);
            }
        }

        /// <summary>
        /// 内存假云：可配置可用性、失败方式与异常注入，用来把 merger 的每条分支都逼出来。
        /// <para>刻意**不复制** <see cref="TryDownload"/> 返回的 payload，用来验证 merger 自己做了防御性复制。</para>
        /// </summary>
        private sealed class FakeCloud : ICloudSaveService
        {
            private readonly Dictionary<string, CloudSaveEnvelope> _store =
                new Dictionary<string, CloudSaveEnvelope>(StringComparer.Ordinal);

            public bool Available { get; set; } = true;

            public bool ThrowOnAvailability { get; set; }

            public bool ThrowOnDownload { get; set; }

            public bool ThrowOnUpload { get; set; }

            public bool FailUpload { get; set; }

            public string FailUploadReason { get; set; } = "upload rejected";

            public int DownloadCallCount { get; private set; }

            public int UploadCallCount { get; private set; }

            public bool IsAvailable
            {
                get
                {
                    if (ThrowOnAvailability)
                    {
                        throw new InvalidOperationException("availability probe failed");
                    }

                    return Available;
                }
            }

            public void Seed(string key, byte[] payload, long revision, long updatedAtUnixSeconds)
            {
                _store[key] = new CloudSaveEnvelope(key, payload, revision, updatedAtUnixSeconds);
            }

            public bool HasStored(string key)
            {
                return _store.ContainsKey(key);
            }

            public CloudSaveEnvelope Require(string key)
            {
                CloudSaveEnvelope envelope;
                Assert.True(_store.TryGetValue(key, out envelope), "云端没有该 key：" + key);
                return envelope;
            }

            public CloudSaveEnvelope? TryDownload(string key)
            {
                DownloadCallCount++;

                if (ThrowOnDownload)
                {
                    throw new InvalidOperationException("download failed");
                }

                CloudSaveEnvelope envelope;
                return _store.TryGetValue(key, out envelope) ? envelope : (CloudSaveEnvelope?)null;
            }

            public CloudSaveSyncOutcome Upload(in CloudSaveEnvelope envelope)
            {
                UploadCallCount++;

                if (ThrowOnUpload)
                {
                    throw new InvalidOperationException("upload failed");
                }

                if (FailUpload)
                {
                    return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, FailUploadReason);
                }

                _store[envelope.Key] = envelope;
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Uploaded, envelope.Revision, "stored");
            }
        }
    }
}

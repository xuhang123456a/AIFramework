using System;
using System.Collections.Generic;
using System.Diagnostics;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="CloudSaveSyncClient"/> 的行为契约验收：下载 → 合并 → 上传的固定顺序、
    /// 重试白名单与上限、线性退避的"应等待时长"、版本冲突的一次重试、merge 违约的降级、计数器的累计语义。
    /// <para>
    /// <b>这些用例守的是"两台设备同时改档时会不会丢进度"</b>：重试次数少一次会让一次抖动变成一次失败同步，
    /// 冲突处理少一步会让后写的一方静默盖掉先写的一方，merge 抛异常后仍然上传会把云端改成一份没合并的内容。
    /// 因此断言集中在四件事——<b>顺序</b>（先下载后上传、空档不合并）、<b>次数</b>（谁重试几次、谁一次都不重试）、
    /// <b>版本基准</b>（上传携带的 versionHint 必须是刚拉到的那一版）、<b>不丢本地</b>（任何失败分支返回的内容都含本地进度）。
    /// </para>
    /// <para>
    /// 替身刻意只用"脚本化假传输 + 可拨动假时钟"：真实 HTTP 的超时、TLS、401/409 无法在无头环境复现，
    /// 只能把每个错误码逐条钉住。整个文件<b>不等待任何真实时间</b>（"应等待时长"只是被记录下来的数字，
    /// 由适配层负责真的 sleep），因此可以在毫秒级重复执行。
    /// </para>
    /// </summary>
    public sealed class CloudSaveSyncClientTests
    {
        /// <summary>测试用槽位键（与云存档合并器测试保持同一个键，便于对照两份契约）。</summary>
        private const string SlotKey = "inventory.0";

        /// <summary>默认线性退避基数：与 <see cref="CloudSaveSyncClient.DefaultBaseBackoffMilliseconds"/> 一致。</summary>
        private const int BaseBackoff = 200;

        // ================================================================== 构造与参数

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            var time = new FakeTimeProvider();
            var transport = new FakeTransport();

            // 两个依赖缺一不可：没有传输无处发请求，没有时钟违反"逻辑层禁读系统时钟"的铁律。
            Assert.Throws<ArgumentNullException>(() => new CloudSaveSyncClient(null!, time));
            Assert.Throws<ArgumentNullException>(() => new CloudSaveSyncClient(transport, null!));
        }

        [Fact]
        public void Constructor_CountersStartAtDocumentedDefaults()
        {
            var client = new CloudSaveSyncClient(new FakeTransport(), new FakeTimeProvider());

            Assert.Equal(0, client.AttemptCount);
            Assert.Equal(0, client.ConflictCount);
            Assert.Equal(CloudTransportError.None, client.LastError);
            Assert.Equal(0L, client.UploadedVersion);
            Assert.Equal(0, client.LastBackoffMilliseconds);
            Assert.Equal(0L, client.TotalBackoffMilliseconds);

            // 从未同步过：时间戳是 -1 哨兵（0 是合法的 1970 时刻，不能拿来表示"没有"）。
            Assert.Equal(-1L, client.LastSyncAtUnixSeconds);

            // 这三个数值会写进配置与日志，改动必须是显式决定。
            Assert.Equal(3, CloudSaveSyncClient.DefaultMaxAttempts);
            Assert.Equal(200, CloudSaveSyncClient.DefaultBaseBackoffMilliseconds);
            Assert.Equal(1, CloudSaveSyncClient.MaxConflictRetries);
        }

        [Fact]
        public void Constructor_NonPositiveMaxAttempts_BehavesAsSingleAttempt()
        {
            foreach (int maxAttempts in new[] { 0, -1, int.MinValue })
            {
                var transport = new FakeTransport();
                transport.EnqueueDownload(Fail(CloudTransportError.Network));

                var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), maxAttempts);

                CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

                // 夹到 1：至少发出一次真实请求，拿到一个真实的传输结论。
                Assert.False(outcome.Success);
                Assert.Equal(1, outcome.AttemptsUsed);
                Assert.Equal(1, transport.DownloadCallCount);
            }
        }

        [Fact]
        public void Constructor_NegativeBaseBackoff_TreatedAsZero()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));

            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 2, -500);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 负延迟没有语义 → 按 0 处理（立即重试），而不是算出一个荒谬的负数等待。
            Assert.False(outcome.Success);
            Assert.Equal(2, outcome.AttemptsUsed);
            Assert.Equal(0, client.LastBackoffMilliseconds);
            Assert.Equal(0L, client.TotalBackoffMilliseconds);
        }

        // ================================================================== 空云端档

        [Fact]
        public void Sync_EmptyCloud_DoesNotMergeAndUploadsLocal()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(1, 2, 3);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            Assert.True(outcome.Success);
            Assert.Equal(CloudTransportError.None, outcome.Error);
            Assert.Equal(1L, outcome.Version);
            Assert.Equal(2, outcome.AttemptsUsed);

            // 云端无档：没有"远端内容"可以合并，merge 一次都不该被调用。
            Assert.Equal(0, merge.CallCount);

            Assert.Equal(1, transport.UploadCallCount);
            CloudTransportRequest request = transport.UploadRequests[0];
            Assert.Equal(SlotKey, request.SlotKey);
            Assert.Same(local, request.Payload);

            // 新档必须带 hint=0，否则服务端会把一次新建当成覆盖（或直接判 Conflict）。
            Assert.Equal(0L, request.VersionHint);
            Assert.Same(local, outcome.MergedPayload);
        }

        [Fact]
        public void Sync_EmptyCloud_UsesServerAssignedVersion()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(42));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(7), new MergeSpy().Merge);

            // 版本号由服务端分配（客户端不自己 +1）：真云的版本可能是时间戳/服务端序号。
            Assert.True(outcome.Success);
            Assert.Equal(42L, outcome.Version);
            Assert.Equal(42L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_EmptyCloud_NullLocalPayload_UploadsEmptyPayload()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, null!, new MergeSpy().Merge);

            // null 本地负载视为空负载：仍然照常上传（可能是一个还没有内容的槽位），不是参数错误。
            Assert.True(outcome.Success);
            Assert.Empty(transport.UploadRequests[0].Payload);
            Assert.Empty(outcome.MergedPayload);
        }

        // ================================================================== 有云端档：合并

        [Fact]
        public void Sync_RemotePresent_MergesLocalWithRemoteThenUploads()
        {
            byte[] local = Bytes(1, 2);
            byte[] remote = Bytes(9);
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(remote, 5));
            transport.EnqueueUpload(Uploaded(6));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            Assert.True(outcome.Success);
            Assert.Equal(1, merge.CallCount);

            // 参数顺序固定：第一个本地、第二个云端。顺序写反会让"取 max 合并"变成"远端全胜"。
            Assert.Same(local, merge.Locals[0]);
            Assert.Same(remote, merge.Remotes[0]);

            // 上传的必须是合并结果，而不是本地原文（否则云端那半份进度被覆盖）。
            Assert.Equal(Bytes(1, 2, 9), outcome.MergedPayload);
            Assert.Equal(Bytes(1, 2, 9), transport.UploadRequests[0].Payload);

            // 覆盖云端时携带的基准版本是刚下载到的远端版本：这是服务端判"你是不是基于最新版改的"的唯一依据。
            Assert.Equal(5L, transport.UploadRequests[0].VersionHint);
            Assert.Equal(6L, outcome.Version);
            Assert.Equal(6L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_RemoteVersionWithoutPayload_IsTreatedAsRemote()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Array.Empty<byte>(), 7));
            transport.EnqueueUpload(Uploaded(8));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(4), merge.Merge);

            // 空 payload 但版本号非 0 = 云端确实有一个空档，不是"无档"：必须走合并，且 hint 用 7。
            Assert.True(outcome.Success);
            Assert.Equal(1, merge.CallCount);
            Assert.Empty(merge.Remotes[0]);
            Assert.Equal(7L, transport.UploadRequests[0].VersionHint);
        }

        [Fact]
        public void Sync_MergeIsCalledOnlyOncePerSync()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 3));
            transport.EnqueueUpload(Uploaded(4));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            client.Sync(SlotKey, Bytes(1), merge.Merge);

            // 无冲突时不允许"先试合并一次、上传前再合并一次"这种重复调用（merge 可能是昂贵操作）。
            Assert.Equal(1, merge.CallCount);
        }

        // ================================================================== 下载重试

        [Fact]
        public void Sync_NetworkFailureOnDownload_RetriesAndSucceeds()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.True(outcome.Success);
            Assert.Equal(2, transport.DownloadCallCount);
            Assert.Equal(3, outcome.AttemptsUsed); // 2 次下载 + 1 次上传
            Assert.Equal(3, client.AttemptCount);
            Assert.Equal(CloudTransportError.None, client.LastError);
        }

        [Fact]
        public void Sync_TimeoutOnDownload_IsRetried()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Timeout));
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            Assert.True(client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge).Success);
            Assert.Equal(2, transport.DownloadCallCount);
        }

        [Fact]
        public void Sync_ServerErrorOnDownload_IsRetried()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.ServerError));
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            Assert.True(client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge).Success);
            Assert.Equal(2, transport.DownloadCallCount);
        }

        [Fact]
        public void Sync_DownloadRetriesExhausted_ReturnsLastErrorAndNeverUploads()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network)); // 脚本只一条 → 之后重复最后一条
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(5, 6);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(3, outcome.AttemptsUsed);
            Assert.Equal(3, transport.DownloadCallCount);
            Assert.Equal(CloudTransportError.Network, client.LastError);

            // 下载都没成，绝不允许上传：拿一份"不知道云端长什么样"的内容去覆盖云端就是丢档。
            Assert.Equal(0, transport.UploadCallCount);

            // 失败也要把本地原文交回调用方，且不换引用。
            Assert.Same(local, outcome.MergedPayload);
            Assert.Equal(0L, outcome.Version);
            Assert.Equal(0L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_UnauthorizedOnDownload_IsNotRetried()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Unauthorized));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 401/403 重试只会刷爆风控：一次就停。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Unauthorized, outcome.Error);
            Assert.Equal(1, transport.DownloadCallCount);
            Assert.Equal(1, outcome.AttemptsUsed);
        }

        [Fact]
        public void Sync_InvalidPayloadOnDownload_IsNotRetried()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.InvalidPayload));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 同一份请求再发一次，服务端还会以同样的理由拒绝。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(1, transport.DownloadCallCount);
        }

        [Fact]
        public void Sync_ConflictOnDownload_IsNotRetriedAndNotCountedAsConflict()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Conflict));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 下载返回 Conflict 属于服务端违约（下载没有"版本基准"可言）：不重试、也不计入冲突计数
            // （ConflictCount 的语义是"上传被拒导致的冲突"）。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Conflict, outcome.Error);
            Assert.Equal(1, transport.DownloadCallCount);
            Assert.Equal(0, client.ConflictCount);
            Assert.Equal(0, transport.UploadCallCount);
        }

        // ================================================================== 上传重试

        [Fact]
        public void Sync_NetworkFailureOnUpload_RetriesAndSucceeds()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.Network));
            transport.EnqueueUpload(Uploaded(3));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(1, 2);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, new MergeSpy().Merge);

            Assert.True(outcome.Success);
            Assert.Equal(2, transport.UploadCallCount);
            Assert.Equal(3, outcome.AttemptsUsed);
            Assert.Equal(3L, outcome.Version);
            Assert.Equal(3L, client.UploadedVersion);

            // 重试必须重发同一份内容与同一个 versionHint（幂等重放）：换 payload 就等于换了一次写入。
            Assert.Same(local, transport.UploadRequests[0].Payload);
            Assert.Same(transport.UploadRequests[0].Payload, transport.UploadRequests[1].Payload);
            Assert.Equal(0L, transport.UploadRequests[1].VersionHint);
        }

        [Fact]
        public void Sync_UploadRetriesExhausted_FailsWithLastError()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.ServerError));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 2);
            byte[] local = Bytes(8);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.ServerError, outcome.Error);
            Assert.Equal(2, transport.UploadCallCount);
            Assert.Equal(3, outcome.AttemptsUsed); // 1 次下载 + 2 次上传
            Assert.Equal(0L, client.UploadedVersion);
            Assert.Same(local, outcome.MergedPayload);
        }

        [Fact]
        public void Sync_UnauthorizedOnUpload_FailsImmediately()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.Unauthorized));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Unauthorized, outcome.Error);
            Assert.Equal(1, transport.UploadCallCount);
            Assert.Equal(2, outcome.AttemptsUsed);
        }

        [Fact]
        public void Sync_InvalidPayloadOnUpload_FailsImmediately()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.InvalidPayload));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(1, transport.UploadCallCount);
        }

        // ================================================================== 传输违约（异常）

        [Fact]
        public void Sync_DownloadThrows_NormalizedToNetworkAndRetried()
        {
            var transport = new FakeTransport { ThrowOnDownload = true };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 2);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 契约要求实现方不抛；违约时按"网络不可达"处理，且仍然遵守重试上限。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(2, transport.DownloadCallCount);
            Assert.Equal(2, outcome.AttemptsUsed);
        }

        [Fact]
        public void Sync_UploadThrows_NormalizedToNetworkAndRetried()
        {
            var transport = new FakeTransport { ThrowOnUpload = true };
            transport.EnqueueDownload(NoRemote());
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 2);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(2, transport.UploadCallCount);
            Assert.Equal(CloudTransportError.Network, client.LastError);
        }

        [Fact]
        public void Sync_AvailabilityProbeThrows_TreatedAsUnavailable()
        {
            var transport = new FakeTransport { ThrowOnAvailability = true };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(0, transport.DownloadCallCount);
        }

        [Fact]
        public void Sync_TransportUnavailable_FailsWithoutSendingRequests()
        {
            var transport = new FakeTransport { Available = false };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 离线时不发任何请求：省电、省流量，也避免把"没登录"写成一片失败日志。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(0, transport.DownloadCallCount);
            Assert.Equal(0, transport.UploadCallCount);
            Assert.Equal(0, outcome.AttemptsUsed);
            Assert.Equal(0, client.AttemptCount);
        }

        // ================================================================== 参数非法

        [Fact]
        public void Sync_EmptySlotKey_FailsWithoutTouchingTransport()
        {
            var transport = new FakeTransport();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(3, 4);

            CloudSyncOutcome outcome = client.Sync(string.Empty, local, new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(0, outcome.AttemptsUsed);
            Assert.Equal(0, transport.DownloadCallCount);
            Assert.Same(local, outcome.MergedPayload);
            Assert.Equal(CloudTransportError.InvalidPayload, client.LastError);
        }

        [Fact]
        public void Sync_NullSlotKey_FailsWithoutTouchingTransport()
        {
            var transport = new FakeTransport();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(null!, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(0, transport.DownloadCallCount);
        }

        [Fact]
        public void Sync_NullMerge_FailsWithoutTouchingTransport()
        {
            var transport = new FakeTransport();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), null!);

            // 合并函数缺失是调用方的装配错误：必须在触网前就响，而不是下载完才发现无法合并。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(0, transport.DownloadCallCount);
            Assert.Equal(0, transport.UploadCallCount);
        }

        // ================================================================== 冲突处理

        [Fact]
        public void Sync_Conflict_ReDownloadsReMergesAndReUploads()
        {
            byte[] local = Bytes(1);
            byte[] firstRemote = Bytes(2);
            byte[] secondRemote = Bytes(3);
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(firstRemote, 5));
            transport.EnqueueDownload(Remote(secondRemote, 6));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            transport.EnqueueUpload(Uploaded(7));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            Assert.True(outcome.Success);
            Assert.Equal(1, client.ConflictCount);

            // 顺序：下载 → 合并 → 上传(Conflict) → 重新下载 → 重新合并 → 重新上传。
            Assert.Equal(2, transport.DownloadCallCount);
            Assert.Equal(2, transport.UploadCallCount);
            Assert.Equal(4, outcome.AttemptsUsed);

            // 第一次合并拿到的是本地 + 第一版远端；第二次合并的"本地侧"是上一次的合并结果（已含本地进度），
            // 远端侧是最新拉到的那一版。这样重传的内容既不会丢本地，也不会丢中间这一轮云端的新数据。
            Assert.Equal(2, merge.CallCount);
            Assert.Same(local, merge.Locals[0]);
            Assert.Same(firstRemote, merge.Remotes[0]);
            Assert.Same(transport.UploadRequests[0].Payload, merge.Locals[1]);
            Assert.Same(secondRemote, merge.Remotes[1]);

            // 版本基准必须换成刚拉到的那一版（6），继续用 5 会被服务端再拒一次。
            Assert.Equal(5L, transport.UploadRequests[0].VersionHint);
            Assert.Equal(6L, transport.UploadRequests[1].VersionHint);

            Assert.Equal(Bytes(1, 2, 3), transport.UploadRequests[1].Payload);
            Assert.Equal(Bytes(1, 2, 3), outcome.MergedPayload);
            Assert.Equal(7L, outcome.Version);
            Assert.Equal(7L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_ConflictTwice_FailsWithConflict()
        {
            byte[] local = Bytes(1);
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 5)); // 之后重复这一条
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict)); // 之后重复这一条
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            // 二次冲突 = 另一台设备正在高频抢写同一个槽位：继续抢只会互相覆盖，显式失败交回调用方。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Conflict, outcome.Error);
            Assert.Equal(2, client.ConflictCount);
            Assert.Equal(2, transport.UploadCallCount);
            Assert.Equal(2, transport.DownloadCallCount);
            Assert.Equal(4, outcome.AttemptsUsed);
            Assert.Equal(0L, client.UploadedVersion);
            Assert.Equal(5L, outcome.Version); // 已知的云端版本，不代表写入成功
        }

        [Fact]
        public void Sync_ConflictReDownloadFails_ReturnsDownloadErrorAndKeepsMergedContent()
        {
            byte[] local = Bytes(1);
            byte[] firstRemote = Bytes(2);
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(firstRemote, 5));
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 2);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.Network, outcome.Error);
            Assert.Equal(1, client.ConflictCount);

            // 1 次成功下载 + 1 次上传 + 2 次失败的重下载（maxAttempts=2 全部用尽）
            Assert.Equal(3, transport.DownloadCallCount);
            Assert.Equal(4, outcome.AttemptsUsed);

            // 已经合并过一次：交回调用方的是合并结果（含本地 + 那份远端），不是本地原文。
            Assert.Equal(Bytes(1, 2), outcome.MergedPayload);
        }

        [Fact]
        public void Sync_ConflictReMergeThrows_FailsInvalidPayloadWithoutSecondUpload()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 5));
            transport.EnqueueDownload(Remote(Bytes(3), 6));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            transport.EnqueueUpload(Uploaded(7));
            var merge = new MergeSpy { ThrowOnCall = 2 };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), merge.Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(2, merge.CallCount);

            // 第二次合并失败 → 不上传：绝不能把"没合并过"的内容推上去盖掉云端。
            Assert.Equal(1, transport.UploadCallCount);
            Assert.Equal(Bytes(1, 2), outcome.MergedPayload);
            Assert.Equal(0L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_UploadTimeoutThenConflict_RecoversByMergingLatest()
        {
            byte[] local = Bytes(1, 2);
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueDownload(Remote(Bytes(1, 2), 2)); // 超时的那次其实已经落地了
            transport.EnqueueUpload(Fail(CloudTransportError.Timeout));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            transport.EnqueueUpload(Uploaded(3));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            // "上传超时"不等于"没写成功"：重试被服务端判为 Conflict，说明第一次其实落地了。
            // 冲突处理重新下载 → 重合并 → 再上传，因此这条链最终仍能收敛，且内容里本地与云端都在。
            Assert.True(outcome.Success);
            Assert.Equal(1, client.ConflictCount);
            Assert.Equal(3, transport.UploadCallCount); // 超时 1 次 + 重试撞 Conflict 1 次 + 冲突后成功 1 次
            Assert.Equal(5, outcome.AttemptsUsed);      // 1 下载 + 2 上传 + 1 重下载 + 1 上传
            Assert.Equal(CloudTransportError.None, client.LastError);

            // 最后一次上传的基准版本是刚拉到的那一版（2），不是最初的 0。
            Assert.Equal(2L, transport.UploadRequests[2].VersionHint);
            Assert.Equal(Bytes(1, 2, 1, 2), transport.UploadRequests[2].Payload);
            Assert.Equal(3L, outcome.Version);
        }

        [Fact]
        public void Sync_TransportReturnsDefaultStruct_TreatedAsEmptyCloud()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(default(CloudTransportResponse));
            transport.EnqueueUpload(Uploaded(1));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(4);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            // 实现方违约返回 default（Error=None 但 payload 在运行时是 null）：必须当成"云端无档"，
            // 而不是 NullReferenceException 或"把 null 交给 merge"。
            Assert.True(outcome.Success);
            Assert.Equal(0, merge.CallCount);
            Assert.Same(local, outcome.MergedPayload);
        }

        [Fact]
        public void Sync_ConflictReDownloadEmpty_FallsBackToNewSaveUpload()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 5));
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            transport.EnqueueUpload(Uploaded(1));
            var merge = new MergeSpy();
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), merge.Merge);

            Assert.True(outcome.Success);

            // 冲突窗口里云端被删了：重下载到"无档"，此时没有可合并的内容，按新档上传（hint 回到 0）。
            Assert.Equal(1, merge.CallCount);
            Assert.Equal(0L, transport.UploadRequests[1].VersionHint);
            Assert.Equal(Bytes(1, 2), transport.UploadRequests[1].Payload);
            Assert.Equal(1L, outcome.Version);
        }

        // ================================================================== merge 违约

        [Fact]
        public void Sync_MergeThrows_FailsWithInvalidPayloadAndDoesNotUpload()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(9), 5));
            transport.EnqueueUpload(Uploaded(6));
            var merge = new MergeSpy { Throw = true };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());
            byte[] local = Bytes(1, 2);

            CloudSyncOutcome outcome = client.Sync(SlotKey, local, merge.Merge);

            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(0, transport.UploadCallCount);
            Assert.Equal(1, transport.DownloadCallCount);

            // 失败时交回本地原文，且版本号只是"已知的云端版本"，调用方不得据此推进本地版本。
            Assert.Same(local, outcome.MergedPayload);
            Assert.Equal(5L, outcome.Version);
            Assert.Equal(CloudTransportError.InvalidPayload, client.LastError);
        }

        [Fact]
        public void Sync_MergeReturnsNull_FailsWithInvalidPayload()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(9), 5));
            transport.EnqueueUpload(Uploaded(6));
            var merge = new MergeSpy { ReturnNull = true };
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), merge.Merge);

            // 返回 null 与抛异常同等对待：拿不到可信内容就不上传。
            Assert.False(outcome.Success);
            Assert.Equal(CloudTransportError.InvalidPayload, outcome.Error);
            Assert.Equal(0, transport.UploadCallCount);
        }

        // ================================================================== 退避（只记录，不 sleep）

        [Fact]
        public void Sync_RetriesRecordLinearBackoff_WithoutSleeping()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 3, BaseBackoff);
            var merge = new MergeSpy();

            var stopwatch = Stopwatch.StartNew();
            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), merge.Merge);
            stopwatch.Stop();

            Assert.False(outcome.Success);
            Assert.Equal(3, outcome.AttemptsUsed);

            // 线性：第 1 次失败后 200ms，第 2 次失败后 400ms；第 3 次失败后不再重试，因此不再记退避。
            Assert.Equal(400, client.LastBackoffMilliseconds);
            Assert.Equal(600L, client.TotalBackoffMilliseconds);

            // "应等待 600ms"只是被记录下来的数字：纯逻辑层禁止阻塞，真实 sleep 属于适配层。
            // 若有人把 Thread.Sleep 塞进 Core，这条断言会立刻变红。
            Assert.True(
                stopwatch.ElapsedMilliseconds < 300,
                "重试退避不得在 Core 内真实等待，实测耗时 " + stopwatch.ElapsedMilliseconds + "ms");
        }

        [Fact]
        public void Sync_BackoffResetsPerCall_ButTotalAccumulates()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 3, BaseBackoff);
            var merge = new MergeSpy();

            client.Sync(SlotKey, Bytes(1), merge.Merge);
            Assert.Equal(400, client.LastBackoffMilliseconds);
            Assert.Equal(600L, client.TotalBackoffMilliseconds);

            client.Sync(SlotKey, Bytes(1), merge.Merge);

            // 每次 Sync 的"最近一次退避"独立；累计值跨调用相加（用于遥测"总共让玩家等过多久"）。
            Assert.Equal(400, client.LastBackoffMilliseconds);
            Assert.Equal(1200L, client.TotalBackoffMilliseconds);
        }

        [Fact]
        public void Sync_BaseBackoffZero_RetriesWithoutAnyDelay()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 3, 0);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 基数配成 0 依然要重试（只是不等待）：这是"立即重试"的显式配置，不是"关闭重试"。
            Assert.False(outcome.Success);
            Assert.Equal(3, outcome.AttemptsUsed);
            Assert.Equal(0, client.LastBackoffMilliseconds);
            Assert.Equal(0L, client.TotalBackoffMilliseconds);
        }

        [Fact]
        public void Sync_MaxAttemptsOne_DoesNotRetry()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 1);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.False(outcome.Success);
            Assert.Equal(1, outcome.AttemptsUsed);
            Assert.Equal(1, transport.DownloadCallCount);

            // 没有"下一次尝试"，就不该记一笔永远不会被执行的等待。
            Assert.Equal(0, client.LastBackoffMilliseconds);
            Assert.Equal(0L, client.TotalBackoffMilliseconds);
        }

        [Fact]
        public void Sync_BackoffAttemptNumberingIsPerOperation()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Timeout)); // 下载失败 1 次 → 退避 100
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Fail(CloudTransportError.Timeout)); // 上传失败 1 次 → 退避 100
            transport.EnqueueUpload(Uploaded(2));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider(), 3, 100);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.True(outcome.Success);

            // 退避的尝试序号按"每个传输动作"重新从 1 开始，而不是按整次 Sync 累加：
            // 否则一次下载重试会把上传的退避时长也顶到 200/300，玩家等的时间凭空变长。
            Assert.Equal(100, client.LastBackoffMilliseconds);
            Assert.Equal(200L, client.TotalBackoffMilliseconds);
            Assert.Equal(4, outcome.AttemptsUsed);
        }

        // ================================================================== 计数器

        [Fact]
        public void Sync_CountersAccumulateAcrossCalls()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            transport.EnqueueUpload(Uploaded(2));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome first = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);
            CloudSyncOutcome second = client.Sync(SlotKey, Bytes(2), new MergeSpy().Merge);

            // AttemptsUsed 是"本次"，AttemptCount 是"累计"。
            Assert.Equal(2, first.AttemptsUsed);
            Assert.Equal(2, second.AttemptsUsed);
            Assert.Equal(4, client.AttemptCount);
            Assert.Equal(0, client.ConflictCount);
            Assert.Equal(1L, first.Version);
            Assert.Equal(2L, second.Version);
            Assert.Equal(2L, client.UploadedVersion);
        }

        [Fact]
        public void Sync_ConflictCountAccumulatesAcrossCalls()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 5));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict)); // 每次上传都冲突
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);
            Assert.Equal(2, client.ConflictCount); // 第 1 次 + 重试后第 2 次

            client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);
            Assert.Equal(4, client.ConflictCount);
        }

        [Fact]
        public void Sync_LastErrorReturnsToNoneAfterSuccess()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Fail(CloudTransportError.Network));
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 重试成功后"最近错误"必须回到 None，否则调用方会按上一次的失败做降级。
            Assert.True(outcome.Success);
            Assert.Equal(CloudTransportError.None, client.LastError);
            Assert.Equal(CloudTransportError.None, outcome.Error);
        }

        [Fact]
        public void Sync_UploadedVersionIsNotChangedByLaterFailure()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(7));
            transport.EnqueueDownload(Fail(CloudTransportError.Unauthorized));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            Assert.True(client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge).Success);
            Assert.Equal(7L, client.UploadedVersion);

            CloudSyncOutcome failing = client.Sync(SlotKey, Bytes(2), new MergeSpy().Merge);

            // UploadedVersion 的语义是"最近一次成功上传的版本"：一次失败不得把它清 0（那会导致本地台账被迫重传）。
            Assert.False(failing.Success);
            Assert.Equal(7L, client.UploadedVersion);
            Assert.Equal(CloudTransportError.Unauthorized, failing.Error);
        }

        [Fact]
        public void Sync_AttemptsUsedCoversEveryTransportRoundTrip()
        {
            var transport = new FakeTransport();
            transport.EnqueueDownload(Remote(Bytes(2), 5));
            transport.EnqueueDownload(Remote(Bytes(3), 6));
            transport.EnqueueUpload(Fail(CloudTransportError.Conflict));
            transport.EnqueueUpload(Uploaded(7));
            var client = new CloudSaveSyncClient(transport, new FakeTimeProvider());

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            Assert.Equal(4, outcome.AttemptsUsed);
            Assert.Equal(4, client.AttemptCount);
        }

        // ================================================================== 时间来源

        [Fact]
        public void Sync_StampsStartTimeFromTimeProvider()
        {
            var time = new FakeTimeProvider();
            time.SetUtcNow(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, time);

            client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 时间只经 ITimeProvider 取（逻辑层禁读系统时钟）；这个戳只用于诊断，不参与同步判定。
            Assert.Equal(1795176000L, client.LastSyncAtUnixSeconds);
        }

        [Fact]
        public void Sync_ClockFailure_DoesNotFailTheSync()
        {
            var time = new FakeTimeProvider { ThrowOnUtcNow = true };
            var transport = new FakeTransport();
            transport.EnqueueDownload(NoRemote());
            transport.EnqueueUpload(Uploaded(1));
            var client = new CloudSaveSyncClient(transport, time);

            CloudSyncOutcome outcome = client.Sync(SlotKey, Bytes(1), new MergeSpy().Merge);

            // 时钟坏了只影响诊断戳（-1 哨兵）；存档同步本身不该因为"读不到时间"而失败。
            Assert.True(outcome.Success);
            Assert.Equal(-1L, client.LastSyncAtUnixSeconds);
        }

        // ================================================================== 契约结构体的归一化

        [Fact]
        public void TransportRequest_NormalizesNullsAndNegativeHint()
        {
            var request = new CloudTransportRequest(null!, null!, -3);

            Assert.Equal(string.Empty, request.SlotKey);
            Assert.Empty(request.Payload);
            Assert.Equal(0L, request.VersionHint);
        }

        [Fact]
        public void TransportResponse_NormalizesNullsAndNegativeVersion()
        {
            var response = new CloudTransportResponse(CloudTransportError.ServerError, null!, -9, null!);

            Assert.Equal(CloudTransportError.ServerError, response.Error);
            Assert.Empty(response.Payload);
            Assert.Equal(0L, response.Version);
            Assert.Equal(string.Empty, response.Message);
        }

        [Fact]
        public void SyncOutcome_ContradictorySuccess_IsNormalizedToFailure()
        {
            // "成功但带错误码"是自相矛盾的状态：归一为失败，避免调用方只看 Success 就把错误码吞掉。
            var outcome = new CloudSyncOutcome(true, null!, -1, CloudTransportError.ServerError, -1);

            Assert.False(outcome.Success);
            Assert.Empty(outcome.MergedPayload);
            Assert.Equal(0L, outcome.Version);
            Assert.Equal(CloudTransportError.ServerError, outcome.Error);
            Assert.Equal(0, outcome.AttemptsUsed);
        }

        // ================================================================== 夹具

        /// <summary>字面量负载（本测试只关心"传了什么/顺序如何"，不关心存档格式）。</summary>
        private static byte[] Bytes(params byte[] values)
        {
            return values;
        }

        /// <summary>云端无此档的标准响应：<c>error=None + payload 空 + version=0</c>。</summary>
        private static CloudTransportResponse NoRemote()
        {
            return new CloudTransportResponse(CloudTransportError.None, Array.Empty<byte>(), 0, "no remote");
        }

        /// <summary>云端有档：指定 payload 与版本。</summary>
        private static CloudTransportResponse Remote(byte[] payload, long version)
        {
            return new CloudTransportResponse(CloudTransportError.None, payload, version, "remote");
        }

        /// <summary>上传成功：服务端分配版本。</summary>
        private static CloudTransportResponse Uploaded(long version)
        {
            return new CloudTransportResponse(CloudTransportError.None, Array.Empty<byte>(), version, "stored");
        }

        /// <summary>传输失败（payload 与版本无意义）。</summary>
        private static CloudTransportResponse Fail(CloudTransportError error)
        {
            return new CloudTransportResponse(error, Array.Empty<byte>(), 0, error.ToString());
        }

        /// <summary>
        /// 脚本化假传输：按入队顺序吐出预设结果，<b>取完后重复最后一条</b>
        /// （这样"一直失败"只写一条、"第 2 次成功"写两条，脚本本身就是用例说明）。
        /// <para>刻意记录每次调用的键与请求，用来断言调用次数、顺序、以及上传携带的版本基准。</para>
        /// </summary>
        private sealed class FakeTransport : ICloudTransport
        {
            private readonly List<CloudTransportResponse> _downloadScript = new List<CloudTransportResponse>();
            private readonly List<CloudTransportResponse> _uploadScript = new List<CloudTransportResponse>();
            private int _downloadIndex;
            private int _uploadIndex;

            /// <summary>是否可用（默认可用）。</summary>
            public bool Available { get; set; } = true;

            /// <summary>读可用性时抛异常（模拟实现违约）。</summary>
            public bool ThrowOnAvailability { get; set; }

            /// <summary>下载抛异常（模拟实现违约）。</summary>
            public bool ThrowOnDownload { get; set; }

            /// <summary>上传抛异常（模拟实现违约）。</summary>
            public bool ThrowOnUpload { get; set; }

            /// <summary>下载调用次数。</summary>
            public int DownloadCallCount { get; private set; }

            /// <summary>上传调用次数。</summary>
            public int UploadCallCount { get; private set; }

            /// <summary>每次下载的槽位键（按调用顺序）。</summary>
            public List<string> DownloadKeys { get; } = new List<string>();

            /// <summary>每次上传的请求副本（结构体可安全复制；payload 是引用，用来断言"同一个数组被重发"）。</summary>
            public List<CloudTransportRequest> UploadRequests { get; } = new List<CloudTransportRequest>();

            public bool IsAvailable
            {
                get
                {
                    if (ThrowOnAvailability)
                    {
                        throw new InvalidOperationException("fake availability failure");
                    }

                    return Available;
                }
            }

            /// <summary>入队一条下载响应。</summary>
            /// <param name="response">响应。</param>
            /// <returns>自身（链式书写）。</returns>
            public FakeTransport EnqueueDownload(CloudTransportResponse response)
            {
                _downloadScript.Add(response);
                return this;
            }

            /// <summary>入队一条上传响应。</summary>
            /// <param name="response">响应。</param>
            /// <returns>自身（链式书写）。</returns>
            public FakeTransport EnqueueUpload(CloudTransportResponse response)
            {
                _uploadScript.Add(response);
                return this;
            }

            public CloudTransportResponse Download(string slotKey)
            {
                DownloadCallCount++;
                DownloadKeys.Add(slotKey);

                if (ThrowOnDownload)
                {
                    throw new InvalidOperationException("fake download failure");
                }

                return Next(_downloadScript, ref _downloadIndex, NoRemote());
            }

            public CloudTransportResponse Upload(in CloudTransportRequest request)
            {
                UploadCallCount++;
                UploadRequests.Add(request);

                if (ThrowOnUpload)
                {
                    throw new InvalidOperationException("fake upload failure");
                }

                return Next(_uploadScript, ref _uploadIndex, Uploaded(1));
            }

            /// <summary>取脚本里的下一条；取完后重复最后一条；脚本为空时用默认值。</summary>
            private static CloudTransportResponse Next(
                List<CloudTransportResponse> script,
                ref int index,
                CloudTransportResponse fallback)
            {
                if (script.Count == 0)
                {
                    return fallback;
                }

                if (index < script.Count - 1)
                {
                    CloudTransportResponse response = script[index];
                    index++;
                    return response;
                }

                return script[script.Count - 1];
            }
        }

        /// <summary>
        /// 合并替身：默认把"本地 + 远端"首尾相接（便于断言合并结果的来源与顺序），
        /// 并可切成"抛异常 / 返回 null / 第 n 次调用抛异常"三种违约姿势。
        /// </summary>
        private sealed class MergeSpy
        {
            private readonly List<byte[]> _locals = new List<byte[]>();
            private readonly List<byte[]> _remotes = new List<byte[]>();

            /// <summary>调用次数。</summary>
            public int CallCount
            {
                get { return _locals.Count; }
            }

            /// <summary>每次调用的第一个实参（本地侧）。</summary>
            public IReadOnlyList<byte[]> Locals
            {
                get { return _locals; }
            }

            /// <summary>每次调用的第二个实参（远端侧）。</summary>
            public IReadOnlyList<byte[]> Remotes
            {
                get { return _remotes; }
            }

            /// <summary>是否每次都抛异常。</summary>
            public bool Throw { get; set; }

            /// <summary>是否每次都返回 <c>null</c>。</summary>
            public bool ReturnNull { get; set; }

            /// <summary>第几次调用（1 起）抛异常；0 = 从不。</summary>
            public int ThrowOnCall { get; set; }

            /// <summary>合并（方法组直接当 <see cref="Func{T1,T2,TResult}"/> 用）。</summary>
            /// <param name="local">本地侧。</param>
            /// <param name="remote">远端侧。</param>
            /// <returns>合并结果。</returns>
            public byte[] Merge(byte[] local, byte[] remote)
            {
                _locals.Add(local);
                _remotes.Add(remote);

                if (Throw)
                {
                    throw new InvalidOperationException("fake merge failure");
                }

                if (ThrowOnCall > 0 && _locals.Count == ThrowOnCall)
                {
                    throw new InvalidOperationException("fake merge failure #" + ThrowOnCall);
                }

                if (ReturnNull)
                {
                    return null!;
                }

                var merged = new byte[local.Length + remote.Length];
                Array.Copy(local, 0, merged, 0, local.Length);
                Array.Copy(remote, 0, merged, local.Length, remote.Length);
                return merged;
            }
        }

        /// <summary>可拨动的假时钟：本类只需要 <see cref="UtcNow"/>（单调钟不参与同步逻辑）。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private DateTime _utcNow = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

            /// <summary>读时间是否抛异常（模拟时间源违约）。</summary>
            public bool ThrowOnUtcNow { get; set; }

            public double ElapsedSeconds
            {
                get { return 0d; }
            }

            public DateTime UtcNow
            {
                get
                {
                    if (ThrowOnUtcNow)
                    {
                        throw new InvalidOperationException("fake clock failure");
                    }

                    return _utcNow;
                }
            }

            /// <summary>拨动墙钟。</summary>
            /// <param name="value">新的 UTC 时刻。</param>
            public void SetUtcNow(DateTime value)
            {
                _utcNow = value;
            }
        }
    }
}

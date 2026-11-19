#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;
using AIFramework.Core.Save;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 云存档冲突合并器：把"本地存档"与"云端存档"对齐，并输出**最终应采用的内容**。
    /// <para>
    /// <b>它在框架里的位置</b>：只依赖 <see cref="ICloudSaveService"/> 契约与 <see cref="InventorySaveCodec"/> 定义的
    /// 负载格式，因此对 Console 内存假云、Unity 本地文件模拟云、未来的 HTTP 云完全等价；换实现调用方一行不用改。
    /// </para>
    /// <para>
    /// <b>最高优先级红线：绝不丢本地</b>。<see cref="CloudSaveSyncResult.Unavailable"/> 的所有分支
    /// （云端不可用、下载抛异常、上传被拒、key 非法）都把 <c>resolvedPayload</c> 保持为传入的本地负载，
    /// 让调用方可以原样继续游戏并稍后重试；"同步失败"永远不等于"本地存档作废"。
    /// </para>
    /// <para>
    /// <b>为什么合并放在 Core 而不是适配层</b>：合并规则（同 itemId 取 max、并集、解析失败回退）是
    /// 业务语义，必须被无头单测逐条锁住；放在任一适配层都会出现"Console 合并对了、Unity 合并错了"的双份实现。
    /// </para>
    /// <para>
    /// 线程模型：非线程安全。存档同步与结算上报同线程串行调用（与 CQE 总线一致）。
    /// </para>
    /// </summary>
    public sealed class CloudSaveMerger
    {
        /// <summary>不可用原因：key 为空（不做任何网络访问）。</summary>
        public const string ReasonInvalidKey = "invalid key";

        /// <summary>不可用原因：<see cref="ICloudSaveService.IsAvailable"/> 为 <c>false</c>。</summary>
        public const string ReasonCloudUnavailable = "cloud unavailable";

        /// <summary>不可用原因：<see cref="ICloudSaveService.TryDownload"/> 抛异常（按不可用处理，不向上抛）。</summary>
        public const string ReasonDownloadFailed = "download failed";

        /// <summary>不可用原因：上传被拒或上传抛异常。</summary>
        public const string ReasonUploadFailed = "upload failed";

        /// <summary>成功原因：云端没有该 key，已上传本地内容。</summary>
        public const string ReasonUploadedNew = "uploaded (no remote)";

        /// <summary>成功原因：双方 revision 与 payload 完全一致，未写入任何一侧。</summary>
        public const string ReasonInSync = "in sync";

        /// <summary>成功原因：<see cref="CloudSaveConflictPolicy.LocalWins"/> 生效，已覆盖云端。</summary>
        public const string ReasonLocalWins = "policy: local wins";

        /// <summary>成功原因：<see cref="CloudSaveConflictPolicy.RemoteWins"/> 生效，已采用云端。</summary>
        public const string ReasonRemoteWins = "policy: remote wins";

        /// <summary>成功原因：<see cref="CloudSaveConflictPolicy.NewerWins"/> 判定本地更新，已覆盖云端。</summary>
        public const string ReasonLocalNewer = "policy: local is newer";

        /// <summary>成功原因：<see cref="CloudSaveConflictPolicy.NewerWins"/> 判定云端更新，已采用云端。</summary>
        public const string ReasonRemoteNewer = "policy: remote is newer";

        /// <summary>成功原因：<see cref="CloudSaveConflictPolicy.MergeByKey"/> 合并成功，已上传合并结果。</summary>
        public const string ReasonMerged = "policy: merged by key";

        /// <summary>成功原因：合并失败（坏 payload），回退 <see cref="CloudSaveConflictPolicy.NewerWins"/> 且本地更新。</summary>
        public const string ReasonMergeFallbackLocalNewer = "merge failed; local is newer";

        /// <summary>云端端口；构造后不可变。</summary>
        private readonly ICloudSaveService _cloud;

        /// <summary>冲突策略；构造后不可变。</summary>
        private readonly CloudSaveConflictPolicy _policy;

        /// <summary>以 <see cref="CloudSaveConflictPolicy.NewerWins"/> 策略创建合并器。</summary>
        /// <param name="cloud">云存档端口（非 <c>null</c>）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="cloud"/> 为 <c>null</c>。</exception>
        public CloudSaveMerger(ICloudSaveService cloud)
            : this(cloud, CloudSaveConflictPolicy.NewerWins)
        {
        }

        /// <summary>以指定策略创建合并器。</summary>
        /// <param name="cloud">云存档端口（非 <c>null</c>）。</param>
        /// <param name="policy">冲突策略；非法枚举值按 <see cref="CloudSaveConflictPolicy.LocalWins"/> 处理（宁可覆盖云端，也不丢本地）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="cloud"/> 为 <c>null</c>。</exception>
        public CloudSaveMerger(ICloudSaveService cloud, CloudSaveConflictPolicy policy = CloudSaveConflictPolicy.NewerWins)
        {
            _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
            _policy = policy;
        }

        /// <summary>当前冲突策略。</summary>
        public CloudSaveConflictPolicy Policy
        {
            get { return _policy; }
        }

        /// <summary>
        /// 把本地存档与云存档对齐。返回结果，并输出最终应采用的内容（远端赢时为远端 payload）。
        /// <para>顺序固定：先判 key → 探测云端 → 无远端则上传 → 完全一致则同步完成 → 否则按策略解决冲突。</para>
        /// </summary>
        /// <param name="key">存档逻辑键（空串直接判不可用，不触网）。</param>
        /// <param name="localPayload">本地负载；<c>null</c> 视为空负载（仍然尝试上传，绝不因此丢弃版本号）。</param>
        /// <param name="localRevision">本地版本号（冲突并需要覆盖云端时会向上递增，见实现说明）。</param>
        /// <param name="localUpdatedAtUnixSeconds">本地最后更新时间（Unix 秒，UTC）。</param>
        /// <param name="resolvedPayload">最终应采用的内容；<b>不可用/上传失败/本地胜时就是本地负载</b>，采用远端的场景为远端 payload 的副本。</param>
        /// <returns>六态同步结果。</returns>
        public CloudSaveSyncOutcome Sync(
            string key,
            byte[] localPayload,
            long localRevision,
            long localUpdatedAtUnixSeconds,
            out byte[] resolvedPayload)
        {
            byte[] local = localPayload ?? Array.Empty<byte>();
            resolvedPayload = local;

            if (string.IsNullOrEmpty(key))
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, localRevision, ReasonInvalidKey);
            }

            CloudSaveEnvelope? downloaded;
            try
            {
                if (!_cloud.IsAvailable)
                {
                    return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, localRevision, ReasonCloudUnavailable);
                }

                downloaded = _cloud.TryDownload(key);
            }
            catch (Exception)
            {
                // 下载异常一律降级为"云端不可用"：异常绝不穿透存档流程，本地内容保持原样。
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, localRevision, ReasonDownloadFailed);
            }

            if (downloaded == null)
            {
                return Upload(
                    key, local, localRevision, localRevision, localUpdatedAtUnixSeconds,
                    CloudSaveSyncResult.Uploaded, ReasonUploadedNew);
            }

            CloudSaveEnvelope remote = downloaded.Value;

            if (remote.Revision == localRevision && BytesEqual(remote.Payload, local))
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.InSync, localRevision, ReasonInSync);
            }

            // 冲突分支上传时统一用 max(本地, 远端) + 1：覆盖云端必须严格大于云端已有版本，
            // 否则任何遵守"版本号单调递增"的云端实现都会拒绝这次写入。
            long uploadRevision = NextRevision(localRevision, remote.Revision);

            switch (_policy)
            {
                case CloudSaveConflictPolicy.RemoteWins:
                    resolvedPayload = Copy(remote.Payload);
                    return new CloudSaveSyncOutcome(
                        CloudSaveSyncResult.ConflictResolvedRemote, remote.Revision, ReasonRemoteWins);

                case CloudSaveConflictPolicy.MergeByKey:
                {
                    byte[] mergedPayload;
                    if (TryMerge(local, remote.Payload, out mergedPayload))
                    {
                        resolvedPayload = mergedPayload;
                        return Upload(
                            key, mergedPayload, uploadRevision, localRevision, localUpdatedAtUnixSeconds,
                            CloudSaveSyncResult.ConflictResolvedLocal, ReasonMerged);
                    }

                    // 负载不是可合并结构（旧档/其它业务格式）：退回 NewerWins 语义，绝不因为"合并不了"而丢任何一侧。
                    return ResolveByNewerWins(
                        key, local, localRevision, localUpdatedAtUnixSeconds, remote, uploadRevision,
                        ReasonMergeFallbackLocalNewer, ref resolvedPayload);
                }

                case CloudSaveConflictPolicy.LocalWins:
                    return Upload(
                        key, local, uploadRevision, localRevision, localUpdatedAtUnixSeconds,
                        CloudSaveSyncResult.ConflictResolvedLocal, ReasonLocalWins);

                case CloudSaveConflictPolicy.NewerWins:
                default:
                    // default 同时兜住非法枚举值：未知策略按"本地优先"处理。
                    return ResolveByNewerWins(
                        key, local, localRevision, localUpdatedAtUnixSeconds, remote, uploadRevision,
                        ReasonLocalNewer, ref resolvedPayload);
            }
        }

        /// <summary>
        /// 只做冲突判定，不触网（测试/预检用）。
        /// <para>
        /// <b>与 <see cref="Sync"/> 的口径差异（有意为之）</b>：本方法拿不到本地 payload，
        /// 因此只能用 <see cref="CloudSaveEnvelope.Revision"/> 判"是否一致"；<see cref="Sync"/> 会再比对 payload 字节。
        /// <see cref="CloudSaveConflictPolicy.MergeByKey"/> 在这里无法判断"能不能合并"（同样缺本地 payload），
        /// 一律按"可合并"作答（⚠ 返回 <see cref="CloudSaveSyncResult.ConflictResolvedLocal"/>）；
        /// 真合并失败后的回退由 <see cref="Sync"/> 决定。
        /// </para>
        /// </summary>
        /// <param name="policy">冲突策略。</param>
        /// <param name="remote">远端包络；<c>null</c> 表示云端没有该 key。</param>
        /// <param name="localRevision">本地版本号。</param>
        /// <param name="localUpdatedAtUnixSeconds">本地最后更新时间（Unix 秒，UTC）。</param>
        /// <returns>预测的同步结果（不会返回 <see cref="CloudSaveSyncResult.Unavailable"/>：可用性不属于纯判定输入）。</returns>
        public static CloudSaveSyncResult Decide(
            CloudSaveConflictPolicy policy,
            CloudSaveEnvelope? remote,
            long localRevision,
            long localUpdatedAtUnixSeconds)
        {
            if (remote == null)
            {
                return CloudSaveSyncResult.Uploaded;
            }

            CloudSaveEnvelope remoteEnvelope = remote.Value;
            if (remoteEnvelope.Revision == localRevision)
            {
                return CloudSaveSyncResult.InSync;
            }

            switch (policy)
            {
                case CloudSaveConflictPolicy.RemoteWins:
                    return CloudSaveSyncResult.ConflictResolvedRemote;

                case CloudSaveConflictPolicy.NewerWins:
                    // 同刻（local == remote）本地优先，避免两台设备在同一秒反复互相覆盖（横跳）。
                    return localUpdatedAtUnixSeconds >= remoteEnvelope.UpdatedAtUnixSeconds
                        ? CloudSaveSyncResult.ConflictResolvedLocal
                        : CloudSaveSyncResult.ConflictResolvedRemote;

                case CloudSaveConflictPolicy.MergeByKey:
                    // 能否真合并取决于 payload，不在本方法入参里 → 按"合并成功"作答（见 XML 注释）。
                    return CloudSaveSyncResult.ConflictResolvedLocal;

                case CloudSaveConflictPolicy.LocalWins:
                default:
                    // 未知策略按本地优先：宁可多覆盖一次云端，也不静默丢掉本地进度。
                    return CloudSaveSyncResult.ConflictResolvedLocal;
            }
        }

        /// <summary>
        /// 按 key 合并两份背包负载：同 itemId 取较大 count，键取并集，输出按 itemId 升序。
        /// <para>
        /// <b>格式约定（Phase 1）</b>：payload 必须符合 <see cref="InventorySaveCodec"/> 的
        /// <c>[int32 count] + count × { int32 itemId; int32 count }</c> 结构。任一侧解析失败、
        /// 或并集超过 <see cref="InventorySaveCodec.MaxEntries"/>，返回 <c>false</c> 且 <paramref name="mergedPayload"/>
        /// 为空数组——<b>不抛异常</b>，由调用方决定回退策略。
        /// </para>
        /// <para>
        /// <b>为什么取 max 而不是求和</b>：两端的行囊是"同一份进度的两个快照"，不是两笔交易；
        /// 求和会把"同一件物品在两端各有一份"翻倍成凭空多出来的道具（线上事故级 bug），
        /// 取最大值是唯一在没有权威服务端时不会刷道具的保守语义。
        /// </para>
        /// </summary>
        /// <param name="localPayload">本地负载；<c>null</c> 视为不可合并。</param>
        /// <param name="remotePayload">远端负载；<c>null</c> 视为不可合并。</param>
        /// <param name="mergedPayload">合并结果（升序编码）；失败时为空数组。</param>
        /// <returns>是否合并成功。</returns>
        public static bool TryMerge(byte[]? localPayload, byte[]? remotePayload, out byte[] mergedPayload)
        {
            mergedPayload = Array.Empty<byte>();

            if (localPayload == null || remotePayload == null)
            {
                return false;
            }

            InventoryEntrySnapshot[] localEntries;
            if (!InventorySaveCodec.TryDecode(localPayload, out localEntries))
            {
                return false;
            }

            InventoryEntrySnapshot[] remoteEntries;
            if (!InventorySaveCodec.TryDecode(remotePayload, out remoteEntries))
            {
                return false;
            }

            var counts = new Dictionary<int, int>(localEntries.Length + remoteEntries.Length);
            for (int i = 0; i < localEntries.Length; i++)
            {
                counts[localEntries[i].ItemId] = localEntries[i].Count;
            }

            for (int i = 0; i < remoteEntries.Length; i++)
            {
                int itemId = remoteEntries[i].ItemId;
                int incoming = remoteEntries[i].Count;

                int existing;
                if (!counts.TryGetValue(itemId, out existing) || incoming > existing)
                {
                    counts[itemId] = incoming;
                }
            }

            // 并集可能超过编码上限（两侧各有近 MaxEntries 条互不相同的条目）：
            // 这种情况不能指望 Encode 抛异常兜底，提前判失败走 NewerWins 回退。
            if (counts.Count > InventorySaveCodec.MaxEntries)
            {
                return false;
            }

            int[] itemIds = new int[counts.Count];
            counts.Keys.CopyTo(itemIds, 0);
            Array.Sort(itemIds);

            var mergedEntries = new List<InventoryEntrySnapshot>(itemIds.Length);
            for (int i = 0; i < itemIds.Length; i++)
            {
                mergedEntries.Add(new InventoryEntrySnapshot(itemIds[i], counts[itemIds[i]]));
            }

            mergedPayload = InventorySaveCodec.Encode(mergedEntries);
            return true;
        }

        /// <summary>
        /// <see cref="CloudSaveConflictPolicy.NewerWins"/> 语义的公共分支（合并失败回退也走这里）。
        /// </summary>
        /// <param name="uploadReason">本地胜时写进 outcome 的原因，用于区分"策略判定"与"合并回退"。</param>
        private CloudSaveSyncOutcome ResolveByNewerWins(
            string key,
            byte[] local,
            long localRevision,
            long localUpdatedAtUnixSeconds,
            in CloudSaveEnvelope remote,
            long uploadRevision,
            string uploadReason,
            ref byte[] resolvedPayload)
        {
            // 同刻本地优先：两台设备在同一秒各自改档时，若判"远端新"会来回横跳
            // （A 覆盖 B、下一次 B 又覆盖 A），本地优先让收敛方向唯一。
            if (localUpdatedAtUnixSeconds >= remote.UpdatedAtUnixSeconds)
            {
                return Upload(
                    key, local, uploadRevision, localRevision, localUpdatedAtUnixSeconds,
                    CloudSaveSyncResult.ConflictResolvedLocal, uploadReason);
            }

            resolvedPayload = Copy(remote.Payload);
            return new CloudSaveSyncOutcome(
                CloudSaveSyncResult.ConflictResolvedRemote, remote.Revision, ReasonRemoteNewer);
        }

        /// <summary>
        /// 上传并翻译结果：云端返回 <see cref="CloudSaveSyncResult.Unavailable"/> 或抛异常时，
        /// 统一转成 <see cref="CloudSaveSyncResult.Unavailable"/>，成功则用调用方指定的"成功语义"作答
        /// （<c>Uploaded</c> / <c>ConflictResolvedLocal</c>）。
        /// <para>
        /// 失败时 <see cref="CloudSaveSyncOutcome.Revision"/> 固定回报 <paramref name="localRevision"/>：
        /// 云端没有接受写入，就没有"权威版本号"可言，绝不能把冲突分支里那个递增后的候选版本号当成既成事实
        /// 回给调用方（否则一次失败的上传会让本地版本号凭空跳一格）。
        /// </para>
        /// </summary>
        /// <param name="revision">本次希望写入云端的版本号（冲突分支会递增）。</param>
        /// <param name="localRevision">本地当前版本号（失败时回报它）。</param>
        private CloudSaveSyncOutcome Upload(
            string key,
            byte[] payload,
            long revision,
            long localRevision,
            long updatedAtUnixSeconds,
            CloudSaveSyncResult successResult,
            string successReason)
        {
            var envelope = new CloudSaveEnvelope(key, payload, revision, updatedAtUnixSeconds);

            try
            {
                CloudSaveSyncOutcome upload = _cloud.Upload(in envelope);
                if (upload.Result == CloudSaveSyncResult.Unavailable)
                {
                    // 原因优先用云端给的（例如"revision not increasing"），空则用统一文案。
                    string reason = upload.Reason.Length == 0 ? ReasonUploadFailed : upload.Reason;
                    return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, localRevision, reason);
                }

                return new CloudSaveSyncOutcome(successResult, revision, successReason);
            }
            catch (Exception)
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, localRevision, ReasonUploadFailed);
            }
        }

        /// <summary>覆盖云端时要用的版本号：max(本地, 远端) + 1（见 <see cref="Sync"/> 内注释）。</summary>
        private static long NextRevision(long localRevision, long remoteRevision)
        {
            long max = localRevision > remoteRevision ? localRevision : remoteRevision;

            // long.MaxValue 无法再递增：返回原值，让云端的单调性校验拒绝这次写入（显式失败），
            // 绝不回绕成负数——那会让"版本更大"的比较整体失效。
            return max == long.MaxValue ? max : max + 1;
        }

        /// <summary>逐字节比较（不引 LINQ，也不为比较分配枚举器）。</summary>
        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>复制远端 payload：云端实现可能把内部缓冲直接交出来，不复制就会让调用方改坏云端台账。</summary>
        private static byte[] Copy(byte[] source)
        {
            var copy = new byte[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }
}

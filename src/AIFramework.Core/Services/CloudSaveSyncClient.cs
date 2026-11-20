#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 一次云同步的结果：<b>成功与否 + 最终应采用的内容 + 权威版本 + 错误码 + 本次用掉的尝试次数</b>。
    /// <para>
    /// <b>为什么失败时也带 payload</b>：调用方拿到结果后要么写盘、要么继续游戏，两条路都需要
    /// "现在这份内容应该是什么"。失败时这里给的是<b>当前已知的最完整内容</b>
    /// （还没合并过就是本地原文，合并过就是合并结果——合并只增不减，因此它包含本地全部进度）。
    /// 若失败时返回空数组，调用方一次手滑就会把本地存档写空——这是本框架最忌讳的丢失事故。
    /// </para>
    /// <para>
    /// <b><see cref="Version"/> 在失败时不代表已写入云端</b>：它是"已知的云端版本"
    /// （下载成功过就是远端版本，否则为 0）。调用方<b>必须</b>先看 <see cref="Success"/>，
    /// 只有成功时才可把 <see cref="Version"/> 写回本地台账，否则一次失败的上传会让本地版本号凭空前进一格。
    /// </para>
    /// </summary>
    public readonly struct CloudSyncOutcome
    {
        /// <summary>构造一次同步结果。</summary>
        /// <param name="success">是否成功（与 <paramref name="error"/> 冲突时以"有错误即失败"为准，见 <see cref="Success"/>）。</param>
        /// <param name="mergedPayload">最终应采用的内容（<c>null</c> 归一化为空数组；引用，构造时未复制）。</param>
        /// <param name="version">版本号（负值按 0 归一化；失败时仅代表"已知的云端版本"，见类型注释）。</param>
        /// <param name="error">错误码；<see cref="CloudTransportError.None"/> 表示没有错误。</param>
        /// <param name="attemptsUsed">本次同步实际发出的传输往返次数（负值按 0 归一化）。</param>
        public CloudSyncOutcome(bool success, byte[] mergedPayload, long version, CloudTransportError error, int attemptsUsed)
        {
            // 自洽归一化：error ≠ None 时 Success 必为 false。
            // 允许构造出"成功但带错误码"的矛盾状态，等于给调用方留了一个必然踩的坑。
            Success = success && error == CloudTransportError.None;
            MergedPayload = mergedPayload ?? Array.Empty<byte>();
            Version = version < 0 ? 0 : version;
            Error = error;
            AttemptsUsed = attemptsUsed < 0 ? 0 : attemptsUsed;
        }

        /// <summary>是否成功（成功 ⇔ <see cref="Error"/> 为 <see cref="CloudTransportError.None"/>）。</summary>
        public bool Success { get; }

        /// <summary>最终应采用的内容（失败时也是"当前已知最完整内容"，见类型注释）。</summary>
        public byte[] MergedPayload { get; }

        /// <summary>版本号：成功上传时为服务端新分配的版本；失败时为已知的云端版本（0 = 未知/无档）。</summary>
        public long Version { get; }

        /// <summary>错误码（<see cref="CloudTransportError.None"/> = 没有错误）。</summary>
        public CloudTransportError Error { get; }

        /// <summary>本次同步实际发出的传输往返次数（下载 + 上传 + 冲突重试，全部计入）。</summary>
        public int AttemptsUsed { get; }
    }

    /// <summary>
    /// 云同步客户端：把"下载 → 合并 → 上传"这条**业务流程**连同**重试退避**与**版本冲突处理**收在一处。
    /// <para>
    /// <b>它为什么不直接说 HTTP</b>：只依赖 <see cref="ICloudTransport"/>（字节管道）与
    /// <see cref="ITimeProvider"/>（唯一时钟来源），因此同一份代码可以跑在内存假传输、本地文件传输
    /// 与真实 HTTPS 传输上；HTTP 的超时/401/409/分片全部属于适配层。
    /// </para>
    /// <para>
    /// <b>为什么这些规则必须在 Core</b>：重试次数、退避时长、冲突最多重试一次、合并失败不上传——
    /// 这四条决定了"两边同时改档时谁的数据活下来"。写进任一适配层，Console 与 Unity 就会漂移出
    /// "假云上对、真机上丢档"的双份实现。放在 Core 才能被无头单测逐条钉死。
    /// </para>
    /// <para>
    /// <b>一次 <see cref="Sync"/> 的固定顺序</b>：
    /// <list type="number">
    /// <item>参数校验（空槽位键 / <c>null</c> 合并函数）→ <see cref="CloudTransportError.InvalidPayload"/>，
    /// <b>不触网</b>：脏参数换一个就能修好，不该浪费一次网络往返。</item>
    /// <item>探活（<see cref="ICloudTransport.IsAvailable"/>）→ 不可用直接按 <see cref="CloudTransportError.Network"/>
    /// 失败，<b>不发任何请求</b>。</item>
    /// <item>下载（可重试）：<c>error=None + payload 空 + version=0</c> 视为云端无档。</item>
    /// <item>云端无档 → <b>直接上传本地内容</b>（不调用 merge）；有档 → 调 <c>merge(local, remote)</c>，
    /// 抛异常/<c>null</c> 一律 <see cref="CloudTransportError.InvalidPayload"/> 且<b>不上传</b>。</item>
    /// <item>上传（可重试，携带已知版本作为乐观并发依据）。</item>
    /// <item>上传被拒为 <see cref="CloudTransportError.Conflict"/> → <b>重新下载 → 重合并 → 重上传</b>，
    /// <b>最多 1 次</b>，再冲突即失败（避免两台设备互相"抢写"打成无限循环）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>重试口径</b>：只有 <see cref="CloudTransportError.Network"/> /
    /// <see cref="CloudTransportError.Timeout"/> / <see cref="CloudTransportError.ServerError"/> 重试，
    /// 每个传输动作（下载、上传各自）最多 <c>maxAttempts</c> 次；
    /// <see cref="CloudTransportError.Unauthorized"/> / <see cref="CloudTransportError.InvalidPayload"/>
    /// 立即失败（重试只会刷爆风控、或原样再失败一次）。
    /// </para>
    /// <para>
    /// <b>退避：只算"应等待多久"，<u>绝不真的 Sleep</u></b>。第 n 次失败后应等待
    /// <c>baseBackoffMilliseconds * n</c> 毫秒（线性）。真实等待由<b>适配层</b>负责：
    /// 适配层在后台线程上调 <see cref="Sync"/>，失败后读 <see cref="LastBackoffMilliseconds"/> 再
    /// <c>Thread.Sleep</c> / <c>Task.Delay</c>，或者干脆交给操作系统的任务调度器。
    /// 纯逻辑层一旦 <c>Sleep</c>，就会把主线程（存档/结算路径）钉死，而且无头测试会被真实等待拖成分钟级。
    /// 应等待时长通过 <see cref="LastBackoffMilliseconds"/>（本次）与
    /// <see cref="TotalBackoffMilliseconds"/>（累计）两个只读值暴露，测试直接断言数值。
    /// </para>
    /// <para>
    /// <b>时间来源</b>：只经 <see cref="ITimeProvider"/> 取时间（逻辑层铁律），且时间只用于诊断盖章
    /// （<see cref="LastSyncAtUnixSeconds"/>）；时钟违约不会让同步失败，只是该戳记为 -1。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步、非线程安全</b>。同一条总线上的同步不得并发调用（计数器与"最近一次"状态是裸字段）；
    /// 需要并发请由调用方串行化，或每个账号一个实例。
    /// </para>
    /// <para>
    /// <b>分配</b>：本类不复制 payload（<see cref="Sync"/> 的入参与 <see cref="CloudTransportRequest"/>
    /// 都是引用），不分配大对象；唯一的分配来自传输实现自身与调用方传入的 merge 结果。
    /// </para>
    /// </summary>
    public sealed class CloudSaveSyncClient
    {
        /// <summary>默认最大尝试次数（含第一次）：3 次足够扛过一次抖动，又不会让离线玩家等太久。</summary>
        public const int DefaultMaxAttempts = 3;

        /// <summary>默认线性退避基数（毫秒）：第 n 次失败后应等待 <c>200 * n</c> 毫秒。</summary>
        public const int DefaultBaseBackoffMilliseconds = 200;

        /// <summary>冲突后允许的重试次数上限（1 次）：二次冲突说明有另一台设备正在高频写同一个槽位，继续抢写只会互相覆盖。</summary>
        public const int MaxConflictRetries = 1;

        /// <summary>诊断文案：传输实现违约抛异常（已归一为 <see cref="CloudTransportError.Network"/>）。</summary>
        private const string ReasonTransportThrew = "transport threw";

        /// <summary>Unix 纪元（UTC）。只做算术换算，不读系统时钟。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>传输端口（构造后不可变）。</summary>
        private readonly ICloudTransport _transport;

        /// <summary>时间源（唯一时钟来源；构造后不可变）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>每个传输动作的最大尝试次数（已夹到 ≥ 1）。</summary>
        private readonly int _maxAttempts;

        /// <summary>线性退避基数毫秒（已夹到 ≥ 0）。</summary>
        private readonly int _baseBackoffMilliseconds;

        /// <summary>创建云同步客户端。</summary>
        /// <param name="transport">传输端口（非 <c>null</c>）。</param>
        /// <param name="time">时间源（非 <c>null</c>；时间只用于诊断盖章，但逻辑层禁止直接读系统时钟）。</param>
        /// <param name="maxAttempts">
        /// 每个传输动作的最大尝试次数（含第一次）。<b>≤ 0 按 1 处理</b>：一次都不试就宣布失败，
        /// 等于把"断网"和"根本没发请求"混成一件事；夹到 1 至少能给出一个真实的传输结论。
        /// </param>
        /// <param name="baseBackoffMilliseconds">
        /// 线性退避基数（毫秒）。<b>负值按 0 处理</b>：负延迟没有语义，按 0 处理等于"立即重试"，
        /// 而不是让实现去算一个荒谬的负数等待。
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="transport"/> 或 <paramref name="time"/> 为 <c>null</c>（装配期编程错误，必须立刻响）。
        /// </exception>
        public CloudSaveSyncClient(
            ICloudTransport transport,
            ITimeProvider time,
            int maxAttempts = DefaultMaxAttempts,
            int baseBackoffMilliseconds = DefaultBaseBackoffMilliseconds)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
            _baseBackoffMilliseconds = baseBackoffMilliseconds < 0 ? 0 : baseBackoffMilliseconds;
            LastSyncAtUnixSeconds = -1;
        }

        /// <summary>累计的传输往返次数（下载 + 上传 + 冲突重试；跨多次 <see cref="Sync"/> 累加）。</summary>
        public int AttemptCount { get; private set; }

        /// <summary>累计的冲突次数（服务端返回 <see cref="CloudTransportError.Conflict"/> 的次数，含最终仍冲突的那一次）。</summary>
        public int ConflictCount { get; private set; }

        /// <summary>最近一次错误（成功一次后回到 <see cref="CloudTransportError.None"/>；初始为 <see cref="CloudTransportError.None"/>）。</summary>
        public CloudTransportError LastError { get; private set; }

        /// <summary>最近一次<b>成功</b>上传后服务端分配的版本（0 = 从未成功上传；失败不会改写它）。</summary>
        public long UploadedVersion { get; private set; }

        /// <summary>
        /// 最近一次退避的"应等待时长"（毫秒）：第 n 次失败后为 <c>baseBackoffMilliseconds * n</c>。
        /// <para>每次 <see cref="Sync"/> 开头清零；本次没有发生重试则为 0。<b>本类不会真的等待</b>，真实等待由适配层执行。</para>
        /// </summary>
        public int LastBackoffMilliseconds { get; private set; }

        /// <summary>
        /// 累计的"应等待时长"（毫秒）：实例生命周期内所有退避之和（跨多次 <see cref="Sync"/> 累加），用于遥测"这次同步本该阻塞多久"。
        /// </summary>
        public long TotalBackoffMilliseconds { get; private set; }

        /// <summary>
        /// 最近一次 <see cref="Sync"/> 的开始时刻（Unix 秒，UTC，来自 <see cref="ITimeProvider"/>）。
        /// <para>-1 = 尚未同步过，或时间源违约/给出 1970 之前的时间。<b>时间只用于诊断</b>，不影响同步结论。</para>
        /// </summary>
        public long LastSyncAtUnixSeconds { get; private set; }

        /// <summary>
        /// 一次同步：<b>下载 → 本地合并 → 上传</b>（含重试退避与一次冲突重试）。
        /// <para>
        /// <b>永不抛异常</b>：传输违约、合并函数抛异常、时钟违约全部被归一成错误码
        /// （<see cref="CloudTransportError.Network"/> / <see cref="CloudTransportError.InvalidPayload"/>）。
        /// 存档流程不允许因为一次网络抖动把正在玩的进程打断。
        /// </para>
        /// <para>
        /// <b>merge 的调用约定</b>：仅在云端<b>确实有档</b>时调用，签名是 <c>merge(local, remote)</c>——
        /// 参数顺序固定（第一个是本地、第二个是云端），实现方不该依赖顺序之外的约定。
        /// 冲突重试时第一个参数是<b>上一次合并的结果</b>（它已包含本地全部内容），第二个是最新拉到的远端内容。
        /// </para>
        /// </summary>
        /// <param name="slotKey">存档槽位键（空串/<c>null</c> → <see cref="CloudTransportError.InvalidPayload"/>，不触网）。</param>
        /// <param name="localPayload">本地负载（<c>null</c> 视为空负载；仍然照常上传，绝不因此丢掉版本号）。</param>
        /// <param name="merge">合并函数（<c>null</c> → <see cref="CloudTransportError.InvalidPayload"/>，不触网）。</param>
        /// <returns>同步结果（<b>永不为 <c>null</c></b>：结构体返回值）。</returns>
        public CloudSyncOutcome Sync(string slotKey, byte[] localPayload, Func<byte[], byte[], byte[]> merge)
        {
            // current = "调用方现在应该采用的内容"：合并前是本地原文，合并后是合并结果。
            // 任何失败分支都把它原样回给调用方，保证"同步失败"永远不等于"本地存档作废"。
            byte[] current = localPayload ?? Array.Empty<byte>();
            int attemptsUsed = 0;

            LastBackoffMilliseconds = 0;
            LastError = CloudTransportError.None;
            LastSyncAtUnixSeconds = SafeNowUnixSeconds();

            if (string.IsNullOrEmpty(slotKey))
            {
                return Fail(CloudTransportError.InvalidPayload, current, 0, attemptsUsed);
            }

            if (merge == null)
            {
                return Fail(CloudTransportError.InvalidPayload, current, 0, attemptsUsed);
            }

            if (!SafeIsAvailable())
            {
                // 探活失败不发任何请求：离线时一次都不该打网络（省电、省流量、也避免无意义的失败日志）。
                return Fail(CloudTransportError.Network, current, 0, attemptsUsed);
            }

            // ---------------- 1) 下载 ----------------
            CloudTransportResponse remote = DownloadWithRetry(slotKey, ref attemptsUsed);
            if (remote.Error != CloudTransportError.None)
            {
                return Fail(remote.Error, current, 0, attemptsUsed);
            }

            byte[] remotePayload = PayloadOf(in remote);
            long remoteVersion = remote.Version;
            bool hasRemote = remoteVersion > 0 || remotePayload.Length > 0;

            // ---------------- 2) 合并 ----------------
            long uploadHint = 0;
            if (hasRemote)
            {
                byte[]? merged;
                try
                {
                    merged = merge(current, remotePayload);
                }
                catch (Exception)
                {
                    // 合并失败 = 拿不到可信内容 → 不上传。宁可这一次同步什么都不做，
                    // 也绝不把一份"没合并过"的内容推上去盖掉云端（那才是真正的丢档）。
                    return Fail(CloudTransportError.InvalidPayload, current, remoteVersion, attemptsUsed);
                }

                if (merged == null)
                {
                    return Fail(CloudTransportError.InvalidPayload, current, remoteVersion, attemptsUsed);
                }

                current = merged;
                uploadHint = remoteVersion;
            }

            // ---------------- 3) 上传（含一次冲突重试） ----------------
            int conflictRetries = 0;

            while (true)
            {
                var request = new CloudTransportRequest(slotKey, current, uploadHint);
                CloudTransportResponse upload = UploadWithRetry(in request, ref attemptsUsed);

                if (upload.Error == CloudTransportError.None)
                {
                    long uploadedVersion = upload.Version;
                    UploadedVersion = uploadedVersion;

                    // 整条链路成功：把"最近错误"清回 None。重试成功也算成功，
                    // 否则调用方会在一次已经自愈的抖动之后一直读到旧的错误码并持续降级。
                    LastError = CloudTransportError.None;
                    return new CloudSyncOutcome(true, current, uploadedVersion, CloudTransportError.None, attemptsUsed);
                }

                if (upload.Error != CloudTransportError.Conflict)
                {
                    return Fail(upload.Error, current, uploadHint, attemptsUsed);
                }

                ConflictCount++;
                LastError = CloudTransportError.Conflict;

                if (conflictRetries >= MaxConflictRetries)
                {
                    // 二次冲突：另一台设备正在高频写同一个槽位。继续抢写只会来回覆盖，
                    // 显式失败让调用方把"本地为准"的决定权交回玩家/产品策略。
                    return Fail(CloudTransportError.Conflict, current, uploadHint, attemptsUsed);
                }

                conflictRetries++;

                // 冲突处置：重新拉最新 → 重新合并 → 再上传（版本基准换成刚拉到的那一版）。
                CloudTransportResponse latest = DownloadWithRetry(slotKey, ref attemptsUsed);
                if (latest.Error != CloudTransportError.None)
                {
                    return Fail(latest.Error, current, uploadHint, attemptsUsed);
                }

                byte[] latestPayload = PayloadOf(in latest);
                long latestVersion = latest.Version;

                if (latestVersion == 0 && latestPayload.Length == 0)
                {
                    // 冲突窗口里云端被删了：退化成"新档上传"（hint=0），比直接失败更符合用户预期。
                    uploadHint = 0;
                    continue;
                }

                byte[]? remapped;
                try
                {
                    remapped = merge(current, latestPayload);
                }
                catch (Exception)
                {
                    return Fail(CloudTransportError.InvalidPayload, current, latestVersion, attemptsUsed);
                }

                if (remapped == null)
                {
                    return Fail(CloudTransportError.InvalidPayload, current, latestVersion, attemptsUsed);
                }

                current = remapped;
                uploadHint = latestVersion;
            }
        }

        /// <summary>
        /// 下载并重试：只有 Network / Timeout / ServerError 会重试，每个动作最多 <see cref="_maxAttempts"/> 次。
        /// <para>每次失败后记录"应等待时长"，但<b>不真的等待</b>（见类型注释的退避口径）。</para>
        /// </summary>
        /// <param name="slotKey">存档槽位键。</param>
        /// <param name="attemptsUsed">本次 <see cref="Sync"/> 的尝试计数（就地累加）。</param>
        /// <returns>最后一次传输结果（<see cref="CloudTransportError.None"/> 表示成功）。</returns>
        private CloudTransportResponse DownloadWithRetry(string slotKey, ref int attemptsUsed)
        {
            CloudTransportResponse response = default;
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                response = CallDownload(slotKey);
                attemptsUsed++;
                AttemptCount++;

                if (response.Error == CloudTransportError.None)
                {
                    return response;
                }

                LastError = response.Error;

                if (!IsRetryable(response.Error) || attempt >= _maxAttempts)
                {
                    return response;
                }

                RecordBackoff(attempt);
            }

            // 构造期已保证 _maxAttempts ≥ 1，循环必然执行过；这一行只为满足编译器的确定赋值分析。
            return response;
        }

        /// <summary>
        /// 上传并重试：口径与 <see cref="DownloadWithRetry"/> 一致，但 <see cref="CloudTransportError.Conflict"/>
        /// <b>不算可重试错误</b>——它必须回到外层走"重新下载 → 重合并"的路径，无脑重发同一份内容只会再被拒一次。
        /// </summary>
        /// <param name="request">上传请求。</param>
        /// <param name="attemptsUsed">本次 <see cref="Sync"/> 的尝试计数（就地累加）。</param>
        /// <returns>最后一次传输结果（<see cref="CloudTransportError.None"/> 表示成功）。</returns>
        private CloudTransportResponse UploadWithRetry(in CloudTransportRequest request, ref int attemptsUsed)
        {
            CloudTransportResponse response = default;
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                response = CallUpload(in request);
                attemptsUsed++;
                AttemptCount++;

                if (response.Error == CloudTransportError.None)
                {
                    return response;
                }

                LastError = response.Error;

                if (!IsRetryable(response.Error) || attempt >= _maxAttempts)
                {
                    return response;
                }

                RecordBackoff(attempt);
            }

            return response;
        }

        /// <summary>调一次下载，并把实现方违约抛出的异常归一成 <see cref="CloudTransportError.Network"/>（不崩）。</summary>
        /// <param name="slotKey">存档槽位键。</param>
        /// <returns>传输结果。</returns>
        private CloudTransportResponse CallDownload(string slotKey)
        {
            try
            {
                return _transport.Download(slotKey);
            }
            catch (Exception)
            {
                return new CloudTransportResponse(
                    CloudTransportError.Network, Array.Empty<byte>(), 0, ReasonTransportThrew);
            }
        }

        /// <summary>调一次上传，并把实现方违约抛出的异常归一成 <see cref="CloudTransportError.Network"/>（不崩）。</summary>
        /// <param name="request">上传请求。</param>
        /// <returns>传输结果。</returns>
        private CloudTransportResponse CallUpload(in CloudTransportRequest request)
        {
            try
            {
                return _transport.Upload(in request);
            }
            catch (Exception)
            {
                return new CloudTransportResponse(
                    CloudTransportError.Network, Array.Empty<byte>(), 0, ReasonTransportThrew);
            }
        }

        /// <summary>可重试错误码白名单（<b>本仓唯一口径</b>）：网络 / 超时 / 服务端 5xx。</summary>
        /// <param name="error">错误码。</param>
        /// <returns><c>true</c> = 值得再试一次。</returns>
        private static bool IsRetryable(CloudTransportError error)
        {
            return error == CloudTransportError.Network
                || error == CloudTransportError.Timeout
                || error == CloudTransportError.ServerError;
        }

        /// <summary>
        /// 记录第 <paramref name="attempt"/> 次失败后<b>应等待</b>的时长（线性：base * attempt），
        /// <b>不执行任何等待</b>。真实 sleep 属于适配层（见类型注释）。
        /// </summary>
        /// <param name="attempt">刚失败的尝试序号（从 1 开始）。</param>
        private void RecordBackoff(int attempt)
        {
            long delay = (long)_baseBackoffMilliseconds * attempt;
            if (delay > int.MaxValue)
            {
                // int 毫秒约 24.8 天，超出只可能是配置笔误；夹住而不是溢出成负数。
                delay = int.MaxValue;
            }

            LastBackoffMilliseconds = (int)delay;
            TotalBackoffMilliseconds += delay;
        }

        /// <summary>
        /// 构造失败结果并记下"最近错误"。
        /// <para><paramref name="error"/> 传 <see cref="CloudTransportError.None"/> 是调用方的编程错误，
        /// 这里兜成 <see cref="CloudTransportError.Network"/>（fail-closed：失败结果必须带一个具体原因）。</para>
        /// </summary>
        /// <param name="error">错误码。</param>
        /// <param name="payload">当前应采用的内容。</param>
        /// <param name="version">已知的云端版本（0 = 未知/无档）。</param>
        /// <param name="attemptsUsed">本次同步已发出的传输往返次数。</param>
        /// <returns>失败结果。</returns>
        private CloudSyncOutcome Fail(CloudTransportError error, byte[] payload, long version, int attemptsUsed)
        {
            CloudTransportError normalized = error == CloudTransportError.None ? CloudTransportError.Network : error;
            LastError = normalized;
            return new CloudSyncOutcome(false, payload, version, normalized, attemptsUsed);
        }

        /// <summary>读取响应 payload；实现方返回 <c>default(CloudTransportResponse)</c> 时 payload 为 <c>null</c>，这里兜住。</summary>
        /// <param name="response">传输结果。</param>
        /// <returns>负载字节（永不为 <c>null</c>）。</returns>
        private static byte[] PayloadOf(in CloudTransportResponse response)
        {
            return response.Payload ?? Array.Empty<byte>();
        }

        /// <summary>探活；实现方违约抛异常时按"不可用"处理（降级总比崩溃好）。</summary>
        /// <returns>传输层是否可用。</returns>
        private bool SafeIsAvailable()
        {
            try
            {
                return _transport.IsAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 当前 UTC 时间的 Unix 秒（仅用于诊断盖章）；时间源违约或早于 1970 时返回 -1。
        /// <para>用 -1 而不是 0 作哨兵：0 是"1970-01-01"这个合法时刻，与"读不到时间"必须区分开。</para>
        /// </summary>
        /// <returns>Unix 秒；-1 = 时间不可用。</returns>
        private long SafeNowUnixSeconds()
        {
            try
            {
                long seconds = (long)(_time.UtcNow - UnixEpoch).TotalSeconds;
                return seconds < 0 ? -1 : seconds;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}

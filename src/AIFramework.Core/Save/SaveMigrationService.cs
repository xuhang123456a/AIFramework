#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Save
{
    /// <summary>
    /// 存档迁移服务：把<b>游客档</b>并进<b>正式账号槽位</b>，并给出可审计的备份与失败原因。
    /// <para>
    /// <b>它在框架里的位置</b>：只依赖 <see cref="IStoragePort"/>（落盘）、<see cref="ITimeProvider"/>（诊断盖章）
    /// 与 <see cref="SaveService"/> / <see cref="InventorySaveCodec"/> 已经定义的存档包络和负载格式。
    /// 因此对"Console 内存存储 / Unity <c>persistentDataPath</c> / 云端文件"完全等价，换实现调用方一行不改。
    /// </para>
    /// <para>
    /// <b>为什么这段逻辑必须在 Core 而不是适配层</b>：游客合并的失败口径（坏档不覆盖、写失败不丢游客档、重复请求幂等）
    /// 是<b>数据安全</b>，不是 IO 细节；写进任一适配器都会漂移出"只在真机上丢档"的差异。Core 是唯一能被
    /// Console 与 Unity 共用、又能被无头单测逐条钉死的位置（与 <c>CloudSaveMerger</c> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>三条不变量（每条都有对应用例锁住）</b>：
    /// </para>
    /// <list type="number">
    /// <item><b>绝不覆盖坏档</b>：目标槽位的字节读不出合法档（magic / 长度 / CRC / schema 版本任一不成立）时直接失败，
    /// 一个字节都不写——坏档是人工恢复的唯一线索，被"看起来成功的迁移"覆盖掉就永久没了。</item>
    /// <item><b>写失败 = 游客档原样保留</b>：目标写成功之后才让游客档失效，顺序绝不反过来。</item>
    /// <item><b>幂等</b>：成功一次后游客档即失效，同一请求重放只会得到
    /// <see cref="ReasonGuestSaveMissing"/>，不会再写一次目标、也不会产生第二份备份。</item>
    /// </list>
    /// <para>
    /// <b>固定执行顺序（同一个输入永远同一条路径）</b>：
    /// 校验槽位键 → 读游客档 → 读目标档 → 裁决/合并出待写负载 → 条目上限 → 写备份（可选）→ 写目标 → 清理游客档。
    /// 每一步失败都<b>就地返回</b>，不会"跳过前面继续写后面"。
    /// </para>
    /// <para>
    /// <b>槽位键格式与 <see cref="SaveService"/> 对齐</b>：物理 key = <c>&lt;prefix&gt;.&lt;slot&gt;</c>
    /// （如 <c>save.acc-42</c>），备份 key = <c>&lt;prefix&gt;.&lt;guestSlot&gt;.guest</c>。
    /// 本类不调用 <see cref="SaveService"/> 的读写入口——它的槽位是 <c>int</c>，而迁移的槽位是"账号 id 这样的字符串"，
    /// 强行映射成数字会引入一张必须持久化的对照表；因此这里按同一套包络格式直接读写 key，
    /// 但 <b>CRC、包络版本、schema 迁移链全部复用现有实现</b>（<see cref="Crc32"/> / <see cref="SaveService.HeaderSize"/>
    /// / <see cref="SaveMigrations"/>），不另立一套字节格式。
    /// </para>
    /// <para>
    /// <b>时间来源</b>：只经 <see cref="ITimeProvider"/> 取时间（逻辑层铁律），且时间<b>只用于诊断盖章</b>
    /// （<see cref="LastMigrationAtUnixSeconds"/>）；裁决新旧只信存档自带的 <c>updatedTicks</c>，
    /// 本机时钟不参与任何数据判定（换机场景下本机时钟完全不可信）。
    /// </para>
    /// <para>
    /// <b>异常边界（刻意收窄）</b>：只有<b>写</b>失败被归一成 <see cref="ReasonWriteFailed"/>——它是迁移动线的最后一步，
    /// 且必须保住游客档。<see cref="IStoragePort"/> 违约抛出的异常（<see cref="IStoragePort.Read"/> 本该用 <c>null</c>
    /// 表达"没有"、<see cref="IStoragePort.Delete"/> 本该静默成功）<b>向上传播</b>，不在这里吞掉：
    /// 把一次 IO 错误伪装成"坏档"会让客服按错误的路径排查，也会掩盖存储实现自己的缺陷（与 <see cref="SaveService"/> 一致）。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步、非线程安全</b>。迁移只在登录成功、启动恢复这类低频路径上串行调用；
    /// 同一对槽位并发迁移必须由调用方串行化（两台设备同时迁移同一条账号档的最终结果由最后写入者决定，
    /// 真实并发裁决需要服务端权威版本号，不在本类能力范围内）。
    /// </para>
    /// </summary>
    public sealed class SaveMigrationService : ISaveMigrationService
    {
        /// <summary>
        /// 写入目标槽位的条目数上限（含复制分支与合并分支）。
        /// <para>
        /// <b>为什么在 <see cref="InventorySaveCodec.MaxEntries"/>（4096）之外再设一个更小的上限</b>：前者是"防坏档
        /// 声明超大 count 把内存打爆"的格式硬上限；这里是"一次迁移能接受的业务体量"。正常背包只有几十种道具，
        /// 超过 200 条基本意味着负载不是背包（串了子系统）或已被污染，此时拒绝迁移比写进去更难查。
        /// </para>
        /// </summary>
        public const int MaxMergedEntries = 200;

        /// <summary>备份槽位后缀：备份 key = <c>&lt;prefix&gt;.&lt;guestSlot&gt;.guest</c>。</summary>
        public const string GuestBackupSuffix = ".guest";

        /// <summary>失败原因：游客档不存在（也包含"已经成功迁移过"这一幂等场景）。</summary>
        public const string ReasonGuestSaveMissing = "guest save missing";

        /// <summary>失败原因：游客档存在但读不出合法档（magic / 长度 / CRC / 版本坏）。</summary>
        public const string ReasonGuestSaveCorrupt = "guest save corrupt";

        /// <summary>失败原因：目标槽位存在但读不出合法档——<b>绝不覆盖</b>，等内容人工恢复。</summary>
        public const string ReasonTargetSaveCorrupt = "target save corrupt";

        /// <summary>失败原因：待写入的条目数超过 <see cref="MaxMergedEntries"/>。</summary>
        public const string ReasonTooManyEntries = "too many entries";

        /// <summary>失败原因：两侧/单侧负载不是可合并的背包结构（<see cref="SaveMergePolicy.UnionBest"/> 专用）。</summary>
        public const string ReasonMergeFailed = "merge failed";

        /// <summary>失败原因：写备份或写目标槽位抛异常（存储端违约/磁盘满/权限）；游客档已保留。</summary>
        public const string ReasonWriteFailed = "write failed";

        /// <summary>失败原因：游客或目标槽位键为空（<c>null</c> 归一化后为空串），不触盘。</summary>
        public const string ReasonInvalidSlotKey = "invalid slot key";

        /// <summary>失败原因：游客槽位与目标槽位是同一个（合并结果会被紧接着的"清理游客档"删掉）。</summary>
        public const string ReasonSameSlot = "guest slot equals target slot";

        /// <summary>包络魔数（与 <see cref="SaveService"/> 完全一致：<c>"AISV"</c>）。</summary>
        private static readonly byte[] Magic = { (byte)'A', (byte)'I', (byte)'S', (byte)'V' };

        /// <summary>Unix 纪元（UTC）。只做算术换算，不读系统时钟。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>占位空包络：非 Valid 槽位用它填结构体，避免把 <c>default(SaveEnvelope)</c>（Payload 为 null）漏出去。</summary>
        private static readonly SaveEnvelope EmptyEnvelope =
            new SaveEnvelope(SaveMigrations.CurrentVersion, Array.Empty<byte>());

        /// <summary>落盘端口；构造后不可变。</summary>
        private readonly IStoragePort _storage;

        /// <summary>时间源（仅诊断盖章）；构造后不可变。</summary>
        private readonly ITimeProvider _time;

        /// <summary>槽位键前缀（已归一化，空串按 <c>"save"</c>）。</summary>
        private readonly string _slotPrefix;

        /// <summary>创建迁移服务。</summary>
        /// <param name="storage">落盘端口（非 <c>null</c>）。</param>
        /// <param name="time">时间源（非 <c>null</c>；只用于诊断盖章，但逻辑层禁止直接读系统时钟）。</param>
        /// <param name="defaultSlotPrefix">
        /// 槽位键前缀（默认 <c>"save"</c>；<c>null</c>/空串按 <c>"save"</c> 处理，与 <see cref="SaveService"/> 一致——
        /// 空前缀会让不同类型的存档挤在同一个 key 上）。
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="storage"/> 或 <paramref name="time"/> 为 <c>null</c>（装配期编程错误，必须立刻响）。
        /// </exception>
        public SaveMigrationService(IStoragePort storage, ITimeProvider time, string defaultSlotPrefix = "save")
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _slotPrefix = string.IsNullOrEmpty(defaultSlotPrefix) ? "save" : defaultSlotPrefix;
            LastMigrationAtUnixSeconds = -1;
        }

        /// <summary>
        /// 最近一次迁移调用的开始时刻（Unix 秒，UTC，来自 <see cref="ITimeProvider"/>）。
        /// <para>
        /// -1 = 尚未调用过，或时间源违约/早于 1970。<b>只用于诊断</b>（日志、工单），不参与任何迁移判定；
        /// 读取该属性不会抛异常，时钟坏掉也绝不会让迁移失败。
        /// </para>
        /// </summary>
        public long LastMigrationAtUnixSeconds { get; private set; }

        /// <summary>
        /// 槽位逻辑名 → 物理存储 key（<c>&lt;prefix&gt;.&lt;slot&gt;</c>，与 <see cref="SaveService.Key"/> 同格式）。
        /// <para>适配层用它做存档列表/清理/排查；本类内部所有读写都走它，避免两处拼 key 拼出不一致。</para>
        /// </summary>
        /// <param name="slotKey">槽位逻辑名（<c>null</c> 按空串处理）。</param>
        /// <returns>物理存储 key。</returns>
        public string Key(string slotKey)
        {
            return _slotPrefix + "." + (slotKey ?? string.Empty);
        }

        /// <summary>游客备份的物理存储 key（<c>Key(slot) + ".guest"</c>）。</summary>
        /// <param name="slotKey">游客槽位逻辑名（<c>null</c> 按空串处理）。</param>
        /// <returns>备份的物理存储 key。</returns>
        public string BackupKey(string slotKey)
        {
            return Key(slotKey) + GuestBackupSuffix;
        }

        /// <inheritdoc />
        public SaveMigrationResult MigrateGuestToAccount(in SaveMigrationRequest request)
        {
            // 诊断盖章放在最前面：失败分支也能回答"这次请求是什么时候来的"。时钟违约只影响这个数值。
            LastMigrationAtUnixSeconds = SafeNowUnixSeconds();

            string guestSlot = request.GuestSlotKey;
            string targetSlot = request.TargetSlotKey;

            if (guestSlot.Length == 0 || targetSlot.Length == 0)
            {
                return Failure(targetSlot, ReasonInvalidSlotKey);
            }

            // 同一槽位：待写负载会写回游客档自己的 key，紧接着"清理游客档"就把刚写好的结果删了。
            // 这种请求只可能是装配错误（账号 id 与游客号共用），必须在触盘之前挡掉。
            if (string.Equals(guestSlot, targetSlot, StringComparison.Ordinal))
            {
                return Failure(targetSlot, ReasonSameSlot);
            }

            string guestKey = Key(guestSlot);
            string targetKey = Key(targetSlot);

            LoadedSlot guest = LoadSlot(guestKey);
            if (guest.State == SlotState.Missing)
            {
                // 也包含"上一次已经成功迁移过"：幂等的正确表现是同一个失败，而不是再合并一次。
                return Failure(targetSlot, ReasonGuestSaveMissing);
            }

            if (guest.State == SlotState.Corrupt)
            {
                return Failure(targetSlot, ReasonGuestSaveCorrupt);
            }

            LoadedSlot target = LoadSlot(targetKey);
            if (target.State == SlotState.Corrupt)
            {
                // 坏档绝不覆盖（哪怕 policy 说游客优先）：留着字节才有人工恢复的可能。
                return Failure(targetSlot, ReasonTargetSaveCorrupt);
            }

            byte[] resolvedPayload;
            int mergedEntries;
            string resolveReason;
            if (!TryResolvePayload(guest, target, request.Policy, out resolvedPayload, out mergedEntries, out resolveReason))
            {
                return Failure(targetSlot, resolveReason);
            }

            // 统一的上限闸门（复制/整档策略都走这里；并集分支在编码前已先判一次，见 TryResolvePayload）。
            if (mergedEntries > MaxMergedEntries)
            {
                return Failure(targetSlot, ReasonTooManyEntries);
            }

            string backupKey = string.Empty;
            try
            {
                if (request.KeepGuestBackup)
                {
                    // 备份先于目标写：备份写失败时目标槽位还没被动过，游客档也还在，整个请求可以安全重试。
                    // 写入的是**原始包络字节**（而不是迁移后的负载）：备份要能当"时间胶囊"用，
                    // 原样保存连 schema 版本一起留下，任何一版客户端都能自己迁移着读回来。
                    // 备份槽位是固定 key（同一游客槽位再次迁移会覆盖它）：它的语义是"这次迁移前的原档"，
                    // 不是历史版本库；要留多版本请由适配层按时间另存。
                    // 反向代价（已知并接受）：备份写成功、目标写失败时，磁盘上会留一份孤儿备份——
                    // 那只是游客档的副本，游客档本身仍在，重试会原样覆盖它。
                    backupKey = BackupKey(guestSlot);
                    _storage.Write(backupKey, guest.Raw);
                }

                _storage.Write(targetKey, EncodeEnvelope(resolvedPayload));
            }
            catch (Exception)
            {
                // 写失败（磁盘满 / 权限 / 云端半截响应）：目标按存储端口的原子写契约保持原样，
                // 游客档一个字节都不删——"写失败"绝不能被翻译成"游客进度作废"。
                return Failure(targetSlot, ReasonWriteFailed);
            }

            // 只有目标确实写好之后才让游客档失效：顺序一旦反过来，写失败就是真丢档。
            _storage.Delete(guestKey);

            return new SaveMigrationResult(true, targetSlot, backupKey, mergedEntries, string.Empty);
        }

        /// <summary>
        /// 裁决出"应该写进目标槽位的负载"与条目数。
        /// <para>
        /// 目标不存在时不看策略，直接复制游客负载（没有可冲突的对象）；目标存在时按策略整档选择或按 itemId 并集。
        /// 返回值里的 <paramref name="reason"/> 只在返回 <c>false</c> 时有效。
        /// </para>
        /// </summary>
        private static bool TryResolvePayload(
            in LoadedSlot guest,
            in LoadedSlot target,
            SaveMergePolicy policy,
            out byte[] payload,
            out int mergedEntries,
            out string reason)
        {
            payload = Array.Empty<byte>();
            mergedEntries = 0;
            reason = string.Empty;

            if (target.State == SlotState.Missing)
            {
                // 不重编码：原样搬运游客负载字节，能保证"格式未知的负载"也能迁移（迁移只负责搬，不负责解释）。
                payload = guest.Envelope.Payload;
                mergedEntries = guest.EntryCount;
                return true;
            }

            switch (NormalizePolicy(policy))
            {
                case SaveMergePolicy.PreferRemote:
                    payload = target.Envelope.Payload;
                    mergedEntries = target.EntryCount;
                    return true;

                case SaveMergePolicy.NewerWins:
                {
                    // "谁更新"只信存档自带的 updatedTicks（条目里的最大值）：换机场景下本机时钟、文件 mtime
                    // 都可能被改，拿它们判新旧会让"把系统时间调快"变成"永久赢"。相同（含双方都未知 = 0）取本地。
                    long guestTicks = MaxUpdatedTicks(guest.Entries);
                    long targetTicks = MaxUpdatedTicks(target.Entries);
                    LoadedSlot winner = guestTicks >= targetTicks ? guest : target;
                    payload = winner.Envelope.Payload;
                    mergedEntries = winner.EntryCount;
                    return true;
                }

                case SaveMergePolicy.UnionBest:
                {
                    if (!guest.Decodable || !target.Decodable)
                    {
                        // 并集要求两侧都能按背包格式解出来。解不出来时明确失败，**不**退回"整档二选一"：
                        // 那会在玩家不知情的情况下丢掉另一端的进度，而游客档随后就被清理掉，等于静默删档。
                        reason = ReasonMergeFailed;
                        return false;
                    }

                    InventorySaveEntry[] union = MergeUnion(guest.Entries, target.Entries);
                    if (union.Length > MaxMergedEntries)
                    {
                        // 必须在 EncodeV2 之前判：两侧各自合法（各 ≤ 4096 条）时并集可能超过编码器硬上限，直接编码会抛。
                        reason = ReasonTooManyEntries;
                        return false;
                    }

                    payload = InventorySaveCodec.EncodeV2(union);
                    mergedEntries = union.Length;
                    return true;
                }

                case SaveMergePolicy.PreferLocal:
                default:
                    payload = guest.Envelope.Payload;
                    mergedEntries = guest.EntryCount;
                    return true;
            }
        }

        /// <summary>非法枚举值一律按 <see cref="SaveMergePolicy.PreferLocal"/> 处理（宁可多覆盖一次目标，也不静默丢游客进度）。</summary>
        private static SaveMergePolicy NormalizePolicy(SaveMergePolicy policy)
        {
            switch (policy)
            {
                case SaveMergePolicy.PreferLocal:
                case SaveMergePolicy.PreferRemote:
                case SaveMergePolicy.NewerWins:
                case SaveMergePolicy.UnionBest:
                    return policy;

                default:
                    return SaveMergePolicy.PreferLocal;
            }
        }

        /// <summary>
        /// 读一个槽位：<b>键不存在 = <see cref="SlotState.Missing"/>；键有字节但读不出合法档 = <see cref="SlotState.Corrupt"/></b>。
        /// <para>
        /// 这两种情况必须分开：<see cref="SaveService.TryLoad"/> 把它们都归成"没有存档"（读档侧足够），
        /// 但迁移侧对"目标坏档"必须拒绝写入，否则会把人工恢复的唯一线索覆盖掉。
        /// </para>
        /// </summary>
        private LoadedSlot LoadSlot(string key)
        {
            byte[]? bytes = _storage.Read(key);
            if (bytes == null)
            {
                return new LoadedSlot(SlotState.Missing, EmptyEnvelope, Array.Empty<byte>(), Array.Empty<InventorySaveEntry>(), true);
            }

            if (!TryDecodeEnvelope(bytes, out SaveEnvelope envelope))
            {
                return new LoadedSlot(SlotState.Corrupt, EmptyEnvelope, bytes, Array.Empty<InventorySaveEntry>(), true);
            }

            bool decodable = TryDecodeEntries(envelope.Payload, out InventorySaveEntry[] entries);
            return new LoadedSlot(SlotState.Valid, envelope, bytes, entries, decodable);
        }

        /// <summary>
        /// 解包络：与 <see cref="SaveService.TryLoad"/> 逐条同序同判定（magic → 包络格式 → 长度 → CRC → schema 版本 →
        /// 迁移链），只是按<b>字符串 key</b>读、并且把失败原因收敛成"不可用"。
        /// </summary>
        private static bool TryDecodeEnvelope(byte[] bytes, out SaveEnvelope envelope)
        {
            envelope = EmptyEnvelope;

            if (bytes.Length < SaveService.HeaderSize)
            {
                return false;
            }

            if (bytes[0] != Magic[0] || bytes[1] != Magic[1] || bytes[2] != Magic[2] || bytes[3] != Magic[3])
            {
                return false;
            }

            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != SaveService.FormatVersion)
            {
                return false;
            }

            int schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));

            if (length < 0 || bytes.Length != SaveService.HeaderSize + length)
            {
                return false;
            }

            var payload = new byte[length];
            Array.Copy(bytes, SaveService.HeaderSize, payload, 0, length);

            if (Crc32.Compute(payload) != expectedCrc)
            {
                return false;
            }

            // 版本判定放在 CRC 之后：坏档的版本字段本身不可信，先确认字节完整，再谈"这个版本能不能读"。
            if (schemaVersion > SaveService.CurrentVersion || schemaVersion < 1)
            {
                return false;
            }

            if (schemaVersion < SaveService.CurrentVersion)
            {
                if (!SaveMigrations.Migrate(payload, schemaVersion, SaveService.CurrentVersion, out byte[] migrated, out _))
                {
                    return false;
                }

                payload = migrated;
            }

            envelope = new SaveEnvelope(SaveService.CurrentVersion, payload);
            return true;
        }

        /// <summary>按当前包络格式写一档（字段顺序与 <see cref="SaveService.Save"/> 完全一致）。</summary>
        private static byte[] EncodeEnvelope(byte[] payload)
        {
            var buffer = new byte[SaveService.HeaderSize + payload.Length];
            Magic.CopyTo(buffer, 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), SaveService.FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), SaveService.CurrentVersion);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), Crc32.Compute(payload));
            payload.CopyTo(buffer, SaveService.HeaderSize);
            return buffer;
        }

        /// <summary>
        /// 解背包条目（v1/v2 自动识别）。
        /// <para>
        /// <b>0 字节负载按"0 条"处理</b>：<see cref="SaveService"/> 允许写空负载（全新档还没有任何条目），
        /// 而 <see cref="InventorySaveCodec"/> 的合法编码至少 4 字节——把 0 字节当成"坏负载"会让空背包玩家无法迁移。
        /// </para>
        /// </summary>
        private static bool TryDecodeEntries(byte[] payload, out InventorySaveEntry[] entries)
        {
            entries = Array.Empty<InventorySaveEntry>();

            if (payload.Length == 0)
            {
                return true;
            }

            return InventorySaveCodec.TryDecodeAuto(payload, out entries);
        }

        /// <summary>一档存档的"最后更新时间"：条目 <see cref="InventorySaveEntry.UpdatedTicks"/> 的最大值（空档 = 0 = 未知）。</summary>
        private static long MaxUpdatedTicks(InventorySaveEntry[] entries)
        {
            long max = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].UpdatedTicks > max)
                {
                    max = entries[i].UpdatedTicks;
                }
            }

            return max;
        }

        /// <summary>
        /// 按 itemId 求并集：同 id 取 <c>count</c> 较大者与 <c>updatedTicks</c> 较大者，输出按 itemId 升序。
        /// <para>
        /// <b>为什么取 max 而不是求和</b>：两侧是同一份进度的两个快照，不是两笔交易；求和会把"同一件道具两端各有一份"
        /// 翻倍成凭空多出来的道具（线上刷道具级事故）。取最大值是唯一在没有权威服务端时不会刷道具的保守语义。
        /// </para>
        /// <para>
        /// <b>为什么必须排序</b>：合并结果会被编码进存档并参与后续字节比较/云同步，"同样的输入产出同样的字节"
        /// 才能让幂等与冲突判定可靠；<see cref="Dictionary{TKey,TValue}"/> 的遍历顺序不保证，绝不能直接拿来编码。
        /// </para>
        /// </summary>
        private static InventorySaveEntry[] MergeUnion(InventorySaveEntry[] local, InventorySaveEntry[] remote)
        {
            var counts = new Dictionary<int, int>(local.Length + remote.Length);
            var ticks = new Dictionary<int, long>(local.Length + remote.Length);

            Accumulate(local, counts, ticks);
            Accumulate(remote, counts, ticks);

            var itemIds = new int[counts.Count];
            counts.Keys.CopyTo(itemIds, 0);
            Array.Sort(itemIds);

            var merged = new InventorySaveEntry[itemIds.Length];
            for (int i = 0; i < itemIds.Length; i++)
            {
                int itemId = itemIds[i];
                merged[i] = new InventorySaveEntry(itemId, counts[itemId], ticks[itemId]);
            }

            return merged;
        }

        /// <summary>把一档的条目并进累加表（同 id 取较大值；时间戳 0 = 未知，不会盖掉已知的更大值）。</summary>
        private static void Accumulate(InventorySaveEntry[] entries, Dictionary<int, int> counts, Dictionary<int, long> ticks)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                InventorySaveEntry entry = entries[i];

                int existingCount;
                if (!counts.TryGetValue(entry.ItemId, out existingCount) || entry.Count > existingCount)
                {
                    counts[entry.ItemId] = entry.Count;
                }

                long existingTicks;
                if (!ticks.TryGetValue(entry.ItemId, out existingTicks) || entry.UpdatedTicks > existingTicks)
                {
                    ticks[entry.ItemId] = entry.UpdatedTicks;
                }
            }
        }

        /// <summary>构造失败结果（失败时条目数恒为 0、备份 key 恒为空串）。</summary>
        private static SaveMigrationResult Failure(string targetSlotKey, string reason)
        {
            return new SaveMigrationResult(false, targetSlotKey, string.Empty, 0, reason);
        }

        /// <summary>
        /// 当前 UTC 时间的 Unix 秒（仅用于诊断盖章）；时间源违约或早于 1970 时返回 -1。
        /// <para>用 -1 而不是 0 作哨兵：0 是"1970-01-01"这个合法时刻，与"读不到时间"必须区分开。</para>
        /// </summary>
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

        /// <summary>槽位读取状态。<b>Missing 与 Corrupt 必须分开</b>（见 <see cref="LoadSlot"/>）。</summary>
        private enum SlotState : byte
        {
            /// <summary>键不存在。</summary>
            Missing = 0,

            /// <summary>存在且解出了合法包络（schema 已迁移到 <see cref="SaveService.CurrentVersion"/>）。</summary>
            Valid = 1,

            /// <summary>存在但读不出合法档（magic / 长度 / CRC / 版本 / 迁移失败）。</summary>
            Corrupt = 2
        }

        /// <summary>一个槽位的读取结果（原始字节 + 解出的包络 + 解出的条目），<b>只读</b>。</summary>
        private readonly struct LoadedSlot
        {
            internal LoadedSlot(
                SlotState state,
                in SaveEnvelope envelope,
                byte[] raw,
                InventorySaveEntry[] entries,
                bool decodable)
            {
                State = state;
                Envelope = envelope;
                Raw = raw;
                Entries = entries;
                Decodable = decodable;
            }

            /// <summary>读取状态。</summary>
            internal SlotState State { get; }

            /// <summary>解出的包络（<see cref="SlotState.Valid"/> 之外是占位空包络）。</summary>
            internal SaveEnvelope Envelope { get; }

            /// <summary>原始字节（备份用；<see cref="SlotState.Missing"/> 时为空数组）。</summary>
            internal byte[] Raw { get; }

            /// <summary>解出的背包条目（解不出时为空数组）。</summary>
            internal InventorySaveEntry[] Entries { get; }

            /// <summary>负载能否按背包格式解出（0 字节负载算能，解出 0 条）。</summary>
            internal bool Decodable { get; }

            /// <summary>条目数（解不出时按 0 计，只影响诊断与上限判定，不阻止整档复制）。</summary>
            internal int EntryCount
            {
                get { return Decodable ? Entries.Length : 0; }
            }
        }
    }
}

#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 存档合并策略：游客档要迁移进一个<b>已经有存档</b>的正式账号槽位时，冲突由谁说了算。
    /// <para>
    /// <b>为什么策略进契约而不是留给实现自己看着办</b>：同一份游客进度换一个装配入口（Console 假云 / Unity 真机 /
    /// 未来 HTTP 后端）必须得到<b>逐字一致</b>的裁决结果，否则会出现"命令行迁移对了、真机迁移把道具吞了"的
    /// 只在线上复现的差异。策略显式进契约后，<c>SaveMigrationService</c> 是被无头单测钉死的唯一实现。
    /// </para>
    /// <para>
    /// <b>为什么游客合并推荐 <see cref="UnionBest"/></b>：游客档与账号档是"同一进度的两个快照"，不是两笔交易；
    /// 整档二选一必然丢掉另一端这段时间攒下的道具。按 itemId 取较大值是唯一在没有权威服务端时
    /// 不会凭空刷道具、也尽量不丢进度的保守语义（与 <c>CloudSaveMerger</c> 的 MergeByKey 同一取舍）。
    /// </para>
    /// <para>数值顺序固定（会进日志/埋点/申诉工单），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum SaveMergePolicy : byte
    {
        /// <summary>冲突取本地：用<b>游客档</b>整档覆盖目标槽位（游客这台设备是"本地"，账号已存的那份是"远端"）。</summary>
        PreferLocal = 0,

        /// <summary>冲突取远端：保留<b>目标槽位</b>已有内容，游客档内容被丢弃（丢弃前应开 <c>keepGuestBackup</c>）。</summary>
        PreferRemote = 1,

        /// <summary>
        /// 按 updatedAt 时间戳取新（<b>相同取本地</b>）。时间戳只来自存档自身（条目 <c>updatedTicks</c> 的最大值），
        /// 绝不读本机时钟：换机场景下本机时钟完全不可信。
        /// </summary>
        NewerWins = 2,

        /// <summary>
        /// 资源/道具取较大值、进度取更远（推荐用于游客合并）：
        /// 同一 itemId 取 <c>count</c> 较大者、<c>updatedTicks</c> 取较大者，两侧 itemId 取并集，输出按 itemId 升序。
        /// </summary>
        UnionBest = 3
    }

    /// <summary>
    /// 一次存档迁移请求（<b>只读值类型</b>）：游客档槽位 → 正式账号档槽位。
    /// <para>
    /// <b>槽位键是逻辑名，不是落盘键</b>：实现按自己的前缀拼出物理 key（与 <c>SaveService.Key</c> 同一格式），
    /// 因此适配层换存储（文件系统 / <c>persistentDataPath</c> / 云端）时这里一个字都不用改。
    /// </para>
    /// <para>
    /// <b>字符串在读取期兜 <c>null</c></b>：<c>default(SaveMigrationRequest)</c> 会绕过构造器（字段为 <c>null</c>），
    /// getter 一律返回空串。读方永远不用判 <c>null</c>，只需判是否为空串（空串 = 非法槽位，实现必须拒绝且不触盘）。
    /// </para>
    /// </summary>
    public readonly struct SaveMigrationRequest
    {
        /// <summary>游客槽位键的归一化副本（读取期兜 <c>null</c>，见类型注释）。</summary>
        private readonly string? _guestSlotKey;

        /// <summary>目标槽位键的归一化副本（读取期兜 <c>null</c>，见类型注释）。</summary>
        private readonly string? _targetSlotKey;

        /// <summary>构造一次迁移请求。</summary>
        /// <param name="guestSlotKey">游客档槽位逻辑名（<c>null</c> → 空串；空串表示"非法/缺失"，实现必须拒绝）。</param>
        /// <param name="targetSlotKey">正式账号存档槽位逻辑名（<c>null</c> → 空串；必须与游客槽位不同，见下）。</param>
        /// <param name="policy">目标槽位已有存档时的冲突策略（非法枚举值由实现按 <see cref="SaveMergePolicy.PreferLocal"/> 处理）。</param>
        /// <param name="keepGuestBackup">
        /// 是否在成功后把<b>原游客档字节</b>复制到备份槽位。
        /// <para>
        /// <b>注意它不等于"保留游客槽位"</b>：无论开不开，成功迁移后游客槽位都会失效（否则同一请求重放会把同一份进度
        /// 反复合并/反复备份）。开它只是额外留一份可人工恢复的原档。
        /// </para>
        /// </param>
        public SaveMigrationRequest(string guestSlotKey, string targetSlotKey, SaveMergePolicy policy, bool keepGuestBackup)
        {
            _guestSlotKey = guestSlotKey;
            _targetSlotKey = targetSlotKey;
            Policy = policy;
            KeepGuestBackup = keepGuestBackup;
        }

        /// <summary>游客档槽位逻辑名（空串 = 非法/缺失；<b>永远不为 <c>null</c></b>）。</summary>
        public string GuestSlotKey
        {
            get { return _guestSlotKey ?? string.Empty; }
        }

        /// <summary>正式账号存档槽位逻辑名（空串 = 非法/缺失；<b>永远不为 <c>null</c></b>）。</summary>
        public string TargetSlotKey
        {
            get { return _targetSlotKey ?? string.Empty; }
        }

        /// <summary>冲突策略。</summary>
        public SaveMergePolicy Policy { get; }

        /// <summary>成功后是否额外留一份原游客档字节的备份（不改变"游客槽位迁移后失效"的语义）。</summary>
        public bool KeepGuestBackup { get; }
    }

    /// <summary>
    /// 一次存档迁移的结果（<b>只读值类型</b>）。
    /// <para>
    /// <b>失败时 <see cref="MergedEntries"/> 恒为 0、<see cref="BackupSlotKey"/> 恒为空串</b>：
    /// "失败却报告合并了 5 条"这种自相矛盾的状态会让调用方在日志里追一个根本不存在的成功。
    /// 这两条由构造器强制归一（不是"实现方记得传对"的君子协定）。
    /// </para>
    /// </summary>
    public readonly struct SaveMigrationResult
    {
        /// <summary>目标槽位键的归一化副本（读取期兜 <c>null</c>）。</summary>
        private readonly string? _targetSlotKey;

        /// <summary>备份槽位键的归一化副本（读取期兜 <c>null</c>）。</summary>
        private readonly string? _backupSlotKey;

        /// <summary>诊断原因的归一化副本（读取期兜 <c>null</c>）。</summary>
        private readonly string? _reason;

        /// <summary>构造一次迁移结果。</summary>
        /// <param name="success">是否成功（调用方唯一的业务判定依据）。</param>
        /// <param name="targetSlotKey">目标槽位逻辑名（失败时也回填，便于日志定位）。</param>
        /// <param name="backupSlotKey">
        /// 备份的<b>物理存储 key</b>（形如 <c>save.&lt;guestSlot&gt;.guest</c>）；未开备份或失败时为空串。
        /// <para>
        /// <b>单位注意</b>：本字段与 <see cref="SaveMigrationRequest.GuestSlotKey"/> 不同，它已经是落盘 key
        /// （可直接拿去找存储端要字节），因为备份恢复路径不经过本服务的槽位解析。
        /// </para>
        /// </param>
        /// <param name="mergedEntries">写入目标槽位的条目数（<b>失败时恒被归一为 0</b>；负值归一为 0）。</param>
        /// <param name="reason">人类可读失败原因（成功时为空串；<b>禁止作为业务判定依据</b>）。</param>
        public SaveMigrationResult(bool success, string targetSlotKey, string backupSlotKey, int mergedEntries, string reason)
        {
            Success = success;
            _targetSlotKey = targetSlotKey;

            // 失败路径归一化：没写成功就不存在"写进去几条"与"备份在哪"这两件事。
            _backupSlotKey = success ? backupSlotKey : null;
            MergedEntries = success ? (mergedEntries < 0 ? 0 : mergedEntries) : 0;
            _reason = reason;
        }

        /// <summary>是否成功（<b>业务判定只看它</b>）。</summary>
        public bool Success { get; }

        /// <summary>目标槽位逻辑名（失败时同样回填；<b>永远不为 <c>null</c></b>）。</summary>
        public string TargetSlotKey
        {
            get { return _targetSlotKey ?? string.Empty; }
        }

        /// <summary>备份的物理存储 key（未开备份/失败时为空串；<b>永远不为 <c>null</c></b>）。</summary>
        public string BackupSlotKey
        {
            get { return _backupSlotKey ?? string.Empty; }
        }

        /// <summary>写入目标槽位的条目数（失败时恒为 0）。</summary>
        public int MergedEntries { get; }

        /// <summary>失败原因（成功时为空串；禁止按文案分支）。</summary>
        public string Reason
        {
            get { return _reason ?? string.Empty; }
        }
    }

    /// <summary>
    /// 存档迁移端口：把游客档并入正式账号槽位（游客→正式账号合并、换机/重装后的设备迁移都走它）。
    /// <para>
    /// <b>为什么是同步签名</b>：契约必须能被"纯内存存储"和"本地文件"直接实现，这样合并/裁决/备份语义
    /// 才能在无头测试与断网真机上完整验收；真实云端的异步（下载、上传、重试）由适配层用"请求队列 + 回调"
    /// 包一层再喂进本端口（与 <see cref="ICloudSaveService"/>、<see cref="IAccountService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>实现必须遵守的硬约束</b>（这几条是数据安全，不是风格）：
    /// </para>
    /// <list type="number">
    /// <item>游客档不存在 → <c>success=false</c> 且<b>不改动任何槽位</b>（幂等：重复调用只会得到同一个失败）。</item>
    /// <item>目标档损坏（CRC / 版本不可读）→ <c>success=false</c>，<b>绝不覆盖坏档</b>（留给人工恢复）。</item>
    /// <item>写入失败 → <c>success=false</c> 且<b>保留游客档</b>（写失败不等于游客进度作废）。</item>
    /// <item>全同步、非线程安全：登录/启动路径单线程串行调用，不得并发迁移同一对槽位。</item>
    /// </list>
    /// </summary>
    public interface ISaveMigrationService
    {
        /// <summary>把游客档迁移到正式档槽位（正式档不存在则直接复制；存在则按 policy 合并）。</summary>
        /// <param name="request">迁移请求（<c>in</c> 传参，不复制结构体）。</param>
        /// <returns>
        /// 迁移结果（<b>永不为 <c>null</c></b>：结构体返回值）。
        /// <para>
        /// <b>所有可预期的失败都必须落成 <c>success=false</c> 结果而不是异常</b>：缺档、坏档、写失败、条目超限、非法参数
        /// 一律走返回值。登录路径上的调用方据此决定"稍后重试 / 引导人工恢复"，不允许因为一次存档问题把进程打断。
        /// </para>
        /// </returns>
        SaveMigrationResult MigrateGuestToAccount(in SaveMigrationRequest request);
    }
}

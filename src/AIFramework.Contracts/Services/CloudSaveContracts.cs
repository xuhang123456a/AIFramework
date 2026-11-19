#nullable enable
using System;

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 云存档冲突策略：本地与云端都改过同一个 key 时，由谁说了算。
    /// <para>
    /// <b>为什么把策略做成枚举而不是"让实现自己看着办"</b>：冲突解决是**产品决策**（单机向游戏可以
    /// "同刻本地优先"，联网竞技向必须"服务端权威"），不是网络实现的自由度。策略显式进契约后，
    /// 换任何云端实现（HTTP / 本地 mock / 编辑器假云）都不会悄悄改变结算语义。
    /// </para>
    /// </summary>
    public enum CloudSaveConflictPolicy : byte
    {
        /// <summary>本地优先：冲突时把本地内容推上去覆盖云端（单机/离线优先场景）。</summary>
        LocalWins = 0,

        /// <summary>远端优先：冲突时拉云端内容覆盖本地（多端同账号、服务端权威场景）。</summary>
        RemoteWins = 1,

        /// <summary>较新者优先：按 <see cref="CloudSaveEnvelope.UpdatedAtUnixSeconds"/> 比较，<b>同刻本地优先</b>。</summary>
        NewerWins = 2,

        /// <summary>按 key 合并：仅当负载是"可合并结构"（当前约定为背包编码）时才有效，否则回退 <see cref="NewerWins"/>。</summary>
        MergeByKey = 3
    }

    /// <summary>
    /// 一档云端存档的包络：逻辑键 + 原始负载 + 单调版本号 + 更新时间，<b>只读</b>。
    /// <para>
    /// <b>为什么是版本号 + 时间戳两个字段</b>：版本号判"是不是同一版"（可靠、可比较、由写入方单调递增），
    /// 时间戳判"谁更新"（跨设备唯一可用的先后依据）。只留其中一个都会出错：只有版本号时两台设备各自
    /// 递增会撞号；只有时间戳时客户端改系统时间就能覆盖云端。
    /// </para>
    /// <para>
    /// <b>payload 是引用而不是拷贝</b>：构造时不做防御性复制，避免每次同步都多一份大对象。
    /// 因此实现方在把内部台账的包络交给调用方时**必须自行复制 payload**（见 Console 实现），
    /// 否则外部一次改写就会污染云端台账。
    /// </para>
    /// </summary>
    public readonly struct CloudSaveEnvelope
    {
        /// <summary>构造一份云端存档包络。</summary>
        /// <param name="key">逻辑键（非空；是否合法由实现方按自身命名规则校验）。</param>
        /// <param name="payload">原始负载字节；<b>不复制</b>。</param>
        /// <param name="revision">单调版本号（同一 key 每次上传必须递增）。</param>
        /// <param name="updatedAtUnixSeconds">最后更新时间（Unix 秒，UTC）。</param>
        public CloudSaveEnvelope(string key, byte[] payload, long revision, long updatedAtUnixSeconds)
        {
            // 兜 null：包络是跨层数据，构造期就归一化，读取方永远不用再判 null。
            Key = key ?? string.Empty;
            Payload = payload ?? Array.Empty<byte>();
            Revision = revision;
            UpdatedAtUnixSeconds = updatedAtUnixSeconds;
        }

        /// <summary>逻辑键（云端按它分档；空串表示调用方给了非法/缺失的键）。</summary>
        public string Key { get; }

        /// <summary>原始负载字节（引用，构造时未复制）。</summary>
        public byte[] Payload { get; }

        /// <summary>单调版本号：同一 key 的每次成功上传都必须严格大于云端已有版本。</summary>
        public long Revision { get; }

        /// <summary>最后更新时间（Unix 秒，UTC；仅用于"谁更新"的比较，不参与版本号单调性）。</summary>
        public long UpdatedAtUnixSeconds { get; }
    }

    /// <summary>
    /// 一次云同步的六态结果。
    /// <para>
    /// <b>为什么不上传/下载结果只有 Uploaded/Downloaded 两态</b>：冲突被解决后必须能区分
    /// "是我覆盖了云端"还是"云端覆盖了我"——UI 要提示"已用本地存档覆盖云端"还是"已从云端恢复"，
    /// 出问题时也要能从日志里还原当时谁覆盖了谁。<c>Unavailable</c> 单独成态是为了让调用方
    /// 明确"本地内容原封未动，可以稍后重试"，而不是把它当成一次成功的同步。
    /// </para>
    /// </summary>
    public enum CloudSaveSyncResult : byte
    {
        /// <summary>云端原本没有该 key，已把本地内容上传。</summary>
        Uploaded = 0,

        /// <summary>从云端拉取并采用远端内容（无冲突分支下不会出现，专指"云端有、本地无"的对齐结果）。</summary>
        Downloaded = 1,

        /// <summary>双方已一致（revision 与 payload 均相同），未产生任何写入。</summary>
        InSync = 2,

        /// <summary>发生冲突，按策略最终采用本地内容（并已上传）。</summary>
        ConflictResolvedLocal = 3,

        /// <summary>发生冲突，按策略最终采用远端内容（本地内容被替换）。</summary>
        ConflictResolvedRemote = 4,

        /// <summary>云端不可用 / 上传被拒 / 参数非法：<b>本地内容一律不变</b>，可稍后重试。</summary>
        Unavailable = 5
    }

    /// <summary>
    /// 同步结果 + 同步后的权威版本号 + 人类可读原因。<b>只读结构体</b>：同步路径上不产生额外托管分配。
    /// <para>
    /// <see cref="Revision"/> 的语义是"同步完成后该 key 在云端的权威版本号"：
    /// 下载/同步时为远端版本，上传时为刚写入的版本（覆盖冲突时会递增，见 <c>CloudSaveMerger</c>）。
    /// </para>
    /// </summary>
    public readonly struct CloudSaveSyncOutcome
    {
        /// <summary>构造一次同步结果。</summary>
        /// <param name="result">六态结果。</param>
        /// <param name="revision">同步后的权威版本号（<see cref="CloudSaveSyncResult.Unavailable"/> 时无权威值，返回本地已知版本号，调用方不得据此推进本地版本）。</param>
        /// <param name="reason">人类可读原因（诊断/日志用，禁止作为业务判定依据；空串表示无附加说明）。</param>
        public CloudSaveSyncOutcome(CloudSaveSyncResult result, long revision, string reason)
        {
            Result = result;
            Revision = revision;

            // reason 只用于诊断，绝不允许因为它为 null 而抛异常。
            Reason = reason ?? string.Empty;
        }

        /// <summary>六态结果。</summary>
        public CloudSaveSyncResult Result { get; }

        /// <summary>同步后的权威版本号。</summary>
        public long Revision { get; }

        /// <summary>原因说明（诊断用，可能为空串）。</summary>
        public string Reason { get; }

        /// <summary>
        /// 是否"云端与本地已经对齐"：除 <see cref="CloudSaveSyncResult.Unavailable"/> 外都为 <c>true</c>
        /// （冲突被解决也算对齐，具体方向看 <see cref="Result"/>）。
        /// </summary>
        public bool IsSuccess
        {
            get { return Result != CloudSaveSyncResult.Unavailable; }
        }
    }

    /// <summary>云存档端口：实现方决定走 HTTP/WebSocket/本地 mock；框架只定义契约。</summary>
    /// <remarks>
    /// <b>为什么是同步签名</b>：契约必须能被"纯内存假云"和"本地文件模拟云"直接实现，
    /// 这样存档/冲突逻辑可以在无头测试与断网真机上完整验收；真正的 HTTP 由适配层用
    /// "请求队列 + 后台线程等待"包一层再喂进本端口。把 <c>Task</c> 写进契约会让纯逻辑层
    /// 被迫依赖异步调度，且无法在帧内确定性调用（与 <c>IMatchValidator</c> 同一取舍）。
    /// <para>
    /// <b>禁止抛异常</b>：<see cref="TryDownload"/> 用 <c>null</c> 表达"取不到"，
    /// <see cref="Upload"/> 用 <see cref="CloudSaveSyncResult.Unavailable"/> 表达"没写成功"。
    /// 存档流程不允许因为网络抖动把本地正在玩的进程打断。
    /// </para>
    /// <para>线程模型：由实现方声明；框架侧按单线程串行调用（存档/结算都在主线程）。</para>
    /// </remarks>
    public interface ICloudSaveService
    {
        /// <summary>云端当前是否可用（离线/未登录/未部署都必须如实返回 <c>false</c>，不得"假装成功"）。</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 拉取指定 key 的云端存档。
        /// <para>键不存在、云端不可用、读取失败一律返回 <c>null</c>（调用方按"云端无此档"处理），<b>禁止抛异常</b>。</para>
        /// </summary>
        /// <param name="key">逻辑键。</param>
        /// <returns>云端包络副本；取不到时为 <c>null</c>。</returns>
        CloudSaveEnvelope? TryDownload(string key);

        /// <summary>
        /// 上传一档存档。
        /// <para>实现方必须校验版本号单调性（同 key 新版本必须严格大于旧版本），不满足时返回
        /// <see cref="CloudSaveSyncResult.Unavailable"/> 并在 <see cref="CloudSaveSyncOutcome.Reason"/> 里说明原因。</para>
        /// </summary>
        /// <param name="envelope">待上传包络（<c>in</c> 传参，不复制结构体）。</param>
        /// <returns>上传结果；写入失败时为 <see cref="CloudSaveSyncResult.Unavailable"/>。</returns>
        CloudSaveSyncOutcome Upload(in CloudSaveEnvelope envelope);
    }
}

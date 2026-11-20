#nullable enable
using System;

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 云存档**传输层**错误码：把"一次 HTTP 往返出了什么事"压成 7 个互斥的字节常量。
    /// <para>
    /// <b>为什么传输层要和 <see cref="CloudSaveSyncResult"/> 分开</b>：后者描述的是<b>业务结局</b>
    /// （谁赢了、有没有写云端），本枚举描述的是<b>这一趟请求为什么没成</b>。两者混用会导致
    /// "重试一次就好"与"换 token 才好"在同一个返回值里不可区分——而这两件事的处置方式完全相反：
    /// <see cref="Network"/> 该退避重试，<see cref="Unauthorized"/> 重试一万次也没用。
    /// </para>
    /// <para>
    /// <b>重试语义由 Core 决定，不由传输实现决定</b>：适配层的 HTTP 实现只负责把
    /// <c>HttpRequestException</c> / <c>TaskCanceledException</c> / 401 / 409 / 5xx 如实映射到本枚举，
    /// 是否重试、退避多久一律由 <c>CloudSaveSyncClient</c> 统一裁决（见 <c>IsRetryable</c>）。
    /// 若让每个适配器自己决定重试，Console 假云与 Unity 真机就会漂移出"一个重试三次、一个只试一次"。
    /// </para>
    /// </summary>
    public enum CloudTransportError : byte
    {
        /// <summary>没有错误：下载时表示"取到了（payload 空 + version=0 则是云端无此档）"，上传时表示"已写入"。</summary>
        None = 0,

        /// <summary>网络不可达 / 连接被重置 / DNS 失败 / TLS 握手失败：<b>可重试</b>。</summary>
        Network = 1,

        /// <summary>请求超时（含客户端主动取消等待）：<b>可重试</b>。<b>注意</b>超时不等于"没写成功"，上传超时后重试必须靠版本号保证幂等。</summary>
        Timeout = 2,

        /// <summary>鉴权失败（401/403、token 过期、未登录）：<b>不可重试</b>，重试只会刷爆风控。</summary>
        Unauthorized = 3,

        /// <summary>版本冲突（服务端已有版本与请求里的 <see cref="CloudTransportRequest.VersionHint"/> 不一致，通常是 409）：<b>不可直接重试</b>，必须重新下载、重合并后再上传。</summary>
        Conflict = 4,

        /// <summary>服务端错误（5xx、网关错误、后端未部署）：<b>可重试</b>。</summary>
        ServerError = 5,

        /// <summary>请求/响应负载非法（序列化失败、字段缺失、服务端拒收 payload）：<b>不可重试</b>，同一份内容再发一次结果一样。</summary>
        InvalidPayload = 6
    }

    /// <summary>
    /// 一次上传请求：<b>槽位键 + 待写负载 + 客户端已知版本</b>。<b>只读结构体</b>，payload 是引用（构造时不复制）。
    /// <para>
    /// <b>为什么"已知版本"必须随请求一起走</b>：这是<b>乐观并发控制</b>的唯一依据。服务端只有在
    /// "你看到的版本 == 我现在的版本"时才允许写入，否则返回 <see cref="CloudTransportError.Conflict"/>。
    /// 少了它，两端同时改档时后写的一方会静默盖掉先写的一方——这正是云存档最典型的数据丢失事故。
    /// </para>
    /// <para>
    /// <b>为什么是 <c>long</c> 而不是 <c>int</c></b>：版本号单调递增且永不回绕，<c>int</c> 在长线运营里
    /// 是定时炸弹（与 <see cref="LeaderboardEntry.Score"/> 同一取舍）。
    /// </para>
    /// </summary>
    public readonly struct CloudTransportRequest
    {
        /// <summary>构造一次上传请求。</summary>
        /// <param name="slotKey">存档槽位键（<c>null</c> 归一化为空串；空串由同步客户端在上传前拦下，不会真的发出去）。</param>
        /// <param name="payload">待写负载字节；<b>不复制</b>（调用方在请求存活期内不得改写）。<c>null</c> 归一化为空数组。</param>
        /// <param name="versionHint">
        /// 客户端已知的云端版本；<b>0 = 新档</b>（云端应当还没有这个 key）。
        /// 负值按 0 归一化：版本号的定义域是非负整数，"负数版本"只可能来自未初始化的变量，
        /// 按"新档"处理会让服务端明确拒绝（Conflict）而不是让一次越界写入静默通过。
        /// </param>
        public CloudTransportRequest(string slotKey, byte[] payload, long versionHint)
        {
            // 跨层数据在构造期归一化：读取方永远不用再判 null。
            SlotKey = slotKey ?? string.Empty;
            Payload = payload ?? Array.Empty<byte>();
            VersionHint = versionHint < 0 ? 0 : versionHint;
        }

        /// <summary>存档槽位键（云端按它分档；空串 = 调用方给了非法键，同步客户端不会发出这样的请求）。</summary>
        public string SlotKey { get; }

        /// <summary>待写负载字节（引用，构造时未复制）。</summary>
        public byte[] Payload { get; }

        /// <summary>客户端已知版本；0 = 新档（见构造函数注释）。</summary>
        public long VersionHint { get; }
    }

    /// <summary>
    /// 一次传输往返的结果：<b>错误码 + 负载 + 版本 + 人读消息</b>。<b>只读结构体</b>，payload 是引用。
    /// <para>
    /// <b>为什么下载也用同一个结构体而不是 nullable 返回值</b>：云存档的下载有<b>三种</b>结局——
    /// "取到了新档"、"云端确实没有这个档"、"请求失败"。用 <c>null</c> 表达会把后两者合并，
    /// 于是"断网"会被当成"云端无档"，接着客户端就会拿本地内容去覆盖云端，
    /// 静默毁掉另一台设备刚写上去的进度。本结构体用一个 <see cref="Error"/> 把三者分开：
    /// <see cref="CloudTransportError.None"/> + 空 payload + version=0 = 云端无此档；
    /// 其它错误码 = 这一趟没成，<b>本地内容不得因此被覆盖</b>。
    /// </para>
    /// <para>
    /// <b>payload 是引用而不是拷贝</b>：与 <see cref="CloudSaveEnvelope"/> 同一取舍（避免每次同步多一份大对象）。
    /// 因此实现方把内部缓冲交出来时<b>必须自行复制</b>，否则调用方一次改写就污染了云端台账。
    /// </para>
    /// </summary>
    public readonly struct CloudTransportResponse
    {
        /// <summary>构造一次传输结果。</summary>
        /// <param name="error">错误码；<see cref="CloudTransportError.None"/> 表示这一趟成功了。</param>
        /// <param name="payload">
        /// 响应负载；<b>不复制</b>。<c>null</c> 归一化为空数组（<see cref="CloudTransportError.None"/> + 空数组 = 云端无此档）。
        /// </param>
        /// <param name="version">
        /// 云端版本：下载时为远端当前版本（0 = 无档），上传成功时为服务端<b>新分配</b>的版本。
        /// 负值按 0 归一化（版本号定义域是非负整数）。
        /// </param>
        /// <param name="message">人读消息（诊断/日志用，禁止作为业务判定依据；<c>null</c> 归一化为空串）。</param>
        public CloudTransportResponse(CloudTransportError error, byte[] payload, long version, string message)
        {
            Error = error;
            Payload = payload ?? Array.Empty<byte>();
            Version = version < 0 ? 0 : version;
            Message = message ?? string.Empty;
        }

        /// <summary>错误码（<see cref="CloudTransportError.None"/> = 成功）。</summary>
        public CloudTransportError Error { get; }

        /// <summary>响应负载（引用，构造时未复制；无档/失败时为空数组，<b>永不为 null</b>——除非实现方返回 <c>default</c>）。</summary>
        public byte[] Payload { get; }

        /// <summary>云端版本（0 = 无档或未分配；语义见构造函数注释）。</summary>
        public long Version { get; }

        /// <summary>人读消息（可能为空串；仅用于诊断，禁止参与判定）。</summary>
        public string Message { get; }
    }

    /// <summary>
    /// 云存档**传输端口**：只描述"一个槽位的下载/上传长什么样"，不描述 HTTP、不描述重试、不描述合并。
    /// <para>
    /// <b>为什么要独立于 <see cref="ICloudSaveService"/></b>：后者是<b>业务端口</b>（含合并策略、可用性、
    /// 人读原因），前者是<b>字节管道</b>。把两者拆开之后，"down → merge → up"这条流程可以在纯内存环境里
    /// 被完整验收，而真实 HTTP（超时、401、409、分片）只存在于适配层的一个类里——换传输实现不用改同步逻辑，
    /// 换同步逻辑也不用改传输实现。
    /// </para>
    /// <para>
    /// <b>为什么是同步签名</b>：与 <see cref="ICloudSaveService"/> 同一取舍——契约必须能被纯内存假云直接实现，
    /// 这样冲突/重试/退避全部可在无头测试里毫秒级复现。真正的异步 HTTP 由适配层用
    /// "后台线程发请求 + 主线程轮询结果"包一层再喂进本端口；把 <c>Task</c> 写进契约会让纯逻辑层被迫依赖异步调度。
    /// </para>
    /// <para>
    /// <b>禁止抛异常是本端口的目标而不是前提</b>：实现方应当用 <see cref="CloudTransportResponse.Error"/>
    /// 表达失败；但同步客户端<b>仍然</b>会把任何冒出来的异常归一成 <see cref="CloudTransportError.Network"/>
    /// （见 <c>CloudSaveSyncClient</c>），因此适配层即使直接让 <c>HttpRequestException</c> 穿透，
    /// 也不会把正在进行的存档流程打断。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：由实现方声明；框架侧按单线程串行调用（存档/结算都在主线程或同一个后台存档线程上）。
    /// </para>
    /// </summary>
    public interface ICloudTransport
    {
        /// <summary>
        /// 传输层当前是否可用（未登录 / 离线 / 后端未部署都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// <para>同步客户端在触网前先探活：不可用时不发任何请求，直接按 <see cref="CloudTransportError.Network"/> 失败。</para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>拉取云端档（无档返回 <c>error=None + payload 空 + version=0</c>）。</summary>
        /// <param name="slotKey">存档槽位键（非空；合法性由实现方按自身命名规则校验，非法时返回 <see cref="CloudTransportError.InvalidPayload"/>）。</param>
        /// <returns>传输结果（<b>永不为 <c>null</c></b>：结构体返回值）。</returns>
        CloudTransportResponse Download(string slotKey);

        /// <summary>上传；服务端版本与 <see cref="CloudTransportRequest.VersionHint"/> 不一致时返回 <see cref="CloudTransportError.Conflict"/>。</summary>
        /// <param name="request">上传请求（<c>in</c> 传参，不复制结构体）。</param>
        /// <returns>传输结果；成功时 <see cref="CloudTransportResponse.Version"/> 为服务端新分配的版本。</returns>
        CloudTransportResponse Upload(in CloudTransportRequest request);
    }
}

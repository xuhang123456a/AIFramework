#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 一局结算的服务端校验结果。<b>四态而不是 bool</b>：<c>Rejected</c>（作弊/数据非法）与
    /// <c>Unavailable</c> / <c>Timeout</c>（网络问题）必须分开——前者要封号/扣分，后者只能重试，
    /// 混成一个 false 会让"断网"被当成"作弊"处理。
    /// </summary>
    public enum NetworkValidationResult : byte
    {
        /// <summary>服务端认可本局数据。</summary>
        Accepted = 0,

        /// <summary>服务端明确拒绝（重放、重复提交、分数不合理等）。</summary>
        Rejected = 1,

        /// <summary>校验服务不可用（离线、未登录、服务端未部署）。可重试，不作为作弊证据。</summary>
        Unavailable = 2,

        /// <summary>请求超时。可重试。</summary>
        Timeout = 3
    }

    /// <summary>
    /// 一局的待校验摘要（客户端上报）。
    /// <para>
    /// <b>只放可被服务端独立复算的字段</b>：内核类型 + 关卡 + 随机种子 + 帧数就能让服务端重放整局；
    /// 结算指纹是"重放是否一致"的快速比对；分数则是最终上报值。刻意不放玩家昵称、道具列表这类
    /// 会随版本变化的结构，避免客户端与服务端为了对齐 schema 反复发版。
    /// </para>
    /// <para>只读结构体：上报路径上不产生堆分配，也不会在异步队列里被改脏。</para>
    /// </summary>
    public readonly struct MatchSummary
    {
        /// <summary>构造一份上报摘要。</summary>
        /// <param name="kernelType">内核类型（对应 <c>GameplayKernelType</c> 的数值）。</param>
        /// <param name="levelId">关卡 ID。</param>
        /// <param name="randomSeed">本局随机种子。</param>
        /// <param name="frameCount">本局推进的总帧数（回合制按结算步数）。</param>
        /// <param name="stateFingerprint">终局状态指纹（64 位折叠哈希），用于重放一致性比对。</param>
        /// <param name="score">客户端上报分数。</param>
        public MatchSummary(int kernelType, int levelId, int randomSeed, int frameCount, long stateFingerprint, int score)
        {
            KernelType = kernelType;
            LevelId = levelId;
            RandomSeed = randomSeed;
            FrameCount = frameCount;
            StateFingerprint = stateFingerprint;
            Score = score;
        }

        /// <summary>内核类型数值。</summary>
        public int KernelType { get; }

        /// <summary>关卡 ID。</summary>
        public int LevelId { get; }

        /// <summary>本局随机种子。</summary>
        public int RandomSeed { get; }

        /// <summary>本局推进的总帧数。</summary>
        public int FrameCount { get; }

        /// <summary>终局状态指纹。</summary>
        public long StateFingerprint { get; }

        /// <summary>客户端上报分数。</summary>
        public int Score { get; }
    }

    /// <summary>
    /// 校验响应。<c>ServerScore</c> 与 <c>Reason</c> 的存在意义：服务端可能在认可本局的同时给出
    /// 修正分数（如去掉异常加成的部分），客户端需要能读回权威值而不是直接采信本地分。
    /// </summary>
    public readonly struct ValidationResponse
    {
        /// <summary>构造一份校验响应。</summary>
        /// <param name="result">四态结果。</param>
        /// <param name="serverScore">服务端权威分数；不可用/超时时为 0。</param>
        /// <param name="reason">人类可读原因（空串表示无附加说明），仅用于日志与诊断，禁止作为业务判定依据。</param>
        public ValidationResponse(NetworkValidationResult result, int serverScore, string reason)
        {
            Result = result;
            ServerScore = serverScore;

            // 允许调用方传 null：Reason 是诊断字段，绝不允许为了它抛异常。
            Reason = reason ?? string.Empty;
        }

        /// <summary>四态结果。</summary>
        public NetworkValidationResult Result { get; }

        /// <summary>服务端权威分数。</summary>
        public int ServerScore { get; }

        /// <summary>原因说明（诊断用，可能为空串）。</summary>
        public string Reason { get; }

        /// <summary>是否被服务端认可（只表达 <see cref="NetworkValidationResult.Accepted"/>，不表达"能否重试"）。</summary>
        public bool IsAccepted
        {
            get { return Result == NetworkValidationResult.Accepted; }
        }
    }

    /// <summary>
    /// 服务端校验端口：框架只定义契约，具体走 HTTP/WebSocket/离线 mock。
    /// <para>
    /// <b>为什么是同步签名</b>：契约面要能被离线实现（纯内存台账、编辑器 mock）直接满足，
    /// 异步 HTTP 由装配层用"请求队列 + 后台线程/协程"包一层再喂进本端口；
    /// 反过来把 async 写进契约，会让纯逻辑层被迫依赖 <c>System.Threading.Tasks</c> 且无法在帧内确定性调用。
    /// </para>
    /// <para>实现方必须在网络不可用时返回 <see cref="NetworkValidationResult.Unavailable"/>，不得抛异常。</para>
    /// </summary>
    public interface IMatchValidator
    {
        /// <summary>校验一局摘要。</summary>
        /// <param name="summary">只读摘要（<c>in</c> 传参，不复制 48 字节结构体）。</param>
        /// <returns>四态校验结果。</returns>
        ValidationResponse Validate(in MatchSummary summary);
    }
}

#nullable enable
namespace AIFramework.Contracts.Testing
{
    /// <summary>
    /// 假网关一次往返的结局：把真实网络里<b>必须区分</b>、而本地仿真最容易糊成一团的四件事压成 4 个字节常量。
    /// <para>
    /// <b>为什么不是 <c>bool ok</c></b>：<see cref="Timeout"/>、<see cref="ServerError"/>、<see cref="Malformed"/>
    /// 三者在真实链路上的处置方式<b>完全相反</b>——超时后重试必须靠版本号保证幂等（请求可能已经写成功了），
    /// 5xx 是"服务端这会儿不行"（可退避重试），响应畸形是"同一份请求再发一次结果一样"（<b>不可重试</b>）。
    /// 合成一个 <c>false</c> 之后，这三种情况在调用方眼里变成同一件事，于是"畸形响应被无限重试"
    /// 这类事故只能等真机反馈才发现。
    /// </para>
    /// <para>
    /// <b>与 <c>CloudTransportError</c> 的关系</b>：那是真实传输端口的 7 态错误码（还含鉴权失败、版本冲突、
    /// 负载非法），这里是仿真网关的 4 态最小集，只覆盖"无头测试需要逐条钉死"的失败面。
    /// <b>刻意不补齐第五态</b>：需要更细语义时应当直接构造真实端口的结构体，
    /// 而不是把仿真层做成第二个错误码体系——两套枚举一旦漂移，测试通过就再也证明不了真机行为。
    /// </para>
    /// </summary>
    public enum MockTransportOutcome : byte
    {
        /// <summary>成功：<see cref="MockResponse.Error"/> 为空串，payload 原样返回（空 payload = "接口存在但这一次没有内容"）。</summary>
        Ok = 0,

        /// <summary>
        /// 超时：等价于"等不到响应"。<b>超时不等于没写成功</b>——服务端可能已经落库，只是响应没回来，
        /// 因此超时后的重试必须由版本号/幂等键保证不会写两遍。
        /// </summary>
        Timeout = 1,

        /// <summary>服务端错误（5xx / 网关错误 / 后端未部署）：可退避重试。</summary>
        ServerError = 2,

        /// <summary>
        /// 响应畸形（字段缺失、JSON 截断、编码错误、契约版本对不上）：<b>不可重试</b>，
        /// 同一份请求再发一次结果一样，重试只会把同一个错误刷成日志风暴。
        /// </summary>
        Malformed = 3
    }

    /// <summary>
    /// 一条仿真规则：<b>命中条件（operation + 请求体子串）→ 结局 + 负载 + 延迟读数</b>。<b>只读结构体</b>。
    /// <para>
    /// <b>为什么匹配条件是"方法名 + 请求体子串"而不是完整请求</b>：真实 HTTP 端口上的请求体里有时间戳、
    /// 版本号、随机 nonce 这类每次都变的部分，写死完整请求等于把测试焊在某一帧的数据上。
    /// 子串匹配让用例可以只钉住"这次上传带的是 slot=main"这类稳定特征，
    /// 而把易变字段留给被测算的客户端自己去填。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="RequestContains"/> 用 <c>null</c> 而不是空串表示"任意请求体"</b>：空串在
    /// <c>string.IndexOf</c> 语义下本身就是"任意字符串的子串"，两种写法会给出<b>同一结果却有两种含义</b>。
    /// 本结构体把 <c>null</c> 定为"通配"这一唯一显式写法，空串则保留它自然的"恒真子串"语义，
    /// 于是"我到底是想通配还是漏填了"在代码里一眼可辨。
    /// </para>
    /// <para>
    /// <b><see cref="DelayMs"/> 只是记录字段，网关不会真的 sleep</b>：见
    /// <c>MockGateway.Invoke</c> 的注释——无头测试的价值是毫秒级反馈，
    /// 一条 3 秒的仿真延迟会把一个 40 例的测试类拖成两分钟，而且延迟本身在纯内存环境里证明不了任何事。
    /// 延迟只作为"这条规则想模拟多久的往返"的元数据被记录与断言，
    /// 真正的退避/超时行为由客户端的调度器在自己的时钟上验收。
    /// </para>
    /// </summary>
    public readonly struct MockRule
    {
        /// <summary>规则命中后的负载（<c>null</c> 归一化为空串，见 <see cref="Payload"/>）。</summary>
        private readonly string? _payload;

        /// <summary>规则针对的 operation（<c>null</c> 归一化为空串，见 <see cref="Operation"/>）。</summary>
        private readonly string? _operation;

        /// <summary>构造一条仿真规则。</summary>
        /// <param name="operation">
        /// 目标 operation（方法名/路径，按 <c>StringComparison.Ordinal</c> 精确比较）。
        /// <c>null</c> 归一化为空串；<b>空 operation 的规则永远不会被命中</b>
        /// （空 operation 的调用一律按未命中处理，见 <c>MockGateway.Invoke</c>），注册它只会占一个 <c>RuleCount</c>。
        /// </param>
        /// <param name="requestContains">
        /// 请求体必须包含的子串；<c>null</c> = 匹配任意请求体。空串按"任意字符串的子串"自然成立（见类型注释）。
        /// </param>
        /// <param name="outcome">命中后的结局。</param>
        /// <param name="payload">命中后原样返回的负载（<c>null</c> 归一化为空串；<b>不复制、不校验</b>，仿真层不解析 JSON）。</param>
        /// <param name="delayMs">
        /// 期望的往返延迟（毫秒）；<b>仅记录，不真 sleep</b>。负值按 0 归一化
        /// （"负延迟"只可能来自未初始化的变量，按 0 处理比让调用方读到 -1 再去判方向更省事）。
        /// </param>
        public MockRule(string? operation, string? requestContains, MockTransportOutcome outcome, string? payload, int delayMs)
        {
            // 跨层数据在构造期归一化：读取方永远不用再判 null（与 CloudTransportRequest 同一取舍）。
            _operation = operation;
            RequestContains = requestContains;
            Outcome = outcome;
            _payload = payload;
            DelayMs = delayMs < 0 ? 0 : delayMs;
        }

        /// <summary>目标 operation（<b>永不为 null</b>；空串 = 这条规则不可能被命中）。</summary>
        public string Operation
        {
            get { return _operation ?? string.Empty; }
        }

        /// <summary>请求体必须包含的子串；<c>null</c> = 匹配任意请求体。</summary>
        public string? RequestContains { get; }

        /// <summary>命中后的结局。</summary>
        public MockTransportOutcome Outcome { get; }

        /// <summary>命中后原样返回的负载（<b>永不为 null</b>；引用，构造时未复制）。</summary>
        public string Payload
        {
            get { return _payload ?? string.Empty; }
        }

        /// <summary>期望的往返延迟（毫秒，<b>≥ 0</b>）；仅记录，网关不会 sleep。</summary>
        public int DelayMs { get; }
    }

    /// <summary>
    /// 一次网关调用的记录：<b>operation + 请求体 + 调用时刻 + 实际结局</b>。<b>只读结构体</b>。
    /// <para>
    /// <b>为什么连"未命中"也要记</b>：未命中往往才是真正的缺陷信号——客户端拼错了方法名、
    /// 请求体里漏了关键字段，都会表现为"规则没命中、拿到默认空响应、然后业务静默走偏"。
    /// 只记命中会把这条线索直接抹掉，测试只能看到"结果不对"却看不到"请求长什么样"。
    /// </para>
    /// <para>
    /// <b>为什么记的是"实际结局"而不是"规则声明的结局"</b>：未命中时没有规则可引用，
    /// 而调用方真正关心的是"这一次它收到了什么"。两者在命中时必然相同，未命中时前者是 <see cref="MockTransportOutcome.Ok"/>。
    /// </para>
    /// </summary>
    public readonly struct MockCallRecord
    {
        /// <summary>本次调用的 operation（<c>null</c> 归一化为空串）。</summary>
        private readonly string? _operation;

        /// <summary>本次调用的请求体（<c>null</c> 归一化为空串）。</summary>
        private readonly string? _request;

        /// <summary>构造一条调用记录。</summary>
        /// <param name="operation">本次调用的 operation（<c>null</c> 归一化为空串；空 operation 由网关改写成 <c>"(empty)"</c> 后再传入）。</param>
        /// <param name="request">本次调用的请求体原文（<c>null</c> 归一化为空串；<b>不截断</b>，便于断言完整请求）。</param>
        /// <param name="atUnixSeconds">调用时刻（Unix 秒，由 <c>ITimeProvider</c> 取得；负值原样保留，表示 1970 之前）。</param>
        /// <param name="outcome">本次调用实际拿到的结局。</param>
        public MockCallRecord(string? operation, string? request, long atUnixSeconds, MockTransportOutcome outcome)
        {
            _operation = operation;
            _request = request;
            AtUnixSeconds = atUnixSeconds;
            Outcome = outcome;
        }

        /// <summary>本次调用的 operation（<b>永不为 null</b>；空 operation 被改写为 <c>"(empty)"</c>）。</summary>
        public string Operation
        {
            get { return _operation ?? string.Empty; }
        }

        /// <summary>本次调用的请求体原文（<b>永不为 null</b>；引用，构造时未复制）。</summary>
        public string Request
        {
            get { return _request ?? string.Empty; }
        }

        /// <summary>调用时刻（Unix 秒；取自 <c>ITimeProvider.UtcNow</c>，<b>不是</b>系统时间）。</summary>
        public long AtUnixSeconds { get; }

        /// <summary>本次调用实际拿到的结局。</summary>
        public MockTransportOutcome Outcome { get; }
    }

    /// <summary>
    /// 一次网关调用的返回值：<b>结局 + 负载 + 错误文案</b>。<b>只读结构体</b>。
    /// <para>
    /// <b>为什么错误文案是固定字符串而不是异常</b>：假网关被用来验收"客户端拿到失败之后怎么处置"，
    /// 抛异常会把处置路径整个跳过（异常要么被兜底捕获归一成网络错误，要么直接中断测试）。
    /// 固定文案还让断言可以精确比对，不必依赖异常类型或消息本地化。
    /// </para>
    /// <para>
    /// <b>文案的唯一来源是 <see cref="ErrorText"/></b>：网关按它填 <see cref="Error"/>，
    /// 适配层把仿真结果翻译成真实端口结构体时也应当复用它，避免两处各写一份而漂移出
    /// "测试里是 timeout、真机上是 Timeout"这种无法 grep 的差异。
    /// </para>
    /// </summary>
    public readonly struct MockResponse
    {
        /// <summary><see cref="MockTransportOutcome.Timeout"/> 的固定错误文案。</summary>
        public const string TimeoutErrorText = "timeout";

        /// <summary><see cref="MockTransportOutcome.ServerError"/> 的固定错误文案。</summary>
        public const string ServerErrorErrorText = "server error";

        /// <summary><see cref="MockTransportOutcome.Malformed"/> 的固定错误文案。</summary>
        public const string MalformedErrorText = "malformed response";

        /// <summary>响应负载（<c>null</c> 归一化为空串）。</summary>
        private readonly string? _payload;

        /// <summary>错误文案（<c>null</c> 归一化为空串）。</summary>
        private readonly string? _error;

        /// <summary>构造一次仿真响应。</summary>
        /// <param name="outcome">结局。</param>
        /// <param name="payload">负载（<c>null</c> 归一化为空串；<b>不复制</b>）。</param>
        /// <param name="error">错误文案（<c>null</c> 归一化为空串；成功时应传空串，见 <see cref="ErrorText"/>）。</param>
        public MockResponse(MockTransportOutcome outcome, string? payload, string? error)
        {
            Outcome = outcome;
            _payload = payload;
            _error = error;
        }

        /// <summary>结局。</summary>
        public MockTransportOutcome Outcome { get; }

        /// <summary>负载（<b>永不为 null</b>；未命中规则时为空串）。</summary>
        public string Payload
        {
            get { return _payload ?? string.Empty; }
        }

        /// <summary>错误文案（<b>永不为 null</b>；<see cref="MockTransportOutcome.Ok"/> 时为空串）。</summary>
        public string Error
        {
            get { return _error ?? string.Empty; }
        }

        /// <summary>
        /// 结局对应的固定错误文案：<see cref="MockTransportOutcome.Ok"/> → 空串，
        /// <see cref="MockTransportOutcome.Timeout"/> → <see cref="TimeoutErrorText"/>，
        /// <see cref="MockTransportOutcome.ServerError"/> → <see cref="ServerErrorErrorText"/>，
        /// <see cref="MockTransportOutcome.Malformed"/> → <see cref="MalformedErrorText"/>。
        /// <para>
        /// <b>越界枚举值（如 <c>(MockTransportOutcome)9</c>）返回空串</b>：那只会来自一次错误的外部转换，
        /// 给它编一句"unknown outcome"反而会让日志里出现一条根本不存在的失败原因；
        /// 空串让这种脏数据在断言里立刻现形，而不是伪装成一次有意义的失败。
        /// </para>
        /// </summary>
        /// <param name="outcome">要翻译的结局。</param>
        /// <returns>固定文案（<b>永不为 null</b>）。</returns>
        public static string ErrorText(MockTransportOutcome outcome)
        {
            switch (outcome)
            {
                case MockTransportOutcome.Timeout:
                    return TimeoutErrorText;
                case MockTransportOutcome.ServerError:
                    return ServerErrorErrorText;
                case MockTransportOutcome.Malformed:
                    return MalformedErrorText;
                default:
                    // Ok 与越界值都落在这里：Ok 本来就该是空串，越界值见方法注释。
                    return string.Empty;
            }
        }
    }
}

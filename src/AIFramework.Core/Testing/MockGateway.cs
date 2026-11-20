#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Testing;

namespace AIFramework.Core.Testing
{
    /// <summary>
    /// 可编程的"服务端契约仿真"网关：按 <b>operation + 请求体子串</b> 注册响应，
    /// 按需注入延迟读数/失败结局，并记录每一次调用。
    /// <para>
    /// <b>为什么需要它，而不是让每个测试自己写一个假端口</b>：真实 HTTP 端口（<c>ICloudTransport</c>、
    /// <c>ILeaderboardServer</c>、社交/推送端口）在无头环境里不可达，而"接口存在但服务端返回 5xx"
    /// "响应体截断""请求超时但服务端其实写成功了"这些分支又恰恰是最容易出事、最难在真机复现的部分。
    /// 若每个测试各自 <c>new</c> 一个一次性假实现，注入能力就会漂移：一个能注入超时，另一个不能；
    /// 一个记录请求，另一个不记录。结果是同一个客户端在不同测试里被验成两种行为。
    /// 本类把这套能力收成一个可复用对象：<b>规则是数据，注入是注册，取证是调用记录</b>。
    /// </para>
    /// <para>
    /// <b>它是什么、不是什么</b>：它是<b>契约仿真</b>（按方法名返回一段预置负载），
    /// <b>不是</b>协议实现——不解析 JSON、不校验 schema、不模拟分页、不模拟重试与幂等。
    /// 这些语义属于被测算的客户端或真实适配层；仿真层一旦开始"懂业务"，
    /// 测试就会变成在验仿真层自己，而不是在验客户端。
    /// </para>
    /// <para>
    /// <b>匹配规则（唯一口径）</b>：
    /// <list type="number">
    /// <item><description>先按 operation 精确相等（<see cref="StringComparison.Ordinal"/>，大小写敏感）。</description></item>
    /// <item><description>再按 <see cref="MockRule.RequestContains"/>：<c>null</c> 匹配任意请求体；
    /// 否则请求体必须包含该子串（同样是 Ordinal，大小写敏感）。</description></item>
    /// <item><description>同一 operation 下<b>后注册优先</b>：从规则表尾部向前扫描，第一个命中即返回。
    /// 这让"先铺一条通配规则、再针对某个请求覆盖一条特例"成为自然写法，
    /// 而不必为了让特例优先去调整注册顺序或引入优先级字段。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>未命中 = <see cref="MockTransportOutcome.Ok"/> + 空负载，不是错误</b>：这模拟的是
    /// "接口存在，但这一次没有内容"（例如云端确实没有这个存档槽）。把它做成失败会让每个用例
    /// 都得先铺一条兜底规则，否则"没注册的那条路径"会莫名其妙地失败；而真正的缺陷信号
    /// （方法名拼错、请求体漏字段）由 <see cref="Calls"/> 与 <see cref="LastMatchedRuleIndex"/> 提供——
    /// 记录里能看到请求原文，未命中也能被断言出来，不需要靠"顺手报个错"来提示。
    /// </para>
    /// <para>
    /// <b><see cref="MockRule.DelayMs"/> 不真 sleep</b>：无头测试的价值是毫秒级反馈，一条 3 秒的仿真延迟
    /// 会把一个几十例的测试类拖成分钟级，而"等了 3 秒"在纯内存环境里证明不了任何事。
    /// 延迟只作为元数据随规则记录，供断言"这条规则打算模拟多久的往返"；
    /// 真正的退避/超时/取消行为应当由客户端的调度器在<b>它自己的</b> <see cref="ITimeProvider"/> 上验收
    /// （快进时钟，而不是让测试进程真的睡）。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步、非线程安全、无锁</b>。规则表与调用记录都是普通字段，
    /// 调用方必须串行使用（与 <c>LeaderboardPager</c>、<c>FriendService</c> 同一约定）。
    /// 刻意不加锁：本类只活在测试进程里，加锁会让"并发下到底发生了什么"变得不可断言，
    /// 而它要复现的恰恰是单线程串行调用下的服务端行为。
    /// </para>
    /// <para>
    /// <b>不做的事</b>：不缓存上一次响应（缓存会让"改完规则再调一次"看到旧值，多一份失效逻辑却零收益）；
    /// 不做通配符/正则匹配（子串足够表达"这次上传带的是 slot=main"，而正则会让测试失败原因变得难读）；
    /// 不做规则删除（<see cref="Reset"/> 是唯一清空入口，避免"删错一条导致后续用例静默走默认分支"）。
    /// </para>
    /// </summary>
    public sealed class MockGateway
    {
        /// <summary>调用记录容量上限（环形覆盖，见 <see cref="Calls"/>）。</summary>
        public const int MaxCallRecords = 256;

        /// <summary>
        /// 空 operation 在调用记录里的归一化标记（<c>"(empty)"</c>）。
        /// <para>
        /// 为什么要一个可见标记而不是记空串：空串在日志/断言里和"没打印出来"无法区分，
        /// 而"客户端调了一个没有名字的 operation"是一条必须被看见的缺陷线索。
        /// </para>
        /// </summary>
        public const string EmptyOperationMarker = "(empty)";

        /// <summary>Unix 纪元（只用它做"UTC 时刻 → Unix 秒"的换算，禁止直接读系统时间）。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>时间源（调用记录的时刻来源；构造后不可变）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>规则表（按注册顺序追加；匹配时从尾部向前扫描，实现"后注册优先"）。</summary>
        private readonly List<MockRule> _rules = new List<MockRule>();

        /// <summary>调用记录环形缓冲（容量固定 <see cref="MaxCallRecords"/>，写满后覆盖最旧的一条）。</summary>
        private readonly MockCallRecord[] _records = new MockCallRecord[MaxCallRecords];

        /// <summary>下一次写入的环形下标。</summary>
        private int _nextRecord;

        /// <summary>当前有效记录数（<b>≤</b> <see cref="MaxCallRecords"/>；不是总调用次数）。</summary>
        private int _recordCount;

        /// <summary>最近一次命中规则的下标（未命中 -1）。</summary>
        private int _lastMatchedRuleIndex = -1;

        /// <summary>创建仿真网关。</summary>
        /// <param name="time">
        /// 时间源（非 <c>null</c>）：调用记录的 <see cref="MockCallRecord.AtUnixSeconds"/> 一律取自它的
        /// <see cref="ITimeProvider.UtcNow"/>，<b>绝不读系统时间</b>——否则"这次调用发生在哪一秒"
        /// 会随机器时区/执行时刻漂移，时间断言永远写不稳。
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public MockGateway(ITimeProvider time)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>当前规则条数（含永远不会被命中的空 operation 规则与重复注册的规则）。</summary>
        public int RuleCount
        {
            get { return _rules.Count; }
        }

        /// <summary>
        /// 调用记录（<b>时间升序副本</b>，最旧在前）。
        /// <para>
        /// 时刻在<b>每次调用时</b>采样，记录按调用顺序追加，因此在时间源单调不减时，
        /// 调用顺序即时间升序；即使注入的时钟被拨回，顺序仍严格等于调用先后，
        /// 这样"下标 = 第几次调用"始终成立，断言不会因为时钟回调而错位。
        /// </para>
        /// <para>
        /// <b>返回的是防御性副本</b>（每次访问都新建数组）：调用方可以随意排序/改写而不会污染网关内部状态。
        /// 记录容量固定 <see cref="MaxCallRecords"/>，超出后最旧的一条被覆盖——
        /// 仿真网关只服务于单个用例，256 条足够覆盖任何真实场景，而无限增长会把长跑测试变成内存泄漏。
        /// </para>
        /// </summary>
        public MockCallRecord[] Calls
        {
            get
            {
                var copy = new MockCallRecord[_recordCount];
                int start = (_nextRecord - _recordCount + MaxCallRecords) % MaxCallRecords;
                for (int i = 0; i < _recordCount; i++)
                {
                    copy[i] = _records[(start + i) % MaxCallRecords];
                }

                return copy;
            }
        }

        /// <summary>
        /// 最近一次命中规则在规则表里的下标（未命中为 -1；尚未调用过也是 -1）。
        /// <para>
        /// 用它区分"命中了兜底规则"与"根本没命中"：只看返回值时两者都可能是 Ok + 空负载，
        /// 但前者说明规则按预期工作，后者说明客户端发的方法名/请求体与预期不符。
        /// </para>
        /// </summary>
        public int LastMatchedRuleIndex
        {
            get { return _lastMatchedRuleIndex; }
        }

        /// <summary>
        /// 注册一条规则（后注册优先）。
        /// <para>
        /// 允许重复注册同一个 operation：这正是"先铺通配、再覆盖特例"的用法，
        /// 也允许同一个 operation 注册多条 <see cref="MockRule.RequestContains"/> 不同的规则，
        /// 由请求体决定命中哪一条。
        /// </para>
        /// <para>
        /// <b>不校验、不抛异常</b>：<see cref="MockRule.Operation"/> 为空的规则会被照常收下并计入
        /// <see cref="RuleCount"/>，但永远不会被命中（空 operation 的调用一律按未命中处理）。
        /// 仿真层保持"哑管道"语义——注册期静默拒绝会让"我明明注册了却没生效"变成更难查的问题。
        /// </para>
        /// </summary>
        /// <param name="rule">规则（<c>in</c> 传参，不复制结构体）。</param>
        public void AddRule(in MockRule rule)
        {
            _rules.Add(rule);
        }

        /// <summary>
        /// 注册一条"成功 + 固定 JSON 负载"的规则（等价于
        /// <c>AddRule(new MockRule(operation, null, MockTransportOutcome.Ok, json, 0))</c>）。
        /// <para>
        /// <b>便利方法，不解析也不校验 json</b>：仿真层把负载当不透明字符串透传，
        /// 客户端解析失败才会在用例里现形——那正是要验的东西。
        /// </para>
        /// </summary>
        /// <param name="operation">目标 operation（<c>null</c> 归一化为空串，此时规则永不命中）。</param>
        /// <param name="json">响应负载原文（<c>null</c> 归一化为空串 = 成功但无内容）。</param>
        public void AddJsonRule(string? operation, string? json)
        {
            _rules.Add(new MockRule(operation, null, MockTransportOutcome.Ok, json, 0));
        }

        /// <summary>
        /// 注册一条"指定失败结局 + 空负载"的规则（等价于
        /// <c>AddRule(new MockRule(operation, null, outcome, string.Empty, 0))</c>）。
        /// <para>
        /// <b>传 <see cref="MockTransportOutcome.Ok"/> 是合法的</b>：那等价于一条"成功但无内容"的通配规则，
        /// 用来覆盖更早注册的规则（后注册优先）。这里刻意不拦——
        /// 让调用方用同一条代码路径表达"覆盖成成功"，比逼他换方法更不容易写错。
        /// </para>
        /// </summary>
        /// <param name="operation">目标 operation（<c>null</c> 归一化为空串，此时规则永不命中）。</param>
        /// <param name="outcome">失败结局（决定 <see cref="MockResponse.Error"/> 的固定文案）。</param>
        public void AddErrorRule(string? operation, MockTransportOutcome outcome)
        {
            _rules.Add(new MockRule(operation, null, outcome, string.Empty, 0));
        }

        /// <summary>
        /// 清空规则与调用记录（<see cref="LastMatchedRuleIndex"/> 一并回到 -1）。
        /// <para>
        /// 供同一个测试类在多个用例间复用网关实例：只清记录不清规则会留下"上一条用例的响应"，
        /// 只清规则不清记录会让"本次调了几次"永远算错，所以两者必须一起清。
        /// 时间源不受影响（它不是状态，是依赖）。
        /// </para>
        /// </summary>
        public void Reset()
        {
            _rules.Clear();
            Array.Clear(_records, 0, _records.Length);
            _nextRecord = 0;
            _recordCount = 0;
            _lastMatchedRuleIndex = -1;
        }

        /// <summary>
        /// 调用网关：按规则返回响应，并把本次调用写进记录。
        /// <para>
        /// <b>未命中规则 → <see cref="MockTransportOutcome.Ok"/> + 空负载 + 空错误</b>
        /// （模拟"接口存在但默认空响应"），<b>不是</b>错误；同时 <see cref="LastMatchedRuleIndex"/> 置 -1。
        /// </para>
        /// <para>
        /// <b>入参一律不抛</b>：<paramref name="operation"/> / <paramref name="request"/> 为 <c>null</c> 按空串处理；
        /// <paramref name="operation"/> 为空串时按未命中处理（<b>即使真的注册过一条空 operation 的规则</b>），
        /// 记录里改写成 <see cref="EmptyOperationMarker"/>。这条归一化让"调用方漏传方法名"表现为
        /// 一条可 grep 的 <c>(empty)</c> 记录，而不是一次 NullReferenceException 或一次意外的规则命中。
        /// </para>
        /// <para>
        /// <b>延迟不生效</b>：命中规则的 <see cref="MockRule.DelayMs"/> 只被记录、不被等待（见类型注释）。
        /// 因此本方法在纯内存里是常数时间，可以被循环调用几万次做压力/容量用例。
        /// </para>
        /// </summary>
        /// <param name="operation">方法名/路径（<c>null</c> 或空串按"空 operation"处理）。</param>
        /// <param name="request">请求体原文（<c>null</c> 归一化为空串；不解析）。</param>
        /// <returns>本次调用拿到的响应（结构体，<b>永不为 null</b>）。</returns>
        public MockResponse Invoke(string? operation, string? request)
        {
            string normalizedOperation = operation ?? string.Empty;
            string normalizedRequest = request ?? string.Empty;

            // 空 operation 一律未命中：它只可能来自调用方漏传方法名，让它"碰巧命中某条规则"
            // 会把一个拼写缺陷伪装成一次成功调用。
            int matchedIndex = normalizedOperation.Length == 0
                ? -1
                : FindRuleIndex(normalizedOperation, normalizedRequest);

            MockResponse response;
            if (matchedIndex < 0)
            {
                // 未命中 = "接口存在但这一次没有内容"：Ok + 空负载 + 空错误，而不是失败。
                response = new MockResponse(MockTransportOutcome.Ok, string.Empty, string.Empty);
            }
            else
            {
                MockRule rule = _rules[matchedIndex];
                response = new MockResponse(rule.Outcome, rule.Payload, MockResponse.ErrorText(rule.Outcome));
            }

            _lastMatchedRuleIndex = matchedIndex;
            Record(new MockCallRecord(
                normalizedOperation.Length == 0 ? EmptyOperationMarker : normalizedOperation,
                normalizedRequest,
                NowUnixSeconds(),
                response.Outcome));

            return response;
        }

        /// <summary>
        /// 从规则表尾部向前找第一条命中规则（实现"后注册优先"），未命中返回 -1。
        /// </summary>
        /// <param name="operation">已归一化的 operation（非空）。</param>
        /// <param name="request">已归一化的请求体（非 null）。</param>
        private int FindRuleIndex(string operation, string request)
        {
            for (int i = _rules.Count - 1; i >= 0; i--)
            {
                MockRule rule = _rules[i];
                if (!string.Equals(rule.Operation, operation, StringComparison.Ordinal))
                {
                    continue;
                }

                string? contains = rule.RequestContains;
                if (contains != null && request.IndexOf(contains, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                return i;
            }

            return -1;
        }

        /// <summary>把一条记录写进环形缓冲（写满后覆盖最旧的一条）。</summary>
        /// <param name="record">本次调用记录。</param>
        private void Record(in MockCallRecord record)
        {
            _records[_nextRecord] = record;
            _nextRecord = (_nextRecord + 1) % MaxCallRecords;
            if (_recordCount < MaxCallRecords)
            {
                _recordCount++;
            }
        }

        /// <summary>
        /// 当前 UTC 时刻换算成 Unix 秒（唯一的时间读取点）。
        /// <para>
        /// 用 <see cref="Math.Floor"/> 而不是直接截断：1970 年之前的时刻在截断下会朝 0 取整，
        /// 让"越早的时刻反而更大"，破坏时间升序；向下取整保证单调性对所有时刻都成立。
        /// </para>
        /// </summary>
        private long NowUnixSeconds()
        {
            return (long)Math.Floor((_time.UtcNow - UnixEpoch).TotalSeconds);
        }
    }
}

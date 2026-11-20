using System;
using System.Diagnostics;
using System.Globalization;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Testing;
using AIFramework.Core.Testing;
using Xunit;

namespace AIFramework.Core.Tests.Testing
{
    /// <summary>
    /// <see cref="MockGateway"/> 的行为契约验收：规则匹配（operation / 请求体子串 / 后注册优先 / 未命中）、
    /// 四种结局与固定错误文案、负载透传、调用记录（次数 / 顺序 / 时刻 / 容量覆盖）、
    /// <see cref="MockGateway.Reset"/> 与入参归一化。
    /// <para>
    /// <b>这些用例守的是"仿真层本身可信"</b>：T9-2 的全部价值在于——上层客户端（云存档同步、
    /// 排行榜分页、社交/推送）在无头环境里能复现真机上最难撞见的失败面（超时、5xx、响应畸形）。
    /// 如果仿真网关自己会静默丢规则、错报命中、或把未命中伪装成命中，那么"测试通过"
    /// 就只证明了这个假货自洽，而不能证明客户端的行为。因此断言分四类：
    /// 钉死匹配口径（含大小写与空 operation）、钉死文案（固定字符串，不可漂移）、
    /// 钉死取证（每次调用都留痕，含未命中）、钉死边界（环形容量、default 结构体、null 入参）。
    /// </para>
    /// <para>
    /// <b>替身刻意只用"可拨动的假时钟"</b>：<see cref="MockGateway"/> 是纯内存对象，
    /// 唯一的注入依赖就是 <see cref="ITimeProvider"/>；时间一律取自假时钟，
    /// 绝不依赖真实等待或系统时区（逻辑层禁止读系统时间）。
    /// </para>
    /// </summary>
    public sealed class MockGatewayTests
    {
        /// <summary>测试用固定 UTC 起点（2026-11-20T12:00:00Z）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary><see cref="StartUtc"/> 对应的 Unix 秒（时间断言必须对得上真实换算，不能用魔法数）。</summary>
        private const long StartUnixSeconds = 1795176000L;

        // ---------------------------------------------------------------- 构造与初始状态

        [Fact]
        public void Constructor_NullTimeProvider_Throws()
        {
            // 没有时钟就无法给调用记录打时间戳，而"悄悄用系统时间"会让时间断言随时区漂移。
            Assert.Throws<ArgumentNullException>(() => new MockGateway(null!));
        }

        [Fact]
        public void NewGateway_StartsEmpty()
        {
            var gateway = new MockGateway(new FakeTime());

            Assert.Equal(0, gateway.RuleCount);
            Assert.Empty(gateway.Calls);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        // ---------------------------------------------------------------- 规则匹配

        [Fact]
        public void Invoke_WithoutRules_ReturnsOkWithEmptyPayloadAndError()
        {
            var gateway = new MockGateway(new FakeTime());

            MockResponse response = gateway.Invoke("cloud.upload", "{\"slot\":\"main\"}");

            // 未命中不是错误：模拟"接口存在，但这一次没有内容"（例如云端确实没这个槽位）。
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Empty(response.Payload);
            Assert.Empty(response.Error);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
            Assert.Single(gateway.Calls);
        }

        [Fact]
        public void Invoke_MatchesByOperation_WhenRequestContainsIsNull()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.download", null, MockTransportOutcome.Ok, "{\"v\":7}", 0));

            MockResponse response = gateway.Invoke("cloud.download", "anything at all");

            Assert.Equal("{\"v\":7}", response.Payload);
            Assert.Equal(0, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Invoke_MatchesByOperationAndSubstring()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", "\"slot\":\"main\"", MockTransportOutcome.Ok, "main-ok", 0));
            gateway.AddRule(new MockRule("cloud.upload", "\"slot\":\"backup\"", MockTransportOutcome.Ok, "backup-ok", 0));

            // 同一个 operation 靠请求体里的稳定特征分流：真机上时间戳/nonce 每次都变，只有槽位是稳定的。
            Assert.Equal("main-ok", gateway.Invoke("cloud.upload", "{\"slot\":\"main\"}").Payload);
            Assert.Equal("backup-ok", gateway.Invoke("cloud.upload", "{\"slot\":\"backup\"}").Payload);
        }

        [Fact]
        public void Invoke_RequestWithoutSubstring_FallsThroughToWildcardRule()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", null, MockTransportOutcome.Ok, "wildcard", 0));
            gateway.AddRule(new MockRule("cloud.upload", "\"slot\":\"main\"", MockTransportOutcome.Ok, "main", 0));

            // 特例后注册所以优先；请求体不含特例特征时回落到更早注册的通配规则，而不是"未命中"。
            Assert.Equal("main", gateway.Invoke("cloud.upload", "{\"slot\":\"main\"}").Payload);
            Assert.Equal(1, gateway.LastMatchedRuleIndex);

            Assert.Equal("wildcard", gateway.Invoke("cloud.upload", "{\"slot\":\"other\"}").Payload);
            Assert.Equal(0, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Invoke_SameOperation_LaterRegistrationWins()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("leaderboard.fetch", null, MockTransportOutcome.Ok, "first", 0));
            gateway.AddRule(new MockRule("leaderboard.fetch", null, MockTransportOutcome.ServerError, "second", 0));

            MockResponse response = gateway.Invoke("leaderboard.fetch", string.Empty);

            // 后注册优先让"改一条规则的结局"不必清空整张表（Reset 之外没有删除入口）。
            Assert.Equal(MockTransportOutcome.ServerError, response.Outcome);
            Assert.Equal("second", response.Payload);
            Assert.Equal(1, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Invoke_DifferentOperations_AreIsolated()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("social.getFriends", null, MockTransportOutcome.Ok, "friends", 0));
            gateway.AddRule(new MockRule("push.register", null, MockTransportOutcome.Timeout, "push", 0));

            Assert.Equal("friends", gateway.Invoke("social.getFriends", string.Empty).Payload);
            Assert.Equal(MockTransportOutcome.Timeout, gateway.Invoke("push.register", string.Empty).Outcome);

            // 没注册的 operation 不会被别的 operation 的规则串味。
            MockResponse unknown = gateway.Invoke("iap.purchase", string.Empty);
            Assert.Equal(MockTransportOutcome.Ok, unknown.Outcome);
            Assert.Empty(unknown.Payload);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Invoke_OperationComparison_IsOrdinalCaseSensitive()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("Cloud.Download", null, MockTransportOutcome.Ok, "hit", 0));

            // 大小写敏感：HTTP 路径/方法名在服务端是精确匹配的，仿真层若做忽略大小写，
            // 会让客户端"拼错大小写"的缺陷在测试里永远看不见。
            Assert.Equal("hit", gateway.Invoke("Cloud.Download", string.Empty).Payload);

            MockResponse response = gateway.Invoke("cloud.download", string.Empty);
            Assert.Empty(response.Payload);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Invoke_RequestContainsComparison_IsOrdinalCaseSensitive()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", "MAIN", MockTransportOutcome.Ok, "hit", 0));

            Assert.Equal("hit", gateway.Invoke("cloud.upload", "slot=MAIN").Payload);
            Assert.Empty(gateway.Invoke("cloud.upload", "slot=main").Payload);
        }

        [Fact]
        public void Invoke_EmptyRequestContains_MatchesAnyRequest()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.download", string.Empty, MockTransportOutcome.Ok, "hit", 0));

            // 空串按 Ordinal 语义是任意字符串的子串，因此等价于通配；显式表达"通配"仍应写 null。
            Assert.Equal("hit", gateway.Invoke("cloud.download", string.Empty).Payload);
            Assert.Equal("hit", gateway.Invoke("cloud.download", "{\"slot\":\"main\"}").Payload);
        }

        // ---------------------------------------------------------------- LastMatchedRuleIndex

        [Fact]
        public void LastMatchedRuleIndex_ReportsRegistrationIndex()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "0", 0));
            gateway.AddRule(new MockRule("b", null, MockTransportOutcome.Ok, "1", 0));
            gateway.AddRule(new MockRule("c", null, MockTransportOutcome.Ok, "2", 0));

            gateway.Invoke("c", string.Empty);

            // 下标是"注册顺序"而不是"第几次命中"：断言据此定位到底是哪条规则起了作用。
            Assert.Equal(2, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void LastMatchedRuleIndex_IsMinusOne_WhenNoRuleMatched()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "0", 0));

            gateway.Invoke("missing", string.Empty);

            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void LastMatchedRuleIndex_ReflectsLatestCall()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "0", 0));

            gateway.Invoke("a", string.Empty);
            Assert.Equal(0, gateway.LastMatchedRuleIndex);

            // 一次未命中必须把上一次的命中痕迹清掉，否则"上一次命中了"会被误读成本次命中。
            gateway.Invoke("b", string.Empty);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        // ---------------------------------------------------------------- 四种结局

        [Fact]
        public void Invoke_TimeoutRule_ReturnsTimeoutOutcome()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", null, MockTransportOutcome.Timeout, string.Empty, 0));

            MockResponse response = gateway.Invoke("cloud.upload", string.Empty);

            Assert.Equal(MockTransportOutcome.Timeout, response.Outcome);
            Assert.Equal("timeout", response.Error);
            Assert.Empty(response.Payload);
        }

        [Fact]
        public void Invoke_ServerErrorRule_ReturnsServerErrorOutcome()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", null, MockTransportOutcome.ServerError, string.Empty, 0));

            MockResponse response = gateway.Invoke("cloud.upload", string.Empty);

            Assert.Equal(MockTransportOutcome.ServerError, response.Outcome);
            Assert.Equal("server error", response.Error);
        }

        [Fact]
        public void Invoke_MalformedRule_ReturnsMalformedOutcome()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.download", null, MockTransportOutcome.Malformed, "{trunc", 0));

            MockResponse response = gateway.Invoke("cloud.download", string.Empty);

            Assert.Equal(MockTransportOutcome.Malformed, response.Outcome);
            Assert.Equal("malformed response", response.Error);
            // 畸形响应仍然透传负载：客户端"先解析、再判错误码"的顺序才可能被验出来。
            Assert.Equal("{trunc", response.Payload);
        }

        [Fact]
        public void Invoke_OkRule_KeepsErrorEmpty()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "payload", 0));

            Assert.Empty(gateway.Invoke("a", string.Empty).Error);
        }

        [Theory]
        [InlineData(MockTransportOutcome.Ok, "")]
        [InlineData(MockTransportOutcome.Timeout, "timeout")]
        [InlineData(MockTransportOutcome.ServerError, "server error")]
        [InlineData(MockTransportOutcome.Malformed, "malformed response")]
        public void Invoke_ErrorText_IsFixedPerOutcome(MockTransportOutcome outcome, string expectedError)
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("op", null, outcome, string.Empty, 0));

            // 文案固定是契约的一部分：客户端/日志/告警可能按它做 grep，措辞漂移等于埋雷。
            Assert.Equal(expectedError, gateway.Invoke("op", string.Empty).Error);
        }

        [Theory]
        [InlineData(MockTransportOutcome.Ok)]
        [InlineData(MockTransportOutcome.Timeout)]
        [InlineData(MockTransportOutcome.ServerError)]
        [InlineData(MockTransportOutcome.Malformed)]
        public void Invoke_PayloadIsPassedThroughVerbatim(MockTransportOutcome outcome)
        {
            const string Payload = "{\"rows\":[],\"total\":0}";
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("op", null, outcome, Payload, 0));

            // 仿真层把负载当不透明字符串：错误结局也照样透传，是否读取由客户端自己决定。
            Assert.Equal(Payload, gateway.Invoke("op", string.Empty).Payload);
        }

        // ---------------------------------------------------------------- 调用记录

        [Fact]
        public void Calls_RecordEveryInvoke_IncludingMisses()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("hit", null, MockTransportOutcome.Ok, "p", 0));

            gateway.Invoke("hit", string.Empty);
            gateway.Invoke("miss", string.Empty);
            gateway.Invoke("hit", string.Empty);

            // 未命中也要留痕：拼错方法名/漏字段正是靠"请求原文 + 未命中"这条线索定位的。
            Assert.Equal(3, gateway.Calls.Length);
            Assert.Equal(MockTransportOutcome.Ok, gateway.Calls[1].Outcome);
        }

        [Fact]
        public void Calls_PreserveCallOrder()
        {
            var gateway = new MockGateway(new FakeTime());

            gateway.Invoke("first", string.Empty);
            gateway.Invoke("second", string.Empty);
            gateway.Invoke("third", string.Empty);

            MockCallRecord[] calls = gateway.Calls;
            Assert.Equal("first", calls[0].Operation);
            Assert.Equal("second", calls[1].Operation);
            Assert.Equal("third", calls[2].Operation);
        }

        [Fact]
        public void Calls_UseTimeProviderUtcNow()
        {
            var gateway = new MockGateway(new FakeTime());

            gateway.Invoke("a", string.Empty);

            // 时刻必须来自注入的时钟：读系统时间会让这条断言随时区/执行时刻漂移。
            Assert.Equal(StartUnixSeconds, gateway.Calls[0].AtUnixSeconds);
        }

        [Fact]
        public void Calls_TrackClockAdvance()
        {
            var time = new FakeTime();
            var gateway = new MockGateway(time);

            gateway.Invoke("a", string.Empty);
            time.AdvanceSeconds(90);
            gateway.Invoke("a", string.Empty);

            MockCallRecord[] calls = gateway.Calls;
            Assert.Equal(StartUnixSeconds, calls[0].AtUnixSeconds);
            Assert.Equal(StartUnixSeconds + 90, calls[1].AtUnixSeconds);
            Assert.True(calls[1].AtUnixSeconds > calls[0].AtUnixSeconds);
        }

        [Fact]
        public void Calls_RecordOperationRequestAndOutcome()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("cloud.upload", null, MockTransportOutcome.ServerError, string.Empty, 0));

            gateway.Invoke("cloud.upload", "{\"slot\":\"main\",\"v\":3}");

            MockCallRecord call = gateway.Calls[0];
            Assert.Equal("cloud.upload", call.Operation);
            // 请求体按原文记录（不截断、不改写）：否则"请求里到底带没带这个字段"无从断言。
            Assert.Equal("{\"slot\":\"main\",\"v\":3}", call.Request);
            Assert.Equal(MockTransportOutcome.ServerError, call.Outcome);
        }

        [Fact]
        public void Calls_RecordEmptyOperationMarker()
        {
            var gateway = new MockGateway(new FakeTime());

            gateway.Invoke(string.Empty, "body");

            // 空串在日志里和"没打印出来"无法区分，因此改写成可见标记。
            Assert.Equal("(empty)", gateway.Calls[0].Operation);
            Assert.Equal("(empty)", MockGateway.EmptyOperationMarker);
        }

        [Fact]
        public void Calls_Capacity_OverwritesOldestRecord()
        {
            var gateway = new MockGateway(new FakeTime());
            for (int i = 0; i < MockGateway.MaxCallRecords + 1; i++)
            {
                gateway.Invoke("op" + i.ToString(CultureInfo.InvariantCulture), string.Empty);
            }

            MockCallRecord[] calls = gateway.Calls;

            // 容量固定 256：超出后覆盖最旧的一条，长跑测试不会变成内存泄漏。
            Assert.Equal(MockGateway.MaxCallRecords, calls.Length);
            Assert.Equal("op1", calls[0].Operation);
            Assert.Equal("op256", calls[calls.Length - 1].Operation);
        }

        [Fact]
        public void Calls_Capacity_KeepsAllAtBoundary()
        {
            var gateway = new MockGateway(new FakeTime());
            for (int i = 0; i < MockGateway.MaxCallRecords; i++)
            {
                gateway.Invoke("op" + i.ToString(CultureInfo.InvariantCulture), string.Empty);
            }

            MockCallRecord[] calls = gateway.Calls;
            Assert.Equal(MockGateway.MaxCallRecords, calls.Length);
            // 恰好写满时不应丢任何一条（环形下标此时回到 0，最易写错的地方）。
            Assert.Equal("op0", calls[0].Operation);
            Assert.Equal("op255", calls[calls.Length - 1].Operation);
        }

        [Fact]
        public void Calls_ReturnDefensiveCopy()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.Invoke("a", string.Empty);

            MockCallRecord[] snapshot = gateway.Calls;
            snapshot[0] = new MockCallRecord("hacked", "hacked", 0L, MockTransportOutcome.Malformed);

            // 副本语义：调用方随意改写不得污染网关内部台账（否则一条断言会污染后续用例）。
            Assert.Equal("a", gateway.Calls[0].Operation);
            Assert.NotSame(snapshot, gateway.Calls);
        }

        // ---------------------------------------------------------------- Reset

        [Fact]
        public void Reset_ClearsRulesAndCalls()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.ServerError, "p", 0));
            gateway.Invoke("a", string.Empty);

            gateway.Reset();

            Assert.Equal(0, gateway.RuleCount);
            Assert.Empty(gateway.Calls);

            // 规则真的没了：同名 operation 回到"未命中 = Ok + 空负载"。
            MockResponse response = gateway.Invoke("a", string.Empty);
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Empty(response.Payload);
        }

        [Fact]
        public void Reset_ResetsLastMatchedRuleIndex()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "p", 0));
            gateway.Invoke("a", string.Empty);

            gateway.Reset();

            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        [Fact]
        public void Reset_KeepsGatewayReusable()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.Reset();

            // 时钟是依赖而不是状态：Reset 不得把它弄丢，否则重置后的第一次调用就无法记录时刻。
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Timeout, string.Empty, 0));

            Assert.Equal(MockTransportOutcome.Timeout, gateway.Invoke("a", string.Empty).Outcome);
            Assert.Single(gateway.Calls);
            Assert.Equal(1, gateway.RuleCount);
            Assert.Equal(StartUnixSeconds, gateway.Calls[0].AtUnixSeconds);
        }

        // ---------------------------------------------------------------- 入参归一化

        [Fact]
        public void Invoke_NullArguments_DoNotThrow_AndNormalizeToEmpty()
        {
            var gateway = new MockGateway(new FakeTime());

            MockResponse response = gateway.Invoke(null, null);

            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            MockCallRecord call = gateway.Calls[0];
            Assert.Equal("(empty)", call.Operation);
            Assert.Empty(call.Request);
        }

        [Fact]
        public void Invoke_EmptyOperation_NeverMatches_EvenWhenRuleRegistered()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule(string.Empty, null, MockTransportOutcome.ServerError, "never", 0));

            MockResponse response = gateway.Invoke(string.Empty, string.Empty);

            // 空 operation 只可能来自"漏传方法名"：让它命中一条空 operation 规则，
            // 会把一个拼写缺陷伪装成一次成功调用。规则照常计数，但永远不生效。
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Empty(response.Payload);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
            Assert.Equal(1, gateway.RuleCount);
        }

        [Fact]
        public void Invoke_WhitespaceOperation_IsARealKey()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(new MockRule(" ", null, MockTransportOutcome.Ok, "space", 0));

            // 只把"空串"当空 operation：一个空格是合法（虽然难看）的方法名，不该被静默归一化。
            Assert.Equal("space", gateway.Invoke(" ", string.Empty).Payload);
            Assert.Equal(" ", gateway.Calls[0].Operation);
        }

        [Fact]
        public void AddRule_DefaultStruct_IsCountedButNeverMatches()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddRule(default(MockRule));

            // default(MockRule) 的 operation 是空串：照常计数，但不可能被命中（不抛异常）。
            Assert.Equal(1, gateway.RuleCount);
            Assert.Equal(MockTransportOutcome.Ok, gateway.Invoke(string.Empty, string.Empty).Outcome);
            Assert.Equal(-1, gateway.LastMatchedRuleIndex);
        }

        // ---------------------------------------------------------------- RuleCount 与便利方法

        [Fact]
        public void RuleCount_TracksRegistrations()
        {
            var gateway = new MockGateway(new FakeTime());
            Assert.Equal(0, gateway.RuleCount);

            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "p", 0));
            gateway.AddRule(new MockRule("a", null, MockTransportOutcome.Ok, "q", 0));

            // 重复注册同一个 operation 也算两条：后注册优先正是靠"两条都在表里"成立的。
            Assert.Equal(2, gateway.RuleCount);
        }

        [Fact]
        public void AddJsonRule_RegistersOkRuleWithPayload()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddJsonRule("leaderboard.fetch", "{\"total\":3}");

            MockResponse response = gateway.Invoke("leaderboard.fetch", "any");

            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Equal("{\"total\":3}", response.Payload);
            Assert.Empty(response.Error);
            Assert.Equal(1, gateway.RuleCount);
        }

        [Fact]
        public void AddJsonRule_LaterRegistrationWins()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddJsonRule("op", "old");
            gateway.AddJsonRule("op", "new");

            Assert.Equal("new", gateway.Invoke("op", string.Empty).Payload);
        }

        [Fact]
        public void AddJsonRule_NullJson_BecomesEmptyPayload()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddJsonRule("op", null);

            MockResponse response = gateway.Invoke("op", string.Empty);
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Empty(response.Payload);
        }

        [Fact]
        public void AddErrorRule_RegistersFixedErrorOutcome()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddErrorRule("push.register", MockTransportOutcome.Malformed);

            MockResponse response = gateway.Invoke("push.register", string.Empty);

            Assert.Equal(MockTransportOutcome.Malformed, response.Outcome);
            Assert.Equal("malformed response", response.Error);
            Assert.Empty(response.Payload);
        }

        [Fact]
        public void AddErrorRule_OverridesEarlierJsonRule()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddJsonRule("op", "ok");
            gateway.AddErrorRule("op", MockTransportOutcome.Timeout);

            MockResponse response = gateway.Invoke("op", string.Empty);
            Assert.Equal(MockTransportOutcome.Timeout, response.Outcome);
            Assert.Empty(response.Payload);
        }

        [Fact]
        public void AddErrorRule_WithOkOutcome_ClearsEarlierError()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddErrorRule("op", MockTransportOutcome.ServerError);
            gateway.AddErrorRule("op", MockTransportOutcome.Ok);

            // 传 Ok 是合法的：等价于"覆盖成成功但无内容"，比逼调用方换方法更不容易写错。
            MockResponse response = gateway.Invoke("op", string.Empty);
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.Empty(response.Error);
        }

        [Fact]
        public void AddErrorRule_NullOperation_IsStoredButNeverMatches()
        {
            var gateway = new MockGateway(new FakeTime());
            gateway.AddErrorRule(null, MockTransportOutcome.ServerError);

            Assert.Equal(1, gateway.RuleCount);
            Assert.Equal(MockTransportOutcome.Ok, gateway.Invoke(null, string.Empty).Outcome);
        }

        // ---------------------------------------------------------------- 延迟只是记录字段

        [Fact]
        public void MockRule_NegativeDelay_IsNormalizedToZero()
        {
            // 负延迟只可能来自未初始化变量：按 0 处理，避免调用方读到 -1 再去判方向。
            var rule = new MockRule("op", null, MockTransportOutcome.Ok, "p", -5);

            Assert.Equal(0, rule.DelayMs);
        }

        [Fact]
        public void Invoke_DoesNotSleep_OnLargeDeclaredDelay()
        {
            var time = new FakeTime();
            var gateway = new MockGateway(time);
            gateway.AddRule(new MockRule("op", null, MockTransportOutcome.Ok, "p", 60000));

            var stopwatch = Stopwatch.StartNew();
            MockResponse response = gateway.Invoke("op", string.Empty);
            stopwatch.Stop();

            // 60 秒的声明延迟必须立即返回（否则一个几十例的测试类会被拖成分钟级），
            // 且不得推进注入时钟——延迟是元数据，等待由客户端的调度器在自己的时钟上验收。
            Assert.Equal(MockTransportOutcome.Ok, response.Outcome);
            Assert.True(stopwatch.ElapsedMilliseconds < 2000L, "仿真延迟不得真 sleep，实测 " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            Assert.Equal(StartUnixSeconds, gateway.Calls[0].AtUnixSeconds);
            Assert.Equal(StartUnixSeconds, ToUnixSeconds(time.UtcNow));
        }

        // ---------------------------------------------------------------- 结构体的归一化契约

        [Fact]
        public void MockRule_NullFields_AreNormalized()
        {
            var rule = new MockRule(null, null, MockTransportOutcome.Ok, null, 0);

            // 跨层数据在构造期归一化：读取方永远不用再判 null。
            Assert.Empty(rule.Operation);
            Assert.Null(rule.RequestContains);
            Assert.Empty(rule.Payload);
        }

        [Fact]
        public void DefaultMockRule_IsSafeToRead()
        {
            MockRule rule = default;

            // default 结构体的引用字段是 null：属性必须兜住，否则调用方读一次就 NullReferenceException。
            Assert.Empty(rule.Operation);
            Assert.Null(rule.RequestContains);
            Assert.Empty(rule.Payload);
            Assert.Equal(MockTransportOutcome.Ok, rule.Outcome);
            Assert.Equal(0, rule.DelayMs);
        }

        [Fact]
        public void MockCallRecord_NullFields_AreNormalized()
        {
            var record = new MockCallRecord(null, null, -1L, MockTransportOutcome.Timeout);

            Assert.Empty(record.Operation);
            Assert.Empty(record.Request);
            // 负值原样保留：1970 年之前的时刻是合法读数，夹成 0 会伪造出一个"刚好在纪元"的调用。
            Assert.Equal(-1L, record.AtUnixSeconds);
        }

        [Fact]
        public void MockResponse_NullFields_AreNormalized()
        {
            var response = new MockResponse(MockTransportOutcome.Malformed, null, null);

            Assert.Empty(response.Payload);
            Assert.Empty(response.Error);
        }

        [Theory]
        [InlineData(MockTransportOutcome.Ok, "")]
        [InlineData(MockTransportOutcome.Timeout, "timeout")]
        [InlineData(MockTransportOutcome.ServerError, "server error")]
        [InlineData(MockTransportOutcome.Malformed, "malformed response")]
        public void MockResponse_ErrorText_IsFixedPerOutcome(MockTransportOutcome outcome, string expected)
        {
            // 文案的唯一来源：网关与适配层都复用它，避免两处各写一份而漂移。
            Assert.Equal(expected, MockResponse.ErrorText(outcome));
        }

        [Fact]
        public void MockResponse_ErrorText_OutOfRangeOutcomeIsEmpty()
        {
            // 越界枚举值只会来自一次错误的外部转换：给它编一句"unknown"会让日志出现不存在的失败原因。
            Assert.Empty(MockResponse.ErrorText((MockTransportOutcome)9));
        }

        // ---------------------------------------------------------------- 替身

        /// <summary>
        /// 可拨动的假时钟：本类只用到 <see cref="UtcNow"/>（调用记录的时间戳来源），
        /// <see cref="ElapsedSeconds"/> 保持 0 以免暗示"网关会读单调钟"。
        /// </summary>
        private sealed class FakeTime : ITimeProvider
        {
            private DateTime _utcNow = StartUtc;

            public double ElapsedSeconds { get; set; }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            /// <summary>把墙钟往前拨若干秒（不等待真实时间）。</summary>
            /// <param name="seconds">前进的秒数。</param>
            public void AdvanceSeconds(int seconds)
            {
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }

        /// <summary>把 UTC 时刻换算成 Unix 秒（与网关内部换算同口径，用于交叉验证）。</summary>
        /// <param name="utcNow">UTC 时刻。</param>
        private static long ToUnixSeconds(DateTime utcNow)
        {
            return (long)Math.Floor((utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        }
    }
}

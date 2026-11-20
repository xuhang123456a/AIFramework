#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;

namespace AIFramework.Adapters.Console.E2e
{
    /// <summary>
    /// 一个步骤的执行结果，<b>只读</b>。
    /// <para>
    /// <b><see cref="Executed"/> 与 <see cref="Passed"/> 必须分开</b>：被"上游失败中止"的步骤
    /// 既没有执行、也没有通过，它既不是成功也不是"跑了但失败"。合成一个布尔值后，
    /// 报告会把"没跑"读成"跑挂了"，排障方向直接跑偏（去查一个根本没执行的步骤）。
    /// </para>
    /// <para>
    /// <b>字符串在构造期归一化</b>（<c>null</c> → 空串）：报告消费方（控制台、CI 日志）不用再判空；
    /// <b>负数耗时归一为 0</b>：时间源回拨不该产出"负耗时"这种无法解释的数字。
    /// </para>
    /// </summary>
    public readonly struct E2eStepResult
    {
        /// <summary>步骤名的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _stepName;

        /// <summary>消息的归一化副本（读取期兜底 <c>null</c>）。</summary>
        private readonly string? _message;

        /// <summary>构造一条步骤结果。</summary>
        /// <param name="stepName">步骤名（<c>null</c> 归一化为空串）。</param>
        /// <param name="executed">是否真的执行过（<c>false</c> = 被上游失败中止）。</param>
        /// <param name="passed">是否通过（<see cref="Executed"/> 为 <c>false</c> 时恒为 <c>false</c>）。</param>
        /// <param name="elapsedMilliseconds">耗时毫秒（来自 <see cref="ITimeProvider.ElapsedSeconds"/> 差值；负数归一为 0）。</param>
        /// <param name="message">诊断消息（异常类型名 / 断言失败文案 / 中止原因；<c>null</c> 归一化为空串）。</param>
        public E2eStepResult(string stepName, bool executed, bool passed, long elapsedMilliseconds, string message)
        {
            _stepName = stepName;
            _message = message;
            Executed = executed;
            Passed = passed;
            ElapsedMilliseconds = elapsedMilliseconds > 0L ? elapsedMilliseconds : 0L;
        }

        /// <summary>步骤名（永远不为 <c>null</c>，可能为空串）。</summary>
        public string StepName
        {
            get { return _stepName ?? string.Empty; }
        }

        /// <summary>是否真的执行过（<c>false</c> = 被上游失败中止，未调用执行/断言委托）。</summary>
        public bool Executed { get; }

        /// <summary>是否通过（未执行的步骤恒为 <c>false</c>）。</summary>
        public bool Passed { get; }

        /// <summary>本步耗时毫秒（非负；无断言/未执行时为 0 或实际耗时）。</summary>
        public long ElapsedMilliseconds { get; }

        /// <summary>诊断消息（成功时为空串）。</summary>
        public string Message
        {
            get { return _message ?? string.Empty; }
        }
    }

    /// <summary>
    /// 一次 E2E 运行的报告，<b>只读</b>：场景名 + 每步结果 + 总结论。
    /// <para>
    /// <b>计数口径</b>：
    /// <list type="bullet">
    /// <item><see cref="PassedCount"/> = <see cref="E2eStepResult.Passed"/> 为 <c>true</c> 的步骤数。</item>
    /// <item><see cref="FailedCount"/> = 步骤总数 − <see cref="PassedCount"/>，
    /// <b>包含被中止（未执行）的步骤</b>：整条场景没跑完同样是"这次冒烟失败"，
    /// 若把它们排除在失败之外，报告会出现"步骤 6 / 通过 3 / 失败 0"这种自相矛盾的行。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么不在这里重新推导 <see cref="Passed"/></b>：构造参数就是调用方的结论，
    /// 报告只负责<b>如实呈现</b>。推导一次会让"结论"与"计数"出现两个事实源；
    /// <see cref="E2eRunner"/> 传的就是"全部步骤都通过"，测试可以直接构造报告验收格式与计数。
    /// </para>
    /// <para><b>线程模型</b>：不可变，构造完成后可安全跨线程只读。</para>
    /// </summary>
    public sealed class E2eReport
    {
        /// <summary>创建一份报告。</summary>
        /// <param name="scenarioName">场景名（<c>null</c> 归一化为空串）。</param>
        /// <param name="results">步骤结果（<c>null</c> 归一化为空数组；构造期复制，调用方之后改写传入数组不影响本报告）。</param>
        /// <param name="passed">总结论（由调用方给出，通常 = 没有任何失败步骤）。</param>
        public E2eReport(string scenarioName, E2eStepResult[] results, bool passed)
        {
            ScenarioName = scenarioName ?? string.Empty;
            Passed = passed;

            // 复制：计数与 ToReportLine 都读同一份数组，若允许外部改写，报告会自相矛盾
            // （"通过=1" 的结论配上"失败=0"的行）。空数组直接复用共享实例，避免空场景分配。
            if (results == null || results.Length == 0)
            {
                Results = Array.Empty<E2eStepResult>();
            }
            else
            {
                var copy = new E2eStepResult[results.Length];
                Array.Copy(results, copy, results.Length);
                Results = copy;
            }

            int passedCount = 0;
            for (int i = 0; i < Results.Length; i++)
            {
                if (Results[i].Passed)
                {
                    passedCount++;
                }
            }

            PassedCount = passedCount;
            FailedCount = Results.Length - passedCount;
        }

        /// <summary>场景名（永远不为 <c>null</c>，可能为空串）。</summary>
        public string ScenarioName { get; }

        /// <summary>
        /// 每步结果（按执行顺序；空场景为零长度数组，绝不为 <c>null</c>）。
        /// <para>
        /// 返回内部数组（契约要求 <c>E2eStepResult[]</c>）：调用方<b>只读</b>。
        /// 构造期已复制入参，因此改写"构造时传进来的那个数组"不会影响本报告的计数与行文。
        /// </para>
        /// </summary>
        public E2eStepResult[] Results { get; }

        /// <summary>总结论（构造参数原值，见类型注释）。</summary>
        public bool Passed { get; }

        /// <summary>通过的步骤数。</summary>
        public int PassedCount { get; }

        /// <summary>未通过的步骤数（<b>含被中止的步骤</b>，见类型注释）。</summary>
        public int FailedCount { get; }

        /// <summary>
        /// 单行报告，格式固定（便于 CI 用正则抓取）：
        /// <c>[E2E] 场景=&lt;名字&gt; 步骤=&lt;总数&gt; 通过=&lt;数&gt; 失败=&lt;数&gt; 耗时=&lt;毫秒&gt;ms 结论=&lt;通过|失败&gt;</c>。
        /// <para>
        /// <b>耗时是各步之和</b>，不是墙钟时长：两者之差就是"步骤之间"的开销（装配、日志、GC），
        /// 单独看它才能判断慢在业务链路还是慢在准备阶段。
        /// </para>
        /// </summary>
        /// <returns>固定格式的单行报告。</returns>
        public string ToReportLine()
        {
            long elapsedMilliseconds = 0L;
            for (int i = 0; i < Results.Length; i++)
            {
                elapsedMilliseconds += Results[i].ElapsedMilliseconds;
            }

            return "[E2E] 场景=" + ScenarioName
                + " 步骤=" + Results.Length.ToString(CultureInfo.InvariantCulture)
                + " 通过=" + PassedCount.ToString(CultureInfo.InvariantCulture)
                + " 失败=" + FailedCount.ToString(CultureInfo.InvariantCulture)
                + " 耗时=" + elapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms"
                + " 结论=" + (Passed ? "通过" : "失败");
        }
    }

    /// <summary>
    /// E2E 运行器：按顺序执行一条 <see cref="E2eScenario"/> 并产出 <see cref="E2eReport"/>。
    /// <para>
    /// <b>每步的固定顺序</b>：先 <c>execute()</c>，再 <c>verify()</c>（可选）。
    /// <list type="bullet">
    /// <item><c>execute</c> 抛异常 → 本步失败，消息带<b>异常类型名</b>（不带堆栈：冒烟报告要能贴进单行日志）；
    /// <b>不会</b>再执行断言（执行都没走完，断言只会给出误导性的结论）。</item>
    /// <item><c>verify</c> 返回 <c>false</c> → 本步失败，消息为固定文案
    /// <see cref="MessageVerifyFailed"/>（业务判定看 <see cref="E2eStepResult.Passed"/>，禁止匹配文案）。</item>
    /// <item>没有 <c>verify</c> → 执行不抛异常即通过。</item>
    /// <item>失败且场景 <see cref="E2eScenario.StopOnFailure"/> 为 <c>true</c> → 后续步骤<b>不再执行</b>，
    /// 结果标记 <see cref="E2eStepResult.Executed"/> = <c>false</c>、消息为 <see cref="MessageAborted"/>，
    /// 既不计入通过也不抛异常。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>计时只走 <see cref="ITimeProvider.ElapsedSeconds"/> 的差值</b>：禁止 <c>DateTime.Now</c> /
    /// <c>Stopwatch</c>（AIF003）。好处不只是"符合规范"——注入假时钟后，"这一步花了 250ms"
    /// 可以在无头测试里被精确断言，而真机上的墙钟永远抖动。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步</b>。步骤在调用线程上逐个执行，不排队、不异步、不加锁；
    /// 一条场景必须由同一个线程串行跑完（步骤闭包共享的场景状态不是线程安全的）。
    /// </para>
    /// </summary>
    public sealed class E2eRunner
    {
        /// <summary>断言失败的固定消息（业务判定看 <c>Passed</c>，不要匹配这段文案）。</summary>
        public const string MessageVerifyFailed = "断言未通过（verify 返回 false）";

        /// <summary>被上游失败中止的固定消息。</summary>
        public const string MessageAborted = "被上游失败中止";

        /// <summary>时间源（计时唯一来源）；构造后不可变。</summary>
        private readonly ITimeProvider _time;

        /// <summary>创建运行器。</summary>
        /// <param name="time">时间源（非 <c>null</c>；只用于步骤计时）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public E2eRunner(ITimeProvider time)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>
        /// 执行一条场景并产出报告（<b>永不因步骤失败而抛异常</b>；只有入参为 <c>null</c> 才抛）。
        /// </summary>
        /// <param name="scenario">待执行的场景（非 <c>null</c>）。</param>
        /// <returns>本次运行的报告（步骤数 = 场景步骤数，未执行的步骤同样占位）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scenario"/> 为 <c>null</c>（编程错误）。</exception>
        public E2eReport Run(in E2eScenario scenario)
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            IReadOnlyList<E2eStep> steps = scenario.Steps;
            var results = new E2eStepResult[steps.Count];
            bool aborted = false;
            bool allPassed = true;

            for (int i = 0; i < steps.Count; i++)
            {
                E2eStep step = steps[i];

                if (aborted)
                {
                    // 未执行 ≠ 通过：标记 executed=false 并计入失败数（见 E2eReport 的计数口径）。
                    results[i] = new E2eStepResult(
                        step.Name, executed: false, passed: false, elapsedMilliseconds: 0L, message: MessageAborted);
                    allPassed = false;
                    continue;
                }

                double startedAtSeconds = _time.ElapsedSeconds;
                bool passed;
                string message;

                try
                {
                    step.Execute();

                    Func<bool>? verify = step.Verify;
                    if (verify == null)
                    {
                        passed = true;
                        message = string.Empty;
                    }
                    else
                    {
                        passed = verify();
                        message = passed ? string.Empty : MessageVerifyFailed;
                    }
                }
                catch (Exception ex)
                {
                    // 只留类型名 + 消息：冒烟报告是单行日志，堆栈交给调试器。
                    passed = false;
                    message = "异常 " + ex.GetType().Name + "：" + ex.Message;
                }

                results[i] = new E2eStepResult(
                    step.Name,
                    executed: true,
                    passed: passed,
                    elapsedMilliseconds: ElapsedMillisecondsSince(startedAtSeconds),
                    message: message);

                if (!passed)
                {
                    allPassed = false;
                    if (scenario.StopOnFailure)
                    {
                        aborted = true;
                    }
                }
            }

            return new E2eReport(scenario.Name, results, allPassed);
        }

        /// <summary>
        /// 计算自 <paramref name="startedAtSeconds"/> 起经过的毫秒数（四舍五入到整数毫秒）。
        /// <para>
        /// 时间源回拨、返回 <c>NaN</c>/<c>Infinity</c> 时一律归 0：负耗时或天文数字只会让报告失真，
        /// 冒烟脚本不该因为时钟异常而崩（断言失败已经足够暴露问题）。
        /// </para>
        /// </summary>
        /// <param name="startedAtSeconds">步骤开始时的 <see cref="ITimeProvider.ElapsedSeconds"/>。</param>
        /// <returns>非负毫秒数。</returns>
        private long ElapsedMillisecondsSince(double startedAtSeconds)
        {
            double elapsedSeconds = _time.ElapsedSeconds - startedAtSeconds;
            if (!(elapsedSeconds > 0d) || double.IsInfinity(elapsedSeconds))
            {
                return 0L;
            }

            return (long)Math.Round(elapsedSeconds * 1000d, MidpointRounding.AwayFromZero);
        }
    }
}

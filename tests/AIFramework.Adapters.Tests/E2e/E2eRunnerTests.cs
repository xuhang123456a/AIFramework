using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Adapters.Console;
using AIFramework.Adapters.Console.E2e;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Core.Gameplay;
using AIFramework.Core.Save;
using AIFramework.Features.Inventory;
using Xunit;

namespace AIFramework.Adapters.Tests.E2e
{
    /// <summary>
    /// <see cref="E2eRunner"/> / <see cref="E2eReport"/> 的行为契约，以及
    /// <see cref="ConsoleE2eScenarios.MainFlow"/> 在真实依赖下的整链验收。
    /// <para>
    /// <b>为什么这些用例必须存在</b>：E2E 运行器本身就是"冒烟的可信度来源"。如果它会
    /// 把异常吞成通过、把中止算成通过、或者计时读墙钟，那么它给出的绿灯只是装饰品——
    /// 而这恰恰是最难被发现的一类失败（没有任何红灯）。所以这里逐条钉死四件事：
    /// 失败如何分类、中止如何计数、计时只认 <see cref="ITimeProvider"/>、报告格式固定。
    /// </para>
    /// <para>
    /// 后半段（<c>MainFlow_*</c>）用真总线 + 真背包 + 真商店闭环 + 真回合内核 + 真存档服务跑完整场景，
    /// 并额外验一条负向路径：<b>装配漏注册发放处理器时场景必须红灯</b>——否则"端到端冒烟通过"
    /// 只能证明代码能编译。
    /// </para>
    /// </summary>
    public sealed class E2eRunnerTests
    {
        // ---------------------------------------------------------------- 运行器：失败分类

        [Fact]
        public void Run_AllStepsPass_ReportsAllPassed()
        {
            var scenario = new E2eScenario("all-pass", new[]
            {
                new E2eStep("登录", () => { }),
                new E2eStep("购买", () => { }, () => true),
            });

            E2eReport report = Run(scenario);

            Assert.True(report.Passed);
            Assert.Equal(2, report.PassedCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Equal(2, report.Results.Length);
            Assert.All(report.Results, result =>
            {
                Assert.True(result.Executed);
                Assert.True(result.Passed);
                Assert.Equal(string.Empty, result.Message);
            });
        }

        [Fact]
        public void Run_VerifyFalse_FailsStepWithFixedMessage()
        {
            var scenario = new E2eScenario("verify-false", new[]
            {
                new E2eStep("背包到账", () => { }, () => false),
            });

            E2eReport report = Run(scenario);

            Assert.False(report.Passed);
            Assert.Equal(1, report.FailedCount);
            Assert.True(report.Results[0].Executed);
            Assert.False(report.Results[0].Passed);
            Assert.Equal(E2eRunner.MessageVerifyFailed, report.Results[0].Message);
        }

        [Fact]
        public void Run_ExecuteThrows_FailsStepAndMessageCarriesExceptionType()
        {
            var scenario = new E2eScenario("throws", new[]
            {
                new E2eStep("购买", () => throw new InvalidOperationException("GrantItemCommand 处理器未注册")),
            });

            E2eReport report = Run(scenario);

            Assert.False(report.Passed);
            Assert.False(report.Results[0].Passed);
            Assert.Contains("InvalidOperationException", report.Results[0].Message);
            // 异常消息本身也保留：报告要能直接看出"哪条链路断在哪句提示上"。
            Assert.Contains("GrantItemCommand", report.Results[0].Message);
        }

        [Fact]
        public void Run_ExecuteThrows_DoesNotInvokeVerify()
        {
            bool verifyCalled = false;
            var scenario = new E2eScenario("no-verify-after-throw", new[]
            {
                new E2eStep(
                    "购买",
                    () => throw new ArgumentException("boom"),
                    () =>
                    {
                        verifyCalled = true;
                        return true;
                    }),
            });

            E2eReport report = Run(scenario);

            Assert.False(report.Results[0].Passed);
            Assert.False(verifyCalled);
        }

        [Fact]
        public void Run_VerifyThrows_TreatedAsStepFailureWithExceptionType()
        {
            var scenario = new E2eScenario("verify-throws", new[]
            {
                new E2eStep("存档", () => { }, () => throw new ArgumentException("断言里读快照炸了")),
            });

            E2eReport report = Run(scenario);

            Assert.False(report.Passed);
            Assert.False(report.Results[0].Passed);
            Assert.Contains("ArgumentException", report.Results[0].Message);
        }

        [Fact]
        public void Run_StepWithoutVerify_PassesWhenExecuteDoesNotThrow()
        {
            var scenario = new E2eScenario("no-verify", new[]
            {
                new E2eStep("空动作", () => { }),
            });

            E2eReport report = Run(scenario);

            Assert.True(report.Passed);
            Assert.True(report.Results[0].Passed);
            Assert.Equal(string.Empty, report.Results[0].Message);
        }

        // ---------------------------------------------------------------- 运行器：中止语义

        [Fact]
        public void Run_StopOnFailure_AbortsRemainingStepsWithExecutedFalse()
        {
            var scenario = new E2eScenario("stop-on-failure", new[]
            {
                new E2eStep("登录", () => { }),
                new E2eStep("购买", () => { }, () => false),
                new E2eStep("战斗", () => { }),
            });

            E2eReport report = Run(scenario);

            Assert.False(report.Passed);
            Assert.True(report.Results[0].Passed);

            Assert.True(report.Results[1].Executed);
            Assert.False(report.Results[1].Passed);

            Assert.False(report.Results[2].Executed);
            Assert.False(report.Results[2].Passed);
            Assert.Equal(E2eRunner.MessageAborted, report.Results[2].Message);

            // 未执行同样算失败：整条场景没跑完，报告不能给出"失败 1"这种偏乐观的数字。
            Assert.Equal(1, report.PassedCount);
            Assert.Equal(2, report.FailedCount);
        }

        [Fact]
        public void Run_StopOnFailure_AbortedStepsNeverInvokeTheirDelegates()
        {
            bool executed = false;
            bool verified = false;
            var scenario = new E2eScenario("aborted-not-invoked", new[]
            {
                new E2eStep("第一步就挂", () => throw new InvalidOperationException("boom")),
                new E2eStep(
                    "后续步骤",
                    () => { executed = true; },
                    () =>
                    {
                        verified = true;
                        return true;
                    }),
            });

            E2eReport report = Run(scenario);

            Assert.False(executed);
            Assert.False(verified);
            Assert.False(report.Results[1].Executed);
        }

        [Fact]
        public void Run_StopOnFailure_AbortedStepsRecordZeroElapsed()
        {
            var time = new FakeTime();
            var scenario = new E2eScenario("aborted-elapsed", new[]
            {
                new E2eStep("耗时并失败", () =>
                {
                    time.Advance(0.5);
                    throw new InvalidOperationException("boom");
                }),
                new E2eStep("未执行", () => time.Advance(1.0)),
            });

            E2eReport report = new E2eRunner(time).Run(in scenario);

            Assert.Equal(500L, report.Results[0].ElapsedMilliseconds);
            Assert.Equal(0L, report.Results[1].ElapsedMilliseconds);
        }

        [Fact]
        public void Run_WithoutStopOnFailure_ContinuesAfterFailure()
        {
            int executedCount = 0;
            var scenario = new E2eScenario(
                "keep-going",
                new[]
                {
                    new E2eStep("通过", () => executedCount++),
                    new E2eStep("断言失败", () => executedCount++, () => false),
                    new E2eStep("仍然执行", () => executedCount++),
                },
                stopOnFailure: false);

            E2eReport report = Run(scenario);

            Assert.Equal(3, executedCount);
            Assert.False(report.Passed);
            Assert.True(report.Results[2].Executed);
            Assert.True(report.Results[2].Passed);
            Assert.Equal(2, report.PassedCount);
            Assert.Equal(1, report.FailedCount);
        }

        [Fact]
        public void Run_WithoutStopOnFailure_CountsEachFailureIndependently()
        {
            var scenario = new E2eScenario(
                "two-failures",
                new[]
                {
                    new E2eStep("失败一", () => throw new InvalidOperationException("a")),
                    new E2eStep("失败二", () => { }, () => false),
                },
                stopOnFailure: false);

            E2eReport report = Run(scenario);

            Assert.False(report.Passed);
            Assert.Equal(0, report.PassedCount);
            Assert.Equal(2, report.FailedCount);
            Assert.True(report.Results[0].Executed);
            Assert.True(report.Results[1].Executed);
        }

        // ---------------------------------------------------------------- 运行器：空场景 / 顺序 / 入参

        [Fact]
        public void Run_EmptyScenario_ProducesPassingZeroStepReport()
        {
            var scenario = new E2eScenario("empty", Array.Empty<E2eStep>());

            E2eReport report = Run(scenario);

            // 空场景是"没有失败"，不是"没有结论"：巡检脚本据此判断"这条场景没配步骤"。
            Assert.True(report.Passed);
            Assert.Empty(report.Results);
            Assert.Equal(0, report.PassedCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Equal("[E2E] 场景=empty 步骤=0 通过=0 失败=0 耗时=0ms 结论=通过", report.ToReportLine());
        }

        [Fact]
        public void Run_NullScenario_Throws()
        {
            var runner = new E2eRunner(new FakeTime());

            Assert.Throws<ArgumentNullException>(() => runner.Run(null!));
        }

        [Fact]
        public void Run_ExecutesStepsInDeclarationOrder()
        {
            var order = new List<string>();
            var scenario = new E2eScenario("order", new[]
            {
                new E2eStep("1-登录", () => order.Add("登录")),
                new E2eStep("2-购买", () => order.Add("购买")),
                new E2eStep("3-战斗", () => order.Add("战斗")),
            });

            Run(scenario);

            Assert.Equal(new[] { "登录", "购买", "战斗" }, order);
        }

        // ---------------------------------------------------------------- 运行器：计时

        [Fact]
        public void Run_Elapsed_UsesTimeProviderDelta()
        {
            var time = new FakeTime();
            var scenario = new E2eScenario("timed", new[]
            {
                new E2eStep("四分之一秒", () => time.Advance(0.25)),
            });

            E2eReport report = new E2eRunner(time).Run(in scenario);

            Assert.Equal(250L, report.Results[0].ElapsedMilliseconds);
            Assert.Equal("[E2E] 场景=timed 步骤=1 通过=1 失败=0 耗时=250ms 结论=通过", report.ToReportLine());
        }

        [Fact]
        public void Run_Elapsed_SumsAcrossStepsInReportLine()
        {
            var time = new FakeTime();
            var scenario = new E2eScenario("timed-two", new[]
            {
                new E2eStep("第一步", () => time.Advance(0.1)),
                new E2eStep("第二步", () => time.Advance(0.2)),
            });

            E2eReport report = new E2eRunner(time).Run(in scenario);

            Assert.Equal(100L, report.Results[0].ElapsedMilliseconds);
            Assert.Equal(200L, report.Results[1].ElapsedMilliseconds);
            Assert.Contains("耗时=300ms", report.ToReportLine());
        }

        [Fact]
        public void Run_ClockRewind_ClampsElapsedToZero()
        {
            var time = new FakeTime();
            var scenario = new E2eScenario("rewind", new[]
            {
                new E2eStep("时间回拨", () => time.Advance(-2.0)),
            });

            E2eReport report = new E2eRunner(time).Run(in scenario);

            // 负耗时是不可解释的数字：时间源回拨时归 0，而不是把 -2000ms 写进报告。
            Assert.Equal(0L, report.Results[0].ElapsedMilliseconds);
        }

        [Fact]
        public void Run_ExceptionStep_StillRecordsElapsed()
        {
            var time = new FakeTime();
            var scenario = new E2eScenario("throw-timed", new[]
            {
                new E2eStep("耗时并抛异常", () =>
                {
                    time.Advance(0.5);
                    throw new InvalidOperationException("boom");
                }),
            });

            E2eReport report = new E2eRunner(time).Run(in scenario);

            Assert.False(report.Results[0].Passed);
            Assert.Equal(500L, report.Results[0].ElapsedMilliseconds);
        }

        // ---------------------------------------------------------------- 构造期校验

        [Fact]
        public void Step_NullExecute_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new E2eStep("没有执行体", null!));
        }

        [Fact]
        public void Step_NullName_NormalizedToEmptyString()
        {
            var step = new E2eStep(null!, () => { });

            Assert.Equal(string.Empty, step.Name);
        }

        [Fact]
        public void Scenario_NullSteps_NormalizedToEmptyList()
        {
            var scenario = new E2eScenario("null-steps", null!);

            Assert.Empty(scenario.Steps);
            Assert.True(scenario.StopOnFailure);
        }

        [Fact]
        public void Scenario_NullStepElement_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new E2eScenario(
                "null-element",
                new[] { new E2eStep("正常步骤", () => { }), null! }));
        }

        [Fact]
        public void Scenario_CopiesStepsArray()
        {
            var steps = new[] { new E2eStep("原始步骤", () => { }) };
            var scenario = new E2eScenario("copy", steps);

            // 调用方之后改写传入数组，不得改变已构造的场景（冒烟脚本会复用/拼接步骤数组）。
            steps[0] = new E2eStep("替换步骤", () => { });

            Assert.Single(scenario.Steps);
            Assert.Equal("原始步骤", scenario.Steps[0].Name);
        }

        [Fact]
        public void Scenario_DefaultsToStopOnFailure()
        {
            var scenario = new E2eScenario("default", Array.Empty<E2eStep>());

            Assert.True(scenario.StopOnFailure);
        }

        [Fact]
        public void Runner_NullTimeProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new E2eRunner(null!));
        }

        // ---------------------------------------------------------------- 报告：归一化 / 计数 / 格式

        [Fact]
        public void Report_NullResults_NormalizedToEmptyArray()
        {
            var report = new E2eReport("null-results", null!, passed: false);

            Assert.Empty(report.Results);
            Assert.Equal(0, report.PassedCount);
            Assert.Equal(0, report.FailedCount);
            Assert.False(report.Passed);
        }

        [Fact]
        public void Report_NullScenarioName_NormalizedToEmptyString()
        {
            var report = new E2eReport(null!, Array.Empty<E2eStepResult>(), passed: true);

            Assert.Equal(string.Empty, report.ScenarioName);
            Assert.Equal("[E2E] 场景= 步骤=0 通过=0 失败=0 耗时=0ms 结论=通过", report.ToReportLine());
        }

        [Fact]
        public void Report_CopiesResultsArray()
        {
            var results = new[]
            {
                new E2eStepResult("登录", executed: true, passed: true, elapsedMilliseconds: 5L, message: string.Empty),
            };

            var report = new E2eReport("copy", results, passed: true);

            // 构造后改写传入数组：计数与报告行必须保持不变（否则"通过=1"会配上"失败=1"）。
            results[0] = new E2eStepResult("被改写", executed: false, passed: false, elapsedMilliseconds: 99L, message: "x");

            Assert.Equal(1, report.PassedCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Equal("登录", report.Results[0].StepName);
            Assert.Equal(5L, report.Results[0].ElapsedMilliseconds);
        }

        [Fact]
        public void Report_CountsAndLineFormat_IsExact()
        {
            var results = new[]
            {
                new E2eStepResult("登录", executed: true, passed: true, elapsedMilliseconds: 5L, message: string.Empty),
                new E2eStepResult("购买", executed: true, passed: false, elapsedMilliseconds: 7L, message: "断言未通过"),
            };

            var report = new E2eReport("smoke", results, passed: false);

            Assert.Equal(1, report.PassedCount);
            Assert.Equal(1, report.FailedCount);
            Assert.Equal("[E2E] 场景=smoke 步骤=2 通过=1 失败=1 耗时=12ms 结论=失败", report.ToReportLine());
        }

        [Fact]
        public void Report_FailedCountIncludesAbortedSteps()
        {
            var results = new[]
            {
                new E2eStepResult("登录", executed: true, passed: true, elapsedMilliseconds: 3L, message: string.Empty),
                new E2eStepResult("购买", executed: false, passed: false, elapsedMilliseconds: 0L, message: E2eRunner.MessageAborted),
            };

            var report = new E2eReport("aborted", results, passed: false);

            Assert.Equal(1, report.PassedCount);
            Assert.Equal(1, report.FailedCount);
            Assert.Equal("[E2E] 场景=aborted 步骤=2 通过=1 失败=1 耗时=3ms 结论=失败", report.ToReportLine());
        }

        [Fact]
        public void StepResult_NormalizesNullStringsAndNegativeElapsed()
        {
            var result = new E2eStepResult(null!, executed: true, passed: false, elapsedMilliseconds: -9L, message: null!);

            Assert.Equal(string.Empty, result.StepName);
            Assert.Equal(string.Empty, result.Message);
            Assert.Equal(0L, result.ElapsedMilliseconds);
        }

        // ---------------------------------------------------------------- 主流程：真实依赖整链验收

        [Fact]
        public void MainFlow_RealDependencies_AllStepsPass()
        {
            using (var fixture = new MainFlowFixture())
            {
                E2eReport report = fixture.Run();

                Assert.True(report.Passed, report.ToReportLine());
                Assert.Equal(0, report.FailedCount);
                Assert.Equal(8, report.Results.Length);

                for (int i = 0; i < report.Results.Length; i++)
                {
                    E2eStepResult result = report.Results[i];
                    Assert.True(result.Executed, result.StepName);
                    Assert.True(result.Passed, result.StepName + " → " + result.Message);
                }

                // 三笔真实到账都落在同一个背包账本上：购买 60 + 结算战利品 1 + 对账补发 60。
                Assert.Equal(121, fixture.Inventory.GetCount(ConsoleIapService.GoldItemId));
                Assert.Equal(1, fixture.Inventory.GetCount(ConsoleIapService.NoAdsItemId));
            }
        }

        [Fact]
        public void MainFlow_StepNames_AreUniqueAndNonEmpty()
        {
            using (var fixture = new MainFlowFixture())
            {
                E2eScenario scenario = fixture.Scenario;
                var names = new HashSet<string>(StringComparer.Ordinal);

                for (int i = 0; i < scenario.Steps.Count; i++)
                {
                    string name = scenario.Steps[i].Name;
                    Assert.False(string.IsNullOrEmpty(name));
                    Assert.True(names.Add(name), "步骤名重复：" + name);
                }

                Assert.Equal(ConsoleE2eScenarios.MainFlowName, scenario.Name);
                Assert.True(scenario.StopOnFailure);
            }
        }

        [Fact]
        public void MainFlow_ReportLine_UsesFixedFormat()
        {
            using (var fixture = new MainFlowFixture())
            {
                string line = fixture.Run().ToReportLine();

                Assert.StartsWith("[E2E] 场景=console-main-flow 步骤=8 通过=8 失败=0 耗时=", line);
                Assert.EndsWith("ms 结论=通过", line);
            }
        }

        [Fact]
        public void MainFlow_MissingGrantHandler_FailsAtPurchaseAndAbortsRest()
        {
            // 负向验收：总线没注册 GrantItemCommand 处理器（装配遗漏）时，端到端场景必须红灯。
            // 这条用例就是"绿灯可信"的证明——冒烟不会因为链路断了而假装通过。
            using (var fixture = new MainFlowFixture(registerGrantHandler: false))
            {
                E2eReport report = fixture.Run();

                Assert.False(report.Passed);

                // 登录不依赖总线，仍然通过。
                Assert.True(report.Results[0].Passed);

                // 购买动作执行了但没到账：失败于断言，而不是"被中止"。
                Assert.True(report.Results[1].Executed);
                Assert.False(report.Results[1].Passed);

                // 后续步骤（背包/战斗/结算/存档/对账）全部被中止。
                for (int i = 2; i < report.Results.Length; i++)
                {
                    Assert.False(report.Results[i].Executed, report.Results[i].StepName);
                    Assert.Equal(E2eRunner.MessageAborted, report.Results[i].Message);
                }

                Assert.Equal(1, report.PassedCount);
                Assert.Equal(7, report.FailedCount);
            }
        }

        // ---------------------------------------------------------------- 夹具

        /// <summary>用假时钟跑一条场景（默认时间不前进，因此耗时恒为 0）。</summary>
        /// <param name="scenario">待执行场景。</param>
        /// <returns>运行报告。</returns>
        private static E2eReport Run(E2eScenario scenario)
        {
            var runner = new E2eRunner(new FakeTime());
            return runner.Run(in scenario);
        }

        /// <summary>
        /// 主流程夹具：真 <see cref="CqeBus"/> + 真 <see cref="InventoryHandler"/> + 真商店闭环 +
        /// 真回合内核宿主 + 真 <see cref="SaveService"/>（落在本用例独占的临时目录）。
        /// </summary>
        private sealed class MainFlowFixture : IDisposable
        {
            private readonly string _directory;

            internal MainFlowFixture(bool registerGrantHandler = true)
            {
                Time = new FakeTime();
                Bus = new CqeBus();
                Inventory = new InventoryHandler();
                if (registerGrantHandler)
                {
                    Inventory.Register(Bus);
                }

                PurchaseFlow = new ConsoleStorePurchaseFlow(Bus);
                Host = new GameplaySessionHost(
                    new DemoKernelFactory(new MockSpatialWorld(), DemoKernelFactory.DefaultPhases()));

                _directory = Path.Combine(
                    Path.GetTempPath(), "aiframework-e2e-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);

                Save = new SaveService(new FileSystemStoragePort(_directory), "e2e");
                Scenario = ConsoleE2eScenarios.MainFlow(Time, Bus, PurchaseFlow, Inventory, Host, Save);
            }

            internal FakeTime Time { get; }

            internal CqeBus Bus { get; }

            internal InventoryHandler Inventory { get; }

            internal ConsoleStorePurchaseFlow PurchaseFlow { get; }

            internal GameplaySessionHost Host { get; }

            internal SaveService Save { get; }

            internal E2eScenario Scenario { get; }

            internal E2eReport Run()
            {
                // in 参数必须是变量（属性不能按引用传递），先落到局部。
                E2eScenario scenario = Scenario;
                var runner = new E2eRunner(Time);
                return runner.Run(in scenario);
            }

            public void Dispose()
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                GC.SuppressFinalize(this);
            }
        }

        /// <summary>假时钟：<see cref="ElapsedSeconds"/> 只在显式 <see cref="Advance"/> 时前进，计时断言可精确到毫秒。</summary>
        private sealed class FakeTime : ITimeProvider
        {
            private double _elapsedSeconds;

            private DateTime _utcNow = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            internal void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }
    }
}

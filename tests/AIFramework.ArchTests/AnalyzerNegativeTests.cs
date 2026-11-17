using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AIFramework.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace AIFramework.ArchTests
{
    /// <summary>
    /// AIF001~AIF004 的负向编译回归：每条规则都用「真实程序集名门禁 + 内存 CSharpCompilation」跑真分析器。
    /// 所有被禁命名空间都在同一份源码里自带 stub 声明，避免「符号解析不到 → 一条诊断都没有」的假绿；
    /// 每个用例在断言诊断前先断言内存编译零错误，避免语法写错导致的假红。
    /// </summary>
    public sealed class AnalyzerNegativeTests
    {
        /// <summary>AIF001 / AIF004 的唯一生效程序集名。</summary>
        private const string FeaturesAssemblyName = "AIFramework.Features";

        /// <summary>AIF002 / AIF003 白名单中的一个代表性程序集名。</summary>
        private const string ContractsAssemblyName = "AIFramework.Contracts";

        /// <summary>白名单中的一个强实时内核程序集名。</summary>
        private const string RealtimeAssemblyName = "AIFramework.Kernel.Realtime";

        /// <summary>不在任何白名单里的第三方/宿主程序集名，用来验证门禁真的按程序集名生效。</summary>
        private const string ForeignAssemblyName = "My.Sandbox";

        /// <summary>
        /// AIF001 正例源码：SliceA 既 <c>using</c> 了 SliceB，又把 SliceB 的类型用作字段类型，
        /// 两处引用点都会被分析器看到。
        /// </summary>
        internal const string CrossSliceProbeSource = @"
namespace AIFramework.Features.SliceA
{
    using AIFramework.Features.SliceB;

    public sealed class Probe
    {
        public AIFramework.Features.SliceB.Widget W;
    }
}

namespace AIFramework.Features.SliceB
{
    public sealed class Widget
    {
        public static string Label => ""x"";
    }
}
";

        /// <summary>AIF001 白名单反例：Interactions 切片允许引用任意其他切片（using + 使用点各一处）。</summary>
        internal const string InteractionsSliceProbeSource = @"
namespace AIFramework.Features.Interactions
{
    using AIFramework.Features.SliceB;

    public sealed class Interaction_X
    {
        public AIFramework.Features.SliceB.Widget W;
    }
}

namespace AIFramework.Features.SliceB
{
    public sealed class Widget
    {
        public static string Label => ""x"";
    }
}
";

        /// <summary>AIF001 放行反例：同切片内部的子命名空间互引 + 引用 Contracts，两者都不算跨切片。</summary>
        internal const string SameSliceAndContractsProbeSource = @"
namespace AIFramework.Features.SliceA
{
    public sealed class Widget
    {
        public static string Label => ""x"";
    }
}

namespace AIFramework.Features.SliceA.Tabs
{
    using AIFramework.Contracts.Cqe;
    using AIFramework.Features.SliceA;

    public sealed class TabProbe
    {
        public Widget W;
        public ICqeBus Bus;
    }
}
";

        /// <summary>AIF004 正例源码：Features 切片 <c>using AIFramework.Core</c> 并使用其中的类型。</summary>
        internal const string ForbiddenLayerProbeSource = @"
namespace AIFramework.Core
{
    public sealed class CqeBus
    {
    }
}

namespace AIFramework.Features.SliceA
{
    using AIFramework.Core;

    public sealed class Probe
    {
        public CqeBus Bus;
    }
}
";

        /// <summary>
        /// AIF001 + AIF004 合并夹具：在 Features 程序集里同时跨切片、又引 Core。
        /// 它同时充当「门禁关闭时必须 0 诊断」的对照物 —— 如果夹具本身写坏了，
        /// 门禁用例会假绿，所以另有正例断言它确实会违规。
        /// </summary>
        internal const string CrossSliceAndLayerProbeSource = @"
namespace AIFramework.Core
{
    public sealed class CqeBus
    {
    }
}

namespace AIFramework.Features.SliceA
{
    using AIFramework.Core;
    using AIFramework.Features.SliceB;

    public sealed class Probe
    {
        public AIFramework.Features.SliceB.Widget W;
        public CqeBus Bus;
    }
}

namespace AIFramework.Features.SliceB
{
    public sealed class Widget
    {
        public static string Label => ""x"";
    }
}
";

        /// <summary>AIF002 正例源码：纯逻辑程序集里出现 UnityEngine 的 using 与使用点。</summary>
        internal const string EngineReferenceProbeSource = @"
namespace UnityEngine
{
    public sealed class MonoBehaviour
    {
    }
}

namespace ContractProbe
{
    using UnityEngine;

    public sealed class Boot
    {
        public MonoBehaviour M;
    }
}
";

        /// <summary>AIF003 正例源码：直接读系统墙钟时间。</summary>
        internal const string DateTimeNowProbeSource = @"
namespace KernelProbe
{
    public static class WallClock
    {
        public static System.DateTime Now() => System.DateTime.Now;
    }
}
";

        /// <summary>AIF003 正例源码：无参 <c>new System.Random()</c> 是非确定性随机源。</summary>
        internal const string UnseededRandomProbeSource = @"
namespace KernelProbe
{
    public static class Dice
    {
        public static System.Random Create() => new System.Random();
    }
}
";

        /// <summary>AIF003 放行反例：带种子的 Random、DateTime 上的确定性成员、System.Math 都不该报。</summary>
        internal const string DeterministicApiProbeSource = @"
namespace KernelProbe
{
    public static class Deterministic
    {
        public static System.Random Seeded() => new System.Random(42);

        public static int DaysInJanuary2020() => System.DateTime.DaysInMonth(2020, 1);

        public static int Max(int a, int b) => System.Math.Max(a, b);

        public static System.DateTime Origin => System.DateTime.MinValue;
    }
}
";

        /// <summary>干净源码：Features 切片只依赖 Contracts 契约，自身命名空间自洽。</summary>
        internal const string CleanFeaturesProbeSource = @"
namespace AIFramework.Features.SliceA
{
    using AIFramework.Contracts.Cqe;

    public sealed class Handler
    {
        public ICqeBus Bus;
    }
}
";

        [Fact]
        public async Task CrossSliceReference_InFeaturesAssembly_ReportsAIF001()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(CrossSliceProbeSource);

            // 显式论证条数：源码里只有两个引用点 —— using 指令一处、字段类型一处。
            // 分析器对限定名链只取最右叶子、对 using 内部标识符直接跳过，所以条数一旦多于 2
            // 就说明去重逻辑坏了（AIFramework / Features / SliceB 这些段被重复报点）。
            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 2);
        }

        [Fact]
        public async Task InteractionsSlice_MayReferenceOtherSlices_WithoutAIF001()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(InteractionsSliceProbeSource);

            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 0);
        }

        [Fact]
        public async Task SameSliceAndContractsReferences_AreNotCrossSlice_WithoutAIF001()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(SameSliceAndContractsProbeSource);

            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 0);
            AssertDiagnosticCount(diagnostics, DiagnosticIds.ForbiddenLayerReference, 0);
        }

        [Fact]
        public async Task UsingCoreNamespace_InFeaturesAssembly_ReportsAIF004()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(ForbiddenLayerProbeSource);

            // 显式论证条数：using 指令一处 + 字段类型使用点一处，共 2 处引用点；AIF004 不按切片身份去重。
            AssertDiagnosticCount(diagnostics, DiagnosticIds.ForbiddenLayerReference, 2);
            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 0);
        }

        [Theory]
        [InlineData("AIFramework.Core")]
        [InlineData("AIFramework.Kernel.Turn")]
        [InlineData("AIFramework.Kernel.Realtime")]
        [InlineData("AIFramework.Adapters.Unity")]
        public async Task EveryForbiddenLayerNamespace_InFeaturesAssembly_ReportsAIF004(string forbiddenNamespaceName)
        {
            // 源码里只有一处全限定类型引用（没有 using），所以每条规则恰好 1 条诊断。
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(
                BuildForbiddenLayerProbeSource(forbiddenNamespaceName));

            AssertDiagnosticCount(diagnostics, DiagnosticIds.ForbiddenLayerReference, 1);
            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 0);
        }

        [Theory]
        [InlineData(ForeignAssemblyName)]
        [InlineData("AIFramework.Adapters.Console")]
        public async Task CrossSliceAndLayerRules_AreSilent_OutsideFeaturesAssembly(string assemblyName)
        {
            var compilation = TestCompilation.Create(assemblyName, CrossSliceAndLayerProbeSource);
            TestCompilation.AssertNoCompilerErrors(compilation);

            ImmutableArray<Diagnostic> diagnostics =
                await TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new FeaturesLayoutAnalyzer());

            Assert.True(
                diagnostics.Length == 0,
                "AIF001 / AIF004 只对 " + FeaturesAssemblyName + " 生效，" + assemblyName
                + " 不应产生任何诊断。实际：" + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public async Task CrossSliceAndLayerFixture_ReallyViolates_WhenCompiledAsFeatures()
        {
            // 门禁对照：同一份夹具在 Features 程序集名下必须同时触发 AIF001 与 AIF004（各 2 条），
            // 否则上面「门禁关闭 → 0 诊断」的用例会退化成无意义的假绿。
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeFeaturesAsync(CrossSliceAndLayerProbeSource);

            AssertDiagnosticCount(diagnostics, DiagnosticIds.CrossSlice, 2);
            AssertDiagnosticCount(diagnostics, DiagnosticIds.ForbiddenLayerReference, 2);
        }

        [Fact]
        public async Task EngineNamespace_InPureLogicAssembly_ReportsAIF002()
        {
            var compilation = TestCompilation.Create(ContractsAssemblyName, EngineReferenceProbeSource);
            TestCompilation.AssertNoCompilerErrors(compilation);

            ImmutableArray<Diagnostic> diagnostics =
                await TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new PureLogicAnalyzer());

            // 显式论证条数：using UnityEngine 一处 + MonoBehaviour 字段类型一处。
            AssertDiagnosticCount(diagnostics, DiagnosticIds.EngineReference, 2);
        }

        [Fact]
        public async Task EngineNamespace_OutsidePureLogicWhitelist_ReportsNothing()
        {
            var compilation = TestCompilation.Create(ForeignAssemblyName, EngineReferenceProbeSource);
            TestCompilation.AssertNoCompilerErrors(compilation);

            ImmutableArray<Diagnostic> diagnostics =
                await TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new PureLogicAnalyzer());

            Assert.True(
                diagnostics.Length == 0,
                "AIF002 只对纯逻辑程序集白名单生效，" + ForeignAssemblyName + " 不应产生任何诊断。实际："
                + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public async Task DateTimeNow_InRealtimeKernel_ReportsAIF003()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeRealtimeAsync(DateTimeNowProbeSource);

            // 属性引用只有一个属性访问操作，恰好 1 条；多于 1 条意味着语法节点分支重复报点。
            AssertDiagnosticCount(diagnostics, DiagnosticIds.NonDeterministicApi, 1);
        }

        [Fact]
        public async Task UnseededRandom_InRealtimeKernel_ReportsAIF003()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeRealtimeAsync(UnseededRandomProbeSource);

            AssertDiagnosticCount(diagnostics, DiagnosticIds.NonDeterministicApi, 1);
        }

        [Fact]
        public async Task SeededRandomAndDeterministicDateTimeApis_AreNotReported()
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzeRealtimeAsync(DeterministicApiProbeSource);

            Assert.True(
                diagnostics.Length == 0,
                "new Random(seed)、DateTime.DaysInMonth、DateTime.MinValue、Math.Max 都属于确定性用法，不应报 AIF003。实际："
                + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public async Task NonDeterministicApi_OutsidePureLogicWhitelist_ReportsNothing()
        {
            var compilation = TestCompilation.Create(ForeignAssemblyName, DateTimeNowProbeSource);
            TestCompilation.AssertNoCompilerErrors(compilation);

            ImmutableArray<Diagnostic> diagnostics =
                await TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new PureLogicAnalyzer());

            Assert.True(
                diagnostics.Length == 0,
                "AIF003 只对纯逻辑程序集白名单生效，" + ForeignAssemblyName + " 不应产生任何诊断。实际："
                + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public async Task CleanFeaturesCode_ProducesNoAnalyzerDiagnostics()
        {
            var compilation = TestCompilation.Create(FeaturesAssemblyName, CleanFeaturesProbeSource);
            TestCompilation.AssertNoCompilerErrors(compilation);

            ImmutableArray<Diagnostic> diagnostics = await TestCompilation.GetAnalyzerDiagnosticsAsync(
                compilation,
                new FeaturesLayoutAnalyzer(),
                new PureLogicAnalyzer());

            Assert.True(
                diagnostics.Length == 0,
                "干净切片代码不允许产生任何分析器诊断，实际：" + Environment.NewLine + Describe(diagnostics));
        }

        private static Task<ImmutableArray<Diagnostic>> AnalyzeFeaturesAsync(string source)
        {
            var compilation = TestCompilation.Create(FeaturesAssemblyName, source);
            TestCompilation.AssertNoCompilerErrors(compilation);
            return TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new FeaturesLayoutAnalyzer());
        }

        private static Task<ImmutableArray<Diagnostic>> AnalyzeRealtimeAsync(string source)
        {
            var compilation = TestCompilation.Create(RealtimeAssemblyName, source);
            TestCompilation.AssertNoCompilerErrors(compilation);
            return TestCompilation.GetAnalyzerDiagnosticsAsync(compilation, new PureLogicAnalyzer());
        }

        private static string BuildForbiddenLayerProbeSource(string forbiddenNamespaceName)
        {
            return string.Join(
                "\n",
                "namespace " + forbiddenNamespaceName,
                "{",
                "    public sealed class ForbiddenMarker",
                "    {",
                "    }",
                "}",
                string.Empty,
                "namespace AIFramework.Features.SliceA",
                "{",
                "    public sealed class Probe",
                "    {",
                "        public " + forbiddenNamespaceName + ".ForbiddenMarker Marker;",
                "    }",
                "}");
        }

        private static void AssertDiagnosticCount(
            ImmutableArray<Diagnostic> diagnostics,
            string expectedId,
            int expectedCount)
        {
            int actualCount = 0;
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (string.Equals(diagnostic.Id, expectedId, StringComparison.Ordinal))
                {
                    actualCount++;
                }
            }

            Assert.True(
                actualCount == expectedCount,
                "诊断 " + expectedId + " 期望 " + expectedCount + " 条，实际 " + actualCount + " 条。全部诊断："
                + Environment.NewLine + Describe(diagnostics));
        }

        private static string Describe(ImmutableArray<Diagnostic> diagnostics)
        {
            if (diagnostics.Length == 0)
            {
                return "(无诊断)";
            }

            var lines = new List<string>();
            foreach (Diagnostic diagnostic in diagnostics)
            {
                lines.Add(diagnostic.Id + " | " + diagnostic.GetMessage() + " | 行 " + diagnostic.Location.GetLineSpan());
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

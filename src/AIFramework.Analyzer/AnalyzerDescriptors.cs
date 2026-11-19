#nullable enable
using Microsoft.CodeAnalysis;

namespace AIFramework.Analyzer
{
    /// <summary>
    /// 四条规则的描述符集合：ID 走 <see cref="DiagnosticIds"/>，文案集中在此处，
    /// 全部为 Error 级、Category 固定为 "AIFramework"、默认启用。
    /// </summary>
    internal static class AnalyzerDescriptors
    {
        private const string Category = "AIFramework";

        /// <summary>AIF001：Features 内跨切片互引。</summary>
        internal static readonly DiagnosticDescriptor CrossSlice = new DiagnosticDescriptor(
            id: DiagnosticIds.CrossSlice,
            title: "切片禁止跨切片引用",
            messageFormat: "命名空间 '{0}' 属于切片 {1}，禁止在切片 {2} 中引用；跨切片协作必须走 Contracts 的 CQE/Tag，或收口到 AIFramework.Features.Interactions",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>AIF002：纯逻辑程序集引用引擎命名空间。</summary>
        internal static readonly DiagnosticDescriptor EngineReference = new DiagnosticDescriptor(
            id: DiagnosticIds.EngineReference,
            title: "纯逻辑程序集禁止引用引擎",
            messageFormat: "纯逻辑程序集 '{0}' 禁止引用引擎命名空间 '{1}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>AIF003：逻辑层直接调用非确定性系统 API。</summary>
        internal static readonly DiagnosticDescriptor NonDeterministicApi = new DiagnosticDescriptor(
            id: DiagnosticIds.NonDeterministicApi,
            title: "禁止非确定性系统 API",
            messageFormat: "逻辑层禁止直接调用 '{0}'，时间与随机必须走 ITimeProvider / IRandomSource",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>AIF004：Features 越层引用 Core / Kernel / Adapters。</summary>
        internal static readonly DiagnosticDescriptor ForbiddenLayerReference = new DiagnosticDescriptor(
            id: DiagnosticIds.ForbiddenLayerReference,
            title: "Features 禁止越层引用",
            messageFormat: "Features 切片禁止引用 '{0}'，只能依赖 AIFramework.Contracts 中的接口",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}

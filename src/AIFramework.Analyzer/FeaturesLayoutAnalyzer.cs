#nullable enable
using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AIFramework.Analyzer
{
    /// <summary>
    /// AIF001 + AIF004：只在 <c>AIFramework.Features</c> 程序集内生效。
    /// 同一个编译单元（compilation start）里同时注册两条规则，共用一次引用点解析。
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class FeaturesLayoutAnalyzer : DiagnosticAnalyzer
    {
        private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray = ImmutableArray.Create(
            AnalyzerDescriptors.CrossSlice,
            AnalyzerDescriptors.ForbiddenLayerReference);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            if (!string.Equals(context.Compilation.AssemblyName, SliceNamespaces.FeaturesAssemblyName, StringComparison.Ordinal))
            {
                return;
            }

            // 标识符 / 泛型名 / using 指令走同一个解析入口：using 指令在解析内部独占处理，
            // 其内部的标识符会被跳过，因此同一处引用只会产生一条诊断。
            context.RegisterSyntaxNodeAction(
                AnalyzeReferenceNode,
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName,
                SyntaxKind.UsingDirective);
        }

        private static void AnalyzeReferenceNode(SyntaxNodeAnalysisContext context)
        {
            NamespaceReference? reference = SliceNamespaces.ResolveReference(context.Node, context.SemanticModel);
            if (!(reference is NamespaceReference resolved))
            {
                return;
            }

            ReportCrossSlice(context, resolved);
            ReportForbiddenLayer(context, resolved);
        }

        private static void ReportCrossSlice(SyntaxNodeAnalysisContext context, NamespaceReference reference)
        {
            string? targetSlice = SliceNamespaces.GetSliceName(reference.NamespaceName);
            if (targetSlice == null)
            {
                return;
            }

            if (string.Equals(targetSlice, SliceNamespaces.InteractionsSliceName, StringComparison.Ordinal))
            {
                // Interactions 是对外收口的双边互动切片，任何切片都可以引用。
                return;
            }

            string? currentNamespace = SliceNamespaces.GetCurrentNamespaceName(context.SemanticModel, context.Node, context.CancellationToken);
            if (currentNamespace == null)
            {
                return;
            }

            string? currentSlice = SliceNamespaces.GetSliceName(currentNamespace);
            if (currentSlice == null)
            {
                return;
            }

            if (string.Equals(currentSlice, targetSlice, StringComparison.Ordinal)
                || string.Equals(currentSlice, SliceNamespaces.InteractionsSliceName, StringComparison.Ordinal))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDescriptors.CrossSlice,
                reference.Location,
                reference.NamespaceName,
                targetSlice,
                currentSlice));
        }

        private static void ReportForbiddenLayer(SyntaxNodeAnalysisContext context, NamespaceReference reference)
        {
            if (!IsForbiddenLayerNamespace(reference.NamespaceName))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDescriptors.ForbiddenLayerReference,
                reference.Location,
                reference.NamespaceName));
        }

        private static bool IsForbiddenLayerNamespace(string namespaceName)
        {
            // 按命名空间前缀判定，不依赖程序集身份：Features 本身不引用这些程序集，
            // 该规则主要防"Unity 里 asmdef 引用过宽"与内存编译负向测试。
            return SliceNamespaces.IsNamespaceWithin(namespaceName, SliceNamespaces.CoreNamespace)
                || SliceNamespaces.IsNamespaceWithin(namespaceName, SliceNamespaces.KernelNamespace)
                || SliceNamespaces.IsNamespaceWithin(namespaceName, SliceNamespaces.AdaptersNamespace);
        }
    }
}

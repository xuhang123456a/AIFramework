using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace AIFramework.Analyzer
{
    /// <summary>
    /// AIF002 + AIF003：在 5 个纯逻辑程序集（含 Contracts）内生效。
    /// 门禁一律按 <see cref="Compilation.AssemblyName"/> 精确匹配，不靠命名空间前缀猜程序集身份。
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PureLogicAnalyzer : DiagnosticAnalyzer
    {
        private const string DateTimeTypeName = "DateTime";
        private const string DateTimeTypeFullName = "System.DateTime";
        private const string RandomTypeFullName = "System.Random";
        private const string NowPropertyName = "Now";
        private const string UtcNowPropertyName = "UtcNow";

        private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray = ImmutableArray.Create(
            AnalyzerDescriptors.EngineReference,
            AnalyzerDescriptors.NonDeterministicApi);

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
            string? assemblyName = context.Compilation.AssemblyName;
            if (assemblyName == null || !SliceNamespaces.IsPureLogicAssembly(assemblyName))
            {
                return;
            }

            string reportedAssemblyName = assemblyName;

            context.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeEngineReference(nodeContext, reportedAssemblyName),
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName,
                SyntaxKind.UsingDirective);

            context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
            context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
        }

        private static void AnalyzeEngineReference(SyntaxNodeAnalysisContext context, string assemblyName)
        {
            NamespaceReference? reference = SliceNamespaces.ResolveReference(context.Node, context.SemanticModel);
            if (!(reference is NamespaceReference resolved))
            {
                return;
            }

            if (!SliceNamespaces.IsEngineNamespace(resolved.NamespaceName))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDescriptors.EngineReference,
                resolved.Location,
                assemblyName,
                resolved.NamespaceName));
        }

        private static void AnalyzePropertyReference(OperationAnalysisContext context)
        {
            if (!(context.Operation is IPropertyReferenceOperation operation))
            {
                return;
            }

            IPropertySymbol? property = operation.Property;
            if (property == null)
            {
                return;
            }

            string propertyName = property.Name;
            if (!string.Equals(propertyName, NowPropertyName, StringComparison.Ordinal)
                && !string.Equals(propertyName, UtcNowPropertyName, StringComparison.Ordinal))
            {
                return;
            }

            INamedTypeSymbol? containingType = property.ContainingType;
            if (containingType == null || !string.Equals(containingType.Name, DateTimeTypeName, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(containingType.ToDisplayString(), DateTimeTypeFullName, StringComparison.Ordinal))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDescriptors.NonDeterministicApi,
                operation.Syntax.GetLocation(),
                property.ToDisplayString()));
        }

        private static void AnalyzeObjectCreation(OperationAnalysisContext context)
        {
            if (!(context.Operation is IObjectCreationOperation operation))
            {
                return;
            }

            // 无参 new Random() 才报；new Random(seed) 是确定性用法，放行。
            if (operation.Arguments.Length != 0)
            {
                return;
            }

            IMethodSymbol? constructor = operation.Constructor;
            if (constructor == null)
            {
                return;
            }

            ITypeSymbol? createdType = operation.Type;
            if (createdType == null)
            {
                return;
            }

            if (!string.Equals(createdType.Name, "Random", StringComparison.Ordinal)
                || !string.Equals(createdType.ToDisplayString(), RandomTypeFullName, StringComparison.Ordinal))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDescriptors.NonDeterministicApi,
                operation.Syntax.GetLocation(),
                createdType.ToDisplayString() + "()"));
        }
    }
}

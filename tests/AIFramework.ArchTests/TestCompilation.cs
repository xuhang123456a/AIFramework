using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AIFramework.Contracts.Cqe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace AIFramework.ArchTests
{
    /// <summary>
    /// 内存编译装配器：把一段源码片段编译成可挂载 Roslyn 分析器的 <see cref="CSharpCompilation"/>。
    /// 程序集名（<c>assemblyName</c>）由调用方显式传入，这里不做任何预处理，
    /// 保证测到的是分析器真实的门禁行为，而不是测试夹具替它做的过滤。
    /// </summary>
    internal static class TestCompilation
    {
        /// <summary>
        /// 引用集合：CoreLib（<see cref="object"/> 所在程序集）+ 其所在运行时目录下的 System.Runtime / System.Console
        /// + 本仓 AIFramework.Contracts（供切片代码引用 CQE 契约）。
        /// </summary>
        private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

        /// <summary>用固定 C# 9 解析选项编译单个语法树，和纯逻辑工程的 LangVersion 对齐。</summary>
        internal static CSharpCompilation Create(string assemblyName, string source)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.CSharp9, DocumentationMode.None),
                path: assemblyName + ".Probe.cs");

            return CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>
        /// 断言内存编译本身零错误。编译有问题时分析器拿不到符号，负向用例会变成假绿或假红，
        /// 所以每个用例在断言诊断之前必须先过这一关。
        /// </summary>
        internal static void AssertNoCompilerErrors(Compilation compilation)
        {
            ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
            var errors = new List<string>();
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add(diagnostic.ToString());
                }
            }

            Assert.True(
                errors.Count == 0,
                "内存编译必须零错误，否则诊断断言失真。实际编译错误："
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors));
        }

        /// <summary>挂载分析器并只取分析器诊断（不含编译器自身的诊断）。</summary>
        internal static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(
            Compilation compilation,
            params DiagnosticAnalyzer[] analyzers)
        {
            CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(ImmutableArray.CreateRange(analyzers));
            return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        }

        private static ImmutableArray<MetadataReference> CreateReferences()
        {
            var references = ImmutableArray.CreateBuilder<MetadataReference>();

            Assembly coreLibrary = typeof(object).Assembly;
            references.Add(MetadataReference.CreateFromFile(coreLibrary.Location));

            string? runtimeDirectory = Path.GetDirectoryName(coreLibrary.Location);
            if (runtimeDirectory != null)
            {
                AddReferenceIfExists(references, Path.Combine(runtimeDirectory, "System.Runtime.dll"));
                AddReferenceIfExists(references, Path.Combine(runtimeDirectory, "System.Console.dll"));
            }

            references.Add(MetadataReference.CreateFromFile(typeof(ICqeBus).Assembly.Location));
            return references.ToImmutable();
        }

        private static void AddReferenceIfExists(ImmutableArray<MetadataReference>.Builder references, string path)
        {
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }
    }
}

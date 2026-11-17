using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AIFramework.Analyzer
{
    /// <summary>
    /// 一处"命名空间 / 类型引用点"的解析结果：目标命名空间字符串 + 报点位置。
    /// </summary>
    internal readonly struct NamespaceReference
    {
        internal NamespaceReference(string namespaceName, Location location)
        {
            NamespaceName = namespaceName;
            Location = location;
        }

        /// <summary>被引用符号所属的命名空间，形如 "AIFramework.Features.SliceB.Tabs"。</summary>
        internal string NamespaceName { get; }

        /// <summary>报表位置：标识符节点或整条 using 指令。</summary>
        internal Location Location { get; }
    }

    /// <summary>
    /// 命名空间解析与切片名提取的公共帮助类。AIF001~AIF004 共用同一套"引用点 → 命名空间"判定，
    /// 保证四条规则对同一个引用点的理解完全一致。
    /// </summary>
    internal static class SliceNamespaces
    {
        /// <summary>Features 聚合程序集名（AIF001 / AIF004 的门禁）。</summary>
        internal const string FeaturesAssemblyName = "AIFramework.Features";

        /// <summary>Features 根命名空间。</summary>
        internal const string FeaturesRootNamespace = "AIFramework.Features";

        /// <summary>跨切片互动的收口切片名。</summary>
        internal const string InteractionsSliceName = "Interactions";

        /// <summary>唯一的跨层契约命名空间。</summary>
        internal const string ContractsNamespace = "AIFramework.Contracts";

        /// <summary>因果总线所在层，Features 不得引用。</summary>
        internal const string CoreNamespace = "AIFramework.Core";

        /// <summary>两套内核所在层的根命名空间，Features 不得引用。</summary>
        internal const string KernelNamespace = "AIFramework.Kernel";

        /// <summary>引擎适配层命名空间根，Features 不得引用。</summary>
        internal const string AdaptersNamespace = "AIFramework.Adapters";

        /// <summary>
        /// 纯逻辑程序集白名单（AIF002 / AIF003 的适用面）。判定只认 <see cref="Compilation.AssemblyName"/> 精确匹配，
        /// 不靠命名空间前缀猜。
        /// </summary>
        private static readonly ImmutableHashSet<string> PureLogicAssemblyNames = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "AIFramework.Contracts",
            "AIFramework.Core",
            "AIFramework.Kernel.Turn",
            "AIFramework.Kernel.Realtime",
            "AIFramework.Features");

        /// <summary>引擎命名空间根：命中根本身或 "根." 前缀都算引擎泄漏。</summary>
        private static readonly ImmutableArray<string> EngineNamespaceRoots = ImmutableArray.Create(
            "UnityEngine",
            "UnityEditor",
            "Godot");

        private const string GlobalAliasPrefix = "global::";

        /// <summary>是否属于需要编译守卫的 5 个纯逻辑程序集之一。</summary>
        internal static bool IsPureLogicAssembly(string? assemblyName)
        {
            return assemblyName is { Length: > 0 } && PureLogicAssemblyNames.Contains(assemblyName);
        }

        /// <summary>目标命名空间是否是 UnityEngine / UnityEditor / Godot（含其子命名空间）。</summary>
        internal static bool IsEngineNamespace(string namespaceName)
        {
            foreach (string engineRoot in EngineNamespaceRoots)
            {
                if (IsNamespaceWithin(namespaceName, engineRoot))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>candidate 是否等于 root 或以 "root." 开头（区分 AIFramework.CoreX 与 AIFramework.Core）。</summary>
        internal static bool IsNamespaceWithin(string candidate, string root)
        {
            if (candidate.Length < root.Length)
            {
                return false;
            }

            if (!candidate.StartsWith(root, StringComparison.Ordinal))
            {
                return false;
            }

            return candidate.Length == root.Length || candidate[root.Length] == '.';
        }

        /// <summary>
        /// 从 "AIFramework.Features.SliceA.Tabs" 中取出切片名 "SliceA"；
        /// 不是 Features 子命名空间（含 Features 根本身、AIFramework.Contracts 等）时返回 null。
        /// </summary>
        internal static string? GetSliceName(string namespaceName)
        {
            if (!IsNamespaceWithin(namespaceName, FeaturesRootNamespace))
            {
                return null;
            }

            if (namespaceName.Length == FeaturesRootNamespace.Length)
            {
                // AIFramework.Features 根命名空间本身：没有切片身份，一律放行。
                return null;
            }

            string remainder = namespaceName.Substring(FeaturesRootNamespace.Length + 1);
            int separatorIndex = remainder.IndexOf('.');
            return separatorIndex < 0 ? remainder : remainder.Substring(0, separatorIndex);
        }

        /// <summary>
        /// 解析一个已注册语法节点的引用语义。返回 null 表示该节点不是"类型 / 命名空间引用点"或被去重跳过。
        /// </summary>
        internal static NamespaceReference? ResolveReference(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node is UsingDirectiveSyntax usingDirective)
            {
                return ResolveUsingDirective(usingDirective, semanticModel);
            }

            if (!(node is NameSyntax nameNode))
            {
                return null;
            }

            // using 指令内部的名字统一由 using 分支出报告，这里直接跳过，保证同一处只报一次。
            if (nameNode.FirstAncestorOrSelf<UsingDirectiveSyntax>() != null)
            {
                return null;
            }

            // namespace A.B 的名字链是声明点而不是引用点，不能算作越层 / 跨切片引用。
            if (IsNamespaceDeclarationName(nameNode))
            {
                return null;
            }

            // 只取限定名最右侧叶子：AIFramework.Core.CqeBus 只由 CqeBus 报一次。
            if (!IsRightmostLeaf(nameNode))
            {
                return null;
            }

            string? namespaceName = ResolveReferencedNamespaceName(semanticModel, nameNode);
            if (namespaceName == null)
            {
                return null;
            }

            // 表达式位置上 A.B.C 会被解析成成员访问链，此时"最后一个命名空间/类型段"才是报点：
            // AIFramework.Core.CqeBus.Instance 只在 CqeBus 上报一次，既不会漏报也不会重复报。
            if (HasLaterNamespaceOrTypeSegment(nameNode, semanticModel))
            {
                return null;
            }

            return new NamespaceReference(namespaceName, nameNode.GetLocation());
        }

        /// <summary>
        /// 判断节点是否是其限定名的"最右叶子"：A.B.C 解析为 QualifiedName(QualifiedName(A,B),C) 时只有 C 为 true，
        /// A、B 一律 false。
        /// </summary>
        internal static bool IsRightmostLeaf(SyntaxNode node)
        {
            SyntaxNode current = node;
            while (true)
            {
                SyntaxNode? parent = current.Parent;
                if (parent is QualifiedNameSyntax qualifiedName)
                {
                    if (qualifiedName.Right != current)
                    {
                        return false;
                    }

                    current = qualifiedName;
                    continue;
                }

                return true;
            }
        }

        /// <summary>
        /// 成员访问 / 限定名链上是否还有更靠后的段仍解析为命名空间或类型。有则说明当前节点只是前缀，
        /// 交由最后一段统一报点，保证一条引用链只产生一条诊断。
        /// </summary>
        private static bool HasLaterNamespaceOrTypeSegment(SyntaxNode node, SemanticModel semanticModel)
        {
            SyntaxNode current = node;
            while (true)
            {
                SyntaxNode? parent = current.Parent;

                if (parent is QualifiedNameSyntax qualifiedName)
                {
                    if (qualifiedName.Right == current)
                    {
                        // 本段已是限定名右尾，继续向上找外层链条。
                        current = parent;
                        continue;
                    }

                    return IsNamespaceOrTypeName(semanticModel, qualifiedName.Right);
                }

                if (parent is MemberAccessExpressionSyntax memberAccess)
                {
                    if (memberAccess.Name == current)
                    {
                        current = parent;
                        continue;
                    }

                    return IsNamespaceOrTypeName(semanticModel, memberAccess.Name);
                }

                // 已到达链尾。
                return false;
            }
        }

        /// <summary>
        /// 判断节点是否属于 namespace 声明自身的名字链（如 <c>namespace AIFramework.Core</c>）：
        /// 那是声明点，不是引用点。
        /// </summary>
        private static bool IsNamespaceDeclarationName(SyntaxNode node)
        {
            SyntaxNode current = node;
            while (current.Parent is QualifiedNameSyntax qualifiedName)
            {
                current = qualifiedName;
            }

            return current.Parent is BaseNamespaceDeclarationSyntax;
        }

        private static bool IsNamespaceOrTypeName(SemanticModel semanticModel, ExpressionSyntax name)
        {
            ISymbol? symbol = semanticModel.GetSymbolInfo(name).Symbol;
            return symbol is INamespaceSymbol || symbol is ITypeSymbol;
        }

        /// <summary>
        /// 取引用点所在命名空间，用于 AIF001 判定"当前切片"。命名空间声明内部直接读语义符号；
        /// 顶层 using（在所有 namespace 声明之外）退化为取本文件第一个命名空间声明。
        /// </summary>
        internal static string? GetCurrentNamespaceName(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
        {
            ISymbol? enclosingSymbol = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
            INamespaceSymbol? enclosingNamespace = enclosingSymbol as INamespaceSymbol ?? enclosingSymbol?.ContainingNamespace;
            if (enclosingNamespace != null && !enclosingNamespace.IsGlobalNamespace)
            {
                return enclosingNamespace.ToDisplayString();
            }

            return GetFileNamespaceName(semanticModel, cancellationToken);
        }

        private static NamespaceReference? ResolveUsingDirective(UsingDirectiveSyntax usingDirective, SemanticModel semanticModel)
        {
            // 别名 using 不在本分支处理，避免与标识符分支对同一处重复报点。
            if (usingDirective.Alias != null)
            {
                return null;
            }

            switch (usingDirective.Name)
            {
                case QualifiedNameSyntax qualifiedName:
                    return ResolveUsingName(qualifiedName, semanticModel, usingDirective);
                case IdentifierNameSyntax identifierName:
                    return ResolveUsingName(identifierName, semanticModel, usingDirective);
                default:
                    return null;
            }
        }

        private static NamespaceReference? ResolveUsingName(
            NameSyntax name,
            SemanticModel semanticModel,
            UsingDirectiveSyntax usingDirective)
        {
            string? namespaceName = ResolveReferencedNamespaceName(semanticModel, name);
            if (namespaceName == null)
            {
                // 兜底：个别版本对 using 名称不返回符号，按文本逐段走全局命名空间，规则判定不受影响。
                namespaceName = ResolveNamespacePrefixByText(semanticModel.Compilation, name.ToString());
            }

            if (namespaceName == null)
            {
                return null;
            }

            return new NamespaceReference(namespaceName, usingDirective.GetLocation());
        }

        /// <summary>
        /// 符号 → 命名空间：命名空间直接取显示名；类型取 ContainingNamespace（对 using static 同样成立）；
        /// 方法、字段、属性等一律返回 null —— 本组规则只关心类型与命名空间引用。
        /// </summary>
        private static string? ResolveReferencedNamespaceName(SemanticModel semanticModel, ExpressionSyntax name)
        {
            SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(name);
            return ToNamespaceName(symbolInfo.Symbol);
        }

        private static string? ToNamespaceName(ISymbol? symbol)
        {
            if (symbol is ITypeParameterSymbol)
            {
                // 泛型参数不是"引用了某个命名空间里的类型"。
                return null;
            }

            if (symbol is INamespaceSymbol namespaceSymbol)
            {
                return namespaceSymbol.IsGlobalNamespace ? null : namespaceSymbol.ToDisplayString();
            }

            if (symbol is ITypeSymbol typeSymbol)
            {
                INamespaceSymbol? containingNamespace = typeSymbol.ContainingNamespace;
                if (containingNamespace == null || containingNamespace.IsGlobalNamespace)
                {
                    return null;
                }

                return containingNamespace.ToDisplayString();
            }

            return null;
        }

        private static string? GetFileNamespaceName(SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (!(semanticModel.SyntaxTree.GetRoot(cancellationToken) is CompilationUnitSyntax root))
            {
                return null;
            }

            BaseNamespaceDeclarationSyntax? namespaceDeclaration = null;
            foreach (MemberDeclarationSyntax member in root.Members)
            {
                if (member is BaseNamespaceDeclarationSyntax candidate)
                {
                    namespaceDeclaration = candidate;
                    break;
                }
            }

            if (namespaceDeclaration == null)
            {
                return null;
            }

            // 下钻到最内层命名空间，保证 "namespace A { namespace B {" 也能得到 A.B。
            while (true)
            {
                BaseNamespaceDeclarationSyntax? inner = null;
                foreach (MemberDeclarationSyntax member in namespaceDeclaration.Members)
                {
                    if (member is BaseNamespaceDeclarationSyntax candidate)
                    {
                        inner = candidate;
                        break;
                    }
                }

                if (inner == null)
                {
                    break;
                }

                namespaceDeclaration = inner;
            }

            INamespaceSymbol? declaredNamespace =
                semanticModel.GetDeclaredSymbol(namespaceDeclaration, cancellationToken) as INamespaceSymbol;
            if (declaredNamespace == null || declaredNamespace.IsGlobalNamespace)
            {
                return null;
            }

            return declaredNamespace.ToDisplayString();
        }

        /// <summary>
        /// 文本兜底解析：只在符号解析拿不到命名空间时使用。逐段匹配全局命名空间，
        /// 遇到类型名（如 using static 的最后一段）即返回已解析出的命名空间前缀。
        /// </summary>
        private static string? ResolveNamespacePrefixByText(Compilation compilation, string nameText)
        {
            string text = nameText.Trim();
            if (text.StartsWith(GlobalAliasPrefix, StringComparison.Ordinal))
            {
                text = text.Substring(GlobalAliasPrefix.Length);
            }

            if (text.Length == 0)
            {
                return null;
            }

            string[] segments = text.Split('.');
            INamespaceSymbol current = compilation.GlobalNamespace;
            for (int i = 0; i < segments.Length; i++)
            {
                INamespaceSymbol? next = null;
                foreach (INamespaceSymbol candidate in current.GetNamespaceMembers())
                {
                    if (string.Equals(candidate.Name, segments[i], StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next == null)
                {
                    return current.IsGlobalNamespace ? null : current.ToDisplayString();
                }

                current = next;
            }

            return current.IsGlobalNamespace ? null : current.ToDisplayString();
        }
    }
}

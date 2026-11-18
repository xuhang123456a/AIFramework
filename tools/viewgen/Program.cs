using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.ViewGen
{
    /// <summary>仓库布局推断：用于把生成物头部注释里的源路径固定成"仓库相对路径"，保证跨机器生成结果一致。</summary>
    public static class RepoLayout
    {
        /// <summary>从给定目录向上找含 <c>AIFramework.Pure.sln</c> 的目录；找不到返回 <c>null</c>。</summary>
        public static string? FindRoot(string startDirectory)
        {
            DirectoryInfo? current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AIFramework.Pure.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        /// <summary>转成仓库相对路径（正斜杠）；不在仓库内时退化为文件名。</summary>
        public static string ToRepoRelative(string repoRoot, string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(repoRoot);

            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).Replace('\\', '/');
            }

            return Path.GetFileName(full);
        }
    }

    internal static class Program
    {
        private const string DefaultBinderNamespace = "AIFramework.Adapters.Unity.Views";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            string? jsonPath = null;
            string? interfaceOut = null;
            string? binderOut = null;
            string binderNamespace = DefaultBinderNamespace;
            bool check = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--check":
                        check = true;
                        break;

                    case "--interface-out":
                        interfaceOut = i + 1 < args.Length ? args[++i] : null;
                        break;

                    case "--binder-out":
                        binderOut = i + 1 < args.Length ? args[++i] : null;
                        break;

                    case "--binder-namespace":
                        binderNamespace = i + 1 < args.Length ? args[++i] : binderNamespace;
                        break;

                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine("未知参数：" + arg);
                            PrintUsage();
                            return 2;
                        }

                        jsonPath = arg;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(jsonPath))
            {
                Console.Error.WriteLine("缺少绑定描述文件路径。");
                PrintUsage();
                return 2;
            }

            string fullJsonPath = Path.GetFullPath(jsonPath);
            if (!File.Exists(fullJsonPath))
            {
                Console.Error.WriteLine("找不到绑定描述：" + fullJsonPath);
                return 2;
            }

            string jsonDirectory = Path.GetDirectoryName(fullJsonPath) ?? ".";
            string? repoRoot = RepoLayout.FindRoot(jsonDirectory);
            string sourceForHeader = repoRoot != null
                ? RepoLayout.ToRepoRelative(repoRoot, fullJsonPath)
                : Path.GetFileName(fullJsonPath);

            PanelSpec spec;
            try
            {
                spec = BindingSpecReader.Parse(File.ReadAllText(fullJsonPath));
            }
            catch (ViewGenException ex)
            {
                Console.Error.WriteLine("绑定描述不合法：" + ex.Message);
                return 3;
            }

            string resolvedInterfaceOut = interfaceOut != null
                ? Path.GetFullPath(interfaceOut)
                : Path.Combine(jsonDirectory, "I" + spec.Panel + "View.g.cs");

            string resolvedBinderOut = binderOut != null
                ? Path.GetFullPath(binderOut)
                : BuildDefaultBinderPath(repoRoot, jsonDirectory, spec);

            var options = new ViewGenOptions
            {
                InterfacePath = resolvedInterfaceOut,
                BinderPath = resolvedBinderOut,
                BinderNamespace = binderNamespace,
                SourcePath = sourceForHeader
            };

            if (check)
            {
                IReadOnlyList<string> stale = ViewCodeGenerator.FindStale(spec, options);
                if (stale.Count == 0)
                {
                    Console.WriteLine("生成物与描述一致：" + sourceForHeader);
                    return 0;
                }

                Console.Error.WriteLine("生成物已过期（请重新生成，禁止手改 .g.cs）：");
                for (int i = 0; i < stale.Count; i++)
                {
                    Console.Error.WriteLine("  - " + stale[i]);
                }

                return 4;
            }

            IReadOnlyList<string> written = ViewCodeGenerator.Emit(spec, options);
            Console.WriteLine("已生成 " + written.Count + " 个文件（源：" + sourceForHeader + "）：");
            for (int i = 0; i < written.Count; i++)
            {
                Console.WriteLine("  - " + written[i]);
            }

            return 0;
        }

        private static string BuildDefaultBinderPath(string? repoRoot, string jsonDirectory, PanelSpec spec)
        {
            if (repoRoot == null)
            {
                return Path.Combine(jsonDirectory, spec.BinderName + ".g.cs");
            }

            return Path.Combine(
                repoRoot,
                "src",
                "Adapters",
                "AIFramework.Adapters.Unity",
                "Runtime",
                "Views",
                spec.BinderName + ".g.cs");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：dotnet run --project tools/viewgen -- <bindings.json> [选项]");
            Console.WriteLine();
            Console.WriteLine("选项：");
            Console.WriteLine("  --interface-out <path>     接口输出路径（默认：与描述同目录的 I<Panel>View.g.cs）");
            Console.WriteLine("  --binder-out <path>        Binder 输出路径（默认：Adapter 包 Runtime/Views/<Panel>ViewBinder.g.cs）");
            Console.WriteLine("  --binder-namespace <ns>    Binder 命名空间（默认：" + DefaultBinderNamespace + "）");
            Console.WriteLine("  --check                    只校验磁盘生成物是否与描述一致（CI 用，过期返回 4）");
        }
    }
}

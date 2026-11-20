using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.AnalyticsCheck
{
    /// <summary>
    /// 命令行入口：扫描 <c>src/</c> 里的埋点调用点，与 <c>AnalyticsContracts.cs</c> 的事件号契约对账。
    /// <para>
    /// 退出码：0 = 无错误（允许"魔法数字/废弃号"提示）；1 = 有错误；2 = 用法错误；3 = 契约解析失败。
    /// </para>
    /// </summary>
    public static class AnalyticsCheckCli
    {
        public const int ExitSuccess = 0;
        public const int ExitErrors = 1;
        public const int ExitUsage = 2;
        public const int ExitParseFailure = 3;

        public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            string root = workingDirectory ?? Directory.GetCurrentDirectory();
            string repository = root;
            bool checkOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--repo":
                        if (i + 1 >= args.Length)
                        {
                            error.WriteLine("--repo 需要一个仓库根目录参数。");
                            return ExitUsage;
                        }

                        repository = Path.GetFullPath(args[++i], root);
                        break;
                    case "--check":
                        // 本工具只做检查、从不改文件；--check 保留为显式开关，与默认行为一致。
                        checkOnly = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage(output);
                        return ExitSuccess;
                    default:
                        error.WriteLine("未知参数：" + args[i]);
                        PrintUsage(error);
                        return ExitUsage;
                }
            }

            if (!Directory.Exists(repository))
            {
                error.WriteLine("仓库根目录不存在：" + repository);
                return ExitUsage;
            }

            string contractPath = Path.Combine(repository, AnalyticsRules.ContractRelativePath);
            if (!File.Exists(contractPath))
            {
                error.WriteLine("[契约解析失败] 找不到埋点契约：" + contractPath);
                return ExitParseFailure;
            }

            string sourceDirectory = Path.Combine(repository, AnalyticsRules.SourceDirectoryName);
            if (!Directory.Exists(sourceDirectory))
            {
                error.WriteLine("[契约解析失败] 找不到源码目录：" + sourceDirectory);
                return ExitParseFailure;
            }

            string contractLocation = ToRelativePath(repository, contractPath);
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                File.ReadAllText(contractPath),
                contractLocation);

            if (!parsed.Success || parsed.Contract == null)
            {
                error.WriteLine("[契约解析失败] " + parsed.FailureReason);
                return ExitParseFailure;
            }

            Dictionary<string, string> files = LoadSourceFiles(repository, sourceDirectory);
            AnalyticsScanResult result = AnalyticsRules.Scan(files, parsed.Contract, parsed.Issues);

            AnalyticsContract contract = parsed.Contract;
            output.WriteLine("埋点契约：" + contractLocation
                + "（常量 " + contract.Events.Count + " 个"
                + (HasReservedEvent(contract) ? "，含保留哨兵 " + AnalyticsRules.ReservedEventName + "=0" : string.Empty)
                + "；可统计事件 " + contract.EventCount + " 个；构造参数 " + contract.ConstructorParameterCount + " 个）");
            output.WriteLine("扫描 " + files.Count + " 个 *.cs 文件（已排除 tests/artifacts/bin/obj），发现构造调用 "
                + result.ConstructorCallCount + " 处。");
            output.WriteLine(DescribeReferenceDistribution(contract, result));

            for (int i = 0; i < result.Issues.Count; i++)
            {
                output.WriteLine(result.Issues[i].ToString());
            }

            output.WriteLine("事件 " + contract.EventCount + " 个、引用 " + result.ReferenceCount
                + " 次、错误 " + result.ErrorCount + "、提示 " + result.HintCount + "。");
            output.WriteLine("说明：文本级扫描（无 Roslyn/AST），注释/字符串/switch 映射里的 AnalyticsEventIds.X 都计入引用"
                + "（不等于真实上报次数）；构造调用按括号配对读取，支持跨行与嵌套调用。");

            if (result.ErrorCount > 0)
            {
                output.WriteLine("--check 未通过：先修上面的错误（悬空引用/撞号/字段数量不符）。");
                return ExitErrors;
            }

            output.WriteLine(checkOnly
                ? "--check 通过：埋点调用与事件号契约一致。"
                : "通过：埋点调用与事件号契约一致。");
            return ExitSuccess;
        }

        /// <summary>读 <c>src/</c> 下全部 <c>*.cs</c>（按路径排序，输出稳定；跳过排除目录）。</summary>
        public static Dictionary<string, string> LoadSourceFiles(string repository, string sourceDirectory)
        {
            if (repository == null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            if (sourceDirectory == null)
            {
                throw new ArgumentNullException(nameof(sourceDirectory));
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] paths = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.Ordinal);

            for (int i = 0; i < paths.Length; i++)
            {
                string relative = ToRelativePath(repository, paths[i]);
                if (AnalyticsRules.IsExcludedPath(relative))
                {
                    continue;
                }

                try
                {
                    files[relative] = File.ReadAllText(paths[i]);
                }
                catch (IOException)
                {
                    // 读不到就当不存在（与 loccheck 同策略）：不因单个文件阻断整仓检查。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return files;
        }

        /// <summary>仓库相对路径，统一用 <c>/</c>（跨平台输出稳定）。</summary>
        public static string ToRelativePath(string repository, string path)
        {
            string relative = Path.GetRelativePath(repository, path);
            return relative.Replace('\\', '/');
        }

        /// <summary>契约里是否声明了保留哨兵 None。</summary>
        private static bool HasReservedEvent(AnalyticsContract contract)
        {
            for (int i = 0; i < contract.Events.Count; i++)
            {
                if (contract.Events[i].IsReserved)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按契约声明顺序列出每个事件的文本引用次数（不含保留哨兵 None）。</summary>
        private static string DescribeReferenceDistribution(AnalyticsContract contract, AnalyticsScanResult result)
        {
            var builder = new System.Text.StringBuilder("引用分布：");
            bool first = true;
            for (int i = 0; i < contract.Events.Count; i++)
            {
                AnalyticsEventDefinition definition = contract.Events[i];
                if (definition.IsReserved)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append('、');
                }

                builder.Append(definition.Name);
                builder.Append('=');
                builder.Append(result.ReferenceCounts.TryGetValue(definition.Name, out int count) ? count : 0);
                first = false;
            }

            if (first)
            {
                builder.Append("(无)");
            }

            return builder.ToString();
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/analyticscheck -- [--repo <仓库根>] [--check]");
            writer.WriteLine();
            writer.WriteLine("扫描 src/ 下所有 *.cs（排除 tests/artifacts/bin/obj），校验埋点调用与事件号契约：");
            writer.WriteLine("  ✗ 悬空引用（AnalyticsEventIds.XXX 不在契约里）");
            writer.WriteLine("  ✗ 构造参数数量与 AnalyticsEvent 构造函数不一致（字段数量/顺序错位）");
            writer.WriteLine("  ✗ 契约内撞号/重复名/非正事件号（保留哨兵 None=0 豁免）");
            writer.WriteLine("  ⚠ 事件号直接写数字（魔法数字）");
            writer.WriteLine("  ⚠ 定义了但引用 0 次（疑似废弃号）");
            writer.WriteLine();
            writer.WriteLine("退出码：0 通过 / 1 有错误 / 2 用法错误 / 3 契约解析失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return AnalyticsCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

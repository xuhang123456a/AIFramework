#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.TableGen
{
    /// <summary>仓库布局推断：默认目标目录都相对仓库根解析，保证从任意工作目录跑结果一致。</summary>
    public static class RepoLayout
    {
        /// <summary>从给定目录向上找含 <c>AIFramework.Pure.sln</c> 的目录；找不到返回 <c>null</c>。</summary>
        /// <param name="startDirectory">起点目录。</param>
        /// <returns>仓库根绝对路径或 <c>null</c>。</returns>
        public static string? FindRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(Path.GetFullPath(startDirectory));
            }
            catch (ArgumentException)
            {
                return null;
            }

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
    }

    /// <summary>
    /// 命令行入口：<c>dotnet run --project tools/tablegen -- [选项]</c>。
    /// <para>
    /// 退出码与 <c>tools/viewgen</c> 对齐：<c>0</c> 成功、<c>1</c> 漂移（<c>--check</c> 不一致）、
    /// <c>2</c> 用法/环境错误、<c>3</c> 表定义不合法。CI 只关心 <c>0</c> 与非 <c>0</c>。
    /// </para>
    /// </summary>
    public static class TableGenCli
    {
        /// <summary>成功。</summary>
        public const int ExitSuccess = 0;

        /// <summary>漂移：重新生成的结果与目标目录不一致。</summary>
        public const int ExitDrift = 1;

        /// <summary>用法或环境错误（参数非法、目录不存在等）。</summary>
        public const int ExitUsage = 2;

        /// <summary>表定义不合法（未知列类型、行字段缺失、值类型不匹配等）。</summary>
        public const int ExitInvalidTable = 3;

        private static readonly string[] DefaultTargetRelativePaths =
        {
            Path.Combine("unity", "Assets", "StreamingAssets", "AIFrameworkDemo"),
            Path.Combine("src", "Adapters", "AIFramework.Adapters.Console", "DemoTables")
        };

        /// <summary>执行一次 CLI。</summary>
        /// <param name="args">命令行参数（不含 exe 名）。</param>
        /// <param name="output">标准输出（测试可注入 <see cref="StringWriter"/>）。</param>
        /// <param name="error">标准错误。</param>
        /// <param name="workingDirectory">解析相对路径的基准目录；默认当前工作目录。</param>
        /// <returns>退出码。</returns>
        public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            string baseDirectory = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());

            string? tablesDirectory = null;
            var targets = new List<string>();
            var verifyDirectories = new List<string>();
            bool check = false;
            bool help = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--check":
                        check = true;
                        break;

                    case "-h":
                    case "--help":
                        help = true;
                        break;

                    case "--tables":
                        if (i + 1 >= args.Length)
                        {
                            return Fail(error, "--tables 需要一个目录参数。");
                        }

                        tablesDirectory = args[++i];
                        break;

                    case "--out":
                        if (i + 1 >= args.Length)
                        {
                            return Fail(error, "--out 需要一个目录参数。");
                        }

                        targets.Add(args[++i]);
                        break;

                    case "--verify-against":
                        if (i + 1 >= args.Length)
                        {
                            return Fail(error, "--verify-against 需要一个目录参数。");
                        }

                        verifyDirectories.Add(args[++i]);
                        break;

                    default:
                        return Fail(error, "未知参数：" + arg);
                }
            }

            if (help)
            {
                PrintUsage(output);
                return ExitSuccess;
            }

            string? repoRoot = RepoLayout.FindRoot(baseDirectory);

            if (tablesDirectory == null)
            {
                if (repoRoot == null)
                {
                    return Fail(error, "找不到仓库根（AIFramework.Pure.sln），请用 --tables 显式指定表定义目录。");
                }

                tablesDirectory = Path.Combine(repoRoot, "tables");
            }

            string resolvedTables = ResolvePath(baseDirectory, tablesDirectory);

            if (targets.Count == 0)
            {
                string root = repoRoot ?? baseDirectory;
                for (int i = 0; i < DefaultTargetRelativePaths.Length; i++)
                {
                    targets.Add(Path.Combine(root, DefaultTargetRelativePaths[i]));
                }
            }

            IReadOnlyList<SchemaExport> exports;
            try
            {
                exports = TableExporter.ExportDirectory(resolvedTables);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Fail(error, ex.Message);
            }
            catch (TableGenException ex)
            {
                error.WriteLine("表定义不合法：" + ex.Message);
                return ExitInvalidTable;
            }
            catch (IOException ex)
            {
                return Fail(error, "读取表定义失败：" + ex.Message);
            }

            if (check)
            {
                int worst = ExitSuccess;
                for (int i = 0; i < targets.Count; i++)
                {
                    string target = ResolvePath(baseDirectory, targets[i]);
                    IReadOnlyList<TableDifference> differences = TableComparison.CompareDirectory(exports, target);
                    if (differences.Count == 0)
                    {
                        output.WriteLine("一致 " + target + "（" + exports.Count + " 张表）");
                        continue;
                    }

                    worst = ExitDrift;
                    error.WriteLine("配置表已漂移：" + target);
                    for (int k = 0; k < differences.Count; k++)
                    {
                        error.WriteLine("  - [" + KindLabel(differences[k].Kind) + "] " + differences[k].Detail);
                    }
                }

                if (worst != ExitSuccess)
                {
                    error.WriteLine("请执行：dotnet run --project tools/tablegen -- 重新导出后提交 .bin。");
                    return worst;
                }

                output.WriteLine("--check 通过：" + exports.Count + " 张表 × " + targets.Count + " 个目标目录逐字节一致。");
            }
            else
            {
                try
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        string target = ResolvePath(baseDirectory, targets[i]);
                        Directory.CreateDirectory(target);
                        for (int k = 0; k < exports.Count; k++)
                        {
                            File.WriteAllBytes(
                                Path.Combine(target, exports[k].TableName + ".bin"),
                                exports[k].Bytes);
                        }

                        output.WriteLine("已导出 " + exports.Count + " 张表 → " + target);
                        for (int k = 0; k < exports.Count; k++)
                        {
                            output.WriteLine(
                                "  " + exports[k].TableName + ".bin  " + exports[k].Bytes.Length
                                + " 字节, " + exports[k].RowCount + " 行");
                        }
                    }
                }
                catch (IOException ex)
                {
                    return Fail(error, "写入导出结果失败：" + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Fail(error, "写入导出结果被拒绝：" + ex.Message);
                }
            }

            for (int i = 0; i < verifyDirectories.Count; i++)
            {
                string verifyTarget = ResolvePath(baseDirectory, verifyDirectories[i]);
                IReadOnlyList<TableDifference> differences = TableComparison.CompareDirectory(exports, verifyTarget);
                output.WriteLine("与既有产物比对：" + verifyTarget);
                if (differences.Count == 0)
                {
                    output.WriteLine("  " + exports.Count + " 张表全部逐字节一致。");
                    continue;
                }

                error.WriteLine("与既有产物不一致：" + verifyTarget);
                for (int k = 0; k < differences.Count; k++)
                {
                    error.WriteLine("  - [" + KindLabel(differences[k].Kind) + "] " + differences[k].Detail);
                }

                return ExitDrift;
            }

            return ExitSuccess;
        }

        private static string KindLabel(TableDifferenceKind kind)
        {
            switch (kind)
            {
                case TableDifferenceKind.Missing:
                    return "缺失";
                case TableDifferenceKind.Content:
                    return "内容不一致";
                case TableDifferenceKind.Extra:
                    return "多余";
                default:
                    return kind.ToString();
            }
        }

        private static string ResolvePath(string baseDirectory, string path)
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }

        private static int Fail(TextWriter error, string message)
        {
            error.WriteLine(message);
            error.WriteLine("用 --help 查看用法。");
            return ExitUsage;
        }

        /// <summary>打印用法。</summary>
        /// <param name="output">输出目标。</param>
        public static void PrintUsage(TextWriter output)
        {
            output.WriteLine("用法：dotnet run --project tools/tablegen -- [选项]");
            output.WriteLine();
            output.WriteLine("选项：");
            output.WriteLine("  --tables <dir>           JSON 表定义目录（默认：<仓库根>/tables）");
            output.WriteLine("  --out <dir>              输出目录，可重复；省略时写两个默认目标：");
            output.WriteLine("                             unity/Assets/StreamingAssets/AIFrameworkDemo");
            output.WriteLine("                             src/Adapters/AIFramework.Adapters.Console/DemoTables");
            output.WriteLine("  --check                  只校验：把 JSON 重新编码并与目标目录逐字节比对（漂移返回 1，不写盘）");
            output.WriteLine("  --verify-against <dir>   额外与既有产物目录逐字节比对，可重复（迁移期用）");
            output.WriteLine("  -h, --help               打印本帮助");
            output.WriteLine();
            output.WriteLine("退出码：0 成功 / 1 漂移 / 2 用法或环境错误 / 3 表定义不合法");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return TableGenCli.Run(args, Console.Out, Console.Error);
        }
    }
}

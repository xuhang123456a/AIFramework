using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.LocConvert
{
    /// <summary>
    /// 命令行入口：TbLocalization JSON ↔ CSV 双向转换 + 冲突检测。
    /// <para>退出码：0 通过；1 有冲突/失败；2 用法错误；3 读写失败。</para>
    /// </summary>
    public static class LocConvertCli
    {
        public const int ExitSuccess = 0;
        public const int ExitIssues = 1;
        public const int ExitUsage = 2;
        public const int ExitIo = 3;

        public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            string root = workingDirectory ?? Directory.GetCurrentDirectory();
            string tablesDirectory = Path.Combine(root, "tables");
            string? exportPath = null;
            string? importPath = null;
            bool checkOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--tables":
                        if (i + 1 >= args.Length)
                        {
                            error.WriteLine("--tables 需要一个目录参数。");
                            return ExitUsage;
                        }

                        tablesDirectory = Path.GetFullPath(args[++i], root);
                        break;
                    case "--export":
                        if (i + 1 >= args.Length)
                        {
                            error.WriteLine("--export 需要一个 CSV 输出路径。");
                            return ExitUsage;
                        }

                        exportPath = Path.GetFullPath(args[++i], root);
                        break;
                    case "--import":
                        if (i + 1 >= args.Length)
                        {
                            error.WriteLine("--import 需要一个 CSV 输入路径。");
                            return ExitUsage;
                        }

                        importPath = Path.GetFullPath(args[++i], root);
                        break;
                    case "--check":
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

            string jsonPath = Path.Combine(tablesDirectory, "TbLocalization.json");

            if (importPath != null)
            {
                if (!File.Exists(importPath))
                {
                    error.WriteLine("CSV 不存在：" + importPath);
                    return ExitIo;
                }

                if (!LocConverter.TryParseCsv(File.ReadAllText(importPath), out List<LocRow> imported, out string parseError))
                {
                    error.WriteLine("[CSV 解析失败] " + parseError);
                    return ExitIssues;
                }

                var issues = new List<string>();
                LocConverter.CheckConflicts(imported, issues);
                foreach (string issue in issues)
                {
                    output.WriteLine("[冲突] " + issue);
                }

                if (issues.Count > 0)
                {
                    output.WriteLine("导入中止：先修掉冲突再导入（避免把错误数据写进表）。");
                    return ExitIssues;
                }

                string json = LocConverter.ToLocalizationJson(imported);
                File.WriteAllText(jsonPath, json);
                output.WriteLine("已导入 " + imported.Count + " 行 → " + jsonPath);
                return ExitSuccess;
            }

            if (!File.Exists(jsonPath))
            {
                error.WriteLine("找不到文案表：" + jsonPath);
                return ExitIo;
            }

            if (!LocConverter.TryParseLocalizationJson(File.ReadAllText(jsonPath), out List<LocRow> rows, out string jsonError))
            {
                error.WriteLine("[JSON 解析失败] " + jsonError);
                return ExitIssues;
            }

            var conflicts = new List<string>();
            LocConverter.CheckConflicts(rows, conflicts);
            foreach (string conflict in conflicts)
            {
                output.WriteLine("[冲突] " + conflict);
            }

            if (exportPath != null)
            {
                File.WriteAllText(exportPath, LocConverter.ExportCsv(rows));
                output.WriteLine("已导出 " + rows.Count + " 行 → " + exportPath);
            }

            if (conflicts.Count > 0)
            {
                return ExitIssues;
            }

            output.WriteLine(checkOnly ? "检查通过：文案表无冲突。" : "完成。");
            return ExitSuccess;
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/locconvert [--tables <目录>] [--export <csv>] [--import <csv>] [--check]");
            writer.WriteLine();
            writer.WriteLine("TbLocalization.json ↔ CSV(key,zh,en,ja) 双向转换；--import 会先查冲突，有冲突即中止。");
            writer.WriteLine("退出码：0 通过 / 1 有冲突或失败 / 2 用法错误 / 3 读写失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return LocConvertCli.Run(args, Console.Out, Console.Error);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.TableGen;

namespace AIFramework.LocCheck
{
    /// <summary>
    /// 命令行入口：文案表 ↔ 代码引用的完整性检查。
    /// <para>
    /// 退出码：0 通过（可以只有"未被引用"提示）；1 有缺 key 等错误；2 用法错误；3 表解析失败。
    /// </para>
    /// </summary>
    public static class LocCheckCli
    {
        public const int ExitSuccess = 0;
        public const int ExitErrors = 1;
        public const int ExitUsage = 2;
        public const int ExitInvalidTable = 3;

        public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            string root = workingDirectory ?? Directory.GetCurrentDirectory();
            string tablesDirectory = Path.Combine(root, "tables");
            var scanRoots = new List<string> { Path.Combine(root, "src"), Path.Combine(root, "unity", "Assets") };
            bool strict = false;

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
                    case "--scan":
                        if (i + 1 >= args.Length)
                        {
                            error.WriteLine("--scan 需要一个目录参数。");
                            return ExitUsage;
                        }

                        scanRoots.Clear();
                        scanRoots.Add(Path.GetFullPath(args[++i], root));
                        break;
                    case "--strict":
                        // 严格模式：把"表里有但没人引用"也当错误（发布前清理废弃文案用）。
                        strict = true;
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

            string localizationPath = Path.Combine(tablesDirectory, "TbLocalization.json");
            if (!File.Exists(localizationPath))
            {
                error.WriteLine("找不到文案表：" + localizationPath);
                return ExitUsage;
            }

            TableData localization;
            try
            {
                localization = TableSchemaReader.Parse(File.ReadAllText(localizationPath), "TbLocalization.json");
            }
            catch (TableGenException exception)
            {
                error.WriteLine("[解析失败] " + exception.Message);
                return ExitInvalidTable;
            }

            var issues = new List<LocIssue>();
            HashSet<string> tableKeys = LocCheckRules.ReadTableKeys(localization, issues);

            // 表本身的语言列缺失也顺带检查：每个 key 至少要有默认语言的一条文案。
            CheckLanguageCoverage(localization, issues);

            Dictionary<string, List<string>> references = LocCheckRules.ScanReferences(scanRoots);
            issues.AddRange(LocCheckRules.CrossCheck(references, tableKeys));

            int errorCount = 0;
            int warningCount = 0;
            foreach (LocIssue issue in issues)
            {
                if (issue.IsError)
                {
                    errorCount++;
                    output.WriteLine(issue.ToString());
                }
                else
                {
                    warningCount++;
                    if (strict)
                    {
                        output.WriteLine(issue.ToString());
                    }
                }
            }

            output.WriteLine("检查 " + tableKeys.Count + " 个 key、" + references.Count + " 处引用：错误 "
                + errorCount + "，提示 " + warningCount + (strict ? "（严格模式）" : "（未引用只提示）"));

            if (errorCount == 0 && (!strict || warningCount == 0))
            {
                output.WriteLine("--check 通过：文案 key 引用与表一致。");
                return ExitSuccess;
            }

            return ExitErrors;
        }

        /// <summary>每个 key 至少要有一条 lang=defaultLanguage 的文案（结构体数组列 entries）。</summary>
        private static void CheckLanguageCoverage(TableData localization, List<LocIssue> issues)
        {
            int entriesColumn = -1;
            for (int c = 0; c < localization.Schema.Columns.Count; c++)
            {
                if (string.Equals(localization.Schema.Columns[c].Name, "entries", StringComparison.Ordinal))
                {
                    entriesColumn = c;
                    break;
                }
            }

            if (entriesColumn < 0)
            {
                return;
            }

            int keyColumn = -1;
            for (int c = 0; c < localization.Schema.Columns.Count; c++)
            {
                if (string.Equals(localization.Schema.Columns[c].Name, "key", StringComparison.Ordinal))
                {
                    keyColumn = c;
                    break;
                }
            }

            for (int i = 0; i < localization.Rows.Count; i++)
            {
                System.Text.Json.JsonElement entries = localization.Rows[i][entriesColumn];
                if (entries.ValueKind != System.Text.Json.JsonValueKind.Array || entries.GetArrayLength() == 0)
                {
                    string key = keyColumn >= 0 ? localization.Rows[i][keyColumn].GetString() ?? "(empty)" : "(row " + (i + 1) + ")";
                    issues.Add(new LocIssue(key, "TbLocalization 第 " + (i + 1) + " 行", "entries 为空：该 key 没有任何语言文案", isError: true));
                }
            }
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/loccheck [--tables <目录>] [--scan <目录>] [--strict]");
            writer.WriteLine();
            writer.WriteLine("检查文案表（TbLocalization）与代码/场景里引用的 ui.* key 是否一致。");
            writer.WriteLine("默认扫描 src/ 与 unity/Assets/；--strict 把未被引用也判失败。");
            writer.WriteLine("退出码：0 通过 / 1 有错误 / 2 用法错误 / 3 表解析失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return LocCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

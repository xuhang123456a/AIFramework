using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.TableGen;

namespace AIFramework.TableCheck
{
    /// <summary>
    /// 命令行入口：读 <c>tables/*.json</c> → 跑不变量规则 → 打印问题清单。
    /// <para>
    /// 退出码：0 = 通过；1 = 有问题；2 = 用法错误；3 = 表解析失败。
    /// 与 <c>tablegen --check</c> 互补：它管"JSON 与 .bin 是否一致"，本工具管"数据本身是否讲得通"。
    /// </para>
    /// </summary>
    public static class TableCheckCli
    {
        public const int ExitSuccess = 0;
        public const int ExitViolations = 1;
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

            if (!Directory.Exists(tablesDirectory))
            {
                error.WriteLine("找不到表目录：" + tablesDirectory);
                return ExitUsage;
            }

            Dictionary<string, TableData> tables;
            try
            {
                tables = LoadTables(tablesDirectory);
            }
            catch (TableGenException exception)
            {
                error.WriteLine("[解析失败] " + exception.Message);
                return ExitInvalidTable;
            }

            List<TableIssue> issues = TableCheckRules.Validate(tables);

            output.WriteLine("体检 " + tables.Count + " 张表，发现 " + issues.Count + " 个问题。");
            if (issues.Count == 0)
            {
                output.WriteLine("--check 通过：所有已知不变量成立。");
                return ExitSuccess;
            }

            for (int i = 0; i < issues.Count; i++)
            {
                output.WriteLine("  ✗ " + issues[i]);
            }

            return ExitViolations;
        }

        /// <summary>读目录下全部 <c>*.json</c> 表定义（按文件名排序，输出稳定）。</summary>
        public static Dictionary<string, TableData> LoadTables(string tablesDirectory)
        {
            var tables = new Dictionary<string, TableData>(StringComparer.Ordinal);
            string[] files = Directory.GetFiles(tablesDirectory, "*.json");
            Array.Sort(files, StringComparer.Ordinal);

            foreach (string file in files)
            {
                string json = File.ReadAllText(file);
                TableData data = TableSchemaReader.Parse(json, Path.GetFileName(file));
                tables[data.Schema.TableName] = data;
            }

            return tables;
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/tablecheck [--tables <目录>]");
            writer.WriteLine();
            writer.WriteLine("校验 tables/*.json 的数据不变量（价格>0、主键唯一、经验单调、抽卡权重等）。");
            writer.WriteLine("默认读仓库根下的 tables/。");
            writer.WriteLine("退出码：0 通过 / 1 有问题 / 2 用法错误 / 3 表解析失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return TableCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

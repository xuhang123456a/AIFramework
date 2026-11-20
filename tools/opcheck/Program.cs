using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.TableGen;

namespace AIFramework.OpCheck
{
    /// <summary>
    /// 命令行入口：运营排期体检（跨表/跨活动冲突）。
    /// <para>退出码：0 通过（可带提示）；1 有错误；2 用法错误；3 表解析失败。</para>
    /// </summary>
    public static class OpCheckCli
    {
        public const int ExitSuccess = 0;
        public const int ExitErrors = 1;
        public const int ExitUsage = 2;
        public const int ExitInvalidTable = 3;

        public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null, long? nowUnixSeconds = null)
        {
            string root = workingDirectory ?? Directory.GetCurrentDirectory();
            string tablesDirectory = Path.Combine(root, "tables");
            long now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--now":
                        if (i + 1 >= args.Length || !long.TryParse(args[++i], out long parsedNow))
                        {
                            error.WriteLine("--now 需要一个 Unix 秒整数。");
                            return ExitUsage;
                        }

                        now = parsedNow;
                        break;
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

            var issues = new List<OpsIssue>();

            try
            {
                string activitiesPath = Path.Combine(tablesDirectory, "TbActivitySchedule.json");
                if (File.Exists(activitiesPath))
                {
                    TableData table = TableSchemaReader.Parse(File.ReadAllText(activitiesPath), "TbActivitySchedule.json");
                    OpsRules.CheckActivities(OpsTableReader.ReadActivities(table), now, issues);
                }

                string offersPath = Path.Combine(tablesDirectory, "TbLimitedOffer.json");
                if (File.Exists(offersPath))
                {
                    TableData table = TableSchemaReader.Parse(File.ReadAllText(offersPath), "TbLimitedOffer.json");
                    OpsRules.CheckOffers(OpsTableReader.ReadOffers(table), issues);
                }
            }
            catch (TableGenException exception)
            {
                error.WriteLine("[解析失败] " + exception.Message);
                return ExitInvalidTable;
            }

            int errorCount = 0;
            int warningCount = 0;
            foreach (OpsIssue issue in issues)
            {
                if (issue.IsError)
                {
                    errorCount++;
                    output.WriteLine(issue.ToString());
                }
                else
                {
                    warningCount++;
                    output.WriteLine(issue.ToString());
                }
            }

            output.WriteLine("运营排期体检：错误 " + errorCount + "，提示 " + warningCount + "。");
            if (errorCount == 0)
            {
                output.WriteLine("--check 通过：活动/礼包排期无冲突。");
                return ExitSuccess;
            }

            return ExitErrors;
        }

        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/opcheck [--tables <目录>] [--now <Unix秒>]");
            writer.WriteLine();
            writer.WriteLine("跨表运营排期体检：同轮换组活动窗口重叠、同一道具礼包窗口重叠、同时开启数、当前空窗。");
            writer.WriteLine("退出码：0 通过 / 1 有错误 / 2 用法错误 / 3 表解析失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return OpCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

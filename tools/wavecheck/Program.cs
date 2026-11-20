#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.TableGen;

namespace AIFramework.WaveCheck
{
    /// <summary>
    /// 命令行入口：波次/关卡节奏体检（<c>dotnet run --project tools/wavecheck</c>）。
    /// <para>
    /// 退出码：<c>0</c> 无错误（可带警告）；<c>1</c> 有错误；<c>2</c> 用法/环境错误；<c>3</c> 表解析失败。
    /// <c>--check</c> 与默认行为完全一致，只为让 verify-all 的调用形态统一。
    /// </para>
    /// </summary>
    public static class WaveCheckCli
    {
        /// <summary>成功。</summary>
        public const int ExitSuccess = 0;

        /// <summary>存在错误级问题。</summary>
        public const int ExitErrors = 1;

        /// <summary>用法或环境错误（参数非法、表目录不存在等）。</summary>
        public const int ExitUsage = 2;

        /// <summary>表解析失败（JSON 非法、列缺失、值类型不是 int32）。</summary>
        public const int ExitInvalidTable = 3;

        /// <summary>固定读取的表文件名（本工具只负责波次表，不做通用扫描）。</summary>
        public const string TableFileName = "TbArenaWave.json";

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
                    case "--check":
                        // 与默认行为一致：本工具默认就是"只读校验"，--check 只为统一 verify-all 的调用形态。
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

            string tablePath = Path.Combine(tablesDirectory, TableFileName);
            if (!File.Exists(tablePath))
            {
                // 缺表不是"通过"：波次表驱动的加载路径会因为这一行文件缺失而静默回退内置波次，
                // 门禁必须在这里红一次，否则"表没导出"会一直藏到真机。
                error.WriteLine("[解析失败] 找不到波次表：" + tablePath);
                return ExitInvalidTable;
            }

            List<WaveRow> rows;
            try
            {
                TableData table = TableSchemaReader.Parse(File.ReadAllText(tablePath), TableFileName);
                rows = WaveTableReader.ReadWaves(table);
            }
            catch (TableGenException exception)
            {
                error.WriteLine("[解析失败] " + exception.Message);
                return ExitInvalidTable;
            }
            catch (IOException exception)
            {
                error.WriteLine("读取表文件失败：" + exception.Message);
                return ExitUsage;
            }
            catch (UnauthorizedAccessException exception)
            {
                error.WriteLine("读取表文件被拒绝：" + exception.Message);
                return ExitUsage;
            }

            var issues = new List<WaveIssue>();
            WaveRules.Validate(rows, issues);

            int errorCount = 0;
            int warningCount = 0;
            for (int i = 0; i < issues.Count; i++)
            {
                WaveIssue issue = issues[i];
                if (issue.IsError)
                {
                    errorCount++;
                }
                else
                {
                    warningCount++;
                }

                output.WriteLine(issue.ToString());
            }

            output.WriteLine(
                "波次节奏体检：" + rows.Count + " 波，错误 " + errorCount + "，提示 " + warningCount + "。");
            output.WriteLine(DescribeTimeline(rows));

            if (errorCount == 0)
            {
                output.WriteLine("--check 通过：波次时间轴、难度曲线与奖励节奏一致。");
                return ExitSuccess;
            }

            return ExitErrors;
        }

        /// <summary>打印一行"难度与清场时间从首波到末波的变化"，让人一眼看出曲线形状。</summary>
        private static string DescribeTimeline(IReadOnlyList<WaveRow> rows)
        {
            if (rows.Count == 0)
            {
                return "难度 —；最短清场 —。";
            }

            WaveRow first = rows[0];
            WaveRow last = rows[rows.Count - 1];
            long bossIndex = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsBoss)
                {
                    bossIndex = rows[i].WaveIndex;
                    break;
                }
            }

            return "难度 " + WaveRules.Difficulty(in first) + " → " + WaveRules.Difficulty(in last)
                + "；最短清场 " + WaveRules.ClearTimeMs(in first) + "ms → " + WaveRules.ClearTimeMs(in last)
                + "ms；Boss 波 " + (bossIndex > 0 ? "#" + bossIndex : "（无）") + "。";
        }

        /// <summary>打印用法。</summary>
        /// <param name="writer">输出目标。</param>
        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/wavecheck -- [--tables <目录>] [--check]");
            writer.WriteLine();
            writer.WriteLine("波次/关卡节奏体检：读 tables/TbArenaWave.json，校验跨行时间轴——");
            writer.WriteLine("  1. waveIndex 唯一且从 1 连续递增（错误）；");
            writer.WriteLine("  2. 难度 enemyCount × enemyHp × (1000 + speed) / 1000 逐波非降（回落 = 错误）；");
            writer.WriteLine("  3. 最短清场时间 enemyCount × spawnIntervalMs 相邻突变（变短 / 超 3 倍 = 提示）；");
            writer.WriteLine("  4. 奖励/难度比的相邻波动 ≤ 50%（超出 = 提示）；");
            writer.WriteLine("  5. Boss 行唯一且在最后一行（错误），Boss 波小怪 ≤ 5（超出 = 提示）；");
            writer.WriteLine("  6. 总波数 5..20（越界 = 提示）；列定义区间越界（错误）。");
            writer.WriteLine();
            writer.WriteLine("--check 与默认行为一致（便于接 verify-all）；省略 --tables 时读 <当前目录>/tables。");
            writer.WriteLine("退出码：0 无错误 / 1 有错误 / 2 用法或环境错误 / 3 表解析失败。");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return WaveCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

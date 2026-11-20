#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AIFramework.TableGen;

namespace AIFramework.SkillCheck
{
    /// <summary>
    /// 命令行入口：技能配置体检（<c>dotnet run --project tools/skillcheck</c>）。
    /// <para>
    /// 退出码：<c>0</c> 无错误（可带警告）；<c>1</c> 有错误；<c>2</c> 用法/环境错误；<c>3</c> 表解析失败
    /// （<c>TbSkill.json</c> 缺失、JSON 非法、空表、缺列、列类型不对）。
    /// <c>--check</c> 与默认行为完全一致，只为让 verify-all 的调用形态统一。
    /// </para>
    /// </summary>
    public static class SkillCheckCli
    {
        /// <summary>成功（无错误，可能带警告）。</summary>
        public const int ExitSuccess = 0;

        /// <summary>存在错误级问题。</summary>
        public const int ExitErrors = 1;

        /// <summary>用法或环境错误（参数非法、表目录不存在等）。</summary>
        public const int ExitUsage = 2;

        /// <summary>表解析失败（缺表、JSON 非法、空表、缺列、列类型不对）。</summary>
        public const int ExitInvalidTable = 3;

        /// <summary>固定读取的表文件名（本工具只负责技能表，不做通用扫描）。</summary>
        public const string TableFileName = "TbSkill.json";

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
                // 缺表不是"通过"：技能是战斗侧的驱动数据，表没导出时静默放行会让"技能全不生效"藏到真机。
                error.WriteLine("[解析失败] 找不到技能表：" + tablePath);
                return ExitInvalidTable;
            }

            List<SkillRow> rows;
            try
            {
                TableData table = TableSchemaReader.Parse(File.ReadAllText(tablePath), TableFileName);
                rows = SkillTableReader.ReadSkills(table);
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

            var issues = new List<SkillIssue>();
            SkillRules.Validate(rows, issues);

            int errorCount = 0;
            int warningCount = 0;
            for (int i = 0; i < issues.Count; i++)
            {
                SkillIssue issue = issues[i];
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

            int skillCount = CountSkills(rows);
            output.WriteLine(
                "技能表体检：" + skillCount + " 个技能 / " + rows.Count + " 行，错误 " + errorCount
                + "，提示 " + warningCount + "。");
            output.WriteLine(
                "冷却：均值 " + FormatNumber(SkillRules.MeanCooldownMs(rows)) + "ms / 中位数 "
                + FormatNumber(SkillRules.MedianCooldownMs(rows)) + "ms；效果类型分布（按行）："
                + DescribeEffectDistribution(rows) + "。");

            if (errorCount == 0)
            {
                output.WriteLine("--check 通过：复合主键、等级成长、效果组合与跨技能 Tag 分布一致。");
                return ExitSuccess;
            }

            return ExitErrors;
        }

        /// <summary>打印用法。</summary>
        /// <param name="writer">输出目标。</param>
        public static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法：dotnet run --project tools/skillcheck -- [--tables <目录>] [--check]");
            writer.WriteLine();
            writer.WriteLine("技能配置体检：读 tables/TbSkill.json，按“技能 × 等级”矩阵校验——");
            writer.WriteLine("  1. 复合主键 (skillId, level) 唯一；每个 skillId 恰好 level 1..3 各一行（错误）；");
            writer.WriteLine("  2. cooldownMs ≥ 100、damage ≥ 0、maxTargets 1..20、castRangePermille 100..5000（错误）；");
            writer.WriteLine("  3. 同技能 damage 随 level 严格递增（伤害/治疗类），等级间冷却不得缩短（错误）；");
            writer.WriteLine("  4. 位移(damage≠0) / 控制(cooldownMs<2000) 与效果类型不自洽（错误）；");
            writer.WriteLine("  5. 同一 tagId 挂超过 2 个技能（错误）；全局冷却均值偏离中位数 > 3 倍（提示）；");
            writer.WriteLine("  6. 空表 / 缺列 / 列类型不对 → 解析失败（退出码 3）。");
            writer.WriteLine();
            writer.WriteLine("--check 与默认行为一致（便于接 verify-all）；省略 --tables 时读 <当前目录>/tables。");
            writer.WriteLine("退出码：0 无错误 / 1 有错误 / 2 用法或环境错误 / 3 表解析失败。");
        }

        private static int CountSkills(IReadOnlyList<SkillRow> rows)
        {
            var ids = new HashSet<int>();
            for (int i = 0; i < rows.Count; i++)
            {
                ids.Add(rows[i].SkillId);
            }

            return ids.Count;
        }

        private static string DescribeEffectDistribution(IReadOnlyList<SkillRow> rows)
        {
            var counts = new int[SkillRules.EffectControl + 1];
            int unknown = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                int effectType = rows[i].EffectType;
                if (effectType >= 0 && effectType < counts.Length)
                {
                    counts[effectType]++;
                }
                else
                {
                    unknown++;
                }
            }

            var parts = new List<string>(counts.Length + 1);
            for (int effectType = 0; effectType < counts.Length; effectType++)
            {
                parts.Add(SkillRules.EffectTypeName(effectType) + " " + counts[effectType]);
            }

            if (unknown > 0)
            {
                parts.Add("未定义 " + unknown);
            }

            return string.Join(" / ", parts);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            return SkillCheckCli.Run(args, Console.Out, Console.Error);
        }
    }
}

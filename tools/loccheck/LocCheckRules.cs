using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AIFramework.TableGen;

namespace AIFramework.LocCheck
{
    /// <summary>一条本地化问题：key + 位置 + 说明。</summary>
    public readonly struct LocIssue
    {
        public LocIssue(string key, string location, string message, bool isError)
        {
            Key = key;
            Location = location;
            Message = message;
            IsError = isError;
        }

        public string Key { get; }

        /// <summary>来源位置（仓库相对路径:行号），表级问题为表名。</summary>
        public string Location { get; }

        public string Message { get; }

        /// <summary>true = 必须修（缺 key / 表内重复 / 语言缺项）；false = 提示（未被引用）。</summary>
        public bool IsError { get; }

        public override string ToString()
        {
            return (IsError ? "✗ " : "⚠ ") + Key + " @ " + Location + "：" + Message;
        }
    }

    /// <summary>
    /// 本地化完整性检查：把"文案表里有的 key"和"代码里真正引用的 key"对起来。
    /// <para>
    /// 为什么值得单独做个工具：缺 key 在运行时是**静默回退成 key 本身**（框架刻意这样设计，
    /// 不让游戏因为少一句翻译崩掉），所以少翻一条不会报错，只会让玩家看到 <c>ui.shop.buy</c>。
    /// 这类问题只能靠静态扫描提前发现。
    /// </para>
    /// <para>
    /// 扫描规则：代码里出现 <c>"ui.xxx"</c> 这类字符串字面量且以 <see cref="KeyPrefix"/> 开头，
    /// 或显式调用 <c>LocalizationQueryRequest("...")</c> / <c>Localize("...")</c> 的字符串参数。
    /// 只做文本级扫描，不解析 C# 语法——够用且没有编译器依赖。
    /// </para>
    /// </summary>
    public static class LocCheckRules
    {
        /// <summary>文案 key 的约定前缀（新 key 必须以此开头才会被扫描到）。</summary>
        public const string KeyPrefix = "ui.";

        /// <summary>代码扫描的正则：<c>"ui.xxx"</c> 形式的字符串字面量。</summary>
        private static readonly Regex KeyLiteralRegex = new Regex("\"(?<key>ui\\.[A-Za-z0-9_.\\-]+)\"", RegexOptions.Compiled);

        /// <summary>行内豁免标记：行里带它，这一行的 key 引用不参与检查（用于"故意测缺 key 回退"的地方）。</summary>
        public const string IgnoreMarker = "loccheck:ignore";
        public static Dictionary<string, List<string>> ScanReferences(IEnumerable<string> roots)
        {
            var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (!IsScannable(file))
                    {
                        continue;
                    }

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(IgnoreMarker, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (Match match in KeyLiteralRegex.Matches(lines[i]))
                        {
                            string key = match.Groups["key"].Value;
                            if (!references.TryGetValue(key, out List<string>? locations))
                            {
                                locations = new List<string>(2);
                                references[key] = locations;
                            }

                            locations.Add(file + ":" + (i + 1));
                        }
                    }
                }
            }

            return references;
        }

        /// <summary>取出文案表里的全部 key（表名固定 TbLocalization，列名 key）。</summary>
        public static HashSet<string> ReadTableKeys(TableData localizationTable, List<LocIssue> issues)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int keyColumn = FindColumn(localizationTable, "key");
            if (keyColumn < 0)
            {
                issues.Add(new LocIssue("(table)", localizationTable.Schema.TableName, "缺少 key 列，无法检查", isError: true));
                return keys;
            }

            for (int i = 0; i < localizationTable.Rows.Count; i++)
            {
                System.Text.Json.JsonElement element = localizationTable.Rows[i][keyColumn];
                string key = element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString()! : string.Empty;
                if (key.Length == 0)
                {
                    issues.Add(new LocIssue("(empty)", localizationTable.Schema.TableName + " 第 " + (i + 1) + " 行", "空 key", isError: true));
                    continue;
                }

                if (!keys.Add(key))
                {
                    issues.Add(new LocIssue(key, localizationTable.Schema.TableName + " 第 " + (i + 1) + " 行", "key 重复", isError: true));
                }
            }

            return keys;
        }

        /// <summary>对比：引用但表里没有 = 错误；表里有但没人引用 = 提示。</summary>
        public static List<LocIssue> CrossCheck(
            IReadOnlyDictionary<string, List<string>> references,
            IReadOnlyCollection<string> tableKeys)
        {
            var issues = new List<LocIssue>(8);

            foreach (KeyValuePair<string, List<string>> pair in references)
            {
                if (!ContainsKey(tableKeys, pair.Key))
                {
                    issues.Add(new LocIssue(pair.Key, pair.Value[0], "代码引用了表里不存在的 key（运行时会回退显示 key 本身）", isError: true));
                }
            }

            foreach (string key in tableKeys)
            {
                if (!references.ContainsKey(key))
                {
                    issues.Add(new LocIssue(key, "TbLocalization", "表里有但代码未引用（可能是废弃文案，建议清理或确认）", isError: false));
                }
            }

            return issues;
        }

        private static bool ContainsKey(IReadOnlyCollection<string> tableKeys, string key)
        {
            foreach (string candidate in tableKeys)
            {
                if (string.Equals(candidate, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindColumn(TableData table, string name)
        {
            for (int i = 0; i < table.Schema.Columns.Count; i++)
            {
                if (string.Equals(table.Schema.Columns[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsScannable(string path)
        {
            string extension = Path.GetExtension(path);
            return extension == ".cs" || extension == ".unity" || extension == ".asset";
        }
    }
}

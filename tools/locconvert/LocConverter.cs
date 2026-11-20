using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AIFramework.LocConvert
{
    /// <summary>一条翻译行：key + 三种语言文案（缺失用空串）。</summary>
    public readonly struct LocRow
    {
        public LocRow(string key, string chinese, string english, string japanese)
        {
            Key = key ?? string.Empty;
            Chinese = chinese ?? string.Empty;
            English = english ?? string.Empty;
            Japanese = japanese ?? string.Empty;
        }

        public string Key { get; }

        public string Chinese { get; }

        public string English { get; }

        public string Japanese { get; }
    }

    /// <summary>
    /// 本地化 CSV ↔ TbLocalization JSON 双向转换。
    /// <para>
    /// <b>为什么需要这个工具</b>：翻译同学不写 JSON；运营要 Excel 改文案。没有转换工具时，
    /// 每次发版都要有人手工复制粘贴到 JSON，漏一行/多一行都只能靠玩家发现。
    /// </para>
    /// <para>
    /// <b>为什么用 CSV 而不是 xlsx</b>：CSV 能进 git diff（逐行审阅）、能被任何工具处理；
    /// 二进制 xlsx 在版本库里既不可读也无法合并。
    /// </para>
    /// </summary>
    public static class LocConverter
    {
        /// <summary>CSV 表头（固定列序，导入导出共用）。</summary>
        public const string Header = "key,zh,en,ja";

        /// <summary>CSV 里的语言列顺序（0=简中、1=英文、2=日文）。</summary>
        private static readonly int[] CsvLanguageColumns = { 0, 1, 2 };

        /// <summary>导出 CSV 文本。</summary>
        /// <param name="rows">翻译行。</param>
        /// <returns>带表头的 CSV（LF 换行）。</returns>
        public static string ExportCsv(IReadOnlyList<LocRow> rows)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            var builder = new StringBuilder();
            builder.Append(Header).Append('\n');
            for (int i = 0; i < rows.Count; i++)
            {
                LocRow row = rows[i];
                builder.Append(Escape(row.Key)).Append(',')
                    .Append(Escape(row.Chinese)).Append(',')
                    .Append(Escape(row.English)).Append(',')
                    .Append(Escape(row.Japanese)).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>解析 CSV 文本（表头必须匹配；行数不限）。</summary>
        /// <param name="text">CSV 文本。</param>
        /// <param name="rows">解析结果。</param>
        /// <param name="error">失败原因（成功为空串）。</param>
        /// <returns>成功返回 true。</returns>
        public static bool TryParseCsv(string? text, out List<LocRow> rows, out string error)
        {
            rows = new List<LocRow>();
            if (string.IsNullOrEmpty(text))
            {
                error = "CSV 为空。";
                return false;
            }

            string[] lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int lineIndex = 0;
            if (!string.Equals(lines[0].Trim(), Header, StringComparison.OrdinalIgnoreCase))
            {
                error = "CSV 表头必须是 " + Header + "。";
                return false;
            }

            for (lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (line.Length == 0)
                {
                    continue;
                }

                string[] fields = SplitCsvLine(line);
                if (fields.Length != 4)
                {
                    error = "第 " + (lineIndex + 1) + " 行列数不是 4（遇到逗号需要加引号）。";
                    return false;
                }

                string key = fields[0].Trim();
                if (key.Length == 0)
                {
                    error = "第 " + (lineIndex + 1) + " 行 key 为空。";
                    return false;
                }

                rows.Add(new LocRow(key, fields[1], fields[2], fields[3]));
            }

            if (rows.Count == 0)
            {
                error = "CSV 只有表头，没有翻译行。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>把翻译行转成 TbLocalization JSON 文本（与仓库现有 JSON 结构一致）。</summary>
        public static string ToLocalizationJson(IReadOnlyList<LocRow> rows)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            var builder = new StringBuilder();
            builder.Append("{\n  \"table\": \"TbLocalization\",\n  \"types\": {\n    \"LocalizationEntry\": [\n");
            builder.Append("      { \"name\": \"lang\", \"type\": \"int32\" },\n");
            builder.Append("      { \"name\": \"text\", \"type\": \"string\" }\n    ]\n  },\n");
            builder.Append("  \"columns\": [\n    { \"name\": \"key\", \"type\": \"string\" },\n");
            builder.Append("    { \"name\": \"entries\", \"type\": \"LocalizationEntry[]\" }\n  ],\n");
            builder.Append("  \"rows\": [\n");

            for (int i = 0; i < rows.Count; i++)
            {
                LocRow row = rows[i];
                builder.Append("    {\n      \"key\": ").Append(JsonString(row.Key)).Append(",\n");
                builder.Append("      \"entries\": [\n");
                int written = 0;
                if (row.Chinese.Length > 0)
                {
                    AppendEntry(builder, 0, row.Chinese, ref written);
                }

                if (row.English.Length > 0)
                {
                    AppendEntry(builder, 1, row.English, ref written);
                }

                if (row.Japanese.Length > 0)
                {
                    AppendEntry(builder, 2, row.Japanese, ref written);
                }

                builder.Append("\n      ]\n    }");
                if (i < rows.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("  ]\n}\n");
            return builder.ToString();
        }

        /// <summary>从 TbLocalization JSON 文本读出翻译行（未知语言列忽略；缺行返回空串）。</summary>
        public static bool TryParseLocalizationJson(string? json, out List<LocRow> rows, out string error)
        {
            rows = new List<LocRow>();
            if (string.IsNullOrEmpty(json))
            {
                error = "JSON 为空。";
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json!);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("rows", out JsonElement rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
                {
                    error = "JSON 缺少 rows 数组。";
                    return false;
                }

                foreach (JsonElement row in rowsElement.EnumerateArray())
                {
                    string key = row.GetProperty("key").GetString() ?? string.Empty;
                    string chinese = string.Empty;
                    string english = string.Empty;
                    string japanese = string.Empty;

                    if (row.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement entry in entries.EnumerateArray())
                        {
                            int lang = entry.GetProperty("lang").GetInt32();
                            string text = entry.GetProperty("text").GetString() ?? string.Empty;
                            switch (lang)
                            {
                                case 0:
                                    chinese = text;
                                    break;
                                case 1:
                                    english = text;
                                    break;
                                case 2:
                                    japanese = text;
                                    break;
                            }
                        }
                    }

                    rows.Add(new LocRow(key, chinese, english, japanese));
                }
            }
            catch (JsonException exception)
            {
                error = "JSON 解析失败：" + exception.Message;
                return false;
            }
            catch (InvalidOperationException)
            {
                error = "JSON 结构不符（缺 key/lang/text 字段）。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>冲突检测：key 重复、key 为空、三种语言全空。</summary>
        /// <param name="rows">翻译行。</param>
        /// <param name="issues">输出（"row 3: ..." 形式）。</param>
        public static void CheckConflicts(IReadOnlyList<LocRow> rows, List<string> issues)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (issues == null)
            {
                throw new ArgumentNullException(nameof(issues));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                LocRow row = rows[i];
                if (row.Key.Length == 0)
                {
                    issues.Add("row " + (i + 2) + ": key 为空");
                }
                else if (!seen.Add(row.Key))
                {
                    issues.Add("row " + (i + 2) + ": key 重复：" + row.Key);
                }

                if (row.Chinese.Length == 0 && row.English.Length == 0 && row.Japanese.Length == 0)
                {
                    issues.Add("row " + (i + 2) + ": 三种语言全空：" + row.Key);
                }
            }
        }

        private static void AppendEntry(StringBuilder builder, int lang, string text, ref int written)
        {
            if (written > 0)
            {
                builder.Append(",\n");
            }

            builder.Append("        { \"lang\": ").Append(lang).Append(", \"text\": ")
                .Append(JsonString(text)).Append(" }");
            written++;
        }

        private static string JsonString(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static string Escape(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>最小 CSV 行切分（支持双引号包裹与 "" 转义；不支持跨行字段）。</summary>
        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>(4);
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}

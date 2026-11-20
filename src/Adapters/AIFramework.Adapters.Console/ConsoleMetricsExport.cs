#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 玩家指标看板的 **CSV 导出/读回**适配器：把 <see cref="PlayerMetrics.ToCsv"/> 落成
    /// 一个能被 Excel / pandas / 后台脚本直接吃的文件，并在测试里读回来验证格式。
    /// <para>
    /// <b>为什么导出逻辑不放在 Core</b>：Core 只负责"算对数字"与"拼出一行 CSV 文本"，
    /// 而"写哪个文件、要不要补表头、要不要建目录、用不用 BOM"全是宿主环境的事
    /// （Unity 写 <c>persistentDataPath</c>，Console 写当前目录，云端写对象存储）。
    /// 把文件系统粘在 Core 上，纯逻辑库就再也进不了 Unity/HybridCLR。
    /// </para>
    /// <para>
    /// <b>追加语义（本类最容易出错的地方）</b>：
    /// <list type="bullet">
    /// <item>文件不存在或长度为 0 → 先写表头，再写数据行；</item>
    /// <item>文件已有内容 → 只追加数据行（**绝不重复写表头**，否则导入脚本会把表头当成一行数据）；</item>
    /// <item>文件末尾没有换行（上一次写入被强杀、或用编辑器手工改过）→ 追加前先补一个 <c>\n</c>，
    /// 不然新行会粘在上一行尾部，两行数据合成一行垃圾。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>编码与换行</b>：UTF-8 <b>不带 BOM</b>（BOM 会让部分 pandas/Java 导入器把第一列读成 <c>\ufeffdateUtc</c>），
    /// 换行固定 <c>\n</c>（跨平台一致；Excel 与 pandas 都接受，Windows 记事本自 2018 起也接受）。
    /// 全部字段都是整数与短日期，因此不需要任何 CSV 引号转义。
    /// </para>
    /// <para>线程模型：静态无状态方法，可并发调用不同路径；同一文件并发写入由调用方串行化。</para>
    /// </summary>
    public static class ConsoleMetricsExport
    {
        /// <summary>CSV 表头（转发自 Core，禁止在此处另写一份字面量）。</summary>
        public const string CsvHeader = PlayerMetrics.CsvHeader;

        /// <summary>CSV 列数（表头 + 数据行共用）。</summary>
        public const int ColumnCount = PlayerMetricsSnapshot.CsvColumnCount;

        /// <summary>CSV 行分隔符（LF，跨平台固定）。</summary>
        public const string LineSeparator = "\n";

        /// <summary>
        /// 把"表头 + 一行数据"写进 <paramref name="writer"/>（完整 CSV 文档，不关闭 writer）。
        /// </summary>
        /// <param name="metrics">指标聚合器（非 <c>null</c>）。</param>
        /// <param name="writer">目标写入器（非 <c>null</c>；由调用方决定是控制台、<see cref="StringWriter"/> 还是文件）。</param>
        /// <returns>写入的数据行数（不含表头，恒为 1）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="metrics"/> 或 <paramref name="writer"/> 为 <c>null</c>。</exception>
        public static int Write(PlayerMetrics metrics, TextWriter writer)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            // 直接转发 Core 的 ToCsv()：表头与行序的唯一定义在 Core，适配层不复制格式。
            writer.Write(metrics.ToCsv());
            writer.Flush();
            return 1;
        }

        /// <summary>
        /// 只写一行数据（追加到已有 CSV 时用；不含表头）。
        /// </summary>
        /// <param name="metrics">指标聚合器（非 <c>null</c>）。</param>
        /// <param name="writer">目标写入器（非 <c>null</c>）。</param>
        /// <returns>写入的数据行数（恒为 1）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="metrics"/> 或 <paramref name="writer"/> 为 <c>null</c>。</exception>
        public static int Append(PlayerMetrics metrics, TextWriter writer)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            // 日期列必须与"导出这一刻"一致，因此取聚合器自己的 UTC 日期（同一份时钟，不另造时间源）。
            writer.Write(metrics.Snapshot().ToCsvRow(metrics.CurrentDateUtc));
            writer.Write(LineSeparator);
            writer.Flush();
            return 1;
        }

        /// <summary>
        /// 导出到文件：需要时创建目录；<paramref name="append"/> 为真且文件已有内容时只追加数据行，
        /// 否则（或文件为空）写完整的"表头 + 一行"。
        /// </summary>
        /// <param name="metrics">指标聚合器（非 <c>null</c>）。</param>
        /// <param name="filePath">目标文件路径（非空白；相对路径按当前工作目录解析）。</param>
        /// <param name="append">是否追加（<c>false</c> = 覆盖，用于"每次导出重建一份快照"）。</param>
        /// <returns>写入的数据行数（不含表头，恒为 1）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="metrics"/> 或 <paramref name="filePath"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> 为空白。</exception>
        /// <exception cref="IOException">目录不可写、路径被占用等 IO 失败（由调用方决定是否降级）。</exception>
        public static int ExportToFile(PlayerMetrics metrics, string filePath, bool append = true)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            string fullPath = ResolveFilePath(filePath);

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool hasContent = append && File.Exists(fullPath) && new FileInfo(fullPath).Length > 0L;
            bool needsLeadingNewline = hasContent && !EndsWithNewline(fullPath);

            using (var writer = new StreamWriter(
                fullPath,
                append: append,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                if (!hasContent)
                {
                    // 覆盖写或空文件：先落表头，保证读回方永远能按"首行是表头"解析。
                    writer.Write(CsvHeader);
                    writer.Write(LineSeparator);
                }
                else if (needsLeadingNewline)
                {
                    writer.Write(LineSeparator);
                }

                writer.Write(metrics.Snapshot().ToCsvRow(metrics.CurrentDateUtc));
                writer.Write(LineSeparator);
            }

            return 1;
        }

        /// <summary>
        /// 读回 CSV 并做结构校验（表头必须匹配、每行列数必须等于 <see cref="ColumnCount"/>、
        /// 除日期外每列都能按不变文化解析成整数）。
        /// <para>
        /// 它存在的意义是"导出格式的唯一可执行定义"：测试用它做导出→读回的往返验收，
        /// 后台导入脚本也能直接复用同一套列数/数值约束，不必各写一份。
        /// </para>
        /// </summary>
        /// <param name="filePath">CSV 路径（非空白）。</param>
        /// <param name="rows">解析出的数据行（每行按逗号切分；失败时为已解析到的部分行）。</param>
        /// <param name="error">失败原因（成功为空串）。</param>
        /// <returns>格式合法且至少有一行数据时返回 <c>true</c>。</returns>
        public static bool TryReadRows(string filePath, out List<string[]> rows, out string error)
        {
            rows = new List<string[]>();

            if (filePath == null)
            {
                error = "CSV 路径为 null。";
                return false;
            }

            string path = filePath.Trim();
            if (path.Length == 0)
            {
                error = "CSV 路径为空白。";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "CSV 文件不存在：" + path;
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException exception)
            {
                error = "读取 CSV 失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "读取 CSV 被拒绝：" + exception.Message;
                return false;
            }

            // 手工编辑过的文件可能用 CRLF：统一归一化后再切行，避免第一列末尾粘上 '\r'。
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int lineIndex = 0;
            while (lineIndex < lines.Length && lines[lineIndex].Trim().Length == 0)
            {
                // 允许文件开头有空行（编辑器手滑），但表头必须出现在第一条非空行。
                lineIndex++;
            }

            if (lineIndex >= lines.Length)
            {
                error = "CSV 为空。";
                return false;
            }

            if (!string.Equals(lines[lineIndex].Trim(), CsvHeader, StringComparison.OrdinalIgnoreCase))
            {
                error = "CSV 表头必须是 " + CsvHeader + "。";
                return false;
            }

            for (lineIndex++; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                string[] fields = line.Split(',');
                if (fields.Length != ColumnCount)
                {
                    error = "第 " + (lineIndex + 1) + " 行列数为 " + fields.Length + "，应为 " + ColumnCount + "。";
                    return false;
                }

                // 第 0 列是日期文本，其余 11 列必须是整数：小数点/千分位/空串都在这里被拦下。
                for (int column = 1; column < fields.Length; column++)
                {
                    if (!long.TryParse(fields[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        error = "第 " + (lineIndex + 1) + " 行第 " + (column + 1) + " 列不是整数：" + fields[column];
                        return false;
                    }
                }

                rows.Add(fields);
            }

            if (rows.Count == 0)
            {
                error = "CSV 只有表头，没有数据行。";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>把用户给的路径解析成绝对路径（空白与 <c>null</c> 在这里统一拒绝）。</summary>
        private static string ResolveFilePath(string filePath)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            string trimmed = filePath.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("CSV 目标路径不能为空白。", nameof(filePath));
            }

            return Path.GetFullPath(trimmed);
        }

        /// <summary>文件末尾是否是换行符（只有 <c>\n</c> 才算：追加语义与 <see cref="LineSeparator"/> 保持一致）。</summary>
        private static bool EndsWithNewline(string fullPath)
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length == 0L)
                {
                    return false;
                }

                stream.Seek(-1L, SeekOrigin.End);
                return stream.ReadByte() == '\n';
            }
        }
    }
}

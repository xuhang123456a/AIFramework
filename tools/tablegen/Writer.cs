#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIFramework.TableGen
{
    /// <summary>
    /// 表定义 → 二进制。布局与仓库现有解析器完全一致（小端）：
    /// <c>[int32 rowCount]</c> + rowCount × 按列声明顺序编码的行。
    /// </summary>
    /// <remarks>
    /// 手写小端字节而不是直接用 <see cref="BinaryWriter"/> 的 <c>Write(int)</c>：
    /// 前者与平台字节序无关，也不会误用 <c>BinaryWriter.Write(string)</c>（那是 7-bit 变长长度前缀，和本仓布局不兼容）。
    /// </remarks>
    public static class TableEncoder
    {
        /// <summary>把解析好的表数据编码成 <c>.bin</c> 字节。</summary>
        /// <param name="data">表形状 + 行数据。</param>
        /// <returns>二进制内容。</returns>
        /// <exception cref="TableGenException">某个值类型不匹配或越界。</exception>
        public static byte[] Encode(TableData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return Encode(data.Schema, data.Rows);
        }

        /// <summary>把行数据按表形状编码成 <c>.bin</c> 字节。</summary>
        /// <param name="schema">表形状。</param>
        /// <param name="rows">每行按列顺序排列的 JSON 值。</param>
        /// <returns>二进制内容。</returns>
        public static byte[] Encode(TableSchema schema, IReadOnlyList<JsonElement[]> rows)
        {
            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);

            WriteInt32(writer, rows.Count);
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                JsonElement[] values = rows[rowIndex];
                if (values == null || values.Length != schema.Columns.Count)
                {
                    throw new TableGenException(
                        "表 '" + schema.TableName + "' 第 " + rowIndex + " 行的列数与表定义不符。");
                }

                for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
                {
                    TableColumn column = schema.Columns[columnIndex];
                    WriteValue(writer, column.Kind, column.Struct, values[columnIndex], Context(schema, rowIndex, column));
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        private static string Context(TableSchema schema, int rowIndex, TableColumn column)
        {
            return "表 '" + schema.TableName + "' 第 " + rowIndex + " 行 列 '" + column.Name + "' ";
        }

        private static void WriteValue(
            BinaryWriter writer,
            ColumnKind kind,
            StructType? structType,
            JsonElement value,
            string context)
        {
            switch (kind)
            {
                case ColumnKind.Int32:
                    WriteInt32(writer, TableSchemaReader.ReadInt32(value, context));
                    break;

                case ColumnKind.Float32:
                    WriteInt32(writer, BitConverter.SingleToInt32Bits(TableSchemaReader.ReadFloat32(value, context)));
                    break;

                case ColumnKind.Bool:
                    writer.Write(TableSchemaReader.ReadBool(value, context) ? (byte)1 : (byte)0);
                    break;

                case ColumnKind.String:
                    WriteUtf8(writer, TableSchemaReader.ReadString(value, context));
                    break;

                case ColumnKind.Int32Array:
                {
                    IReadOnlyList<JsonElement> items = TableSchemaReader.ReadArray(value, context);
                    WriteInt32(writer, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        WriteInt32(writer, TableSchemaReader.ReadInt32(items[i], context + "元素[" + i + "]"));
                    }

                    break;
                }

                case ColumnKind.StringArray:
                {
                    IReadOnlyList<JsonElement> items = TableSchemaReader.ReadArray(value, context);
                    WriteInt32(writer, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        WriteUtf8(writer, TableSchemaReader.ReadString(items[i], context + "元素[" + i + "]"));
                    }

                    break;
                }

                case ColumnKind.StructArray:
                {
                    if (structType == null)
                    {
                        throw new TableGenException(context + "结构体类型未声明。");
                    }

                    IReadOnlyList<JsonElement> items = TableSchemaReader.ReadArray(value, context);
                    WriteInt32(writer, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        JsonElement item = items[i];
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            throw new TableGenException(context + "元素[" + i + "] 期望对象（" + structType.Name + "）。");
                        }

                        for (int fieldIndex = 0; fieldIndex < structType.Fields.Count; fieldIndex++)
                        {
                            StructField field = structType.Fields[fieldIndex];
                            JsonElement fieldValue;
                            if (!item.TryGetProperty(field.Name, out fieldValue))
                            {
                                throw new TableGenException(
                                    context + "元素[" + i + "] 缺少字段 '" + field.Name + "'（" + structType.Name + "）。");
                            }

                            WriteValue(writer, field.Kind, null, fieldValue,
                                context + "元素[" + i + "]." + field.Name + " ");
                        }
                    }

                    break;
                }

                default:
                    throw new TableGenException(context + "列类型未实现：" + kind + "。");
            }
        }

        private static void WriteUtf8(BinaryWriter writer, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteInt32(writer, bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteInt32(BinaryWriter writer, int value)
        {
            writer.Write((byte)(value & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 24) & 0xFF));
        }
    }

    /// <summary>回读结果：表名 + 每行"列名 → 值"（值为 <see cref="int"/> / <see cref="float"/> / <see cref="bool"/> / <see cref="string"/> / 数组 / 字段字典）。</summary>
    public sealed class DecodedTable
    {
        /// <summary>表名。</summary>
        public string TableName { get; }

        /// <summary>行数据。</summary>
        public IReadOnlyList<Dictionary<string, object>> Rows { get; }

        internal DecodedTable(string tableName, IReadOnlyList<Dictionary<string, object>> rows)
        {
            TableName = tableName;
            Rows = rows;
        }
    }

    /// <summary>
    /// 字节流 → 值：导出器的逆运算，用于往返测试与排查"导出的字节到底长什么样"。
    /// 越界、负长度、尾部残留一律显式抛 <see cref="TableGenException"/>。
    /// </summary>
    public static class TableBinaryReader
    {
        /// <summary>按表形状解析一份 <c>.bin</c>。</summary>
        /// <param name="schema">表形状。</param>
        /// <param name="payload">二进制内容。</param>
        /// <returns>解析结果。</returns>
        public static DecodedTable Read(TableSchema schema, byte[] payload)
        {
            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var cursor = new Cursor(payload, schema.TableName);
            int rowCount = cursor.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new TableGenException(
                    "表 '" + schema.TableName + "' 的 rowCount 为负：" + rowCount + "。");
            }

            var rows = new List<Dictionary<string, object>>(rowCount);
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var row = new Dictionary<string, object>(schema.Columns.Count, StringComparer.Ordinal);
                for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
                {
                    TableColumn column = schema.Columns[columnIndex];
                    string context = "表 '" + schema.TableName + "' 第 " + rowIndex + " 行 列 '" + column.Name + "' ";
                    row.Add(column.Name, ReadValue(cursor, column.Kind, column.Struct, context));
                }

                rows.Add(row);
            }

            if (cursor.Remaining != 0)
            {
                throw new TableGenException(
                    "表 '" + schema.TableName + "' 尾部残留 " + cursor.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            return new DecodedTable(schema.TableName, rows);
        }

        private static object ReadValue(Cursor cursor, ColumnKind kind, StructType? structType, string context)
        {
            switch (kind)
            {
                case ColumnKind.Int32:
                    return cursor.ReadInt32(context);

                case ColumnKind.Float32:
                    return BitConverter.Int32BitsToSingle(cursor.ReadInt32(context));

                case ColumnKind.Bool:
                {
                    byte raw = cursor.ReadByte(context);
                    if (raw > 1)
                    {
                        throw new TableGenException(context + "bool 字节只能是 0/1，实际 " + raw + "。");
                    }

                    return raw == 1;
                }

                case ColumnKind.String:
                    return cursor.ReadUtf8(context);

                case ColumnKind.Int32Array:
                {
                    int count = cursor.ReadCount(context);
                    var items = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        items[i] = cursor.ReadInt32(context + "元素[" + i + "] ");
                    }

                    return items;
                }

                case ColumnKind.StringArray:
                {
                    int count = cursor.ReadCount(context);
                    var items = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        items[i] = cursor.ReadUtf8(context + "元素[" + i + "] ");
                    }

                    return items;
                }

                case ColumnKind.StructArray:
                {
                    if (structType == null)
                    {
                        throw new TableGenException(context + "结构体类型未声明。");
                    }

                    int count = cursor.ReadCount(context);
                    var items = new List<Dictionary<string, object>>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var fields = new Dictionary<string, object>(structType.Fields.Count, StringComparer.Ordinal);
                        for (int fieldIndex = 0; fieldIndex < structType.Fields.Count; fieldIndex++)
                        {
                            StructField field = structType.Fields[fieldIndex];
                            fields.Add(
                                field.Name,
                                ReadValue(cursor, field.Kind, null, context + "元素[" + i + "]." + field.Name + " "));
                        }

                        items.Add(fields);
                    }

                    return items;
                }

                default:
                    throw new TableGenException(context + "列类型未实现：" + kind + "。");
            }
        }

        /// <summary>小端字节流游标：每次读取都做越界检查。</summary>
        private sealed class Cursor
        {
            private readonly byte[] _payload;
            private readonly string _tableName;
            private int _offset;

            internal Cursor(byte[] payload, string tableName)
            {
                _payload = payload;
                _tableName = tableName;
            }

            internal int Remaining
            {
                get { return _payload.Length - _offset; }
            }

            internal int ReadInt32(string context)
            {
                Require(4, context);
                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += 4;
                return value;
            }

            internal byte ReadByte(string context)
            {
                Require(1, context);
                byte value = _payload[_offset];
                _offset += 1;
                return value;
            }

            /// <summary>读变长数组/结构体的元素数：负数直接拒绝，避免把畸形长度当成巨额分配。</summary>
            internal int ReadCount(string context)
            {
                int count = ReadInt32(context);
                if (count < 0)
                {
                    throw new TableGenException(context + "元素数为负：" + count + "。");
                }

                return count;
            }

            internal string ReadUtf8(string context)
            {
                int length = ReadInt32(context);
                if (length < 0)
                {
                    throw new TableGenException(context + "字符串长度为负：" + length + "。");
                }

                Require(length, context);
                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }

            private void Require(int count, string context)
            {
                if (count > Remaining)
                {
                    throw new TableGenException(
                        "表 '" + _tableName + "' " + context + "读取时字节流已截断：偏移 " + _offset
                        + "，需要 " + count + " 字节，剩余 " + Remaining + " 字节。");
                }
            }
        }
    }

    /// <summary>一张表的导出结果。</summary>
    public sealed class SchemaExport
    {
        /// <summary>表名（决定文件名 <c>&lt;Table&gt;.bin</c>）。</summary>
        public string TableName { get; }

        /// <summary>源 JSON 的绝对路径。</summary>
        public string SourcePath { get; }

        /// <summary>导出字节。</summary>
        public byte[] Bytes { get; }

        /// <summary>行数（仅用于打印，不影响字节）。</summary>
        public int RowCount { get; }

        internal SchemaExport(string tableName, string sourcePath, byte[] bytes, int rowCount)
        {
            TableName = tableName;
            SourcePath = sourcePath;
            Bytes = bytes;
            RowCount = rowCount;
        }
    }

    /// <summary><c>tables/</c> 目录级导出：读全部 <c>*.json</c> 并编码。</summary>
    public static class TableExporter
    {
        /// <summary>导出目录里的全部表（内存中完成，不写盘）。</summary>
        /// <param name="tablesDirectory">JSON 源目录。</param>
        /// <returns>按表名排序的导出结果。</returns>
        /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
        /// <exception cref="TableGenException">任一表定义不合法。</exception>
        public static IReadOnlyList<SchemaExport> ExportDirectory(string tablesDirectory)
        {
            if (string.IsNullOrWhiteSpace(tablesDirectory))
            {
                throw new ArgumentException("表定义目录不能为空。", nameof(tablesDirectory));
            }

            string directory = Path.GetFullPath(tablesDirectory);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("表定义目录不存在：" + directory);
            }

            string[] files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0)
            {
                throw new TableGenException("目录 '" + directory + "' 里没有任何 *.json 表定义。");
            }

            var exports = new List<SchemaExport>(files.Length);
            var seenTables = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string fileName = Path.GetFileNameWithoutExtension(file);
                string sourceName = fileName + ".json";
                TableData data;
                try
                {
                    data = TableSchemaReader.Parse(File.ReadAllText(file), sourceName);
                }
                catch (TableGenException)
                {
                    throw;
                }
                catch (IOException ex)
                {
                    throw new TableGenException("读取表定义 '" + sourceName + "' 失败：" + ex.Message);
                }

                if (!string.Equals(data.Schema.TableName, fileName, StringComparison.Ordinal))
                {
                    throw new TableGenException(
                        "表定义 '" + sourceName + "' 的 'table' 是 '" + data.Schema.TableName
                        + "'，与文件名不一致（文件名必须等于表名）。");
                }

                if (!seenTables.Add(data.Schema.TableName))
                {
                    throw new TableGenException("表名重复：" + data.Schema.TableName + "。");
                }

                exports.Add(new SchemaExport(data.Schema.TableName, file, TableEncoder.Encode(data), data.Rows.Count));
            }

            return exports;
        }
    }

    /// <summary>差异种类。</summary>
    public enum TableDifferenceKind
    {
        /// <summary>目标目录缺这个 <c>.bin</c>。</summary>
        Missing,

        /// <summary>字节内容不一致。</summary>
        Content,

        /// <summary>目标目录里多出 <c>tables/</c> 没有的 <c>.bin</c>。</summary>
        Extra
    }

    /// <summary>一处差异。</summary>
    public sealed class TableDifference
    {
        /// <summary>表名（<see cref="TableDifferenceKind.Extra"/> 时是多出来的文件名去扩展名）。</summary>
        public string TableName { get; }

        /// <summary>目标目录。</summary>
        public string TargetDirectory { get; }

        /// <summary>差异种类。</summary>
        public TableDifferenceKind Kind { get; }

        /// <summary>首个不同字节的下标；非 <see cref="TableDifferenceKind.Content"/> 时为 <c>-1</c>。</summary>
        public int FirstDifferenceOffset { get; }

        /// <summary>面向人的说明。</summary>
        public string Detail { get; }

        internal TableDifference(
            string tableName,
            string targetDirectory,
            TableDifferenceKind kind,
            int firstDifferenceOffset,
            string detail)
        {
            TableName = tableName;
            TargetDirectory = targetDirectory;
            Kind = kind;
            FirstDifferenceOffset = firstDifferenceOffset;
            Detail = detail;
        }
    }

    /// <summary>导出结果与目标目录的逐字节比对（<c>--check</c> / <c>--verify-against</c> 共用）。</summary>
    public static class TableComparison
    {
        /// <summary>逐字节比较；一致返回 <c>-1</c>，否则返回首个不同字节下标（长度不同则返回较短长度）。</summary>
        /// <param name="expected">期望字节。</param>
        /// <param name="actual">实际字节。</param>
        /// <returns>差异下标或 <c>-1</c>。</returns>
        public static int FirstDifference(byte[] expected, byte[] actual)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            int shared = Math.Min(expected.Length, actual.Length);
            for (int i = 0; i < shared; i++)
            {
                if (expected[i] != actual[i])
                {
                    return i;
                }
            }

            return expected.Length == actual.Length ? -1 : shared;
        }

        /// <summary>把导出结果与目标目录里的 <c>&lt;Table&gt;.bin</c> 逐字节比对。</summary>
        /// <param name="exports">导出结果。</param>
        /// <param name="targetDirectory">目标目录。</param>
        /// <returns>差异清单（空表示完全一致）。</returns>
        public static IReadOnlyList<TableDifference> CompareDirectory(
            IReadOnlyList<SchemaExport> exports,
            string targetDirectory)
        {
            if (exports == null)
            {
                throw new ArgumentNullException(nameof(exports));
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentException("目标目录不能为空。", nameof(targetDirectory));
            }

            string directory = Path.GetFullPath(targetDirectory);
            var differences = new List<TableDifference>();
            if (!Directory.Exists(directory))
            {
                for (int i = 0; i < exports.Count; i++)
                {
                    differences.Add(new TableDifference(
                        exports[i].TableName,
                        directory,
                        TableDifferenceKind.Missing,
                        -1,
                        "目标目录不存在，期望存在 " + exports[i].TableName + ".bin。"));
                }

                return differences;
            }

            var expectedTables = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < exports.Count; i++)
            {
                SchemaExport export = exports[i];
                expectedTables.Add(export.TableName);
                string path = Path.Combine(directory, export.TableName + ".bin");
                if (!File.Exists(path))
                {
                    differences.Add(new TableDifference(
                        export.TableName,
                        directory,
                        TableDifferenceKind.Missing,
                        -1,
                        "缺少 " + export.TableName + ".bin，请重新导出。"));
                    continue;
                }

                byte[] actual = File.ReadAllBytes(path);
                int offset = FirstDifference(export.Bytes, actual);
                if (offset >= 0)
                {
                    differences.Add(new TableDifference(
                        export.TableName,
                        directory,
                        TableDifferenceKind.Content,
                        offset,
                        export.TableName + ".bin 与 JSON 源不一致：期望 " + export.Bytes.Length
                        + " 字节，实际 " + actual.Length + " 字节，首个不同字节偏移 " + offset + "。"));
                }
            }

            // 反向检查：目标目录里多出来的 .bin 一定是"删了表没删产物"，同样算漂移。
            string[] existing = Directory.GetFiles(directory, "*.bin", SearchOption.TopDirectoryOnly);
            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < existing.Length; i++)
            {
                string tableName = Path.GetFileNameWithoutExtension(existing[i]);
                if (!expectedTables.Contains(tableName))
                {
                    differences.Add(new TableDifference(
                        tableName,
                        directory,
                        TableDifferenceKind.Extra,
                        -1,
                        "目标目录多出 " + Path.GetFileName(existing[i]) + "，tables/ 里没有对应表定义。"));
                }
            }

            return differences;
        }
    }
}

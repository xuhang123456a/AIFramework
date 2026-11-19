#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AIFramework.TableGen
{
    /// <summary>
    /// 配置表源数据不合法（列类型未知、行字段缺失/多余、值类型不匹配、字节流截断等）。
    /// <para>
    /// 一律显式抛异常、消息里带表名 + 行号 + 列名：导出器静默跳过一行等于把错误数据打进包，
    /// 比构建失败更贵。
    /// </para>
    /// </summary>
    public sealed class TableGenException : Exception
    {
        /// <summary>用给定消息构造。</summary>
        /// <param name="message">面向人的错误说明（含表名、行号、列名）。</param>
        public TableGenException(string message)
            : base(message)
        {
        }
    }

    /// <summary>列的值类别：四种标量 + 两种并列数组 + 一种结构体数组。</summary>
    public enum ColumnKind
    {
        /// <summary>小端 int32，4 字节。</summary>
        Int32,

        /// <summary>IEEE754 小端 float32，4 字节。</summary>
        Float32,

        /// <summary>bool，1 字节（0 / 1）。</summary>
        Bool,

        /// <summary>UTF-8 字符串：<c>[int32 字节长度]</c> + 字节。</summary>
        String,

        /// <summary><c>int32[]</c>：<c>[int32 元素数]</c> + 元素。</summary>
        Int32Array,

        /// <summary><c>string[]</c>：<c>[int32 元素数]</c> + 每个元素按 <see cref="String"/> 编码。</summary>
        StringArray,

        /// <summary>结构体数组：<c>[int32 元素数]</c> + 每个元素按字段声明顺序编码。</summary>
        StructArray
    }

    /// <summary>结构体类型的一个字段（仅允许标量与数组，不允许再嵌套结构体）。</summary>
    public sealed class StructField
    {
        /// <summary>字段名，必须在该结构体内唯一。</summary>
        public string Name { get; }

        /// <summary>声明里的类型名（如 <c>int32</c> / <c>string</c>）。</summary>
        public string TypeName { get; }

        /// <summary>值类别。</summary>
        public ColumnKind Kind { get; }

        internal StructField(string name, string typeName, ColumnKind kind)
        {
            Name = name;
            TypeName = typeName;
            Kind = kind;
        }
    }

    /// <summary>在 JSON 里 <c>types</c> 下声明的结构体（用于 <c>TbLocalization</c> 这类交错布局）。</summary>
    public sealed class StructType
    {
        /// <summary>结构体名（列类型写成 <c>&lt;Name&gt;[]</c> 引用它）。</summary>
        public string Name { get; }

        /// <summary>字段列表，顺序即编码顺序。</summary>
        public IReadOnlyList<StructField> Fields { get; }

        internal StructType(string name, IReadOnlyList<StructField> fields)
        {
            Name = name;
            Fields = fields;
        }
    }

    /// <summary>一列的定义：名字 + 类型。</summary>
    public sealed class TableColumn
    {
        /// <summary>列名（同时是 JSON 行里的字段名）。</summary>
        public string Name { get; }

        /// <summary>声明里的类型名。</summary>
        public string TypeName { get; }

        /// <summary>值类别。</summary>
        public ColumnKind Kind { get; }

        /// <summary><see cref="ColumnKind.StructArray"/> 时指向的结构体定义；其余为 <c>null</c>。</summary>
        public StructType? Struct { get; }

        internal TableColumn(string name, string typeName, ColumnKind kind, StructType? structType)
        {
            Name = name;
            TypeName = typeName;
            Kind = kind;
            Struct = structType;
        }

        /// <summary>该列单行占用的定长字节数；变长返回 <c>-1</c>。</summary>
        public int FixedByteSize
        {
            get
            {
                switch (Kind)
                {
                    case ColumnKind.Int32:
                    case ColumnKind.Float32:
                        return 4;
                    case ColumnKind.Bool:
                        return 1;
                    default:
                        return -1;
                }
            }
        }
    }

    /// <summary>一张表的形状：表名 + 列 + 可用的结构体定义。</summary>
    public sealed class TableSchema
    {
        /// <summary>表名（同时决定输出文件名 <c>&lt;Table&gt;.bin</c>）。</summary>
        public string TableName { get; }

        /// <summary>列定义，顺序即编码顺序。</summary>
        public IReadOnlyList<TableColumn> Columns { get; }

        /// <summary>本表可引用的结构体定义（按名字索引）。</summary>
        public IReadOnlyDictionary<string, StructType> Types { get; }

        internal TableSchema(
            string tableName,
            IReadOnlyList<TableColumn> columns,
            IReadOnlyDictionary<string, StructType> types)
        {
            TableName = tableName;
            Columns = columns;
            Types = types;
        }

        /// <summary>单行是否定长；定长时返回字节数，否则 <c>-1</c>。</summary>
        public int FixedRowByteSize
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Columns.Count; i++)
                {
                    int size = Columns[i].FixedByteSize;
                    if (size < 0)
                    {
                        return -1;
                    }

                    total += size;
                }

                return total;
            }
        }
    }

    /// <summary>一张表解析后的完整源数据：形状 + 已按列顺序排好的行值。</summary>
    public sealed class TableData
    {
        /// <summary>表形状。</summary>
        public TableSchema Schema { get; }

        /// <summary>行数据：每行是与 <see cref="TableSchema.Columns"/> 一一对应的值数组。</summary>
        public IReadOnlyList<JsonElement[]> Rows { get; }

        internal TableData(TableSchema schema, IReadOnlyList<JsonElement[]> rows)
        {
            Schema = schema;
            Rows = rows;
        }
    }

    /// <summary>
    /// <c>tables/*.json</c> 的解析器：只做"形状 + 值校验"，不碰字节流（编码在 <see cref="TableEncoder"/>）。
    /// </summary>
    public static class TableSchemaReader
    {
        private static readonly string[] ScalarTypeNames = { "int32", "float32", "bool", "string" };
        private static readonly string[] ArrayTypeNames = { "int32[]", "string[]" };

        /// <summary>解析一份表定义（含行数据）。</summary>
        /// <param name="json">UTF-8 JSON 文本。</param>
        /// <param name="sourceName">报错用的来源名（通常是仓库相对路径或文件名）。</param>
        /// <returns>表形状与行数据。</returns>
        /// <exception cref="TableGenException">JSON 非法或形状/值不符合约定。</exception>
        public static TableData Parse(string json, string sourceName)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new TableGenException(Describe(sourceName) + "不是合法 JSON：" + ex.Message);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new TableGenException(Describe(sourceName) + "根节点必须是对象。");
                }

                string tableName = ReadTableName(root, sourceName);
                IReadOnlyDictionary<string, StructType> types = ReadTypes(root, sourceName);
                IReadOnlyList<TableColumn> columns = ReadColumns(root, types, sourceName);
                var schema = new TableSchema(tableName, columns, types);
                IReadOnlyList<JsonElement[]> rows = ReadRows(root, schema, sourceName);
                return new TableData(schema, rows);
            }
        }

        /// <summary>只解析表形状（忽略行数据），供文档/测试检查列定义。</summary>
        /// <param name="json">UTF-8 JSON 文本。</param>
        /// <param name="sourceName">报错用的来源名。</param>
        /// <returns>表形状。</returns>
        public static TableSchema ParseSchema(string json, string sourceName)
        {
            return Parse(json, sourceName).Schema;
        }

        private static string Describe(string sourceName)
        {
            return string.IsNullOrEmpty(sourceName) ? "表定义" : "表定义 '" + sourceName + "' ";
        }

        private static string ReadTableName(JsonElement root, string sourceName)
        {
            JsonElement tableElement;
            if (!root.TryGetProperty("table", out tableElement) || tableElement.ValueKind != JsonValueKind.String)
            {
                throw new TableGenException(Describe(sourceName) + "缺少字符串字段 'table'。");
            }

            string tableName = tableElement.GetString() ?? string.Empty;
            if (tableName.Length == 0)
            {
                throw new TableGenException(Describe(sourceName) + "字段 'table' 不能为空。");
            }

            for (int i = 0; i < tableName.Length; i++)
            {
                char c = tableName[i];
                bool legal = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_';
                if (!legal)
                {
                    throw new TableGenException(
                        Describe(sourceName) + "字段 'table' 含非法字符 '" + c + "'（只允许字母、数字、下划线）。");
                }
            }

            return tableName;
        }

        private static IReadOnlyDictionary<string, StructType> ReadTypes(JsonElement root, string sourceName)
        {
            var result = new Dictionary<string, StructType>(StringComparer.Ordinal);
            JsonElement typesElement;
            if (!root.TryGetProperty("types", out typesElement) || typesElement.ValueKind == JsonValueKind.Null)
            {
                return result;
            }

            if (typesElement.ValueKind != JsonValueKind.Object)
            {
                throw new TableGenException(Describe(sourceName) + "字段 'types' 必须是对象。");
            }

            foreach (JsonProperty property in typesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new TableGenException(
                        Describe(sourceName) + "结构体 '" + property.Name + "' 的字段列表必须是数组。");
                }

                var fields = new List<StructField>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement fieldElement in property.Value.EnumerateArray())
                {
                    string fieldName = ReadName(fieldElement, "name", Describe(sourceName) + "结构体 '" + property.Name + "'");
                    if (!seen.Add(fieldName))
                    {
                        throw new TableGenException(
                            Describe(sourceName) + "结构体 '" + property.Name + "' 的字段名重复：'" + fieldName + "'。");
                    }

                    string typeName = ReadName(fieldElement, "type", Describe(sourceName) + "结构体 '" + property.Name + "'");
                    if (Array.IndexOf(ScalarTypeNames, typeName) < 0 && Array.IndexOf(ArrayTypeNames, typeName) < 0)
                    {
                        throw new TableGenException(
                            Describe(sourceName) + "结构体 '" + property.Name + "' 字段 '" + fieldName
                            + "' 类型未知：'" + typeName + "'（只允许 int32 / float32 / bool / string / int32[] / string[]）。");
                    }

                    ColumnKind fieldKind;
                    if (!TryResolveScalarKind(typeName, out fieldKind))
                    {
                        throw new TableGenException(
                            Describe(sourceName) + "结构体 '" + property.Name + "' 字段 '" + fieldName
                            + "' 类型未知：'" + typeName + "'。");
                    }

                    fields.Add(new StructField(fieldName, typeName, fieldKind));
                }

                if (fields.Count == 0)
                {
                    throw new TableGenException(
                        Describe(sourceName) + "结构体 '" + property.Name + "' 至少要有一个字段。");
                }

                if (result.ContainsKey(property.Name))
                {
                    throw new TableGenException(
                        Describe(sourceName) + "结构体名重复：'" + property.Name + "'。");
                }

                result.Add(property.Name, new StructType(property.Name, fields));
            }

            return result;
        }

        private static IReadOnlyList<TableColumn> ReadColumns(
            JsonElement root,
            IReadOnlyDictionary<string, StructType> types,
            string sourceName)
        {
            JsonElement columnsElement;
            if (!root.TryGetProperty("columns", out columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            {
                throw new TableGenException(Describe(sourceName) + "缺少数组字段 'columns'。");
            }

            var columns = new List<TableColumn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement columnElement in columnsElement.EnumerateArray())
            {
                string columnName = ReadName(columnElement, "name", Describe(sourceName) + "列定义");
                if (!seen.Add(columnName))
                {
                    throw new TableGenException(Describe(sourceName) + "列名重复：'" + columnName + "'。");
                }

                string typeName = ReadName(columnElement, "type", Describe(sourceName) + "列 '" + columnName + "'");
                StructType? structType;
                ColumnKind kind = ResolveKind(typeName, types, sourceName, columnName, out structType);
                columns.Add(new TableColumn(columnName, typeName, kind, structType));
            }

            if (columns.Count == 0)
            {
                throw new TableGenException(Describe(sourceName) + "字段 'columns' 至少要声明一列。");
            }

            return columns;
        }

        private static IReadOnlyList<JsonElement[]> ReadRows(JsonElement root, TableSchema schema, string sourceName)
        {
            var rows = new List<JsonElement[]>();
            JsonElement rowsElement;
            if (!root.TryGetProperty("rows", out rowsElement) || rowsElement.ValueKind == JsonValueKind.Null)
            {
                return rows;
            }

            if (rowsElement.ValueKind != JsonValueKind.Array)
            {
                throw new TableGenException(Describe(sourceName) + "字段 'rows' 必须是数组。");
            }

            int rowIndex = 0;
            foreach (JsonElement rowElement in rowsElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Object)
                {
                    throw new TableGenException(
                        Describe(sourceName) + "表 '" + schema.TableName + "' 第 " + rowIndex + " 行必须是对象。");
                }

                var values = new JsonElement[schema.Columns.Count];
                var provided = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in rowElement.EnumerateObject())
                {
                    provided.Add(property.Name);
                }

                for (int i = 0; i < schema.Columns.Count; i++)
                {
                    TableColumn column = schema.Columns[i];
                    JsonElement value;
                    if (!rowElement.TryGetProperty(column.Name, out value))
                    {
                        throw new TableGenException(
                            Describe(sourceName) + "表 '" + schema.TableName + "' 第 " + rowIndex
                            + " 行缺少列 '" + column.Name + "'（声明 " + schema.Columns.Count
                            + " 列，实际 " + provided.Count + " 列）。");
                    }

                    values[i] = value.Clone();
                }

                if (provided.Count != schema.Columns.Count)
                {
                    var extras = new List<string>();
                    foreach (string name in provided)
                    {
                        bool declared = false;
                        for (int i = 0; i < schema.Columns.Count; i++)
                        {
                            if (string.Equals(schema.Columns[i].Name, name, StringComparison.Ordinal))
                            {
                                declared = true;
                                break;
                            }
                        }

                        if (!declared)
                        {
                            extras.Add(name);
                        }
                    }

                    extras.Sort(StringComparer.Ordinal);
                    throw new TableGenException(
                        Describe(sourceName) + "表 '" + schema.TableName + "' 第 " + rowIndex + " 行列数不匹配：声明 "
                        + schema.Columns.Count + " 列，实际 " + provided.Count + " 列，多出列 '"
                        + string.Join("', '", extras) + "'。");
                }

                rows.Add(values);
                rowIndex++;
            }

            return rows;
        }

        private static string ReadName(JsonElement element, string propertyName, string context)
        {
            JsonElement value;
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out value)
                || value.ValueKind != JsonValueKind.String)
            {
                throw new TableGenException(context + "缺少字符串字段 '" + propertyName + "'。");
            }

            string name = value.GetString() ?? string.Empty;
            if (name.Length == 0)
            {
                throw new TableGenException(context + "字段 '" + propertyName + "' 不能为空。");
            }

            return name;
        }

        /// <summary>标量与并列数组类型 → 值类别；未知类型返回 <c>false</c>。</summary>
        private static bool TryResolveScalarKind(string typeName, out ColumnKind kind)
        {
            switch (typeName)
            {
                case "int32":
                    kind = ColumnKind.Int32;
                    return true;
                case "float32":
                    kind = ColumnKind.Float32;
                    return true;
                case "bool":
                    kind = ColumnKind.Bool;
                    return true;
                case "string":
                    kind = ColumnKind.String;
                    return true;
                case "int32[]":
                    kind = ColumnKind.Int32Array;
                    return true;
                case "string[]":
                    kind = ColumnKind.StringArray;
                    return true;
                default:
                    kind = ColumnKind.Int32;
                    return false;
            }
        }

        private static ColumnKind ResolveKind(
            string typeName,
            IReadOnlyDictionary<string, StructType> types,
            string sourceName,
            string owner,
            out StructType? structType)
        {
            structType = null;
            ColumnKind scalarKind;
            if (TryResolveScalarKind(typeName, out scalarKind))
            {
                return scalarKind;
            }

            if (typeName.EndsWith("[]", StringComparison.Ordinal))
            {
                string structName = typeName.Substring(0, typeName.Length - 2);
                StructType? declared;
                if (structName.Length > 0 && types.TryGetValue(structName, out declared))
                {
                    structType = declared;
                    return ColumnKind.StructArray;
                }
            }

            throw new TableGenException(
                Describe(sourceName) + "'" + owner + "' 的列类型未知：'" + typeName
                + "'（允许 int32 / float32 / bool / string / int32[] / string[] / <types 里声明的结构体>[]）。");
        }

        /// <summary>把 JSON 数字读成 int32；越界或非整数抛 <see cref="TableGenException"/>。</summary>
        /// <param name="value">JSON 值。</param>
        /// <param name="context">报错上下文（表名/行号/列名）。</param>
        /// <returns>int32 值。</returns>
        internal static int ReadInt32(JsonElement value, string context)
        {
            int result;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out result))
            {
                throw new TableGenException(
                    context + "期望 int32，实际 " + DescribeValue(value) + "。");
            }

            return result;
        }

        /// <summary>把 JSON 数字读成 float32（先按 double 校验范围，避免 Infinity 静默入库）。</summary>
        /// <param name="value">JSON 值。</param>
        /// <param name="context">报错上下文。</param>
        /// <returns>float32 值。</returns>
        internal static float ReadFloat32(JsonElement value, string context)
        {
            if (value.ValueKind != JsonValueKind.Number)
            {
                throw new TableGenException(
                    context + "期望 float32，实际 " + DescribeValue(value) + "。");
            }

            double asDouble = value.GetDouble();
            float asSingle = (float)asDouble;
            if (float.IsInfinity(asSingle) || float.IsNaN(asSingle))
            {
                throw new TableGenException(
                    context + "数值超出 float32 范围：" + asDouble.ToString("R", CultureInfo.InvariantCulture) + "。");
            }

            return asSingle;
        }

        /// <summary>把 JSON 布尔读成 bool（只接受 true / false，不接受 0 / 1）。</summary>
        /// <param name="value">JSON 值。</param>
        /// <param name="context">报错上下文。</param>
        /// <returns>布尔值。</returns>
        internal static bool ReadBool(JsonElement value, string context)
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            throw new TableGenException(
                context + "期望 bool（true / false），实际 " + DescribeValue(value) + "。");
        }

        /// <summary>把 JSON 字符串读成 C# 字符串。</summary>
        /// <param name="value">JSON 值。</param>
        /// <param name="context">报错上下文。</param>
        /// <returns>字符串。</returns>
        internal static string ReadString(JsonElement value, string context)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new TableGenException(
                    context + "期望 string，实际 " + DescribeValue(value) + "。");
            }

            return value.GetString() ?? string.Empty;
        }

        /// <summary>把 JSON 数组读成元素列表；用于 <c>int32[]</c> / <c>string[]</c> / 结构体数组。</summary>
        /// <param name="value">JSON 值。</param>
        /// <param name="context">报错上下文。</param>
        /// <returns>元素列表。</returns>
        internal static IReadOnlyList<JsonElement> ReadArray(JsonElement value, string context)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new TableGenException(
                    context + "期望数组，实际 " + DescribeValue(value) + "。");
            }

            var items = new List<JsonElement>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                items.Add(item);
            }

            return items;
        }

        private static string DescribeValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    return "数组";
                case JsonValueKind.Object:
                    return "对象";
                case JsonValueKind.Null:
                    return "null";
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return "bool";
                case JsonValueKind.String:
                    return "字符串 \"" + value.GetString() + "\"";
                case JsonValueKind.Number:
                    return "数字 " + value.GetRawText();
                default:
                    return value.ValueKind.ToString();
            }
        }
    }
}

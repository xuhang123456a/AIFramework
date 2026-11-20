using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AIFramework.AnalyticsCheck
{
    /// <summary>一条埋点契约/调用点问题：事件名 + 位置 + 说明。</summary>
    public readonly struct AnalyticsIssue
    {
        public AnalyticsIssue(string eventName, string location, string message, bool isError)
        {
            EventName = eventName ?? string.Empty;
            Location = location ?? string.Empty;
            Message = message ?? string.Empty;
            IsError = isError;
        }

        /// <summary>事件常量名；直接写数字的调用点记数字本身。</summary>
        public string EventName { get; }

        /// <summary>来源位置（仓库相对路径:行号），契约级问题指向常量声明行。</summary>
        public string Location { get; }

        public string Message { get; }

        /// <summary>true = 必须修（撞号/悬空引用/字段数量不符）；false = 提示（魔法数字/废弃号）。</summary>
        public bool IsError { get; }

        public override string ToString()
        {
            return (IsError ? "✗ " : "⚠ ") + "事件=" + EventName + " 位置=" + Location + " " + Message;
        }
    }

    /// <summary>契约里的一条事件号常量。</summary>
    public readonly struct AnalyticsEventDefinition
    {
        public AnalyticsEventDefinition(string name, int value, int line)
        {
            Name = name;
            Value = value;
            Line = line;
        }

        public string Name { get; }

        public int Value { get; }

        /// <summary>常量在契约文件里的行号（1 起）。</summary>
        public int Line { get; }

        /// <summary>是否为保留哨兵（<see cref="AnalyticsRules.ReservedEventName"/>，值固定 0）。</summary>
        public bool IsReserved
        {
            get { return string.Equals(Name, AnalyticsRules.ReservedEventName, StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// 解析出来的埋点契约：事件名 → 事件号，以及 <c>AnalyticsEvent</c> 构造参数个数。
    /// </summary>
    public sealed class AnalyticsContract
    {
        private readonly Dictionary<string, AnalyticsEventDefinition> _byName;

        internal AnalyticsContract(string location, List<AnalyticsEventDefinition> events, int constructorParameterCount)
        {
            Location = location;
            Events = events;
            ConstructorParameterCount = constructorParameterCount;
            _byName = new Dictionary<string, AnalyticsEventDefinition>(StringComparer.Ordinal);

            int eventCount = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (!_byName.ContainsKey(events[i].Name))
                {
                    _byName.Add(events[i].Name, events[i]);
                }

                if (!events[i].IsReserved)
                {
                    eventCount++;
                }
            }

            EventCount = eventCount;
        }

        /// <summary>契约文件位置（仓库相对路径）。</summary>
        public string Location { get; }

        /// <summary>全部常量声明（含保留哨兵），按源码顺序。</summary>
        public IReadOnlyList<AnalyticsEventDefinition> Events { get; }

        /// <summary>可统计事件数（不含保留哨兵 None）。</summary>
        public int EventCount { get; }

        /// <summary><c>AnalyticsEvent</c> 构造函数参数个数（从契约源码提取）。</summary>
        public int ConstructorParameterCount { get; }

        public bool Contains(string name)
        {
            return name != null && _byName.ContainsKey(name);
        }

        public bool TryGetValue(string name, out int value)
        {
            if (name != null && _byName.TryGetValue(name, out AnalyticsEventDefinition definition))
            {
                value = definition.Value;
                return true;
            }

            value = 0;
            return false;
        }
    }

    /// <summary>
    /// 契约解析结果。结构性失败（找不到类/构造函数）时 <see cref="Success"/> 为 false，
    /// 对应 CLI 退出码 3；撞号/重复名/非正号属于规则错误，仍返回契约并由 <see cref="Issues"/> 报告。
    /// </summary>
    public sealed class AnalyticsContractParseResult
    {
        internal AnalyticsContractParseResult(
            AnalyticsContract? contract,
            string? failureReason,
            List<AnalyticsIssue> issues)
        {
            Contract = contract;
            FailureReason = failureReason;
            Issues = issues;
        }

        public bool Success
        {
            get { return Contract != null; }
        }

        public AnalyticsContract? Contract { get; }

        public string? FailureReason { get; }

        public List<AnalyticsIssue> Issues { get; }
    }

    /// <summary>一次扫描的汇总：问题清单 + 每个事件的引用次数。</summary>
    public sealed class AnalyticsScanResult
    {
        internal AnalyticsScanResult(
            List<AnalyticsIssue> issues,
            Dictionary<string, int> referenceCounts,
            int referenceCount,
            int constructorCallCount)
        {
            Issues = issues;
            ReferenceCounts = referenceCounts;
            ReferenceCount = referenceCount;
            ConstructorCallCount = constructorCallCount;

            int errors = 0;
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].IsError)
                {
                    errors++;
                }
            }

            ErrorCount = errors;
            HintCount = issues.Count - errors;
        }

        public IReadOnlyList<AnalyticsIssue> Issues { get; }

        /// <summary>事件名 → 被引用次数（只统计契约里存在的事件）。</summary>
        public IReadOnlyDictionary<string, int> ReferenceCounts { get; }

        /// <summary>全仓 <c>AnalyticsEventIds.X</c> 的文本引用总次数（含悬空引用）。</summary>
        public int ReferenceCount { get; }

        public int ConstructorCallCount { get; }

        public int ErrorCount { get; }

        public int HintCount { get; }
    }

    /// <summary>
    /// 埋点校验规则库（与 CLI 分离，可直接被测试调用）。
    /// <para>
    /// 为什么需要它：埋点的事件号是"后台看板的历史数据主键"。撞号会让两条事件在报表里串档，
    /// 悬空引用会在运行时静默丢事件（<c>AnalyticsEvent.IsValid</c> 不检查常量是否存在），
    /// 而这两类问题都能编译通过，只能靠静态扫描提前发现。
    /// </para>
    /// <para>
    /// <b>刻意不解析 C# AST</b>：工具跑在 .NET 10 上、不引 Roslyn，用正则 + 轻量括号扫描实现。
    /// 已知误报口径：引用次数按**原始文本**统计，注释与字符串里出现的
    /// <c>AnalyticsEventIds.X</c> 也会被计入（见 <see cref="Scan"/>）；
    /// 但常量定义会先屏蔽注释与字符串再提取，避免"注释掉的废弃号"被复活成有效定义。
    /// </para>
    /// </summary>
    public static class AnalyticsRules
    {
        /// <summary>保留哨兵名：值固定为 0（"无效事件"），不参与"废弃号"提示，也不要求为正数。</summary>
        public const string ReservedEventName = "None";

        /// <summary>契约文件的仓库相对路径。</summary>
        public const string ContractRelativePath = "src/AIFramework.Contracts/Services/AnalyticsContracts.cs";

        /// <summary>默认扫描目录名（相对仓库根）。</summary>
        public const string SourceDirectoryName = "src";

        /// <summary>扫描时要跳过的目录段（任意层级，大小写不敏感）。</summary>
        private static readonly string[] ExcludedDirectoryNames = { "tests", "artifacts", "bin", "obj" };

        private static readonly Regex EventIdConstantRegex = new Regex(
            @"\bpublic\s+const\s+int\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[+-]?(?:0[xX][0-9A-Fa-f_]+|\d[\d_]*))\s*;",
            RegexOptions.Compiled);

        private static readonly Regex EventIdsClassRegex = new Regex(
            @"\bclass\s+AnalyticsEventIds\b",
            RegexOptions.Compiled);

        private static readonly Regex ConstructorRegex = new Regex(
            @"\bpublic\s+AnalyticsEvent\s*\((?<params>[^)]*)\)",
            RegexOptions.Compiled);

        private static readonly Regex ReferenceRegex = new Regex(
            @"\bAnalyticsEventIds\s*\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

        private static readonly Regex QualifiedReferenceRegex = new Regex(
            @"^(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*AnalyticsEventIds\s*\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)$",
            RegexOptions.Compiled);

        private static readonly Regex NewAnalyticsEventRegex = new Regex(
            @"\bnew\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*AnalyticsEvent\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex IntegerLiteralRegex = new Regex(
            @"^-?(?:0[xX][0-9A-Fa-f_]+|\d[\d_]*)[uUlL]{0,2}$",
            RegexOptions.Compiled);

        /// <summary>
        /// 从契约源码文本提取事件号常量与构造函数参数个数。
        /// </summary>
        /// <param name="source">契约文件全文。</param>
        /// <param name="location">契约文件位置（用于问题定位，通常是仓库相对路径）。</param>
        public static AnalyticsContractParseResult ParseContract(string source, string location)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            string contractLocation = location ?? string.Empty;
            string masked = AnalyticsCodeMask.Mask(source);

            Match classMatch = EventIdsClassRegex.Match(masked);
            if (!classMatch.Success)
            {
                return Failure(contractLocation, "未找到 AnalyticsEventIds 类声明（契约文件结构变了？）");
            }

            int openBrace = masked.IndexOf('{', classMatch.Index + classMatch.Length);
            if (openBrace < 0)
            {
                return Failure(contractLocation, "AnalyticsEventIds 类体缺失");
            }

            int closeBrace = FindMatchingBrace(masked, openBrace);
            if (closeBrace < 0)
            {
                return Failure(contractLocation, "AnalyticsEventIds 类体括号未闭合");
            }

            var definitions = new List<AnalyticsEventDefinition>();
            foreach (Match match in EventIdConstantRegex.Matches(masked))
            {
                if (match.Index <= openBrace || match.Index >= closeBrace)
                {
                    continue;
                }

                definitions.Add(new AnalyticsEventDefinition(
                    match.Groups["name"].Value,
                    ParseIntegerLiteral(match.Groups["value"].Value),
                    LineOf(masked, match.Index)));
            }

            Match constructorMatch = ConstructorRegex.Match(masked);
            if (!constructorMatch.Success)
            {
                return Failure(contractLocation, "未找到 AnalyticsEvent 构造函数（无法校验字段数量）");
            }

            var contract = new AnalyticsContract(
                contractLocation,
                definitions,
                CountParameters(constructorMatch.Groups["params"].Value));

            return new AnalyticsContractParseResult(contract, null, ValidateContract(contract));
        }

        /// <summary>契约内部一致性：撞号、重复名、非正事件号（保留哨兵 None=0 豁免）。</summary>
        public static List<AnalyticsIssue> ValidateContract(AnalyticsContract contract)
        {
            if (contract == null)
            {
                throw new ArgumentNullException(nameof(contract));
            }

            var issues = new List<AnalyticsIssue>(4);
            var byName = new Dictionary<string, AnalyticsEventDefinition>(StringComparer.Ordinal);
            var byValue = new Dictionary<int, AnalyticsEventDefinition>();

            for (int i = 0; i < contract.Events.Count; i++)
            {
                AnalyticsEventDefinition definition = contract.Events[i];
                string location = LocationOf(contract, definition.Line);

                if (byName.TryGetValue(definition.Name, out AnalyticsEventDefinition firstName))
                {
                    issues.Add(new AnalyticsIssue(
                        definition.Name,
                        location,
                        "事件名重复定义（首次定义在 " + LocationOf(contract, firstName.Line) + "）",
                        isError: true));
                }
                else
                {
                    byName.Add(definition.Name, definition);
                }

                if (byValue.TryGetValue(definition.Value, out AnalyticsEventDefinition firstValue))
                {
                    issues.Add(new AnalyticsIssue(
                        definition.Name,
                        location,
                        "事件号 " + definition.Value + " 重复定义（已被 " + firstValue.Name + " 使用，看板会串档）",
                        isError: true));
                }
                else
                {
                    byValue.Add(definition.Value, definition);
                }

                if (definition.IsReserved)
                {
                    if (definition.Value != 0)
                    {
                        issues.Add(new AnalyticsIssue(
                            definition.Name,
                            location,
                            "保留哨兵 " + ReservedEventName + " 必须为 0（实际 " + definition.Value + "）",
                            isError: true));
                    }
                }
                else if (definition.Value <= 0)
                {
                    issues.Add(new AnalyticsIssue(
                        definition.Name,
                        location,
                        "事件号必须为正数（0 与负数保留给 " + ReservedEventName + "）",
                        isError: true));
                }
            }

            return issues;
        }

        /// <summary>
        /// 扫描调用点：悬空引用（错误）、构造参数数量不符（错误）、直接写数字（提示）、
        /// 定义了但没人引用（提示）。
        /// </summary>
        /// <param name="files">文件位置（仓库相对路径）→ 文件内容。</param>
        /// <param name="contract">已解析的契约。</param>
        /// <param name="initialIssues">契约校验阶段的问题，会并入结果。</param>
        public static AnalyticsScanResult Scan(
            IReadOnlyDictionary<string, string> files,
            AnalyticsContract contract,
            IEnumerable<AnalyticsIssue>? initialIssues = null)
        {
            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            if (contract == null)
            {
                throw new ArgumentNullException(nameof(contract));
            }

            var issues = new List<AnalyticsIssue>();
            if (initialIssues != null)
            {
                foreach (AnalyticsIssue issue in initialIssues)
                {
                    issues.Add(issue);
                }
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < contract.Events.Count; i++)
            {
                if (!counts.ContainsKey(contract.Events[i].Name))
                {
                    counts.Add(contract.Events[i].Name, 0);
                }
            }

            var paths = new List<string>(files.Keys);
            paths.Sort(StringComparer.Ordinal);

            int referenceCount = 0;
            int constructorCallCount = 0;

            for (int p = 0; p < paths.Count; p++)
            {
                string path = paths[p];
                string text = files[path] ?? string.Empty;

                foreach (Match match in ReferenceRegex.Matches(text))
                {
                    string name = match.Groups["name"].Value;
                    referenceCount++;

                    if (!contract.Contains(name))
                    {
                        issues.Add(new AnalyticsIssue(
                            name,
                            path + ":" + LineOf(text, match.Index),
                            "引用了契约中不存在的事件号（悬空引用：拼写错误或常量已删除）",
                            isError: true));
                        continue;
                    }

                    counts[name] = counts[name] + 1;
                }

                foreach (Match match in NewAnalyticsEventRegex.Matches(text))
                {
                    constructorCallCount++;
                    int openParen = match.Index + match.Length - 1;
                    List<string>? arguments = TryReadArguments(text, openParen);
                    if (arguments == null)
                    {
                        continue;
                    }

                    int line = LineOf(text, match.Index);
                    string eventName = DescribeEventArgument(arguments);

                    if (arguments.Count != contract.ConstructorParameterCount)
                    {
                        issues.Add(new AnalyticsIssue(
                            eventName,
                            path + ":" + line,
                            "构造参数 " + arguments.Count + " 个，契约构造函数是 "
                                + contract.ConstructorParameterCount + " 个（字段数量/顺序与契约不一致）",
                            isError: true));
                    }

                    if (arguments.Count > 0 && IntegerLiteralRegex.IsMatch(arguments[0].Trim()))
                    {
                        issues.Add(new AnalyticsIssue(
                            eventName,
                            path + ":" + line,
                            "事件号直接写字面量 " + arguments[0].Trim()
                                + "（魔法数字，改用 AnalyticsEventIds.XXX 防契约漂移）",
                            isError: false));
                    }
                }
            }

            for (int i = 0; i < contract.Events.Count; i++)
            {
                AnalyticsEventDefinition definition = contract.Events[i];
                if (definition.IsReserved)
                {
                    continue;
                }

                if (counts.TryGetValue(definition.Name, out int count) && count == 0)
                {
                    issues.Add(new AnalyticsIssue(
                        definition.Name,
                        LocationOf(contract, definition.Line),
                        "定义了但没有任何引用（疑似废弃号：确认后清理，或补上调用点）",
                        isError: false));
                }
            }

            return new AnalyticsScanResult(issues, counts, referenceCount, constructorCallCount);
        }

        /// <summary>路径里是否含被排除的目录段（tests/artifacts/bin/obj，任意层级、大小写不敏感）。</summary>
        public static bool IsExcludedPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return false;
            }

            string[] segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                for (int j = 0; j < ExcludedDirectoryNames.Length; j++)
                {
                    if (string.Equals(segments[i], ExcludedDirectoryNames[j], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static AnalyticsContractParseResult Failure(string location, string reason)
        {
            var issues = new List<AnalyticsIssue>
            {
                new AnalyticsIssue("(契约)", location, reason, isError: true),
            };

            return new AnalyticsContractParseResult(null, reason, issues);
        }

        private static string LocationOf(AnalyticsContract contract, int line)
        {
            return contract.Location + ":" + line.ToString(CultureInfo.InvariantCulture);
        }

        private static int FindMatchingBrace(string masked, int openBrace)
        {
            int depth = 0;
            for (int i = openBrace; i < masked.Length; i++)
            {
                if (masked[i] == '{')
                {
                    depth++;
                }
                else if (masked[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static int CountParameters(string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                return 0;
            }

            int count = 1;
            int depth = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                char c = parameters[i];
                switch (c)
                {
                    case '<':
                    case '(':
                    case '[':
                    case '{':
                        depth++;
                        break;
                    case '>':
                    case ')':
                    case ']':
                    case '}':
                        if (depth > 0)
                        {
                            depth--;
                        }

                        break;
                    case ',':
                        if (depth == 0)
                        {
                            count++;
                        }

                        break;
                }
            }

            return count;
        }

        private static int ParseIntegerLiteral(string literal)
        {
            string text = literal.Trim().Replace("_", string.Empty);
            bool negative = false;
            if (text.Length > 0 && (text[0] == '-' || text[0] == '+'))
            {
                negative = text[0] == '-';
                text = text.Substring(1);
            }

            int value;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    value = Convert.ToInt32(text.Substring(2), 16);
                }
                catch (FormatException)
                {
                    value = 0;
                }
                catch (OverflowException)
                {
                    value = 0;
                }
            }
            else if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                value = 0;
            }

            return negative ? -value : value;
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            int limit = index < text.Length ? index : text.Length;
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static string DescribeEventArgument(IReadOnlyList<string> arguments)
        {
            if (arguments.Count == 0)
            {
                return "(无参数)";
            }

            string first = arguments[0].Trim();
            if (first.Length == 0)
            {
                return "(空参数)";
            }

            Match reference = QualifiedReferenceRegex.Match(first);
            if (reference.Success)
            {
                return reference.Groups["name"].Value;
            }

            if (IntegerLiteralRegex.IsMatch(first))
            {
                return first;
            }

            return first.Length <= 40 ? first : first.Substring(0, 37) + "...";
        }

        /// <summary>
        /// 从 <paramref name="openParen"/> 开始读构造调用的实参并切成参数列表；
        /// 括号未闭合返回 null（跳过该调用，不制造噪音）。
        /// </summary>
        private static List<string>? TryReadArguments(string text, int openParen)
        {
            var arguments = new List<string>(6);
            int start = openParen + 1;
            int depth = 1;
            int i = start;

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i = SkipLineComment(text, i);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i = SkipBlockComment(text, i);
                    continue;
                }

                if (c == '@' || c == '$')
                {
                    bool verbatim;
                    int quote = StringQuoteIndex(text, i, out verbatim);
                    if (quote >= 0)
                    {
                        i = SkipString(text, quote, verbatim);
                        continue;
                    }
                }

                if (c == '"')
                {
                    i = SkipString(text, i, false);
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipCharLiteral(text, i);
                    continue;
                }

                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0 && c == ')')
                    {
                        arguments.Add(text.Substring(start, i - start));
                        if (arguments.Count == 1 && string.IsNullOrWhiteSpace(arguments[0]))
                        {
                            arguments.Clear();
                        }

                        return arguments;
                    }

                    i++;
                    continue;
                }

                if (c == ',' && depth == 1)
                {
                    arguments.Add(text.Substring(start, i - start));
                    start = i + 1;
                    i++;
                    continue;
                }

                i++;
            }

            return null;
        }

        private static int SkipLineComment(string text, int index)
        {
            int newline = text.IndexOf('\n', index + 2);
            return newline < 0 ? text.Length : newline + 1;
        }

        private static int SkipBlockComment(string text, int index)
        {
            int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 2;
        }

        private static int SkipString(string text, int quoteIndex, bool verbatim)
        {
            if (!verbatim
                && quoteIndex + 2 < text.Length
                && text[quoteIndex + 1] == '"'
                && text[quoteIndex + 2] == '"')
            {
                // C# 11 原始字符串："""..."""（引号数可多于 3）。
                int quotes = 0;
                while (quoteIndex + quotes < text.Length && text[quoteIndex + quotes] == '"')
                {
                    quotes++;
                }

                int close = text.IndexOf(new string('"', quotes), quoteIndex + quotes, StringComparison.Ordinal);
                return close < 0 ? text.Length : close + quotes;
            }

            int i = quoteIndex + 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        return i + 1;
                    }

                    i++;
                    continue;
                }

                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    return i + 1;
                }

                if (c == '\n')
                {
                    return i;
                }

                i++;
            }

            return text.Length;
        }

        private static int SkipCharLiteral(string text, int quoteIndex)
        {
            int i = quoteIndex + 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    return i + 1;
                }

                if (c == '\n')
                {
                    return i;
                }

                i++;
            }

            return text.Length;
        }

        internal static int StringQuoteIndex(string text, int index, out bool verbatim)
        {
            verbatim = false;
            char c = text[index];

            if (c == '@' && index + 1 < text.Length && text[index + 1] == '"')
            {
                verbatim = true;
                return index + 1;
            }

            if (c == '$')
            {
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    return index + 1;
                }

                if (index + 2 < text.Length
                    && (text[index + 1] == '@' || text[index + 1] == '$')
                    && text[index + 2] == '"')
                {
                    verbatim = text[index + 1] == '@';
                    return index + 2;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// 把注释与字符串内容替换成空格（保留换行与字符位置），用于"只认代码里的常量定义"。
    /// </summary>
    internal static class AnalyticsCodeMask
    {
        public static string Mask(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }

            char[] buffer = source.ToCharArray();
            int i = 0;
            while (i < source.Length)
            {
                char c = source[i];

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    int end = source.IndexOf('\n', i + 2);
                    end = end < 0 ? source.Length : end;
                    MaskRange(buffer, source, i, end);
                    i = end;
                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int end = close < 0 ? source.Length : close + 2;
                    MaskRange(buffer, source, i, end);
                    i = end;
                    continue;
                }

                if (c == '@' || c == '$')
                {
                    bool verbatim;
                    int quote = AnalyticsRules.StringQuoteIndex(source, i, out verbatim);
                    if (quote >= 0)
                    {
                        i = MaskString(buffer, source, quote, verbatim);
                        continue;
                    }
                }

                if (c == '"')
                {
                    i = MaskString(buffer, source, i, false);
                    continue;
                }

                if (c == '\'')
                {
                    i = MaskCharLiteral(buffer, source, i);
                    continue;
                }

                i++;
            }

            return new string(buffer);
        }

        private static int MaskString(char[] buffer, string source, int quoteIndex, bool verbatim)
        {
            int length = source.Length;

            if (!verbatim
                && quoteIndex + 2 < length
                && source[quoteIndex + 1] == '"'
                && source[quoteIndex + 2] == '"')
            {
                int quotes = 0;
                while (quoteIndex + quotes < length && source[quoteIndex + quotes] == '"')
                {
                    quotes++;
                }

                int close = source.IndexOf(new string('"', quotes), quoteIndex + quotes, StringComparison.Ordinal);
                int rawEnd = close < 0 ? length : close + quotes;
                MaskRange(buffer, source, quoteIndex, rawEnd);
                return rawEnd;
            }

            int i = quoteIndex + 1;
            while (i < length)
            {
                char c = source[i];
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                    continue;
                }

                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    break;
                }

                if (c == '\n')
                {
                    break;
                }

                i++;
            }

            int end = i < length ? i : length;
            MaskRange(buffer, source, quoteIndex, end);
            return end;
        }

        private static int MaskCharLiteral(char[] buffer, string source, int quoteIndex)
        {
            int length = source.Length;
            int i = quoteIndex + 1;
            while (i < length)
            {
                char c = source[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    break;
                }

                if (c == '\n')
                {
                    break;
                }

                i++;
            }

            int end = i < length ? i : length;
            MaskRange(buffer, source, quoteIndex, end);
            return end;
        }

        private static void MaskRange(char[] buffer, string source, int start, int end)
        {
            int limit = end < source.Length ? end : source.Length;
            for (int i = start; i < limit; i++)
            {
                if (source[i] != '\n' && source[i] != '\r')
                {
                    buffer[i] = ' ';
                }
            }
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIFramework.TableGen;

namespace AIFramework.SkillCheck
{
    /// <summary>一条技能配置问题：定位到技能 + 等级 + 规则 id + 面向人的说明。</summary>
    public readonly struct SkillIssue
    {
        /// <summary>构造一条问题。</summary>
        /// <param name="skillId">技能 id（0 表示表级问题，如空表、全局冷却极端值）。</param>
        /// <param name="level">等级（0 表示整个技能或整表，不定位到某一级）。</param>
        /// <param name="rule">规则 id（structure / range / growth / effect / tag / outlier / count）。</param>
        /// <param name="message">说明 + 修复方向。</param>
        /// <param name="isError">true = 必须改（数据错）；false = 提示（设计味道）。</param>
        public SkillIssue(int skillId, int level, string rule, string message, bool isError)
        {
            SkillId = skillId;
            Level = level;
            Rule = rule ?? string.Empty;
            Message = message ?? string.Empty;
            IsError = isError;
        }

        /// <summary>技能 id；0 表示整表级问题。</summary>
        public int SkillId { get; }

        /// <summary>等级（1..3）；0 表示不定位到某一级。</summary>
        public int Level { get; }

        /// <summary>规则 id，便于日志检索与测试断言。</summary>
        public string Rule { get; }

        /// <summary>问题说明。</summary>
        public string Message { get; }

        /// <summary>true = 错误（退出码 1）；false = 警告（只打印，不挡提交）。</summary>
        public bool IsError { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            string where;
            if (SkillId > 0)
            {
                where = Level > 0 ? "技能 " + SkillId + " L" + Level : "技能 " + SkillId;
            }
            else
            {
                where = "全表";
            }

            return (IsError ? "✗ " : "⚠ ") + "[" + Rule + "] " + where + "：" + Message;
        }
    }

    /// <summary><c>TbSkill</c> 的一行：纯数据，列序与 <c>tables/TbSkill.json</c> 一致。</summary>
    public readonly struct SkillRow
    {
        /// <summary>构造一行。</summary>
        /// <param name="skillId">技能 id（与 level 组成复合主键）。</param>
        /// <param name="name">技能显示名。</param>
        /// <param name="cooldownMs">冷却（毫秒）。</param>
        /// <param name="castRangePermille">施法距离（千分比，1000 = 基准距离）。</param>
        /// <param name="damage">伤害值；<c>effectType=1</c> 时解释为治疗量，位移/控制填 0。</param>
        /// <param name="effectType">效果类型：0 伤害 / 1 治疗 / 2 护盾 / 3 位移 / 4 控制。</param>
        /// <param name="tagId">玩法标签 id（引用 <c>GameplayTags</c> 里的常量）。</param>
        /// <param name="maxTargets">最大目标数。</param>
        /// <param name="level">技能等级（1..3）。</param>
        public SkillRow(
            int skillId,
            string name,
            int cooldownMs,
            int castRangePermille,
            int damage,
            int effectType,
            int tagId,
            int maxTargets,
            int level)
        {
            SkillId = skillId;
            Name = name ?? string.Empty;
            CooldownMs = cooldownMs;
            CastRangePermille = castRangePermille;
            Damage = damage;
            EffectType = effectType;
            TagId = tagId;
            MaxTargets = maxTargets;
            Level = level;
        }

        /// <summary>技能 id。</summary>
        public int SkillId { get; }

        /// <summary>技能显示名。</summary>
        public string Name { get; }

        /// <summary>冷却（毫秒）。</summary>
        public int CooldownMs { get; }

        /// <summary>施法距离（千分比，1000 = 基准距离）。</summary>
        public int CastRangePermille { get; }

        /// <summary>伤害值；治疗类技能这里装治疗量。</summary>
        public int Damage { get; }

        /// <summary>效果类型（0 伤害 / 1 治疗 / 2 护盾 / 3 位移 / 4 控制）。</summary>
        public int EffectType { get; }

        /// <summary>玩法标签 id。</summary>
        public int TagId { get; }

        /// <summary>最大目标数。</summary>
        public int MaxTargets { get; }

        /// <summary>技能等级。</summary>
        public int Level { get; }
    }

    /// <summary>
    /// 技能配置体检：把 <c>TbSkill</c> 当<b>一张“技能 × 等级”的矩阵</b>看，而不是逐行看。
    /// <para>
    /// <b>为什么需要单独一个工具</b>：<c>tablecheck</c> 逐表逐行查“单行是否合法”（冷却 &gt; 0、数量在区间内），
    /// 但它看不见三类只有把行连起来才成立的问题：<br />
    /// 1. <b>复合主键</b>——<c>TbSkill</c> 的主键是 <c>(skillId, level)</c>，同一个 skillId 允许且<em>必须</em>
    /// 有 level 1..3 三行（单列主键工具会把这三行误判成重复）；<br />
    /// 2. <b>跨等级成长</b>——伤害/治疗量不涨反跌、冷却升级后反而变短，逐行全都合法；<br />
    /// 3. <b>效果与数值的组合语义</b>——位移技能填了伤害、控制技能冷却 800ms，单看每个字段都在区间内。
    /// </para>
    /// <para>
    /// <b>规则的严重级别怎么定</b>：能确定是“填错了”的（复合键重复、等级缺档、冷却 &lt; 100、成长不递增、
    /// 位移带伤害、控制冷却过短、同 Tag 技能超 2 个）判<b>错误</b>——它们没有合理的玩法解释；
    /// 只能确定是“数值味道不对”的（全局冷却均值偏离中位数超过 3 倍）判<b>警告</b>——极端值可能是有意做的
    /// “大招 vs 平 A”设计，不该挡住提交。
    /// </para>
    /// </summary>
    public static class SkillRules
    {
        /// <summary>等级下限（含）。</summary>
        public const int MinLevel = 1;

        /// <summary>等级上限（含）。</summary>
        public const int MaxLevel = 3;

        /// <summary>每个技能必须给出的等级行数（level 1..3 各一行）。</summary>
        public const int LevelsPerSkill = MaxLevel - MinLevel + 1;

        /// <summary>冷却下限：低于它等于“无冷却”，技能会退化成机关枪。</summary>
        public const int MinCooldownMs = 100;

        /// <summary>控制类技能的冷却下限：控制太频繁会让对手全程动不了。</summary>
        public const int MinControlCooldownMs = 2000;

        /// <summary>施法距离下限（千分比）。</summary>
        public const int MinCastRangePermille = 100;

        /// <summary>施法距离上限（千分比）。</summary>
        public const int MaxCastRangePermille = 5000;

        /// <summary>最大目标数下限。</summary>
        public const int MinMaxTargets = 1;

        /// <summary>最大目标数上限。</summary>
        public const int MaxMaxTargets = 20;

        /// <summary>同一个 Tag 允许挂的技能数上限：再多，同 Tag 的仲裁会互相打架。</summary>
        public const int MaxSkillsPerTag = 2;

        /// <summary>全局冷却均值相对中位数的允许偏差（3 倍），超过即视为存在极端值。</summary>
        public const double MeanMedianDeviationTolerance = 3d;

        /// <summary>效果类型：伤害。</summary>
        public const int EffectDamage = 0;

        /// <summary>效果类型：治疗（<c>damage</c> 列被解释为治疗量）。</summary>
        public const int EffectHeal = 1;

        /// <summary>效果类型：护盾。</summary>
        public const int EffectShield = 2;

        /// <summary>效果类型：位移（<c>damage</c> 必须为 0）。</summary>
        public const int EffectDisplace = 3;

        /// <summary>效果类型：控制（<c>cooldownMs</c> 必须 ≥ <see cref="MinControlCooldownMs"/>）。</summary>
        public const int EffectControl = 4;

        /// <summary>已定义的玩法标签 id（与 <c>AIFramework.Contracts/Gameplay/Handles.cs</c> 的 <c>GameplayTags</c> 对齐）。</summary>
        /// <remarks>
        /// <b>为什么在这里重复一份数字而不是引用 Contracts</b>：<c>tools/*</c> 只依赖 <c>tablegen</c>（net10.0），
        /// 不引 Unity、也不把纯逻辑程序集拖进工具链。代价是这份清单要靠本注释与 Contracts 手工对齐——
        /// 新增 Tag 时两边一起改，否则本工具会把新 Tag 误报成“未定义”。
        /// </remarks>
        public static readonly int[] KnownTagIds = { 101, 102, 201, 301 };

        /// <summary>取效果类型的中文名（用于问题说明）。</summary>
        /// <param name="effectType">效果类型值。</param>
        /// <returns>中文名；未定义返回 <c>未定义(n)</c>。</returns>
        public static string EffectTypeName(int effectType)
        {
            switch (effectType)
            {
                case EffectDamage:
                    return "伤害";
                case EffectHeal:
                    return "治疗";
                case EffectShield:
                    return "护盾";
                case EffectDisplace:
                    return "位移";
                case EffectControl:
                    return "控制";
                default:
                    return "未定义(" + effectType + ")";
            }
        }

        /// <summary>取玩法标签名（用于问题说明）；未定义返回空串。</summary>
        /// <param name="tagId">标签 id。</param>
        /// <returns>形如 <c>ProjReflected(102)</c>；未定义返回空串。</returns>
        public static string TagName(int tagId)
        {
            switch (tagId)
            {
                case 101:
                    return "ProjUnreflectable(101)";
                case 102:
                    return "ProjReflected(102)";
                case 201:
                    return "UnitShieldActive(201)";
                case 301:
                    return "EventShieldReflected(301)";
                default:
                    return string.Empty;
            }
        }

        /// <summary>某个 Tag 是否在已知清单里。</summary>
        /// <param name="tagId">标签 id。</param>
        /// <returns>已知返回 <c>true</c>。</returns>
        public static bool IsKnownTag(int tagId)
        {
            for (int i = 0; i < KnownTagIds.Length; i++)
            {
                if (KnownTagIds[i] == tagId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>全局冷却均值（毫秒）；空表返回 0。</summary>
        /// <param name="rows">技能行。</param>
        /// <returns>算术平均。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rows"/> 为 <c>null</c>。</exception>
        public static double MeanCooldownMs(IReadOnlyList<SkillRow> rows)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (rows.Count == 0)
            {
                return 0d;
            }

            long total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].CooldownMs;
            }

            return total / (double)rows.Count;
        }

        /// <summary>全局冷却中位数（毫秒）；空表返回 0；偶数行取中间两个的平均。</summary>
        /// <param name="rows">技能行。</param>
        /// <returns>中位数。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rows"/> 为 <c>null</c>。</exception>
        public static double MedianCooldownMs(IReadOnlyList<SkillRow> rows)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (rows.Count == 0)
            {
                return 0d;
            }

            var values = new int[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                values[i] = rows[i].CooldownMs;
            }

            Array.Sort(values);
            int middle = values.Length / 2;
            if ((values.Length & 1) == 1)
            {
                return values[middle];
            }

            return (values[middle - 1] + (double)values[middle]) / 2d;
        }

        /// <summary>跑全部规则，把问题追加到 <paramref name="issues"/>（顺序稳定，便于测试）。</summary>
        /// <param name="rows">按表内行序排列的技能行。</param>
        /// <param name="issues">输出列表。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
        public static void Validate(IReadOnlyList<SkillRow> rows, List<SkillIssue> issues)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (issues == null)
            {
                throw new ArgumentNullException(nameof(issues));
            }

            if (rows.Count == 0)
            {
                // CLI 在读取阶段就把空表判成解析失败（退出码 3）；这里再兜一次，
                // 避免其它调用方把"空表"当成"全部规则通过"。
                issues.Add(new SkillIssue(
                    0,
                    0,
                    "count",
                    "表里一行技能都没有：技能配置等于没配，战斗侧只能回退内置技能",
                    isError: true));
                return;
            }

            List<SkillGroup> groups = GroupBySkill(rows);
            CheckStructure(groups, issues);
            CheckRanges(rows, issues);
            CheckGrowth(groups, issues);
            CheckEffectConsistency(rows, issues);
            CheckTagSharing(groups, issues);
            CheckCooldownOutlier(rows, issues);
        }

        /// <summary>按 skillId 分组（保留首次出现顺序），组内按 level 升序。</summary>
        private static List<SkillGroup> GroupBySkill(IReadOnlyList<SkillRow> rows)
        {
            var groups = new List<SkillGroup>();
            var index = new Dictionary<int, SkillGroup>();
            for (int i = 0; i < rows.Count; i++)
            {
                SkillRow row = rows[i];
                SkillGroup group;
                if (index.ContainsKey(row.SkillId))
                {
                    group = index[row.SkillId];
                }
                else
                {
                    group = new SkillGroup(row.SkillId);
                    index.Add(row.SkillId, group);
                    groups.Add(group);
                }

                group.Rows.Add(row);
            }

            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].Rows.Sort(CompareByLevel);
            }

            return groups;
        }

        private static int CompareByLevel(SkillRow left, SkillRow right)
        {
            return left.Level.CompareTo(right.Level);
        }

        /// <summary>
        /// 规则 1：复合主键 <c>(skillId, level)</c> 唯一，且每个 skillId 恰好 level 1..3 各一行。
        /// <para>
        /// <b>为什么不是 skillId 唯一</b>：技能表是“技能 × 等级”矩阵，同一技能的 3 个等级是正常数据；
        /// 真正会互相覆盖的是<b>同一等级出现两次</b>。把 skillId 当单列主键的工具（tablecheck 的未知表兜底）
        /// 会在这里误报，所以复合键的判定只能由本工具承担。
        /// </para>
        /// </summary>
        private static void CheckStructure(List<SkillGroup> groups, List<SkillIssue> issues)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                SkillGroup group = groups[g];
                if (group.SkillId <= 0)
                {
                    issues.Add(new SkillIssue(
                        group.SkillId,
                        0,
                        "structure",
                        "skillId = " + group.SkillId + " 非法：技能主键必须 > 0",
                        isError: true));
                }

                var seenLevels = new HashSet<int>();
                for (int i = 0; i < group.Rows.Count; i++)
                {
                    SkillRow row = group.Rows[i];
                    if (row.Level < MinLevel || row.Level > MaxLevel)
                    {
                        issues.Add(new SkillIssue(
                            row.SkillId,
                            row.Level,
                            "structure",
                            "level = " + row.Level + " 越界（允许 " + MinLevel + ".." + MaxLevel
                                + "）：每个 skillId 必须恰好 level 1..3 各一行",
                            isError: true));
                    }

                    if (!seenLevels.Add(row.Level))
                    {
                        issues.Add(new SkillIssue(
                            row.SkillId,
                            row.Level,
                            "structure",
                            "复合主键 (skillId, level) 重复：技能 " + row.SkillId + " 的 level " + row.Level
                                + " 出现了 " + CountLevel(group.Rows, row.Level)
                                + " 次（同技能多等级是正常的，同一等级重复才会让后一行覆盖前一行）",
                            isError: true));
                    }
                }

                if (group.Rows.Count == LevelsPerSkill && HasAllLevels(seenLevels))
                {
                    continue;
                }

                issues.Add(new SkillIssue(
                    group.SkillId,
                    0,
                    "structure",
                    "等级缺档：技能 " + group.SkillId + " 需要恰好 level " + MinLevel + ".." + MaxLevel
                        + " 各一行，实际 " + group.Rows.Count + " 行 [" + DescribeLevels(group.Rows) + "]"
                        + (DescribeMissingLevels(seenLevels).Length == 0
                            ? string.Empty
                            : "，缺 " + DescribeMissingLevels(seenLevels)),
                    isError: true));
            }
        }

        /// <summary>规则 2：单行数值区间。</summary>
        private static void CheckRanges(IReadOnlyList<SkillRow> rows, List<SkillIssue> issues)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                SkillRow row = rows[i];

                if (row.CooldownMs < MinCooldownMs)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "range",
                        "cooldownMs = " + row.CooldownMs + " 小于 " + MinCooldownMs
                            + "：低于这个值等于没有冷却，技能会退化成机关枪",
                        isError: true));
                }

                if (row.Damage < 0)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "range",
                        "damage = " + row.Damage + " 为负数：伤害/治疗量不能倒扣（位移与控制用 0）",
                        isError: true));
                }

                if (row.MaxTargets < MinMaxTargets || row.MaxTargets > MaxMaxTargets)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "range",
                        "maxTargets = " + row.MaxTargets + " 越界（允许 " + MinMaxTargets + ".." + MaxMaxTargets
                            + "）：0 个目标等于技能无效，超过 " + MaxMaxTargets + " 个目标会让范围伤害失控",
                        isError: true));
                }

                if (row.CastRangePermille < MinCastRangePermille || row.CastRangePermille > MaxCastRangePermille)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "range",
                        "castRangePermille = " + row.CastRangePermille + " 越界（允许 "
                            + MinCastRangePermille + ".." + MaxCastRangePermille + "）：千分比距离超出战斗场地的可表达范围",
                        isError: true));
                }

                if (row.EffectType < EffectDamage || row.EffectType > EffectControl)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "range",
                        "effectType = " + row.EffectType + " 未定义（允许 " + EffectDamage + " 伤害 / " + EffectHeal
                            + " 治疗 / " + EffectShield + " 护盾 / " + EffectDisplace + " 位移 / " + EffectControl
                            + " 控制）",
                        isError: true));
                }

                if (row.TagId != 0 && !IsKnownTag(row.TagId))
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "tag",
                        "tagId = " + row.TagId + " 不是已定义的 GameplayTag（允许 0 = 不挂标签，或 "
                            + DescribeKnownTags() + "）：拼错的标签在仲裁时永远不命中",
                        isError: true));
                }
            }
        }

        /// <summary>
        /// 规则 3：跨等级成长——伤害/治疗量随 level 严格递增，等级间冷却不得缩短。
        /// <para>
        /// 只对等级完整（1..3 各一行）的技能判定：缺档技能已经在规则 1 判过，再叠加成长噪声只会淹没真问题。
        /// </para>
        /// </summary>
        private static void CheckGrowth(List<SkillGroup> groups, List<SkillIssue> issues)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                SkillGroup group = groups[g];
                if (!group.IsContinuous())
                {
                    continue;
                }

                for (int i = 1; i < group.Rows.Count; i++)
                {
                    SkillRow previous = group.Rows[i - 1];
                    SkillRow current = group.Rows[i];

                    if (current.CooldownMs < previous.CooldownMs)
                    {
                        issues.Add(new SkillIssue(
                            current.SkillId,
                            current.Level,
                            "growth",
                            "cooldownMs 从 L" + previous.Level + " 的 " + previous.CooldownMs + " 降到 L"
                                + current.Level + " 的 " + current.CooldownMs
                                + "：等级间冷却不得缩短（升级后更频繁，强度曲线会翻车）",
                            isError: true));
                    }

                    bool bothGrowthTypes =
                        IsGrowthType(previous.EffectType) && IsGrowthType(current.EffectType);
                    if (!bothGrowthTypes)
                    {
                        continue;
                    }

                    if (current.Damage <= previous.Damage)
                    {
                        issues.Add(new SkillIssue(
                            current.SkillId,
                            current.Level,
                            "growth",
                            "damage = " + current.Damage + " 未严格高于 L" + previous.Level + " 的 "
                                + previous.Damage + "：" + EffectTypeName(current.EffectType)
                                + "类技能的伤害/治疗量必须随等级严格递增（升级不能变弱）",
                            isError: true));
                    }
                }
            }
        }

        /// <summary>规则 4：效果类型与数值必须自洽（位移不带伤害、控制不高频）。</summary>
        private static void CheckEffectConsistency(IReadOnlyList<SkillRow> rows, List<SkillIssue> issues)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                SkillRow row = rows[i];

                if (row.EffectType == EffectDisplace && row.Damage != 0)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "effect",
                        "effectType=3（位移）时 damage 必须为 0，实际 " + row.Damage
                            + "：位移带伤害会绕过伤害管线，成长校验也看不到它",
                        isError: true));
                }

                if (row.EffectType == EffectControl && row.CooldownMs < MinControlCooldownMs)
                {
                    issues.Add(new SkillIssue(
                        row.SkillId,
                        row.Level,
                        "effect",
                        "effectType=4（控制）时 cooldownMs 必须 ≥ " + MinControlCooldownMs + "，实际 "
                            + row.CooldownMs + "：控制太频繁会让对手全程无法行动",
                        isError: true));
                }
            }
        }

        /// <summary>
        /// 规则 5a：同一个 tagId 挂的技能数不得超过 <see cref="MaxSkillsPerTag"/>。
        /// <para>按<b>技能数</b>而不是行数统计：同一技能的 3 个等级共用标签是天经地义的。</para>
        /// </summary>
        private static void CheckTagSharing(List<SkillGroup> groups, List<SkillIssue> issues)
        {
            var order = new List<int>();
            var owners = new Dictionary<int, HashSet<int>>();
            for (int g = 0; g < groups.Count; g++)
            {
                SkillGroup group = groups[g];
                var tagsOfSkill = new HashSet<int>();
                for (int i = 0; i < group.Rows.Count; i++)
                {
                    int tagId = group.Rows[i].TagId;
                    if (tagId <= 0 || !tagsOfSkill.Add(tagId))
                    {
                        continue;
                    }

                    HashSet<int> skills;
                    if (owners.ContainsKey(tagId))
                    {
                        skills = owners[tagId];
                    }
                    else
                    {
                        skills = new HashSet<int>();
                        owners.Add(tagId, skills);
                        order.Add(tagId);
                    }

                    skills.Add(group.SkillId);
                }
            }

            for (int i = 0; i < order.Count; i++)
            {
                int tagId = order[i];
                HashSet<int> skills = owners[tagId];
                if (skills.Count <= MaxSkillsPerTag)
                {
                    continue;
                }

                string tagName = TagName(tagId);
                issues.Add(new SkillIssue(
                    0,
                    0,
                    "tag",
                    "tagId " + tagId + (tagName.Length == 0 ? string.Empty : "（" + tagName + "）") + " 被 "
                        + skills.Count + " 个技能共用 " + DescribeSkillIds(skills) + "（上限 " + MaxSkillsPerTag
                        + "）：同 Tag 效果叠太多，仲裁会互相打架",
                    isError: true));
            }
        }

        /// <summary>
        /// 规则 5b：全局冷却的均值相对中位数偏差超过 <see cref="MeanMedianDeviationTolerance"/> 倍 = 警告。
        /// <para>
        /// <b>为什么用“均值 vs 中位数”而不是直接卡上下限</b>：单个技能冷却很长是合理设计（大招），
        /// 只有当<b>少数极端值把均值整体拉走</b>时，才说明这张表的冷却分布失衡（多数技能舍不得按、
        /// 或某个技能冷却长得没有性价比）。中位数对这个信号不敏感，正好当基准。
        /// </para>
        /// </summary>
        private static void CheckCooldownOutlier(IReadOnlyList<SkillRow> rows, List<SkillIssue> issues)
        {
            double median = MedianCooldownMs(rows);
            if (median <= 0d)
            {
                return;
            }

            double mean = MeanCooldownMs(rows);
            double deviation = Math.Abs(mean - median) / median;
            if (deviation <= MeanMedianDeviationTolerance)
            {
                return;
            }

            issues.Add(new SkillIssue(
                0,
                0,
                "outlier",
                "全局 cooldownMs 均值 " + FormatNumber(mean) + "ms 与中位数 " + FormatNumber(median)
                    + "ms 偏差 " + FormatNumber(deviation) + " 倍（上限 "
                    + FormatNumber(MeanMedianDeviationTolerance) + " 倍）：存在极端冷却值，多数技能的节奏会被它带偏",
                isError: false));
        }

        private static bool IsGrowthType(int effectType)
        {
            return effectType == EffectDamage || effectType == EffectHeal;
        }

        /// <summary>seen 是否恰好覆盖 level 1..3。</summary>
        private static bool HasAllLevels(HashSet<int> seen)
        {
            for (int level = MinLevel; level <= MaxLevel; level++)
            {
                if (!seen.Contains(level))
                {
                    return false;
                }
            }

            return true;
        }

        private static int CountLevel(List<SkillRow> rows, int level)
        {
            int count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Level == level)
                {
                    count++;
                }
            }

            return count;
        }

        private static string DescribeLevels(List<SkillRow> rows)
        {
            var parts = new List<string>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                parts.Add(rows[i].Level.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(", ", parts);
        }

        private static string DescribeMissingLevels(HashSet<int> seen)
        {
            var missing = new List<string>();
            for (int level = MinLevel; level <= MaxLevel; level++)
            {
                if (!seen.Contains(level))
                {
                    missing.Add(level.ToString(CultureInfo.InvariantCulture));
                }
            }

            return string.Join(", ", missing);
        }

        private static string DescribeKnownTags()
        {
            var parts = new List<string>(KnownTagIds.Length);
            for (int i = 0; i < KnownTagIds.Length; i++)
            {
                string name = TagName(KnownTagIds[i]);
                parts.Add(name.Length == 0 ? KnownTagIds[i].ToString(CultureInfo.InvariantCulture) : name);
            }

            return string.Join(" / ", parts);
        }

        private static string DescribeSkillIds(HashSet<int> skills)
        {
            var ids = new List<int>(skills);
            ids.Sort();
            var parts = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                parts.Add(ids[i].ToString(CultureInfo.InvariantCulture));
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>一个 skillId 下的全部行（组内按 level 升序）。</summary>
        private sealed class SkillGroup
        {
            internal SkillGroup(int skillId)
            {
                SkillId = skillId;
                Rows = new List<SkillRow>(LevelsPerSkill);
            }

            internal int SkillId { get; }

            internal List<SkillRow> Rows { get; }

            /// <summary>是否恰好 level 1..3 各一行。</summary>
            internal bool IsContinuous()
            {
                if (Rows.Count != LevelsPerSkill)
                {
                    return false;
                }

                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].Level != MinLevel + i)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    /// <summary>
    /// 从 JSON 表数据读技能行（列名固定，与 <c>tables/TbSkill.json</c> 一致）。
    /// <para>
    /// <b>空表为什么按解析失败处理而不是“0 条通过”</b>：技能表是战斗侧的驱动数据，空表说明导出流程断了
    /// （或者表名被改），此时“无问题”的结论会让门禁放行一张完全没生效的表。缺列/类型不对同理——
    /// 读取阶段直接失败，退出码 3 明确指向“表坏了”，而不是让规则层对一堆 0 值产生一堆误报。
    /// </para>
    /// </summary>
    public static class SkillTableReader
    {
        /// <summary>读取 <c>TbSkill</c> 的全部行（保持表内行序）。</summary>
        /// <param name="table">已解析的表数据。</param>
        /// <returns>技能行列表。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> 为 <c>null</c>。</exception>
        /// <exception cref="TableGenException">空表、缺列或某列类型不对。</exception>
        public static List<SkillRow> ReadSkills(TableData table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (table.Rows.Count == 0)
            {
                throw new TableGenException(
                    "表 " + table.Schema.TableName + " 一行数据都没有：空技能表等于没有配置（空表按解析失败处理）。");
            }

            int skillIdColumn = RequireColumn(table, "skillId");
            int nameColumn = RequireColumn(table, "name");
            int cooldownColumn = RequireColumn(table, "cooldownMs");
            int castRangeColumn = RequireColumn(table, "castRangePermille");
            int damageColumn = RequireColumn(table, "damage");
            int effectTypeColumn = RequireColumn(table, "effectType");
            int tagIdColumn = RequireColumn(table, "tagId");
            int maxTargetsColumn = RequireColumn(table, "maxTargets");
            int levelColumn = RequireColumn(table, "level");

            var rows = new List<SkillRow>(table.Rows.Count);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                rows.Add(new SkillRow(
                    ReadInt32(table, i, skillIdColumn),
                    ReadString(table, i, nameColumn),
                    ReadInt32(table, i, cooldownColumn),
                    ReadInt32(table, i, castRangeColumn),
                    ReadInt32(table, i, damageColumn),
                    ReadInt32(table, i, effectTypeColumn),
                    ReadInt32(table, i, tagIdColumn),
                    ReadInt32(table, i, maxTargetsColumn),
                    ReadInt32(table, i, levelColumn)));
            }

            return rows;
        }

        private static int RequireColumn(TableData table, string name)
        {
            for (int i = 0; i < table.Schema.Columns.Count; i++)
            {
                if (string.Equals(table.Schema.Columns[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new TableGenException(
                "表 " + table.Schema.TableName + " 缺少 skillcheck 需要的列 '" + name
                + "'（列名被改了？列序以 tables/TbSkill.json 为准）。");
        }

        private static int ReadInt32(TableData table, int rowIndex, int columnIndex)
        {
            JsonElement element = table.Rows[rowIndex][columnIndex];
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            {
                throw new TableGenException(
                    "表 " + table.Schema.TableName + " 第 " + rowIndex + " 行 ["
                    + table.Schema.Columns[columnIndex].Name + "] 期望 int32，实际 "
                    + (element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.ValueKind.ToString())
                    + "。");
            }

            return value;
        }

        private static string ReadString(TableData table, int rowIndex, int columnIndex)
        {
            JsonElement element = table.Rows[rowIndex][columnIndex];
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new TableGenException(
                    "表 " + table.Schema.TableName + " 第 " + rowIndex + " 行 ["
                    + table.Schema.Columns[columnIndex].Name + "] 期望 string，实际 "
                    + (element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.ValueKind.ToString())
                    + "。");
            }

            return element.GetString() ?? string.Empty;
        }
    }
}

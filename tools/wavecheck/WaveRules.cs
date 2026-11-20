#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIFramework.TableGen;

namespace AIFramework.WaveCheck
{
    /// <summary>一条波次节奏问题：定位到波次 + 规则 id + 面向人的说明。</summary>
    public readonly struct WaveIssue
    {
        /// <summary>构造一条问题。</summary>
        /// <param name="waveIndex">波次（1 起；0 表示表级问题，如总波数越界）。</param>
        /// <param name="rule">规则 id（structure / range / difficulty / pacing / reward / boss / count）。</param>
        /// <param name="message">说明 + 修复方向。</param>
        /// <param name="isError">true = 必须改（数据错）；false = 提示（设计味道）。</param>
        public WaveIssue(int waveIndex, string rule, string message, bool isError)
        {
            WaveIndex = waveIndex;
            Rule = rule ?? string.Empty;
            Message = message ?? string.Empty;
            IsError = isError;
        }

        /// <summary>波次（1 起）；0 表示整表级问题。</summary>
        public int WaveIndex { get; }

        /// <summary>规则 id，便于日志检索与测试断言。</summary>
        public string Rule { get; }

        /// <summary>问题说明。</summary>
        public string Message { get; }

        /// <summary>true = 错误（退出码 1）；false = 警告（只打印）。</summary>
        public bool IsError { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            string where = WaveIndex > 0 ? "第 " + WaveIndex + " 波" : "全表";
            return (IsError ? "✗ " : "⚠ ") + "[" + Rule + "] " + where + "：" + Message;
        }
    }

    /// <summary><c>TbArenaWave</c> 的一行：纯数据，列序与 <c>tables/TbArenaWave.json</c> 一致。</summary>
    public readonly struct WaveRow
    {
        /// <summary>构造一行。</summary>
        /// <param name="waveIndex">波次序号（1 起，必须连续）。</param>
        /// <param name="enemyCount">本波敌人数量。</param>
        /// <param name="enemyHp">单个敌人血量。</param>
        /// <param name="enemySpeedPermille">敌人速度（千分比，1000 = 基准速度）。</param>
        /// <param name="spawnIntervalMs">出怪间隔（毫秒）。</param>
        /// <param name="rewardGold">通关奖励金币。</param>
        /// <param name="bossFlag">Boss 标记（0 / 1，仅最后一波为 1）。</param>
        public WaveRow(
            int waveIndex,
            int enemyCount,
            int enemyHp,
            int enemySpeedPermille,
            int spawnIntervalMs,
            int rewardGold,
            int bossFlag)
        {
            WaveIndex = waveIndex;
            EnemyCount = enemyCount;
            EnemyHp = enemyHp;
            EnemySpeedPermille = enemySpeedPermille;
            SpawnIntervalMs = spawnIntervalMs;
            RewardGold = rewardGold;
            BossFlag = bossFlag;
        }

        /// <summary>波次序号（1 起）。</summary>
        public int WaveIndex { get; }

        /// <summary>本波敌人数量。</summary>
        public int EnemyCount { get; }

        /// <summary>单个敌人血量。</summary>
        public int EnemyHp { get; }

        /// <summary>敌人速度（千分比，1000 = 基准速度）。</summary>
        public int EnemySpeedPermille { get; }

        /// <summary>出怪间隔（毫秒）。</summary>
        public int SpawnIntervalMs { get; }

        /// <summary>通关奖励金币。</summary>
        public int RewardGold { get; }

        /// <summary>Boss 标记（0 / 1）。</summary>
        public int BossFlag { get; }

        /// <summary>本行是否 Boss 波。</summary>
        public bool IsBoss
        {
            get { return BossFlag == 1; }
        }
    }

    /// <summary>
    /// 波次时间轴体检：把 <c>TbArenaWave</c> 当一条<b>曲线</b>看，而不是逐行看。
    /// <para>
    /// <b>为什么需要单独一个工具</b>：<c>tablecheck</c> 逐表逐行查"单行是否合法"（数量 &gt; 0、间隔在区间内），
    /// 但它看不见"第 4 波比第 3 波更简单"这种只有把行连起来才成立的问题。
    /// 波次表最常见的三种事故恰好都是跨行的：难度回落（数值填反）、节奏突变（少打一个 0）、
    /// 奖励与强度脱钩（只调了一边）。它们逐行都合法，进游戏却表现为"越打越轻松"或"打完这波没奖励"。
    /// </para>
    /// <para>
    /// <b>规则的严重级别怎么定</b>：能确定是"填错了"的（波次断号、难度回落、Boss 不在最后）判错误——
    /// 这些没有合理的玩法解释；只能确定是"味道不对"的（节奏突变、奖励比值波动、Boss 带太多小怪）判警告——
    /// 它们有正当设计场景（例如故意做一次喘息波），不该挡住提交。
    /// </para>
    /// </summary>
    public static class WaveRules
    {
        /// <summary>总波数下限：少于 5 波撑不起难度递进（3 波就是"热身/正戏/收尾"，没有中段）。</summary>
        public const int MinWaves = 5;

        /// <summary>总波数上限：超过 20 波单场竞技场打不完，玩家也看不到头。</summary>
        public const int MaxWaves = 20;

        /// <summary>Boss 波允许的最大小怪数：再多玩家就分不清"该打谁"。</summary>
        public const int MaxBossEscortCount = 5;

        /// <summary>奖励/难度比的允许波动（50%）：超过说明奖励和强度各调各的。</summary>
        public const double RewardRatioTolerance = 0.5;

        /// <summary>清场时长允许的最大增长倍数：超过就是"长时间卡顿"。</summary>
        public const long MaxClearTimeGrowth = 3;

        /// <summary><c>enemyCount</c> 下限（列定义）。</summary>
        public const int MinEnemyCount = 1;

        /// <summary><c>enemyCount</c> 上限。</summary>
        public const int MaxEnemyCount = 30;

        /// <summary><c>enemyHp</c> 下限。</summary>
        public const int MinEnemyHp = 1;

        /// <summary><c>enemyHp</c> 上限。</summary>
        public const int MaxEnemyHp = 10000;

        /// <summary><c>enemySpeedPermille</c> 下限。</summary>
        public const int MinEnemySpeedPermille = 0;

        /// <summary><c>enemySpeedPermille</c> 上限。</summary>
        public const int MaxEnemySpeedPermille = 3000;

        /// <summary><c>spawnIntervalMs</c> 下限。</summary>
        public const int MinSpawnIntervalMs = 50;

        /// <summary><c>spawnIntervalMs</c> 上限。</summary>
        public const int MaxSpawnIntervalMs = 10000;

        /// <summary>
        /// 难度值：<c>enemyCount * enemyHp * (1000 + speedPermille) / 1000</c>。
        /// <para>
        /// <b>为什么是乘法而不是加权和</b>：这三维对"清掉这一波要多久"是乘性关系——
        /// 数量翻倍与血量翻倍的代价相同，速度只是把"能打几下"按千分比缩放。
        /// 用 long 是因为上限组合（30 × 10000 × 4000 / 1000）已经顶到 int32 的十分之一，
        /// 留出余量后加权重也不会溢出。
        /// </para>
        /// </summary>
        /// <param name="row">一行波次。</param>
        /// <returns>本波难度值（只用于比较，不是绝对量纲）。</returns>
        public static long Difficulty(in WaveRow row)
        {
            return (long)row.EnemyCount * row.EnemyHp * (1000L + row.EnemySpeedPermille) / 1000L;
        }

        /// <summary>
        /// 本波最短清场时间（毫秒）：<c>enemyCount * spawnIntervalMs</c>。
        /// <para>这是"把这一波敌人全部放出来"的下界，不含玩家清怪耗时，因此只能用于相邻波对比。</para>
        /// </summary>
        /// <param name="row">一行波次。</param>
        /// <returns>最短清场时间（毫秒）。</returns>
        public static long ClearTimeMs(in WaveRow row)
        {
            return (long)row.EnemyCount * row.SpawnIntervalMs;
        }

        /// <summary>
        /// 奖励/难度比：衡量"这一波的奖励配不配得上它的强度"。
        /// <para>难度为 0 时返回 0（无法定义比值），调用方需按"跳过"处理。</para>
        /// </summary>
        /// <param name="row">一行波次。</param>
        /// <returns>比值；难度非正时为 0。</returns>
        public static double RewardPerDifficulty(in WaveRow row)
        {
            long difficulty = Difficulty(in row);
            return difficulty > 0 ? row.RewardGold / (double)difficulty : 0d;
        }

        /// <summary>跑全部规则，把问题追加到 <paramref name="issues"/>（顺序稳定，便于测试）。</summary>
        /// <param name="rows">按表内行序排列的波次。</param>
        /// <param name="issues">输出列表。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
        public static void Validate(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
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
                issues.Add(new WaveIssue(
                    0,
                    "count",
                    "表里一行波次都没有：竞技场只能退回内置波次，节奏表等于没配",
                    isError: true));
                return;
            }

            CheckWaveCount(rows, issues);
            CheckContinuity(rows, issues);
            CheckBoss(rows, issues);
            CheckRanges(rows, issues);
            CheckDifficultyCurve(rows, issues);
            CheckPacing(rows, issues);
            CheckRewardCoupling(rows, issues);
        }

        /// <summary>规则 6：总波数必须落在 <see cref="MinWaves"/>..<see cref="MaxWaves"/>。</summary>
        private static void CheckWaveCount(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            if (rows.Count < MinWaves || rows.Count > MaxWaves)
            {
                issues.Add(new WaveIssue(
                    0,
                    "count",
                    "总波数 " + rows.Count + " 不在 " + MinWaves + ".." + MaxWaves
                        + "：太少撑不起难度递进，太多玩家看不到头",
                    isError: false));
            }
        }

        /// <summary>规则 1：waveIndex 主键唯一，且从 1 开始连续递增（行序即播放序）。</summary>
        private static void CheckContinuity(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < rows.Count; i++)
            {
                WaveRow row = rows[i];
                if (!seen.Add(row.WaveIndex))
                {
                    issues.Add(new WaveIssue(
                        row.WaveIndex,
                        "structure",
                        "waveIndex 重复：" + row.WaveIndex + "（主键必须唯一，后一行会覆盖前一行）",
                        isError: true));
                }

                int expected = i + 1;
                if (row.WaveIndex != expected)
                {
                    issues.Add(new WaveIssue(
                        row.WaveIndex,
                        "structure",
                        "第 " + expected + " 行（0 起 " + i + "）的 waveIndex 应为 " + expected
                            + "，实际 " + row.WaveIndex + "：必须从 1 开始连续递增，否则加载侧按行号取波次会错位",
                        isError: true));
                }
            }
        }

        /// <summary>规则 5：Boss 行唯一且在最后一行；Boss 波小怪数建议 ≤ <see cref="MaxBossEscortCount"/>。</summary>
        private static void CheckBoss(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            int bossCount = 0;
            int lastBossIndex = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].IsBoss)
                {
                    continue;
                }

                bossCount++;
                lastBossIndex = i;
            }

            if (bossCount == 0)
            {
                return;
            }

            if (bossCount > 1)
            {
                issues.Add(new WaveIssue(
                    0,
                    "boss",
                    "Boss 行不唯一：共 " + bossCount + " 行标了 bossFlag=1（只有最终波才是 Boss）",
                    isError: true));
                return;
            }

            WaveRow boss = rows[lastBossIndex];
            if (lastBossIndex != rows.Count - 1)
            {
                WaveRow tail = rows[rows.Count - 1];
                issues.Add(new WaveIssue(
                    boss.WaveIndex,
                    "boss",
                    "Boss 行不是最后一行（最后一波是 waveIndex=" + tail.WaveIndex
                        + "）：Boss 之后还有波次，玩家会觉得打完 Boss 又被杂兵拖住",
                    isError: true));
            }

            if (boss.EnemyCount > MaxBossEscortCount)
            {
                issues.Add(new WaveIssue(
                    boss.WaveIndex,
                    "boss",
                    "Boss 波带 " + boss.EnemyCount + " 个小怪（建议 ≤ " + MaxBossEscortCount
                        + "）：Boss 与杂兵同屏过多会让玩家分不清该打谁",
                    isError: false));
            }
        }

        /// <summary>列定义区间：越界一律错误（这些值会被加载侧直接拿去布场，越界即不可表达）。</summary>
        private static void CheckRanges(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                WaveRow row = rows[i];
                AddRangeIssue(issues, row.WaveIndex, "enemyCount", row.EnemyCount, MinEnemyCount, MaxEnemyCount);
                AddRangeIssue(issues, row.WaveIndex, "enemyHp", row.EnemyHp, MinEnemyHp, MaxEnemyHp);
                AddRangeIssue(
                    issues,
                    row.WaveIndex,
                    "enemySpeedPermille",
                    row.EnemySpeedPermille,
                    MinEnemySpeedPermille,
                    MaxEnemySpeedPermille);
                AddRangeIssue(
                    issues,
                    row.WaveIndex,
                    "spawnIntervalMs",
                    row.SpawnIntervalMs,
                    MinSpawnIntervalMs,
                    MaxSpawnIntervalMs);

                if (row.RewardGold < 0)
                {
                    issues.Add(new WaveIssue(
                        row.WaveIndex,
                        "range",
                        "rewardGold 为负数：" + row.RewardGold + "（奖励不能倒扣）",
                        isError: true));
                }

                if (row.BossFlag != 0 && row.BossFlag != 1)
                {
                    issues.Add(new WaveIssue(
                        row.WaveIndex,
                        "range",
                        "bossFlag 只能是 0 或 1，实际 " + row.BossFlag,
                        isError: true));
                }
            }
        }

        private static void AddRangeIssue(
            List<WaveIssue> issues,
            int waveIndex,
            string column,
            int value,
            int min,
            int max)
        {
            if (value < min || value > max)
            {
                issues.Add(new WaveIssue(
                    waveIndex,
                    "range",
                    column + " = " + value + " 越界（允许 " + min + ".." + max + "）",
                    isError: true));
            }
        }

        /// <summary>
        /// 规则 2：难度必须逐波非降（允许相等）。
        /// <para>
        /// <b>为什么回落一定是错而不是警告</b>：玩法上可以有"喘息波"，但喘息波靠的是<b>数量/血量</b>回落，
        /// 而不是"综合难度比上一波更低"。综合难度下降意味着后期敌人比前期更软，
        /// 要么是 hp 少写一位，要么是 speed 填反了——两种都没有合理的玩法解释。
        /// </para>
        /// </summary>
        private static void CheckDifficultyCurve(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                WaveRow previous = rows[i - 1];
                WaveRow current = rows[i];
                long previousDifficulty = Difficulty(in previous);
                long currentDifficulty = Difficulty(in current);
                if (currentDifficulty < previousDifficulty)
                {
                    issues.Add(new WaveIssue(
                        current.WaveIndex,
                        "difficulty",
                        "难度 " + currentDifficulty + " 低于上一波（waveIndex=" + previous.WaveIndex
                            + " 的 " + previousDifficulty + "）：enemyCount × enemyHp × 速度 的乘积不该回落，多半是数值误填",
                        isError: true));
                }
            }
        }

        /// <summary>
        /// 规则 3：相邻波的"最短清场时间"节奏。
        /// <para>比上一波更短 = 突然变快（警告）；超过上一波 3 倍 = 长时间卡顿（警告）。</para>
        /// </summary>
        private static void CheckPacing(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                WaveRow previous = rows[i - 1];
                WaveRow current = rows[i];
                long previousClear = ClearTimeMs(in previous);
                long currentClear = ClearTimeMs(in current);

                if (currentClear < previousClear)
                {
                    issues.Add(new WaveIssue(
                        current.WaveIndex,
                        "pacing",
                        "最短清场时间 " + currentClear + "ms 比上一波（" + previousClear
                            + "ms）更短：节奏突然变快，玩家会觉得这一波还没开始就结束了",
                        isError: false));
                    continue;
                }

                if (previousClear > 0 && currentClear > previousClear * MaxClearTimeGrowth)
                {
                    issues.Add(new WaveIssue(
                        current.WaveIndex,
                        "pacing",
                        "最短清场时间 " + currentClear + "ms 是上一波（" + previousClear
                            + "ms）的 " + (currentClear / (double)previousClear)
                                .ToString("0.0", CultureInfo.InvariantCulture)
                            + " 倍：超过 " + MaxClearTimeGrowth + " 倍会形成长时间卡顿",
                        isError: false));
                }
            }
        }

        /// <summary>
        /// 规则 4：奖励与难度同步——奖励/难度比的相邻波动不得超过 <see cref="RewardRatioTolerance"/>。
        /// <para>
        /// <b>为什么比"奖励增长率 ≤ 难度增长率"更严格</b>：只比值增长趋势会漏掉"两边一起暴涨但比例失衡"，
        /// 而比值的相邻波动能同时抓住"奖励涨太快"和"奖励没跟上"两种情况。
        /// 用相对波动而不是绝对差，是因为难度从 50 涨到 100 时，比值 0.5 与 0.6 的绝对差只有 0.1，
        /// 但实际已经是 20% 的奖励偏移。
        /// </para>
        /// </summary>
        private static void CheckRewardCoupling(IReadOnlyList<WaveRow> rows, List<WaveIssue> issues)
        {
            bool hasPrevious = false;
            double previousRatio = 0d;
            for (int i = 0; i < rows.Count; i++)
            {
                WaveRow current = rows[i];
                if (Difficulty(in current) <= 0)
                {
                    // 难度为 0 时比值无定义（该行本身已被区间规则判错），不在这里制造二次噪声。
                    continue;
                }

                double ratio = RewardPerDifficulty(in current);
                if (hasPrevious)
                {
                    bool drifted;
                    double percent;
                    if (previousRatio <= 0d)
                    {
                        drifted = ratio > 0d;
                        percent = 100d;
                    }
                    else
                    {
                        percent = Math.Abs(ratio - previousRatio) / previousRatio * 100d;
                        drifted = percent > RewardRatioTolerance * 100d;
                    }

                    if (drifted)
                    {
                        issues.Add(new WaveIssue(
                            current.WaveIndex,
                            "reward",
                            "奖励/难度比 " + ratio.ToString("0.000", CultureInfo.InvariantCulture)
                                + " 相对上一波（" + previousRatio.ToString("0.000", CultureInfo.InvariantCulture)
                                + "）波动 " + percent.ToString("0.0", CultureInfo.InvariantCulture)
                                + "%（上限 50%）：奖励与强度脱钩，只调了一边",
                            isError: false));
                    }
                }

                previousRatio = ratio;
                hasPrevious = true;
            }
        }
    }

    /// <summary>
    /// 从 JSON 表数据读波次行（列名固定，与 <c>tables/TbArenaWave.json</c> 一致）。
    /// <para>
    /// <b>为什么值类型不对要抛 <see cref="TableGenException"/> 而不是读成 0</b>：
    /// 0 是合法值（speed=0、rewardGold=0），静默兜底会把"列名拼错/类型写错"伪装成一张合法但全 0 的表，
    /// 后续所有难度与节奏告警都会失真。读取阶段直接失败，退出码 3 会明确指向"表坏了"。
    /// </para>
    /// </summary>
    public static class WaveTableReader
    {
        /// <summary>读取 <c>TbArenaWave</c> 的全部行（保持表内行序）。</summary>
        /// <param name="table">已解析的表数据。</param>
        /// <returns>波次行列表。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> 为 <c>null</c>。</exception>
        /// <exception cref="TableGenException">缺列或某列不是 int32。</exception>
        public static List<WaveRow> ReadWaves(TableData table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            int waveIndexColumn = RequireColumn(table, "waveIndex");
            int enemyCountColumn = RequireColumn(table, "enemyCount");
            int enemyHpColumn = RequireColumn(table, "enemyHp");
            int enemySpeedColumn = RequireColumn(table, "enemySpeedPermille");
            int spawnIntervalColumn = RequireColumn(table, "spawnIntervalMs");
            int rewardGoldColumn = RequireColumn(table, "rewardGold");
            int bossFlagColumn = RequireColumn(table, "bossFlag");

            var rows = new List<WaveRow>(table.Rows.Count);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                rows.Add(new WaveRow(
                    ReadInt32(table, i, waveIndexColumn),
                    ReadInt32(table, i, enemyCountColumn),
                    ReadInt32(table, i, enemyHpColumn),
                    ReadInt32(table, i, enemySpeedColumn),
                    ReadInt32(table, i, spawnIntervalColumn),
                    ReadInt32(table, i, rewardGoldColumn),
                    ReadInt32(table, i, bossFlagColumn)));
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
                "表 " + table.Schema.TableName + " 缺少 wavecheck 需要的列 '" + name
                + "'（列名被改了？列序以 tables/TbArenaWave.json 为准）。");
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
    }
}

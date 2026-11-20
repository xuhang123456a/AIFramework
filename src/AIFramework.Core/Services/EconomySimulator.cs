using System;
using System.Collections.Generic;

namespace AIFramework.Core.Services
{
    /// <summary>经济模拟的一天输入：当天获得的货币/道具与消耗。</summary>
    public readonly struct EconomyDay
    {
        /// <summary>构造一天的收支。</summary>
        /// <param name="earnedCurrency">当天产出货币（掉落/任务/活动）。</param>
        /// <param name="spentCurrency">当天消耗货币（商店/升级/抽卡）。</param>
        /// <param name="earnedItems">当天产出道具总量。</param>
        /// <param name="spentItems">当天消耗道具总量。</param>
        public EconomyDay(long earnedCurrency, long spentCurrency, long earnedItems, long spentItems)
        {
            EarnedCurrency = earnedCurrency < 0 ? 0 : earnedCurrency;
            SpentCurrency = spentCurrency < 0 ? 0 : spentCurrency;
            EarnedItems = earnedItems < 0 ? 0 : earnedItems;
            SpentItems = spentItems < 0 ? 0 : spentItems;
        }

        /// <summary>当天产出货币。</summary>
        public long EarnedCurrency { get; }

        /// <summary>当天消耗货币。</summary>
        public long SpentCurrency { get; }

        /// <summary>当天产出道具。</summary>
        public long EarnedItems { get; }

        /// <summary>当天消耗道具。</summary>
        public long SpentItems { get; }
    }

    /// <summary>模拟结论：净流入、通胀判定、告警。</summary>
    public readonly struct EconomyVerdict
    {
        /// <summary>构造结论。</summary>
        /// <param name="days">模拟天数。</param>
        /// <param name="netCurrency">货币净流入（产出-消耗）。</param>
        /// <param name="netItems">道具净流入。</param>
        /// <param name="sinkRatio">消耗占产出的比例（0..1；产出为 0 时记 0）。</param>
        /// <param name="isInflationary">是否通胀（净流入超过阈值）。</param>
        /// <param name="reasons">告警原因。</param>
        public EconomyVerdict(int days, long netCurrency, long netItems, double sinkRatio, bool isInflationary, string[] reasons)
        {
            Days = days;
            NetCurrency = netCurrency;
            NetItems = netItems;
            SinkRatio = sinkRatio;
            IsInflationary = isInflationary;
            Reasons = reasons ?? new string[0];
        }

        /// <summary>模拟天数。</summary>
        public int Days { get; }

        /// <summary>货币净流入。</summary>
        public long NetCurrency { get; }

        /// <summary>道具净流入。</summary>
        public long NetItems { get; }

        /// <summary>消耗/产出比例。</summary>
        public double SinkRatio { get; }

        /// <summary>是否判定为通胀。</summary>
        public bool IsInflationary { get; }

        /// <summary>告警原因。</summary>
        public string[] Reasons { get; }

        /// <summary>一行报告（口径固定，便于数值同学横向对比）。</summary>
        public string ToReportLine()
        {
            return "[Economy] 天=" + Days
                + " 净货币=" + NetCurrency
                + " 净道具=" + NetItems
                + " 回收率=" + SinkRatio.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)
                + " 通胀=" + (IsInflationary ? "是" : "否")
                + (Reasons.Length == 0 ? string.Empty : "（" + string.Join("；", Reasons) + "）");
        }
    }

    /// <summary>
    /// 经济模拟器：把"每日产出/消耗"跑成趋势，提前发现通胀/通缩。
    /// <para>
    /// <b>为什么要模拟而不是只看配置</b>：单看掉落表看不出通胀——玩家每天打的局数、消耗习惯、
    /// 活动叠加才是决定因素。把每日收支当输入跑 N 天，能回答"玩家手里的钱是不是越来越多"。
    /// </para>
    /// <para>
    /// <b>为什么阈值用"净流入/天"和"回收率"两个指标</b>：只看绝对净流入会被大盘规模误导（新服/老服差异大）；
    /// 回收率（消耗/产出）是无量纲的，跨版本可比，适合做回归红线。
    /// </para>
    /// <para>
    /// 本类只做算术与判定，不读表、不碰引擎、不分配大对象。
    /// </para>
    /// </summary>
    public static class EconomySimulator
    {
        /// <summary>默认每日净流入告警阈值（货币）。</summary>
        public const long DefaultDailyNetThreshold = 100000L;

        /// <summary>默认回收率下限（低于该值视为通胀风险）。</summary>
        public const double DefaultMinSinkRatio = 0.6d;

        /// <summary>模拟若干天。</summary>
        /// <param name="days">每日收支（按天顺序）。</param>
        /// <param name="dailyNetThreshold">每日货币净流入告警阈值（&lt;0 按 0）。</param>
        /// <param name="minSinkRatio">回收率下限（&lt;0 按 0，&gt;1 按 1）。</param>
        /// <returns>模拟结论。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="days"/> 为 null。</exception>
        public static EconomyVerdict Simulate(IReadOnlyList<EconomyDay> days, long dailyNetThreshold = DefaultDailyNetThreshold, double minSinkRatio = DefaultMinSinkRatio)
        {
            if (days == null)
            {
                throw new ArgumentNullException(nameof(days));
            }

            long earned = 0;
            long spent = 0;
            long earnedItems = 0;
            long spentItems = 0;

            for (int i = 0; i < days.Count; i++)
            {
                EconomyDay day = days[i];
                earned += day.EarnedCurrency;
                spent += day.SpentCurrency;
                earnedItems += day.EarnedItems;
                spentItems += day.SpentItems;
            }

            long netCurrency = earned - spent;
            long netItems = earnedItems - spentItems;
            double sinkRatio = earned <= 0 ? 0d : Math.Min(1d, (double)spent / earned);

            var reasons = new List<string>(3);
            double effectiveRatio = minSinkRatio < 0d ? 0d : (minSinkRatio > 1d ? 1d : minSinkRatio);
            long threshold = dailyNetThreshold < 0 ? 0 : dailyNetThreshold;

            if (days.Count == 0)
            {
                reasons.Add("没有可模拟的天数（输入为空）");
            }
            else if (netCurrency / days.Count > threshold)
            {
                reasons.Add("日均净流入 " + (netCurrency / days.Count) + " 超过阈值 " + threshold);
            }

            if (earned > 0 && sinkRatio < effectiveRatio)
            {
                reasons.Add("回收率 " + sinkRatio.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)
                    + " 低于下限 " + effectiveRatio.ToString("P1", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (netItems > 0 && earnedItems > 0 && (double)netItems / earnedItems > 0.5d)
            {
                reasons.Add("道具净流入超过产出的一半（资源在囤积，消耗口可能缺失）");
            }

            return new EconomyVerdict(days.Count, netCurrency, netItems, sinkRatio, reasons.Count > 0, reasons.ToArray());
        }
    }
}

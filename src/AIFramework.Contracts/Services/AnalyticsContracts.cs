using System;

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 一条埋点事件：**数值优先、字符串最后**。
    /// <para>
    /// 游戏埋点最贵的不是网络，是调用点的字符串拼接与装箱。这里把"事件"压成
    /// <c>int 事件号 + 3 个数值 + 1 个短标签</c>，热路径不产生任何字符串；
    /// 事件号到可读名的映射留在离线导出/后台侧（见 Console/Analytics 的映射表），
    /// 不在热路径上做字典查找。
    /// </para>
    /// <para>
    /// <see cref="Label"/> 允许为 null（绝大多数事件不需要）；非 null 时应是"枚举名/关卡 id"这类
    /// 有界字符串，而不是每帧变化的拼接结果。
    /// </para>
    /// </summary>
    public readonly struct AnalyticsEvent
    {
        public AnalyticsEvent(int eventId, float value0, float value1, float value2, string? label)
        {
            EventId = eventId;
            Value0 = value0;
            Value1 = value1;
            Value2 = value2;
            Label = label;
        }

        /// <summary>事件号（见 <see cref="AnalyticsEventIds"/>；0 保留为"未定义"）。</summary>
        public int EventId { get; }

        /// <summary>主数值（时长/金额/数量，按事件语义解释）。</summary>
        public float Value0 { get; }

        /// <summary>次数值。</summary>
        public float Value1 { get; }

        /// <summary>第三数值（例如坐标或等级）。</summary>
        public float Value2 { get; }

        /// <summary>可选短标签（null = 无）。</summary>
        public string? Label { get; }

        /// <summary>是否是结构上有效的事件（事件号 > 0 且数值非 NaN/Inf）。</summary>
        public bool IsValid
        {
            get
            {
                return EventId > 0
                    && !float.IsNaN(Value0) && !float.IsInfinity(Value0)
                    && !float.IsNaN(Value1) && !float.IsInfinity(Value1)
                    && !float.IsNaN(Value2) && !float.IsInfinity(Value2);
            }
        }
    }

    /// <summary>
    /// 事件号常量：集中一处，避免各切片刻苦"0x1A2B"这类魔法数。
    /// 新增事件只追加，不复用旧号（后台看板按号聚合，复用会造成历史数据串档）。
    /// </summary>
    public static class AnalyticsEventIds
    {
        /// <summary>保留：无效事件。</summary>
        public const int None = 0;

        /// <summary>应用启动（value0 = 启动耗时 ms）。</summary>
        public const int AppLaunch = 1001;

        /// <summary>进入场景（value0 = 场景 id，label = 场景名）。</summary>
        public const int SceneEnter = 1002;

        /// <summary>战斗开始（value0 = 卡带类型，value1 = 关卡 id）。</summary>
        public const int BattleStart = 2001;

        /// <summary>战斗结束（value0 = 是否胜利，value1 = 时长秒）。</summary>
        public const int BattleEnd = 2002;

        /// <summary>道具获取（value0 = itemId，value1 = 数量，value2 = 来源 tag）。</summary>
        public const int ItemGranted = 3001;

        /// <summary>购买完成（value0 = itemId，value1 = 花费，value2 = 余额）。</summary>
        public const int Purchase = 3002;

        /// <summary>抽卡（value0 = 次数，value1 = 稀有数）。</summary>
        public const int Gacha = 3003;

        /// <summary>广告完播（value0 = 广告位 id）。</summary>
        public const int AdCompleted = 4001;

        /// <summary>IAP 完成（value0 = 商品 id 哈希代理，value1 = 价格档，label = productId）。</summary>
        public const int IapPurchased = 4002;
    }

    /// <summary>
    /// 埋点端口：实现方决定是"直接发网络"还是"先落本地环形缓冲再批量发"。
    /// <para>
    /// <b>为什么是 void 且不返回结果</b>：埋点绝不能阻塞或失败重试业务逻辑——
    /// 调用方只负责"记一笔"，落盘/重试/丢弃策略归实现方。
    /// </para>
    /// </summary>
    public interface IAnalyticsService
    {
        /// <summary>实现是否可用（离线/未接线时为 false，调用方不必分支）。</summary>
        bool IsAvailable { get; }

        /// <summary>记录一条事件；非法事件由实现方静默丢弃（不抛）。</summary>
        void Track(in AnalyticsEvent analyticsEvent);

        /// <summary>立即冲刷（切场景/退出时调用；实现可空实现）。</summary>
        void Flush();

        /// <summary>已被接受的事件数（诊断用）。</summary>
        long AcceptedCount { get; }

        /// <summary>被丢弃的事件数（非法/缓冲满）。</summary>
        long DroppedCount { get; }
    }
}

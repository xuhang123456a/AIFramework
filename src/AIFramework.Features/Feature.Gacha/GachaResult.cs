using System;

namespace AIFramework.Features.Gacha
{
    /// <summary>一次抽卡产出的单项结果：道具 ID + 是否是稀有项（保底改判后的最终状态）。</summary>
    public readonly struct GachaResultItem
    {
        public GachaResultItem(int itemId, bool isRare)
        {
            ItemId = itemId;
            IsRare = isRare;
        }

        /// <summary>抽中的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>最终是否稀有：保底改判后的结果同样记为 <c>true</c>。</summary>
        public bool IsRare { get; }
    }

    /// <summary>
    /// 一次抽卡命令的完整结果（允许分配，非帧内路径）。
    /// 只有命令真正走完"扣费 + 抽卡 + 发货 + 广播"才会生成；被拒绝时不会产生。
    /// </summary>
    public sealed class GachaResult
    {
        public GachaResult(GachaResultItem[] items, int pityBefore, bool pityTriggered)
        {
            Items = items ?? Array.Empty<GachaResultItem>();
            PityBefore = pityBefore;
            PityTriggered = pityTriggered;
        }

        /// <summary>按抽卡顺序排列的结果项；单抽 1 项、十连 10 项。</summary>
        public GachaResultItem[] Items { get; }

        /// <summary>本次命令开始前的保底计数（0..9）。</summary>
        public int PityBefore { get; }

        /// <summary>本次是否由保底触发（第 10 次未出稀有则强制稀有）。十连中任意一项触发即为 <c>true</c>。</summary>
        public bool PityTriggered { get; }
    }
}

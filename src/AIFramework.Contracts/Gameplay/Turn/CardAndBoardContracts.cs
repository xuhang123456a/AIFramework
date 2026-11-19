using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 卡牌展示数据（只读）：内核把"玩家能看到的"整理成值类型，UI 不需要知道牌堆/抽牌规则。
    /// <para>
    /// 刻意不带名字与描述：文案属于配置表与本地化，不该在逻辑层拼字符串（也避免每帧分配）。
    /// </para>
    /// </summary>
    public readonly struct CardViewData
    {
        public CardViewData(int cardId, int cost, int damage, int block)
        {
            CardId = cardId;
            Cost = cost;
            Damage = damage;
            Block = block;
        }

        /// <summary>卡牌模板 ID（对应配置表行）。</summary>
        public int CardId { get; }

        /// <summary>打出消耗的能量。</summary>
        public int Cost { get; }

        /// <summary>造成的伤害（0 表示不造成伤害）。</summary>
        public int Damage { get; }

        /// <summary>提供的护甲。</summary>
        public int Block { get; }
    }

    /// <summary>卡牌战斗只读视图：UI/适配层每帧只读这里，不碰内核内部结构。</summary>
    public interface ICardBattleReadView
    {
        /// <summary>当前回合序号（从 1 开始）。</summary>
        int TurnIndex { get; }

        /// <summary>剩余能量。</summary>
        int Energy { get; }

        /// <summary>玩家血量。</summary>
        int PlayerHp { get; }

        /// <summary>敌人血量。</summary>
        int EnemyHp { get; }

        /// <summary>玩家护甲（回合结束清零）。</summary>
        int PlayerBlock { get; }

        /// <summary>手牌数量。</summary>
        int HandCount { get; }

        /// <summary>抽牌堆剩余。</summary>
        int DrawPileCount { get; }

        /// <summary>弃牌堆数量。</summary>
        int DiscardPileCount { get; }

        /// <summary>取第 index 张手牌（越界返回 false，不抛异常）。</summary>
        bool TryGetHandCard(int index, out CardViewData card);
    }

    /// <summary>卡牌战斗输入端口：适配层只能通过它提交意图，不能直接改状态。</summary>
    public interface ICardBattleInput
    {
        /// <summary>打出第 index 张手牌；能量不足/索引越界/已结束都返回 false。</summary>
        bool TryPlayCard(int handIndex);

        /// <summary>结束本回合（敌人行动、抽牌、能量重置由内核在 <c>Step</c> 里结算）。</summary>
        void EndTurn();
    }

    /// <summary>三消只读视图。</summary>
    public interface IMatch3ReadView
    {
        /// <summary>棋盘宽（列数）。</summary>
        int Width { get; }

        /// <summary>棋盘高（行数）。</summary>
        int Height { get; }

        /// <summary>剩余步数。</summary>
        int MovesLeft { get; }

        /// <summary>当前分数。</summary>
        int Score { get; }

        /// <summary>目标分数。</summary>
        int TargetScore { get; }

        /// <summary>取 (x, y) 的宝石颜色 ID；越界返回 0。</summary>
        int GetGem(int x, int y);
    }

    /// <summary>三消输入端口。</summary>
    public interface IMatch3Input
    {
        /// <summary>交换相邻两格；非相邻/越界/无效交换返回 false（无效交换不消耗步数）。</summary>
        bool TrySwap(int x1, int y1, int x2, int y2);
    }
}

#nullable enable
namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 卡牌定义（纯值类型）：一张具体牌的全部规则字段。卡组里按"多份"堆叠，
    /// 因此运行期只按 <see cref="CardId"/> 取规则，不再反查牌表。
    /// <para><see cref="Name"/> 是内核编译期常量字符串的引用，<see cref="RoguelikeCardView"/> 拼装时不产生分配。</para>
    /// </summary>
    internal readonly struct RoguelikeCard
    {
        internal RoguelikeCard(int cardId, string name, int cost, int damage, int block)
        {
            CardId = cardId;
            Name = name;
            Cost = cost;
            Damage = damage;
            Block = block;
        }

        /// <summary>卡牌模板 ID（同 ID = 同名牌）。</summary>
        internal int CardId { get; }

        /// <summary>牌名（内核常量，不是本地化键）。</summary>
        internal string Name { get; }

        /// <summary>打出消耗的能量。</summary>
        internal int Cost { get; }

        /// <summary>造成的伤害（0 表示不造成伤害）。</summary>
        internal int Damage { get; }

        /// <summary>提供的格挡（0 表示不提供格挡）。</summary>
        internal int Block { get; }
    }

    /// <summary>
    /// 卡池：初始卡组的两种牌 + 战斗奖励/商店购买共用的 6 张奖励牌池。
    /// <para>
    /// <b>为什么奖励牌池是 <c>static readonly</c></b>：它是编译期常量表的等价物（牌面永不变化），
    /// 每局重开都重建一份纯属浪费；构造期一次分配即满足"运行期零分配"。
    /// 数组内容只读访问，<see cref="Reward"/> 只按索引返回结构体副本。
    /// </para>
    /// </summary>
    internal static class RoguelikeCards
    {
        /// <summary>打击：1 费 6 伤害（初始卡组 ×3，也在奖励牌池里）。</summary>
        internal const int StrikeCardId = 1;

        /// <summary>防御：1 费 5 格挡（初始卡组 ×2，也在奖励牌池里）。</summary>
        internal const int DefendCardId = 2;

        /// <summary>重击：2 费 12 伤害。</summary>
        internal const int HeavyStrikeCardId = 3;

        /// <summary>快斩：0 费 3 伤害。</summary>
        internal const int QuickSlashCardId = 4;

        /// <summary>铁壁：2 费 10 格挡。</summary>
        internal const int IronWallCardId = 5;

        /// <summary>双刃：1 费 9 伤害。</summary>
        internal const int TwinFangCardId = 6;

        internal const string StrikeCardName = "打击";

        internal const string DefendCardName = "防御";

        internal const string HeavyStrikeCardName = "重击";

        internal const string QuickSlashCardName = "快斩";

        internal const string IronWallCardName = "铁壁";

        internal const string TwinFangCardName = "双刃";

        /// <summary>
        /// 奖励牌池：战斗胜利随 1 张、商店购买随 1 张，都从这 6 张里抽。
        /// 顺序即抽取索引映射，改动顺序会改变同种子的奖励序列。
        /// </summary>
        private static readonly RoguelikeCard[] RewardPoolArray = CreateRewardPool();

        /// <summary>初始卡组用的打击牌。</summary>
        internal static RoguelikeCard Strike
        {
            get { return new RoguelikeCard(StrikeCardId, StrikeCardName, 1, 6, 0); }
        }

        /// <summary>初始卡组用的防御牌。</summary>
        internal static RoguelikeCard Defend
        {
            get { return new RoguelikeCard(DefendCardId, DefendCardName, 1, 0, 5); }
        }

        /// <summary>奖励牌池大小（抽卡上界）。</summary>
        internal static int RewardCount
        {
            get { return RewardPoolArray.Length; }
        }

        /// <summary>
        /// 取奖励牌池第 <paramref name="index"/> 张（<c>0..RewardCount-1</c>）。
        /// 索引由 <see cref="RoguelikeRandom.NextInt"/> 产生，调用方保证在界内；越界属于内核不变量被破坏。
        /// </summary>
        internal static RoguelikeCard Reward(int index)
        {
            return RewardPoolArray[index];
        }

        private static RoguelikeCard[] CreateRewardPool()
        {
            RoguelikeCard[] pool = new RoguelikeCard[6];
            pool[0] = new RoguelikeCard(StrikeCardId, StrikeCardName, 1, 6, 0);
            pool[1] = new RoguelikeCard(DefendCardId, DefendCardName, 1, 0, 5);
            pool[2] = new RoguelikeCard(HeavyStrikeCardId, HeavyStrikeCardName, 2, 12, 0);
            pool[3] = new RoguelikeCard(QuickSlashCardId, QuickSlashCardName, 0, 3, 0);
            pool[4] = new RoguelikeCard(IronWallCardId, IronWallCardName, 2, 0, 10);
            pool[5] = new RoguelikeCard(TwinFangCardId, TwinFangCardName, 1, 9, 0);
            return pool;
        }
    }
}

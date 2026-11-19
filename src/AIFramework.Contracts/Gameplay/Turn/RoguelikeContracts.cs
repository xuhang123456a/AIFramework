#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 肉鸽地图节点种类：线性 8 层地图上的每一层只有一种节点。
    /// <para>
    /// 编码锁死为 <see cref="byte"/>：节点种类会进存档与遥测，窄类型既能压缩体积，
    /// 也避免后续新增种类时无意改掉既有数值。
    /// </para>
    /// </summary>
    public enum RoguelikeNodeKind : byte
    {
        /// <summary>普通战斗：敌人 20 HP、攻击 6、胜利 15 金币。</summary>
        Battle = 0,

        /// <summary>精英战斗：敌人 32 HP、攻击 9、胜利 25 金币。</summary>
        Elite = 1,

        /// <summary>商店：进入时若金币 &gt;= 50 则花 50 买 1 张随机卡。</summary>
        Shop = 2,

        /// <summary>营地：进入时回复 20 HP（上限 50）。</summary>
        Rest = 3,

        /// <summary>Boss 战斗：敌人 60 HP、攻击 12、胜利 60 金币；打赢即通关整轮 run。</summary>
        Boss = 4
    }

    /// <summary>
    /// 地图节点快照（只读值类型）：UI 只靠它渲染地图，不碰内核内部数组。
    /// <para>
    /// <b>为什么既有 <see cref="NodeId"/> 又有 <see cref="Depth"/></b>：本实现是"每层只有一个候选节点"的线性地图，
    /// 两者数值上天然对齐（<see cref="NodeId"/> 为 0 基槽位、<see cref="Depth"/> 为 1 基层号），
    /// 但语义不同——<see cref="NodeId"/> 是存档里的稳定标识，<see cref="Depth"/> 是给玩家看的"第几层"。
    /// 后续若扩成"每层多选一"，<see cref="Depth"/> 会出现重复，<see cref="NodeId"/> 仍必须唯一。
    /// </para>
    /// </summary>
    public readonly struct RoguelikeNodeView
    {
        public RoguelikeNodeView(int nodeId, int depth, RoguelikeNodeKind kind, bool visited)
        {
            NodeId = nodeId;
            Depth = depth;
            Kind = kind;
            Visited = visited;
        }

        /// <summary>节点稳定 ID（0 基地图槽位，0..7）。</summary>
        public int NodeId { get; }

        /// <summary>层号（1 基，1..8）；第 1 层固定战斗、第 8 层固定 Boss。</summary>
        public int Depth { get; }

        /// <summary>节点种类。</summary>
        public RoguelikeNodeKind Kind { get; }

        /// <summary>是否已进入过（进入即标记，重开一轮会清零）。</summary>
        public bool Visited { get; }
    }

    /// <summary>
    /// 卡牌快照（只读值类型）：手牌列表只经 <see cref="IRoguelikeReadView.GetHandCard(int)"/> 暴露。
    /// <para>
    /// <b>为什么带 <see cref="Name"/></b>：肉鸽卡组的牌数不固定，UI 需要按牌名分组展示；
    /// 名字是内核侧的编译期常量字符串（不拼串、不分配），不是从配置表拉的本地化文本。
    /// </para>
    /// <para>
    /// <b><see cref="InDeck"/> 的口径</b>：卡组 = 本轮 run 的全部牌（<see cref="IRoguelikeReadView.DeckSize"/> 统计的集合），
    /// 手牌是从卡组抽出来的，仍属于卡组，因此手牌的快照恒为 <c>true</c>；
    /// 该位为后续"临时生成牌（如 token）"预留，用于把不属于卡组的牌标成 <c>false</c>。
    /// </para>
    /// </summary>
    public readonly struct RoguelikeCardView
    {
        public RoguelikeCardView(int cardId, string name, int cost, int damage, int block, bool inDeck)
        {
            CardId = cardId;
            Name = name;
            Cost = cost;
            Damage = damage;
            Block = block;
            InDeck = inDeck;
        }

        /// <summary>卡牌模板 ID（同 ID 视为同名牌，卡组里允许重复）。</summary>
        public int CardId { get; }

        /// <summary>牌名（内核编译期常量，非本地化键）。</summary>
        public string Name { get; }

        /// <summary>打出消耗的能量（可以 &lt; 0，但内核拒绝打出）。</summary>
        public int Cost { get; }

        /// <summary>造成的伤害（0 表示不造成伤害）。</summary>
        public int Damage { get; }

        /// <summary>提供的格挡（0 表示不提供格挡）。</summary>
        public int Block { get; }

        /// <summary>是否属于本轮卡组；手牌恒为 <c>true</c>（见类型注释）。</summary>
        public bool InDeck { get; }
    }

    /// <summary>
    /// 肉鸽卡牌只读视图：UI/适配层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// 读数口径：<see cref="CurrentNodeIndex"/> 为 <c>-1</c> 表示"还没进入任何节点"，
    /// 此时 <see cref="InBattle"/> 为 <c>false</c>、<see cref="EnemyHp"/> 为 0、<see cref="Depth"/> 为 0。
    /// 战斗外的 <see cref="Energy"/> 恒为 0（回合开始才重置成 3）。
    /// </para>
    /// </summary>
    public interface IRoguelikeReadView
    {
        /// <summary>地图节点总数（固定 8）。</summary>
        int NodeCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个节点（<c>0..NodeCount-1</c>）。
        /// 越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        RoguelikeNodeView GetNode(int index);

        /// <summary>当前所在节点的 index；<c>-1</c> 表示还没进入任何节点。</summary>
        int CurrentNodeIndex { get; }

        /// <summary>玩家当前 HP（扣到负数不截断，判定只看 <c>&lt;= 0</c>）。</summary>
        int PlayerHp { get; }

        /// <summary>玩家 HP 上限（固定 50）。</summary>
        int PlayerMaxHp { get; }

        /// <summary>当前金币。</summary>
        int Gold { get; }

        /// <summary>本轮卡组的牌数（初始 5，战斗奖励与商店购买都会 +1）。</summary>
        int DeckSize { get; }

        /// <summary>当前手牌张数（战斗外为 0）。</summary>
        int HandCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 张手牌（<c>0..HandCount-1</c>）。
        /// 越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        RoguelikeCardView GetHandCard(int index);

        /// <summary>剩余能量（战斗外的读数为 0）。</summary>
        int Energy { get; }

        /// <summary>当前敌人 HP（战斗外为 0；扣到负数不截断）。</summary>
        int EnemyHp { get; }

        /// <summary>当前敌人 HP 上限（战斗外为 0）。</summary>
        int EnemyMaxHp { get; }

        /// <summary>是否正在战斗中。</summary>
        bool InBattle { get; }

        /// <summary>本轮 run 是否已结束（阵亡或通关）。</summary>
        bool IsRunOver { get; }

        /// <summary>是否通关（仅"打赢第 8 层 Boss"为 <c>true</c>；阵亡时恒为 <c>false</c>）。</summary>
        bool IsVictory { get; }

        /// <summary>当前层号（1..8）；未进入任何节点时为 0。</summary>
        int Depth { get; }
    }

    /// <summary>
    /// 肉鸽输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>所有方法在 run 结束后一律返回 false / 空操作，不抛异常。</para>
    /// </summary>
    public interface IRoguelikeInput
    {
        /// <summary>
        /// 沿地图前进到下一层（每层只有一个候选节点，线性推进）。
        /// 返回 <c>true</c> 表示已进入该节点并结算了进入效果（战斗布场 / 商店购买 / 营地回血）。
        /// 以下情况返回 <c>false</c> 且不改任何状态：run 已结束、正在战斗中、已到达最后一层。
        /// </summary>
        bool TryEnterNextNode();

        /// <summary>
        /// 打出第 <paramref name="handIndex"/> 张手牌。任一校验不过都返回 <c>false</c> 且<b>不改任何状态</b>：
        /// run 已结束 / 不在战斗中 / 索引越界 / 费用非法 / 能量不足。
        /// 伤害先扣敌人格挡（Phase 1 敌人没有格挡，等价于直扣 HP）再扣 HP；格挡加给玩家。
        /// </summary>
        bool TryPlayCard(int handIndex);

        /// <summary>
        /// 结束本回合：敌人攻击（先由格挡吸收）→ 手牌清空 → 重抽 5 张 → 能量重置。
        /// 敌人 HP 已 <c>&lt;= 0</c> 时直接按胜利结算；玩家 HP <c>&lt;= 0</c> 时 run 结束（失败）。
        /// run 已结束或不在战斗中时为空操作。
        /// </summary>
        void EndBattleTurn();

        /// <summary>
        /// 重开整轮 run：HP/金币/卡组/地图进度全部回到初始态，同种子重放出同一张地图与同一套奖励序列；
        /// <c>ProcessedSteps</c> / <c>BattlesWon</c> / <c>CardsAdded</c> 为累计诊断值，重开不清零。
        /// 结算报告已取回（会话所有权已交还）后为空操作。
        /// </summary>
        void Restart();
    }
}

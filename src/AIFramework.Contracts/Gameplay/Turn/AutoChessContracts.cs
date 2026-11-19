#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 自走棋单位快照（只读）：内核把"UI 需要看到的"压成 9 个字段的值类型。
    /// <para>
    /// <b>备战线约定</b>：未上场的单位不在棋盘上，<see cref="X"/> / <see cref="Y"/> 一律报 <c>-1</c>；
    /// 判断"是否在棋盘上"看 <c>X &gt;= 0</c> 即可，不要用 <see cref="Alive"/> 去推断位置
    /// （阵亡单位会保留阵亡时的坐标，供 UI 画尸体）。
    /// </para>
    /// <para>
    /// <b>为什么单独给 <see cref="Alive"/></b>：一轮自动战斗是同步推完的，阵亡单位要留到
    /// <c>EndRound</c> 才清场，期间槽位仍然存在。因此视图里的"数量"是按槽位报的，
    /// 存活数必须逐槽用本字段过滤。
    /// </para>
    /// </summary>
    public readonly struct ChessUnitView
    {
        public ChessUnitView(
            int unitId,
            int owner,
            int x,
            int y,
            int hp,
            int maxHp,
            int attack,
            int range,
            bool alive)
        {
            UnitId = unitId;
            Owner = owner;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Attack = attack;
            Range = range;
            Alive = alive;
        }

        /// <summary>稳定单位 ID（玩家 1..3，敌方 11..13；出售/阵亡后不复用）。</summary>
        public int UnitId { get; }

        /// <summary>阵营：0 = 玩家，1 = 敌方。</summary>
        public int Owner { get; }

        /// <summary>列坐标（<c>0..Width-1</c>）；在备战席上为 <c>-1</c>。</summary>
        public int X { get; }

        /// <summary>行坐标（<c>0..Height-1</c>）；在备战席上为 <c>-1</c>。</summary>
        public int Y { get; }

        /// <summary>当前血量（判定只看 <c>&lt;= 0</c>，扣到负数不截断）。</summary>
        public int Hp { get; }

        /// <summary>血量上限（<c>EndRound</c> 会把存活单位治疗回该值）。</summary>
        public int MaxHp { get; }

        /// <summary>攻击力（命中即扣等量 HP，Phase 1 没有护甲/暴击）。</summary>
        public int Attack { get; }

        /// <summary>攻击距离：与目标的曼哈顿距离小于等于它才能出手。</summary>
        public int Range { get; }

        /// <summary>是否存活（<c>Hp &gt; 0</c>）。备战席上有血的单位同样是存活的。</summary>
        public bool Alive { get; }
    }

    /// <summary>
    /// 自走棋只读视图：UI/适配层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右 <c>0..Width-1</c>，<c>y</c> 向上 <c>0..Height-1</c>；
    /// 玩家半场固定 <c>y = 0..3</c>，敌方半场固定 <c>y = 4..7</c>。距离一律用曼哈顿距离（四邻网格）。
    /// </para>
    /// <para>
    /// <b>槽位约定</b>：<see cref="GetUnit(int)"/> 的 index 是<b>槽位</b>而非存活序号——玩家单位占前
    /// <see cref="PlayerUnitCount"/> 个槽位（<c>0..PlayerUnitCount-1</c>），敌方单位紧随其后
    /// （<c>PlayerUnitCount..PlayerUnitCount+EnemyUnitCount-1</c>），<see cref="UnitAt"/> 返回的也是这套 index。
    /// 槽位在一个回合内保持稳定，出售会立即压缩槽位，阵亡单位则保留槽位到 <c>EndRound</c> 清场。
    /// </para>
    /// </summary>
    public interface IAutoChessReadView
    {
        /// <summary>棋盘列数（固定 8）。</summary>
        int Width { get; }

        /// <summary>棋盘行数（固定 8；<c>y = 0..3</c> 是玩家半场，<c>y = 4..7</c> 是敌方半场）。</summary>
        int Height { get; }

        /// <summary>当前回合序号（从 0 开始，每次 <c>EndRound</c> +1）。</summary>
        int RoundIndex { get; }

        /// <summary>玩家基地血量（初值 100；每输一轮 -10，<c>&lt;= 0</c> 即失败）。</summary>
        int PlayerHp { get; }

        /// <summary>敌方基地血量（初值 100；每赢一轮 -10，<c>&lt;= 0</c> 即胜利）。</summary>
        int EnemyHp { get; }

        /// <summary>玩家阵营槽位数（含本回合阵亡待清场的尸体；出售会立即减少）。</summary>
        int PlayerUnitCount { get; }

        /// <summary>敌方阵营槽位数（每回合固定 3 个；阵亡尸体保留到 <c>EndRound</c>）。</summary>
        int EnemyUnitCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个槽位的单位快照（<c>0..PlayerUnitCount+EnemyUnitCount-1</c>）。
        /// 越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        ChessUnitView GetUnit(int index);

        /// <summary>
        /// 取 (x, y) 上已上场单位的槽位 index；空格、备战席单位与越界一律返回 <c>-1</c>，不抛异常。
        /// </summary>
        int UnitAt(int x, int y);

        /// <summary>本局是否已结束（任一方基地血量归零并被 <c>EndRound</c> 收口，或结算报告已取回）。</summary>
        bool IsFinished { get; }

        /// <summary>本轮战斗是否已由 <c>StartBattle</c> 结算完毕（此后只能 <c>EndRound</c>）。</summary>
        bool IsBattleResolved { get; }
    }

    /// <summary>
    /// 自走棋输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>
    /// 所有方法在对局结束（<see cref="IAutoChessReadView.IsFinished"/>）后一律返回 false / 空操作，不抛异常；
    /// 被拒绝的输入<b>不消耗任何状态</b>（不扣血、不推进回合、不改坐标）。
    /// </para>
    /// </summary>
    public interface IAutoChessInput
    {
        /// <summary>
        /// 把备战席上的 <paramref name="unitId"/> 放到己方半场（<c>y = 0..3</c>）的第一个空格
        /// （扫描顺序 <c>y</c> 从 0 到 3、每行 <c>x</c> 从 0 到 7）。
        /// 单位不存在 / 不是己方 / 已经在场 / 已阵亡 / 己方半场无空格时返回 false。
        /// </summary>
        bool TryPlace(int unitId);

        /// <summary>
        /// 把己方单位挪到 (x, y)：要求单位已在场、目标格在己方半场（<c>y = 0..3</c>）且为空。
        /// 越界、跨过半场、被占、敌方单位、未放置（在备战席）一律返回 false 且不改坐标。
        /// </summary>
        bool TryMoveUnit(int unitId, int x, int y);

        /// <summary>出售己方单位：占位立即释放、槽位压缩。单位不存在 / 非己方 / 已阵亡返回 false。</summary>
        bool TrySell(int unitId);

        /// <summary>
        /// 开始自动战斗：同步推演 30 个 tick，返回时 <see cref="IAutoChessReadView.IsBattleResolved"/> 已为 true。
        /// 本轮基地血量变化已在内部结算；重复调用或对局结束时为空操作。
        /// </summary>
        void StartBattle();

        /// <summary>
        /// 结束本轮：清场（阵亡单位出局、存活单位治疗满血回备战席）、回合序号 +1、重新生成敌方 3 单位。
        /// 只有 <see cref="IAutoChessReadView.IsBattleResolved"/> 为 true 时才生效；随后按基地血量收口对局结束。
        /// </summary>
        void EndRound();
    }
}

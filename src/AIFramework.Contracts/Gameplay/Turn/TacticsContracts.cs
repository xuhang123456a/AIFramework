#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 战棋单位快照（只读）：内核把"UI 需要看到的"压成 9 个字段的值类型。
    /// <para>
    /// <b>为什么带 <see cref="UnitId"/> 又带坐标</b>：<see cref="UnitId"/> 是稳定标识（单位死亡后不下去、
    /// 不复用），而 <see cref="ITacticsReadView.GetUnit(int)"/> 的 index 会因阵亡前移而变化；
    /// 输入的 <c>TrySelect/TryMove/TryAttack</c> 一律收 <see cref="UnitId"/>，UI 只需缓存它。
    /// </para>
    /// <para>
    /// 不带名字、图标与技能列表：文案与美术属于配置表/资源层，逻辑层只出数值（也避免每帧拼字符串）。
    /// </para>
    /// </summary>
    public readonly struct TacticsUnitView
    {
        public TacticsUnitView(
            int unitId,
            int team,
            int x,
            int y,
            int hp,
            int maxHp,
            int attack,
            int moveRange,
            int attackRange)
        {
            UnitId = unitId;
            Team = team;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Attack = attack;
            MoveRange = moveRange;
            AttackRange = attackRange;
        }

        /// <summary>稳定单位 ID（玩家单位 1..3，敌方单位 11..13）。</summary>
        public int UnitId { get; }

        /// <summary>阵营：0 = 玩家，1 = 敌方。</summary>
        public int Team { get; }

        /// <summary>棋盘列坐标（<c>0..Width-1</c>）。</summary>
        public int X { get; }

        /// <summary>棋盘行坐标（<c>0..Height-1</c>）。</summary>
        public int Y { get; }

        /// <summary>当前血量（判定只看 <c>&lt;= 0</c>，扣到负数不截断）。</summary>
        public int Hp { get; }

        /// <summary>血量上限。</summary>
        public int MaxHp { get; }

        /// <summary>攻击力（命中即扣等量 HP，Phase 1 没有护甲/闪避）。</summary>
        public int Attack { get; }

        /// <summary>移动力：一次 <c>TryMove</c> 的曼哈顿距离上限。</summary>
        public int MoveRange { get; }

        /// <summary>攻击距离：曼哈顿距离小于等于它才能出手。</summary>
        public int AttackRange { get; }
    }

    /// <summary>
    /// 战棋只读视图：UI/适配层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// 棋盘约定：<c>x</c> 向右、<c>y</c> 向上；玩家初始在 <c>y == 0</c> 一侧，敌方在 <c>y == Height - 1</c> 一侧。
    /// 距离一律用曼哈顿距离（四邻网格，没有对角线）。
    /// </para>
    /// </summary>
    public interface ITacticsReadView
    {
        /// <summary>棋盘列数（固定 8）。</summary>
        int Width { get; }

        /// <summary>棋盘行数（固定 8）。</summary>
        int Height { get; }

        /// <summary>当前回合序号（从 1 开始，每次 <c>EndTurn</c> +1）。</summary>
        int TurnIndex { get; }

        /// <summary>当前行动阵营：0 = 玩家，1 = 敌方。<c>EndTurn</c> 返回后可读，稳态为 0。</summary>
        int CurrentTeam { get; }

        /// <summary>存活单位总数（含双方；阵亡单位会被移除，因此会变小）。</summary>
        int UnitCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个存活单位（<c>0..UnitCount-1</c>）。
        /// 越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        TacticsUnitView GetUnit(int index);

        /// <summary>
        /// 取 (x, y) 上的单位 index（对应 <see cref="GetUnit(int)"/>）；空格或越界返回 <c>-1</c>，不抛异常。
        /// </summary>
        int UnitAt(int x, int y);

        /// <summary>对局是否结束（任一方全灭，或回合数超过上限判平局）。</summary>
        bool IsFinished { get; }
    }

    /// <summary>
    /// 战棋输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>所有方法在对局结束后一律返回 false / 空操作，不抛异常。</para>
    /// </summary>
    public interface ITacticsInput
    {
        /// <summary>
        /// 选中己方（<see cref="ITacticsReadView.CurrentTeam"/> 阵营）存活单位；
        /// 再传一次当前已选中的 ID 即取消选中。找不到、非己方、已结束时返回 false 且不改选中状态。
        /// </summary>
        bool TrySelect(int unitId);

        /// <summary>
        /// 把 <paramref name="unitId"/> 移动到 (x, y)。只有同时满足以下条件才返回 true：
        /// 己方单位、目标格在界内且空、曼哈顿距离 &lt;= 移动力、且存在一条四邻不被占用的路径。
        /// 任一不满足返回 false 且<b>不消耗任何状态</b>。
        /// </summary>
        bool TryMove(int unitId, int x, int y);

        /// <summary>
        /// 用 <paramref name="attackerUnitId"/> 攻击 <paramref name="targetUnitId"/>。要求双方存活、互为敌对阵营、
        /// 曼哈顿距离 &lt;= 攻方攻击距离；命中直接扣攻方攻击力等量 HP，目标 HP &lt;= 0 立即被移除。
        /// </summary>
        bool TryAttack(int attackerUnitId, int targetUnitId);

        /// <summary>
        /// 玩家结束回合：切到敌方 → 敌方 AI 立即行动 → 切回玩家 → 回合序号 +1。
        /// 对局已结束时为空操作。
        /// </summary>
        void EndTurn();
    }
}

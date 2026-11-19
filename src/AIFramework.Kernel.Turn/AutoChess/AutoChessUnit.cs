#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>单位所处位置：备战席还是棋盘。</summary>
    internal enum AutoChessUnitState : byte
    {
        /// <summary>备战席：未上场，坐标为 <c>-1</c>，不占格。</summary>
        Bench = 0,

        /// <summary>棋盘：已上场（含本轮阵亡、尚未被 <c>EndRound</c> 清场的尸体）。</summary>
        Board = 1
    }

    /// <summary>
    /// 棋盘/备战席上的一个单位（可变结构体，只存在于 <see cref="AutoChessBoard"/> 的预分配数组里）。
    /// <para>
    /// <b>为什么可变</b>：一轮战斗要改几十次坐标与血量，用类会把每次攻击变成引用追逐；
    /// 结构体数组让整盘单位连续存放，战斗主循环的遍历/最近敌人搜索都是顺序内存访问且零分配。
    /// 对外一律只出不可变的 <see cref="ChessUnitView"/> 快照。
    /// </para>
    /// <para>
    /// <b>生死口径</b>：<see cref="Hp"/> <c>&lt;= 0</c> 即阵亡；阵亡只清占位、保留槽位与坐标，
    /// 直到 <c>EndRound</c> 清场才从数组移除。备战席单位血量恒为正，因此"存活"与"是否上场"是两件事。
    /// </para>
    /// </summary>
    internal struct AutoChessUnit
    {
        /// <summary>稳定单位 ID（玩家 1..3，敌方 11..13；出售/阵亡后不复用）。</summary>
        internal int UnitId;

        /// <summary>阵营：0 = 玩家，1 = 敌方。</summary>
        internal int Owner;

        /// <summary>列坐标；在备战席上为 <c>-1</c>。</summary>
        internal int X;

        /// <summary>行坐标；在备战席上为 <c>-1</c>。</summary>
        internal int Y;

        /// <summary>当前血量（<c>&lt;= 0</c> 即阵亡）。</summary>
        internal int Hp;

        /// <summary>血量上限。</summary>
        internal int MaxHp;

        /// <summary>攻击力。</summary>
        internal int Attack;

        /// <summary>攻击距离。</summary>
        internal int Range;

        /// <summary>所处位置：备战席或棋盘。</summary>
        internal AutoChessUnitState State;

        /// <summary>是否存活：血量大于 0（备战席单位同样算存活）。</summary>
        internal bool IsAlive
        {
            get { return Hp > 0; }
        }

        /// <summary>是否已在棋盘上：已上场且尚未阵亡。</summary>
        internal bool IsOnBoard
        {
            get { return State == AutoChessUnitState.Board && Hp > 0; }
        }

        /// <summary>转成对外只读快照；只读拷贝，不分配。</summary>
        internal ChessUnitView ToView()
        {
            return new ChessUnitView(UnitId, Owner, X, Y, Hp, MaxHp, Attack, Range, Hp > 0);
        }
    }
}

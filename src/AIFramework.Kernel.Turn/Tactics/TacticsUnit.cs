#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Tactics
{
    /// <summary>
    /// 棋盘上的一个单位（可变结构体，只存在于 <see cref="TacticsBoard"/> 的预分配数组里）。
    /// <para>
    /// <b>为什么可变</b>：每回合都要改坐标与血量，用类会让"移动"变成字段写入 + 引用追逐；
    /// 结构体数组让整盘单位连续存放，<see cref="TacticsBoard"/> 的遍历/最近单位搜索都是顺序内存访问且零分配。
    /// 对外一律只出不可变的 <see cref="TacticsUnitView"/> 快照。
    /// </para>
    /// </summary>
    internal struct TacticsUnit
    {
        /// <summary>稳定单位 ID（玩家 1..3，敌方 11..13；阵亡后不复用）。</summary>
        internal int UnitId;

        /// <summary>阵营：0 = 玩家，1 = 敌方。</summary>
        internal int Team;

        /// <summary>列坐标。</summary>
        internal int X;

        /// <summary>行坐标。</summary>
        internal int Y;

        /// <summary>当前血量（&lt;= 0 即阵亡）。</summary>
        internal int Hp;

        /// <summary>血量上限。</summary>
        internal int MaxHp;

        /// <summary>攻击力。</summary>
        internal int Attack;

        /// <summary>移动力。</summary>
        internal int MoveRange;

        /// <summary>攻击距离。</summary>
        internal int AttackRange;

        /// <summary>转成对外只读快照；只读拷贝，不分配。</summary>
        internal TacticsUnitView ToView()
        {
            return new TacticsUnitView(UnitId, Team, X, Y, Hp, MaxHp, Attack, MoveRange, AttackRange);
        }
    }
}

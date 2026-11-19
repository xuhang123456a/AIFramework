#nullable enable
namespace AIFramework.Features.TowerDefense
{
    /// <summary>
    /// 塔定义行：<c>TbTower</c> 的一行，纯数据、无行为。
    /// <para>
    /// <b>为什么射程按格计而不是按像素/世界单位</b>：Meta 切片只认棋盘坐标（8×8 格），
    /// 一换算成世界单位就必然要读引擎的地图尺寸，切片立刻变成引擎绑定的。
    /// 世界坐标只由适配层在布场时换算（见 <c>TowerDefenseController</c>）。
    /// </para>
    /// </summary>
    public readonly struct TowerDef
    {
        public TowerDef(int towerId, int cost, int damage, int range)
        {
            TowerId = towerId;
            Cost = cost;
            Damage = damage;
            Range = range;
        }

        /// <summary>塔 ID：建造命令里引用它，同表内唯一。</summary>
        public int TowerId { get; }

        /// <summary>建造单价（金币数量）。0 是合法的"免费塔"。</summary>
        public int Cost { get; }

        /// <summary>单发伤害，必须 &gt; 0（0 伤害的塔等于没有塔，属于配置错误）。</summary>
        public int Damage { get; }

        /// <summary>射程，单位是棋盘格，必须 &gt; 0。</summary>
        public int Range { get; }
    }
}

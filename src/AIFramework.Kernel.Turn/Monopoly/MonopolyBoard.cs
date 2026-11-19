#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// 大富翁固定棋盘：20 格环形的种类/价格表 + 运行期所有者/等级表。
    /// <para>
    /// <b>固定布局（写死）</b>：
    /// <code>
    /// 索引  0    1    2    3    4     5    6    7    8      9    10   11   12    13   14   15   16     17   18   19
    /// 种类  GO   地   地   地   Jail  地   地   地   Chance 地   地   地   Tax   地   地   地   Bonus  地   地   地
    /// 价格  0    200  220  240  0     260  280  300 0      320  340  360  0     380  400  420  0      440  460  480
    /// </code>
    /// 地产按出现顺序价格从 200 起每块 +20，共 15 块（200..480）。
    /// <para>
    /// <b>为什么地产是 15 块而不是需求文字里的 10 块</b>：20 格中 0/4/8/12/16 五格非地产，其余必然是 15 格；
    /// "10 块"与"其余为 Property"两条表述冲突，本实现取后者（价格序列 200/220/240/… 与数量无关）。
    /// </para>
    /// <para>
    /// <b>为什么单独成一个类</b>：棋盘表是纯数据 + 就地改写，和回合/经济流程解耦；
    /// 构造期一次分配 4 条定长数组，运行期买地/升级/破产归还只改字段，<b>零堆分配</b>。
    /// </para>
    /// </summary>
    internal sealed class MonopolyBoard
    {
        /// <summary>无主哨兵（所有 <c>Owner</c> 读数用它表示"没人持有"）。</summary>
        internal const int NoOwner = -1;

        private readonly MonopolyTileKind[] _kinds;

        private readonly int[] _prices;

        private readonly int[] _owners;

        private readonly int[] _levels;

        private readonly int _propertyCount;

        internal MonopolyBoard()
        {
            _kinds = new MonopolyTileKind[MonopolyRules.TileCount];
            _prices = new int[MonopolyRules.TileCount];
            _owners = new int[MonopolyRules.TileCount];
            _levels = new int[MonopolyRules.TileCount];

            int propertyOrdinal = 0;
            for (int index = 0; index < MonopolyRules.TileCount; index++)
            {
                MonopolyTileKind kind = LayoutKindAt(index);
                _kinds[index] = kind;
                _prices[index] = MonopolyRules.IsProperty(kind)
                    ? MonopolyRules.PriceForOrdinal(propertyOrdinal++)
                    : 0;
                _owners[index] = NoOwner;
                _levels[index] = 0;
            }

            _propertyCount = propertyOrdinal;
        }

        /// <summary>格子总数（固定 20）。</summary>
        internal int TileCount
        {
            get { return MonopolyRules.TileCount; }
        }

        /// <summary>地产格总数（固定 15）。</summary>
        internal int PropertyCount
        {
            get { return _propertyCount; }
        }

        /// <summary>取格子种类（调用方保证 <paramref name="index"/> 在 0..19）。</summary>
        internal MonopolyTileKind KindAt(int index)
        {
            return _kinds[index];
        }

        /// <summary>取地产买入价（非地产格为 0）。</summary>
        internal int PriceAt(int index)
        {
            return _prices[index];
        }

        /// <summary>取地产所有者（<see cref="NoOwner"/> = 无主；非地产格恒为无主）。</summary>
        internal int OwnerAt(int index)
        {
            return _owners[index];
        }

        /// <summary>取地产等级（0..3；非地产格与无主地产恒为 0）。</summary>
        internal int LevelAt(int index)
        {
            return _levels[index];
        }

        /// <summary>产出一个只读快照（值类型，不分配）。</summary>
        internal MonopolyTileView GetTile(int index)
        {
            return new MonopolyTileView(index, _kinds[index], _owners[index], _levels[index], _prices[index]);
        }

        /// <summary>把无主地产买给 <paramref name="owner"/>：置所有者并把等级复位到 0。</summary>
        internal void Buy(int index, int owner)
        {
            _owners[index] = owner;
            _levels[index] = 0;
        }

        /// <summary>升级地产：等级 +1（调用方负责判上限与扣费）。</summary>
        internal void Upgrade(int index)
        {
            _levels[index]++;
        }

        /// <summary>
        /// 把 <paramref name="owner"/> 的全部地产归还无主（所有者 = -1、等级 = 0，新买家不会继承升级），
        /// 返回归还块数。
        /// </summary>
        internal int ReleaseAll(int owner)
        {
            int released = 0;
            for (int index = 0; index < MonopolyRules.TileCount; index++)
            {
                if (_owners[index] != owner)
                {
                    continue;
                }

                _owners[index] = NoOwner;
                _levels[index] = 0;
                released++;
            }

            return released;
        }

        /// <summary>统计 <paramref name="owner"/> 当前持有的地产数。</summary>
        internal int CountOwnedBy(int owner)
        {
            int count = 0;
            for (int index = 0; index < MonopolyRules.TileCount; index++)
            {
                if (_owners[index] == owner)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>固定布局：0 起点；4 监狱、8 机会、12 税务、16 奖励；其余全部为地产。</summary>
        private static MonopolyTileKind LayoutKindAt(int index)
        {
            switch (index)
            {
                case MonopolyRules.GoTileIndex:
                    return MonopolyTileKind.Go;
                case MonopolyRules.JailTileIndex:
                    return MonopolyTileKind.Jail;
                case MonopolyRules.ChanceTileIndex:
                    return MonopolyTileKind.Chance;
                case MonopolyRules.TaxTileIndex:
                    return MonopolyTileKind.Tax;
                case MonopolyRules.BonusTileIndex:
                    return MonopolyTileKind.Bonus;
                default:
                    return MonopolyTileKind.Property;
            }
        }
    }
}

#nullable enable
using System;

namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// 自走棋战场：单位槽位、占位格、备战席/棋盘的增删与移动。只管"格子与单位"的存取，
    /// 不含战斗推演（<see cref="AutoChessBattle"/>）、基地血量与回合（<see cref="AutoChessWorld"/>）。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右 <c>0..Width-1</c>，<c>y</c> 向上 <c>0..Height-1</c>；
    /// 玩家半场 <c>y = 0..3</c>，敌方半场 <c>y = 4..7</c>。距离一律用曼哈顿距离。
    /// </para>
    /// <para>
    /// <b>槽位约定（对外 index 的唯一来源）</b>：玩家单位永远占前若干槽位、敌方单位紧随其后；
    /// 出售会立即压缩槽位（后续 index 前移），阵亡只清占位并保留槽位到 <see cref="ResetForNextRound"/>，
    /// 因此一轮战斗期间 index 稳定、UI 可以直接按槽位绘制尸体。
    /// </para>
    /// <para>
    /// <b>占位表</b>：<see cref="_occupancy"/> 存"该格上的槽位 index"，空格为 <c>-1</c>；
    /// 备战席单位不占格。槽位增删后整表重建（64 格，代价可忽略），移动只做两格增量更新。
    /// </para>
    /// <para>
    /// <b>零分配</b>：单位数组与占位表都在构造期一次分配；放置/挪位/出售/伤害/走位路径不申请托管堆内存。
    /// </para>
    /// <para>
    /// <b>文件拆分</b>：本文件只放内核状态与查询/战斗原语；备战席与棋盘的增删挪移在
    /// <c>AutoChessBoard.Placement.cs</c>（同一 <c>partial</c> 类的另一半，按"读"与"写"分文件以守住 400 行预算）。
    /// </para>
    /// </summary>
    internal sealed partial class AutoChessBoard
    {
        /// <summary>单位槽位数组（下标即对外 index；出售时后续元素前移，阵亡时保留）。</summary>
        private readonly AutoChessUnit[] _units;

        /// <summary>占位表：<c>y * Width + x</c> → 槽位 index，<c>-1</c> 表示空格。</summary>
        private readonly int[] _occupancy;

        private int _slotCount;

        internal AutoChessBoard(int playerCount)
        {
            if (playerCount < 1 || playerCount > AutoChessRules.MaxPlayerUnits)
            {
                throw new ArgumentOutOfRangeException(nameof(playerCount), "玩家单位数必须夹在 1..3。");
            }

            _units = new AutoChessUnit[AutoChessRules.MaxUnits];
            _occupancy = new int[AutoChessRules.BoardWidth * AutoChessRules.BoardHeight];
            _slotCount = 0;
            ClearOccupancy();

            for (int i = 0; i < playerCount; i++)
            {
                AddUnit(
                    AutoChessRules.PlayerUnitIdBase + i,
                    AutoChessRules.PlayerOwner,
                    AutoChessUnitState.Bench,
                    AutoChessRules.BenchCoordinate,
                    AutoChessRules.BenchCoordinate,
                    AutoChessRules.PlayerUnitHp,
                    AutoChessRules.PlayerUnitAttack,
                    AutoChessRules.PlayerUnitRange);
            }

            SpawnEnemies();
        }

        /// <summary>棋盘列数。</summary>
        internal int Width
        {
            get { return AutoChessRules.BoardWidth; }
        }

        /// <summary>棋盘行数。</summary>
        internal int Height
        {
            get { return AutoChessRules.BoardHeight; }
        }

        /// <summary>当前槽位数（含阵亡待清场的尸体；出售后立即减少）。</summary>
        internal int SlotCount
        {
            get { return _slotCount; }
        }

        /// <summary>两格曼哈顿距离。</summary>
        internal static int Manhattan(int x1, int y1, int x2, int y2)
        {
            int dx = x1 > x2 ? x1 - x2 : x2 - x1;
            int dy = y1 > y2 ? y1 - y2 : y2 - y1;
            return dx + dy;
        }

        /// <summary>坐标是否在棋盘内。</summary>
        internal bool InBounds(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height;
        }

        /// <summary>该行是否属于玩家半场（布阵唯一允许的区域）。</summary>
        internal bool IsPlayerHalf(int y)
        {
            return y >= 0 && y < AutoChessRules.PlayerRowCount;
        }

        /// <summary>取第 slotCount 个槽位的值拷贝；调用方负责保证 index 合法（对外入口已做校验）。</summary>
        internal AutoChessUnit GetUnit(int slot)
        {
            return _units[slot];
        }

        /// <summary>按稳定 UnitId 找槽位；找不到返回 <c>-1</c>。</summary>
        internal int FindSlot(int unitId)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].UnitId == unitId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>取 (x, y) 上已上场单位的槽位 index；空格、备战席与越界返回 <c>-1</c>。</summary>
        internal int UnitAt(int x, int y)
        {
            if (!InBounds(x, y))
            {
                return -1;
            }

            return _occupancy[(y * Width) + x];
        }

        /// <summary>某阵营的槽位数（含尸体）。</summary>
        internal int CountOwner(int owner)
        {
            int count = 0;
            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].Owner == owner)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>某阵营"在棋盘上且存活"的单位数：基地判负与战斗结束判定都看它。</summary>
        internal int CountOnBoardAlive(int owner)
        {
            int count = 0;
            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].Owner == owner && _units[i].IsOnBoard)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>某阵营棋盘上存活单位的总血量（tick 用尽后的胜负比较口径；尸体不计）。</summary>
        internal int TotalOnBoardHp(int owner)
        {
            int total = 0;
            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].Owner == owner && _units[i].IsOnBoard)
                {
                    total += _units[i].Hp;
                }
            }

            return total;
        }

        /// <summary>
        /// 找离第 <paramref name="slot"/> 个槽位曼哈顿最近的敌对"在场存活"单位；
        /// 同距离取槽位更小者（确定性），场上没有敌人返回 <c>-1</c>。
        /// </summary>
        internal int FindNearestOpponent(int slot)
        {
            AutoChessUnit self = _units[slot];
            int best = -1;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].Owner == self.Owner || !_units[i].IsOnBoard)
                {
                    continue;
                }

                int distance = Manhattan(self.X, self.Y, _units[i].X, _units[i].Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// 把第 <paramref name="slot"/> 个槽位挪到 (x, y)（只更新两格占位）；越界/被占返回 false。
        /// 被占时按契约"原地不动"，调用方不做二次轴回退。
        /// </summary>
        internal bool TryStep(int slot, int x, int y)
        {
            if (!InBounds(x, y))
            {
                return false;
            }

            int target = (y * Width) + x;
            if (_occupancy[target] >= 0)
            {
                return false;
            }

            AutoChessUnit unit = _units[slot];
            _occupancy[(unit.Y * Width) + unit.X] = -1;
            _units[slot].X = x;
            _units[slot].Y = y;
            _occupancy[target] = slot;
            return true;
        }

        /// <summary>
        /// 对第 <paramref name="slot"/> 个槽位扣血；HP 降到 <c>&lt;= 0</c> 立即清占位（保留槽位与坐标，供 UI 画尸体）。
        /// 返回 true 表示本次攻击致死。
        /// </summary>
        internal bool ApplyDamage(int slot, int damage)
        {
            if (damage <= 0 || !_units[slot].IsOnBoard)
            {
                return false;
            }

            AutoChessUnit unit = _units[slot];
            unit.Hp -= damage;
            _units[slot] = unit;

            if (unit.Hp > 0)
            {
                return false;
            }

            _occupancy[(unit.Y * Width) + unit.X] = -1;
            return true;
        }

        /// <summary>移除第 slotCount 个槽位：后续元素整体前移，随后重建占位表。</summary>
        private int AddUnit(
            int unitId,
            int owner,
            AutoChessUnitState state,
            int x,
            int y,
            int hp,
            int attack,
            int range)
        {
            if (_slotCount >= _units.Length)
            {
                return -1;
            }

            _units[_slotCount].UnitId = unitId;
            _units[_slotCount].Owner = owner;
            _units[_slotCount].State = state;
            _units[_slotCount].X = x;
            _units[_slotCount].Y = y;
            _units[_slotCount].Hp = hp;
            _units[_slotCount].MaxHp = hp;
            _units[_slotCount].Attack = attack;
            _units[_slotCount].Range = range;

            int slot = _slotCount;
            _slotCount++;
            return slot;
        }

        /// <summary>移除第 slotCount 个槽位：后续元素整体前移，随后重建占位表。</summary>
        private void RemoveAt(int slot)
        {
            for (int i = slot; i < _slotCount - 1; i++)
            {
                _units[i] = _units[i + 1];
            }

            _slotCount--;
            RebuildOccupancy();
        }

        /// <summary>整表重建占位（槽位增删后调用，index 已重排）。</summary>
        private void RebuildOccupancy()
        {
            ClearOccupancy();
            for (int i = 0; i < _slotCount; i++)
            {
                if (_units[i].IsOnBoard)
                {
                    _occupancy[(_units[i].Y * Width) + _units[i].X] = i;
                }
            }
        }

        /// <summary>把占位表全部清成空格。</summary>
        private void ClearOccupancy()
        {
            for (int i = 0; i < _occupancy.Length; i++)
            {
                _occupancy[i] = -1;
            }
        }
    }
}

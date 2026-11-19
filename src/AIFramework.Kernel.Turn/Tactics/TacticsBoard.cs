#nullable enable
using System;

namespace AIFramework.Kernel.Turn.Tactics
{
    /// <summary>
    /// 战棋棋盘：单位槽位、占位格、四邻 BFS 可达性。只管"格子与单位"的存取，不含回合、胜负与 AI。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右（<c>0..Width-1</c>），<c>y</c> 向上（<c>0</c> 为玩家底线，<c>Height-1</c> 为敌方底线），
    /// 距离一律用曼哈顿距离。
    /// </para>
    /// <para>
    /// <b>占位表</b>：<see cref="_occupancy"/> 存"该格上的单位 index"，空格为 <c>-1</c>。
    /// index 会因阵亡前移而整体变化，因此只在单位增删后整表重建（64 格，代价可忽略），
    /// 移动只做两格增量更新。
    /// </para>
    /// <para>
    /// <b>零分配</b>：单位数组、占位表、BFS 距离表与 BFS 队列全部在构造期一次分配；
    /// 移动/攻击/可达性判定路径不申请托管堆内存。
    /// </para>
    /// </summary>
    internal sealed class TacticsBoard
    {
        /// <summary>单位数组（下标即 index，阵亡时后续元素前移）。</summary>
        private readonly TacticsUnit[] _units;

        /// <summary>占位表：<c>y * Width + x</c> → 单位 index，<c>-1</c> 表示空格。</summary>
        private readonly int[] _occupancy;

        /// <summary>BFS 距离表，每次判定前整表重置为 <c>-1</c>。</summary>
        private readonly int[] _bfsDistance;

        /// <summary>BFS 队列（定长，最大宽度即格数）。</summary>
        private readonly int[] _bfsQueue;

        private int _unitCount;

        internal TacticsBoard(int width, int height, int maxUnits)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "棋盘宽度必须为正。");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "棋盘高度必须为正。");
            }

            if (maxUnits <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxUnits), "单位容量必须为正。");
            }

            Width = width;
            Height = height;
            _units = new TacticsUnit[maxUnits];
            _occupancy = new int[width * height];
            _bfsDistance = new int[width * height];
            _bfsQueue = new int[width * height];
            _unitCount = 0;
            ClearOccupancy();
        }

        internal int Width { get; }

        internal int Height { get; }

        internal int UnitCount
        {
            get { return _unitCount; }
        }

        /// <summary>单位容量（双方总数上限）。</summary>
        internal int Capacity
        {
            get { return _units.Length; }
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

        /// <summary>放入一个单位；容量已满 / 越界 / 目标格被占返回 false（不抛异常）。</summary>
        internal bool TryAdd(
            int unitId,
            int team,
            int x,
            int y,
            int hp,
            int attack,
            int moveRange,
            int attackRange)
        {
            if (_unitCount >= _units.Length || !InBounds(x, y) || _occupancy[(y * Width) + x] >= 0)
            {
                return false;
            }

            _units[_unitCount].UnitId = unitId;
            _units[_unitCount].Team = team;
            _units[_unitCount].X = x;
            _units[_unitCount].Y = y;
            _units[_unitCount].Hp = hp;
            _units[_unitCount].MaxHp = hp;
            _units[_unitCount].Attack = attack;
            _units[_unitCount].MoveRange = moveRange;
            _units[_unitCount].AttackRange = attackRange;
            _occupancy[(y * Width) + x] = _unitCount;
            _unitCount++;
            return true;
        }

        /// <summary>取第 index 个单位的值拷贝；调用方负责保证 index 合法（对外入口已做校验）。</summary>
        internal TacticsUnit GetUnit(int index)
        {
            return _units[index];
        }

        /// <summary>按稳定 UnitId 找当前 index；找不到返回 false。</summary>
        internal bool TryFindIndex(int unitId, out int index)
        {
            for (int i = 0; i < _unitCount; i++)
            {
                if (_units[i].UnitId == unitId)
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        /// <summary>取 (x, y) 上的单位 index；空格或越界返回 <c>-1</c>。</summary>
        internal int UnitAt(int x, int y)
        {
            if (!InBounds(x, y))
            {
                return -1;
            }

            return _occupancy[(y * Width) + x];
        }

        /// <summary>
        /// 从 (fromX, fromY) 出发，四邻移动、绕不开被占格，能否在 <paramref name="maxSteps"/> 步内走到 (toX, toY)。
        /// 目标格被占 / 越界 / 曼哈顿距离已超限直接返回 false（快速拒绝，省掉 BFS）。
        /// </summary>
        internal bool IsReachable(int fromX, int fromY, int toX, int toY, int maxSteps)
        {
            if (!InBounds(fromX, fromY) || !InBounds(toX, toY))
            {
                return false;
            }

            if (_occupancy[(toY * Width) + toX] >= 0)
            {
                return false;
            }

            if (Manhattan(fromX, fromY, toX, toY) > maxSteps)
            {
                return false;
            }

            for (int i = 0; i < _bfsDistance.Length; i++)
            {
                _bfsDistance[i] = -1;
            }

            int target = (toY * Width) + toX;
            int head = 0;
            int tail = 0;

            _bfsDistance[(fromY * Width) + fromX] = 0;
            _bfsQueue[tail] = (fromY * Width) + fromX;
            tail++;

            while (head < tail)
            {
                int current = _bfsQueue[head];
                head++;

                if (current == target)
                {
                    return true;
                }

                int distance = _bfsDistance[current];
                if (distance >= maxSteps)
                {
                    continue;
                }

                int x = current % Width;
                int y = current / Width;
                int next = distance + 1;

                if (TryPush(x + 1, y, next, target, ref tail)
                    || TryPush(x - 1, y, next, target, ref tail)
                    || TryPush(x, y + 1, next, target, ref tail)
                    || TryPush(x, y - 1, next, target, ref tail))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>把第 index 个单位挪到 (x, y)（只更新两格占位）；越界/被占返回 false。</summary>
        internal bool MoveUnit(int index, int x, int y)
        {
            if (!InBounds(x, y) || _occupancy[(y * Width) + x] >= 0)
            {
                return false;
            }

            TacticsUnit unit = _units[index];
            _occupancy[(unit.Y * Width) + unit.X] = -1;
            unit.X = x;
            unit.Y = y;
            _units[index] = unit;
            _occupancy[(y * Width) + x] = index;
            return true;
        }

        /// <summary>
        /// 对第 index 个单位扣血；HP 降到 <c>&lt;= 0</c> 立即移除并让后续 index 前移。
        /// 返回 true 表示该单位已阵亡并被移除。
        /// </summary>
        internal bool ApplyDamage(int index, int damage)
        {
            if (damage <= 0)
            {
                return false;
            }

            TacticsUnit unit = _units[index];
            unit.Hp -= damage;
            if (unit.Hp > 0)
            {
                _units[index] = unit;
                return false;
            }

            RemoveAt(index);
            return true;
        }

        /// <summary>移除第 index 个单位：后续元素整体前移，随后重建占位表。</summary>
        internal void RemoveAt(int index)
        {
            for (int i = index; i < _unitCount - 1; i++)
            {
                _units[i] = _units[i + 1];
            }

            _unitCount--;
            RebuildOccupancy();
        }

        /// <summary>某阵营的存活单位数。</summary>
        internal int CountTeam(int team)
        {
            int count = 0;
            for (int i = 0; i < _unitCount; i++)
            {
                if (_units[i].Team == team)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 找离 (x, y) 曼哈顿最近的指定阵营单位 index；同距离取 index 更小者（确定性），
        /// 该阵营已全灭返回 <c>-1</c>。
        /// </summary>
        internal int NearestTeamUnit(int team, int x, int y)
        {
            int best = -1;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < _unitCount; i++)
            {
                if (_units[i].Team != team)
                {
                    continue;
                }

                int distance = Manhattan(x, y, _units[i].X, _units[i].Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>BFS 入队：越界/被占/已访问都跳过；返回 true 表示入队的正是目标格。</summary>
        private bool TryPush(int x, int y, int distance, int target, ref int tail)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return false;
            }

            int index = (y * Width) + x;
            if (_occupancy[index] >= 0 || _bfsDistance[index] >= 0)
            {
                return false;
            }

            _bfsDistance[index] = distance;
            _bfsQueue[tail] = index;
            tail++;
            return index == target;
        }

        /// <summary>整表重建占位（单位增删后调用，index 已重排）。</summary>
        private void RebuildOccupancy()
        {
            ClearOccupancy();
            for (int i = 0; i < _unitCount; i++)
            {
                _occupancy[(_units[i].Y * Width) + _units[i].X] = i;
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

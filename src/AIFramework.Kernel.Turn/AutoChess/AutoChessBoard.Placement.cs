#nullable enable
namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// <see cref="AutoChessBoard"/> 的"写"半边：备战席与棋盘之间的放置、挪位、出售，以及回合重置的清场与重建。
    /// <para>
    /// <b>为什么拆文件</b>：单位状态与查询/战斗原语已经占满 <c>AutoChessBoard.cs</c>，再加布阵写操作会越 400 行预算；
    /// 按"读原语 / 写操作"切两半既保住可读性，也让"布阵只在玩家半场"这条规则集中在一个文件里，
    /// 评审时不必在 400 行里找散落的越界判断。
    /// </para>
    /// <para>
    /// <b>统一拒绝口径</b>：所有 <c>Try*</c> 在拒绝时都<b>不改任何状态</b>（不动坐标、不动占位、不动槽位），
    /// 调用方可以直接重试而不必回滚。
    /// </para>
    /// </summary>
    internal sealed partial class AutoChessBoard
    {
        /// <summary>
        /// 把备战席单位放到己方半场第一个空格（扫描顺序 <c>y</c> 0→3、每行 <c>x</c> 0→7）。
        /// 槽位不存在 / 非玩家 / 已在场 / 已阵亡 / 无空格返回 false，且不改任何状态。
        /// </summary>
        internal bool TryPlace(int unitId)
        {
            int slot = FindSlot(unitId);
            if (slot < 0)
            {
                return false;
            }

            AutoChessUnit unit = _units[slot];
            if (unit.Owner != AutoChessRules.PlayerOwner || unit.State != AutoChessUnitState.Bench || !unit.IsAlive)
            {
                return false;
            }

            if (!TryFindFreeCell(0, AutoChessRules.PlayerRowCount - 1, out int x, out int y))
            {
                return false;
            }

            _units[slot].State = AutoChessUnitState.Board;
            _units[slot].X = x;
            _units[slot].Y = y;
            _occupancy[(y * Width) + x] = slot;
            return true;
        }

        /// <summary>
        /// 把己方在场单位挪到 (x, y)：要求目标格在己方半场且为空。
        /// 槽位不存在 / 非玩家 / 未上场 / 已阵亡 / 越界 / 跨半场 / 被占返回 false。
        /// </summary>
        internal bool TryMoveUnit(int unitId, int x, int y)
        {
            int slot = FindSlot(unitId);
            if (slot < 0)
            {
                return false;
            }

            AutoChessUnit unit = _units[slot];
            if (unit.Owner != AutoChessRules.PlayerOwner || !unit.IsOnBoard)
            {
                return false;
            }

            if (!InBounds(x, y) || !IsPlayerHalf(y))
            {
                return false;
            }

            int target = (y * Width) + x;
            if (_occupancy[target] >= 0)
            {
                return false;
            }

            _occupancy[(unit.Y * Width) + unit.X] = -1;
            _units[slot].X = x;
            _units[slot].Y = y;
            _occupancy[target] = slot;
            return true;
        }

        /// <summary>
        /// 出售己方单位：释放占位并压缩槽位（后续 index 前移）。
        /// 槽位不存在 / 非玩家 / 已阵亡返回 false（尸体由 <see cref="ResetForNextRound"/> 统一清场）。
        /// </summary>
        internal bool TrySell(int unitId)
        {
            int slot = FindSlot(unitId);
            if (slot < 0)
            {
                return false;
            }

            AutoChessUnit unit = _units[slot];
            if (unit.Owner != AutoChessRules.PlayerOwner || !unit.IsAlive)
            {
                return false;
            }

            RemoveAt(slot);
            return true;
        }

        /// <summary>
        /// 回合重置：清出阵亡单位与全部敌方单位（敌方永远占末三位槽位），存活玩家单位治疗满血、收回备战席，
        /// 再重新生成 3 个敌方单位。下一轮的布阵从"己方半场全空 + 全员满血"开始。
        /// </summary>
        internal void ResetForNextRound()
        {
            for (int i = _slotCount - 1; i >= 0; i--)
            {
                if (_units[i].Owner == AutoChessRules.EnemyOwner || !_units[i].IsAlive)
                {
                    RemoveAt(i);
                }
            }

            for (int i = 0; i < _slotCount; i++)
            {
                _units[i].State = AutoChessUnitState.Bench;
                _units[i].X = AutoChessRules.BenchCoordinate;
                _units[i].Y = AutoChessRules.BenchCoordinate;
                _units[i].Hp = _units[i].MaxHp;
            }

            RebuildOccupancy();
            SpawnEnemies();
        }

        /// <summary>追加 3 个敌方单位到敌方半场的头三个空格（<c>y = 4</c> 起、每行 <c>x</c> 递增）。</summary>
        private void SpawnEnemies()
        {
            for (int j = 0; j < AutoChessRules.EnemyUnitCount; j++)
            {
                if (!TryFindFreeCell(AutoChessRules.EnemyRowStart, AutoChessRules.BoardHeight - 1, out int x, out int y))
                {
                    // 3 个单位对 32 格，理论上不可达；真到了这一步就少生成而不是越界。
                    return;
                }

                int slot = AddUnit(
                    AutoChessRules.EnemyUnitIdBase + j,
                    AutoChessRules.EnemyOwner,
                    AutoChessUnitState.Board,
                    x,
                    y,
                    AutoChessRules.EnemyUnitHp,
                    AutoChessRules.EnemyUnitAttack,
                    AutoChessRules.EnemyUnitRange);

                _occupancy[(y * Width) + x] = slot;
            }
        }

        /// <summary>在 [<paramref name="startRow"/>, <paramref name="endRow"/>] 内找头一个空格。</summary>
        private bool TryFindFreeCell(int startRow, int endRow, out int x, out int y)
        {
            for (int row = startRow; row <= endRow; row++)
            {
                for (int column = 0; column < Width; column++)
                {
                    if (_occupancy[(row * Width) + column] < 0)
                    {
                        x = column;
                        y = row;
                        return true;
                    }
                }
            }

            x = -1;
            y = -1;
            return false;
        }
    }
}

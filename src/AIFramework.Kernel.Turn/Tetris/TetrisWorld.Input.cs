#nullable enable
namespace AIFramework.Kernel.Turn.Tetris
{
    /// <summary>
    /// <see cref="TetrisWorld"/> 的输入端口实现（<c>ITetrisInput</c>）：左右移动、旋转踢墙、软降、硬降与按时间的自动下落。
    /// <para>
    /// <b>输入语义</b>：<see cref="TryMoveLeft"/> / <see cref="TryMoveRight"/> / <see cref="TryRotate"/> 被拒时
    /// 不改变任何状态；<see cref="TrySoftDrop"/> 返回"本次有没有真的下移一格"，<b>触底那一次返回 false 并顺手锁定</b>
    /// （这样"软降到底"和"硬降"都能结束一个方块的生命周期，不必再引入一个额外的 Lock 方法）；
    /// <see cref="HardDrop"/> 直落到底、锁死，并按 <c>下落格数 × 2</c> 加分；<see cref="Tick"/> 每攒够一个
    /// <see cref="DropIntervalMs"/> 下落一格（每次调用最多一格）。
    /// </para>
    /// <para>
    /// <b>为什么单独拆文件</b>：本文件只有"意图 → 状态变化"这一层，状态读取与结算在 <c>TetrisWorld.cs</c>；
    /// 两者都不超过 400 行，评审时可以一次读完。文件拆开但仍是同一个 <c>partial</c> 类型，
    /// 对外只有一个 <c>TetrisWorld</c>，字段不会被切成两份真值。
    /// </para>
    /// <para>
    /// <b>零分配</b>：这里所有方法只做整数/双精度运算与位运算查表（<see cref="TetrisShapes"/>），
    /// 落地时才写棋盘数组；不会申请任何托管堆内存。
    /// </para>
    /// </summary>
    public sealed partial class TetrisWorld
    {
        /// <summary>左移一格；出界或与已锁定格碰撞返回 false，位置不变。</summary>
        public bool TryMoveLeft()
        {
            if (IsFinished || CollidesAt(_currentX - 1, _currentY, _currentRotation))
            {
                return false;
            }

            _currentX--;
            return true;
        }

        /// <summary>右移一格；出界或与已锁定格碰撞返回 false，位置不变。</summary>
        public bool TryMoveRight()
        {
            if (IsFinished || CollidesAt(_currentX + 1, _currentY, _currentRotation))
            {
                return false;
            }

            _currentX++;
            return true;
        }

        /// <summary>
        /// 顺时针旋转 90°。原位放不下时依次尝试左、右各踢 1 格；两处都不行则整体回退
        /// （朝向与坐标都不变）并返回 false。
        /// </summary>
        public bool TryRotate()
        {
            if (IsFinished)
            {
                return false;
            }

            int target = (_currentRotation + 1) & 3;

            if (!CollidesAt(_currentX, _currentY, target))
            {
                _currentRotation = target;
                return true;
            }

            if (!CollidesAt(_currentX - 1, _currentY, target))
            {
                _currentRotation = target;
                _currentX--;
                return true;
            }

            if (!CollidesAt(_currentX + 1, _currentY, target))
            {
                _currentRotation = target;
                _currentX++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 下移一格。触底时锁定当前方块并返回 false（本次没有真的位移，但方块已经落地）。
        /// </summary>
        public bool TrySoftDrop()
        {
            if (IsFinished)
            {
                return false;
            }

            return DropOneRow();
        }

        /// <summary>
        /// 直落到底并锁定：不加推演步数（规则 5 的"步数"专指 <see cref="Step(float)"/>），
        /// 但每下落一格加 2 分；落地后的消行与出生流程与软降完全一致。
        /// </summary>
        public void HardDrop()
        {
            if (IsFinished)
            {
                return;
            }

            int dropped = 0;
            while (!CollidesAt(_currentX, _currentY + 1, _currentRotation))
            {
                _currentY++;
                dropped++;
            }

            _score += dropped * HardDropScorePerRow;
            LockCurrentPiece();
        }

        /// <summary>
        /// 自动下落：把 <paramref name="deltaSeconds"/> 累加到毫秒累加器，攒够 <see cref="DropIntervalMs"/>
        /// 就下移一格（触底即锁定），每次调用最多下落一格，避免一帧补算多格导致观感跳变。
        /// 非正数/NaN 的 <paramref name="deltaSeconds"/> 被忽略。
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (IsFinished || !(deltaSeconds > 0.0))
            {
                return;
            }

            _accumulatedMs += deltaSeconds * 1000.0;
            if (_accumulatedMs < DropIntervalMs)
            {
                return;
            }

            _accumulatedMs -= DropIntervalMs;
            DropOneRow();
        }

        /// <summary>
        /// 下落一格；触底则锁定并让下一块出生。返回值 = 是否真的下移（false 表示本次触底）。
        /// </summary>
        private bool DropOneRow()
        {
            if (CollidesAt(_currentX, _currentY + 1, _currentRotation))
            {
                LockCurrentPiece();
                return false;
            }

            _currentY++;
            return true;
        }
    }
}

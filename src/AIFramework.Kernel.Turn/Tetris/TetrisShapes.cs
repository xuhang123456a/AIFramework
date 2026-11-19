#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Tetris
{
    /// <summary>
    /// 七种方块的四向几何表：每种方块都定义在<b>统一 4×4 包围盒</b>内，顺时针旋转就是
    /// <c>(x, y) → (3 - y, x)</c> 的位运算，因此不需要为每种方块手写 4 份朝向数据。
    /// <para>
    /// <b>为什么统一 4×4 而不是 I 用 4×4、其余用 3×3</b>：包围盒统一后，碰撞、踢墙与旋转只有一条代码路径；
    /// 代价是 3 格宽的方旋转后会贴到包围盒右侧（视觉上"横跳"一格），本卡带不做 SRS 的精细偏移表，
    /// 改用"左/右各踢 1 格"的简化踢墙——这正是规则 4 锁定的口径。
    /// </para>
    /// <para>
    /// <b>位序</b>：第 <c>y</c> 行第 <c>x</c> 列对应 <c>1u &lt;&lt; (y * 4 + x)</c>，即行优先（左上为 <c>bit 0</c>）。
    /// 每份掩码必须恰好 4 个 1（由单元测试守着，改动本表会让"消行/旋转"用例立刻失败）。
    /// </para>
    /// <para>
    /// 本类型是纯静态查表，无状态、无分配；旋转掩码按需现算（最多 3 次 16 格位运算），
    /// 以避免静态构造期分配数组，也避免"预缓存表"与基础表两份真相。
    /// </para>
    /// </summary>
    internal static class TetrisShapes
    {
        /// <summary>包围盒边长（七种方块统一 4）。</summary>
        internal const int Box = 4;

        /// <summary>每个方块的格数。</summary>
        internal const int CellCount = 4;

        /// <summary>方块种类数（I/O/T/S/Z/J/L）。</summary>
        internal const int KindCount = 7;

        /// <summary>旋转朝向数（0/90/180/270，顺时针）。</summary>
        internal const int RotationCount = 4;

        /// <summary>I：顶行四格（0,0)(1,0)(2,0)(3,0)。</summary>
        private const uint IBaseMask = 0x000Fu;

        /// <summary>O：中间 2×2（1,1)(2,1)(1,2)(2,2)；它关于包围盒中心对称，旋转后掩码不变。</summary>
        private const uint OBaseMask = 0x0660u;

        /// <summary>T：(0,0)(1,0)(2,0) 加下方中格 (1,1)。</summary>
        private const uint TBaseMask = 0x0027u;

        /// <summary>S：上方右侧两格 (1,0)(2,0) 加下方左侧两格 (0,1)(1,1)。</summary>
        private const uint SBaseMask = 0x0036u;

        /// <summary>Z：上方左侧两格 (0,0)(1,0) 加下方右侧两格 (1,1)(2,1)。</summary>
        private const uint ZBaseMask = 0x0063u;

        /// <summary>J：左上角 (0,0) 加下方三格 (0,1)(1,1)(2,1)。</summary>
        private const uint JBaseMask = 0x0071u;

        /// <summary>L：右上角 (2,0) 加下方三格 (0,1)(1,1)(2,1)。</summary>
        private const uint LBaseMask = 0x0074u;

        /// <summary>取方块的出生朝向掩码（朝向 0）。</summary>
        internal static uint BaseMask(TetrominoKind kind)
        {
            switch (kind)
            {
                case TetrominoKind.I:
                    return IBaseMask;
                case TetrominoKind.O:
                    return OBaseMask;
                case TetrominoKind.T:
                    return TBaseMask;
                case TetrominoKind.S:
                    return SBaseMask;
                case TetrominoKind.Z:
                    return ZBaseMask;
                case TetrominoKind.J:
                    return JBaseMask;
                case TetrominoKind.L:
                    return LBaseMask;
                default:
                    return 0u;
            }
        }

        /// <summary>
        /// 取方块旋转 <paramref name="quarterTurns"/> 个 90°（顺时针）后的掩码。
        /// 入参按 4 取模，负数与超量输入都不会越界。
        /// </summary>
        internal static uint Mask(TetrominoKind kind, int quarterTurns)
        {
            int turns = quarterTurns & 3;
            uint mask = BaseMask(kind);

            for (int i = 0; i < turns; i++)
            {
                mask = RotateClockwise(mask);
            }

            return mask;
        }

        /// <summary>顺时针旋转 90°：<c>(x, y) → (3 - y, x)</c>。</summary>
        internal static uint RotateClockwise(uint mask)
        {
            uint rotated = 0u;

            for (int y = 0; y < Box; y++)
            {
                for (int x = 0; x < Box; x++)
                {
                    if ((mask & (1u << ((y << 2) | x))) == 0u)
                    {
                        continue;
                    }

                    int targetX = Box - 1 - y;
                    int targetY = x;
                    rotated |= 1u << ((targetY << 2) | targetX);
                }
            }

            return rotated;
        }

        /// <summary>包围盒内 (x, y) 是否为该方块的实心格。</summary>
        internal static bool HasCell(uint mask, int x, int y)
        {
            return (mask & (1u << ((y << 2) | x))) != 0u;
        }
    }
}

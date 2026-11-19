#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 俄罗斯方块七种方块。枚举值同时是<b>存档/回放里的稳定数值</b>（不要重排），
    /// 也决定棋盘格编码：<c>CellAt</c> 返回 <c>(int)kind + 1</c>。
    /// </summary>
    public enum TetrominoKind : byte
    {
        /// <summary>I 型：一字长条（4 格同列/同行）。</summary>
        I = 0,

        /// <summary>O 型：2×2 方块，旋转外观不变。</summary>
        O = 1,

        /// <summary>T 型。</summary>
        T = 2,

        /// <summary>S 型。</summary>
        S = 3,

        /// <summary>Z 型。</summary>
        Z = 4,

        /// <summary>J 型。</summary>
        J = 5,

        /// <summary>L 型。</summary>
        L = 6
    }

    /// <summary>
    /// 俄罗斯方块只读视图：渲染层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右 <c>0..Width-1</c>，<c>y</c> 向下 <c>0..Height-1</c>（<c>y = 0</c> 是最顶行，
    /// 出生点就在顶行）；下落方向是 <c>y + 1</c>。这与战棋/解谜的"<c>y</c> 向上"相反，是刻意按方块游戏的屏幕习惯定的。
    /// </para>
    /// <para>
    /// <b>为什么只有 <see cref="CellAt"/> 一条取数通道</b>：棋盘固定 10×20，单格查询足够画出整盘，
    /// 不必再暴露"整行快照/整盘数组"这类会诱发每帧拷贝的接口。
    /// </para>
    /// </summary>
    public interface ITetrisReadView
    {
        /// <summary>棋盘列数（固定 10）。</summary>
        int Width { get; }

        /// <summary>棋盘行数（固定 20）。</summary>
        int Height { get; }

        /// <summary>当前已锁定的方块占用的格子总数（不含正在下落的方块）。</summary>
        int FilledCellCount { get; }

        /// <summary>本局累计消除的行数。</summary>
        int ClearedLines { get; }

        /// <summary>当前分数（消行分 + 硬降分）。</summary>
        int Score { get; }

        /// <summary>当前等级（<c>1 + ClearedLines / 10</c>，从 1 开始）。</summary>
        int Level { get; }

        /// <summary>正在下落的方块类型。</summary>
        TetrominoKind CurrentKind { get; }

        /// <summary>正在下落的方块包围盒左上角列坐标（出生时为 3）。</summary>
        int CurrentX { get; }

        /// <summary>正在下落的方块包围盒左上角行坐标（出生时为 0）。</summary>
        int CurrentY { get; }

        /// <summary>下一个方块的 <see cref="TetrominoKind"/> 数值（避免再暴露一个枚举属性）。</summary>
        int NextKindRaw { get; }

        /// <summary>0 空；否则为 TetrominoKind+1（1..7）。</summary>
        /// <remarks>越界（含负数）返回 <c>-1</c>，调用方不必先判界。</remarks>
        int CellAt(int x, int y);

        /// <summary>是否已判负（新方块出生即碰撞）。结算是否已取回由 <c>IGameplaySession.IsFinished</c> 表达。</summary>
        bool IsGameOver { get; }
    }

    /// <summary>
    /// 俄罗斯方块输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>所有方法在对局结束后一律返回 false / 空操作，不抛异常。</para>
    /// </summary>
    public interface ITetrisInput
    {
        /// <summary>左移一格；出界或碰撞返回 false 且不改变位置。</summary>
        bool TryMoveLeft();

        /// <summary>右移一格；出界或碰撞返回 false 且不改变位置。</summary>
        bool TryMoveRight();

        /// <summary>
        /// 顺时针旋转 90°。旋转后越界/碰撞时依次尝试左、右各踢 1 格；
        /// 都不行则拒绝旋转（返回 false，位置与朝向都不变）。
        /// </summary>
        bool TryRotate();

        /// <summary>
        /// 下移一格。返回本次是否真的下移；<b>已经触底时立即锁定该方块并返回 false</b>。
        /// </summary>
        bool TrySoftDrop();

        /// <summary>直落到底并锁定；不加推演步数，但按下落格数加分。</summary>
        void HardDrop();

        /// <summary>自动下落：内部累计时间，累计到当前下落间隔就下移一格（触底即锁定）。</summary>
        void Tick(double deltaSeconds);
    }
}

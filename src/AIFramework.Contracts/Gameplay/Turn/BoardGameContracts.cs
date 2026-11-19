#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 黑白棋单格状态。枚举值同时是运行期棋盘数组的存储编码（<c>byte</c>，0/1/2），
    /// 渲染层直接按值切图，不要重排。
    /// </summary>
    public enum BoardCell : byte
    {
        /// <summary>空格。</summary>
        Empty = 0,

        /// <summary>黑子（本地玩家）。</summary>
        Black = 1,

        /// <summary>白子（AI）。</summary>
        White = 2
    }

    /// <summary>
    /// 棋盘 AI 难度。<see cref="Easy"/> 在合法点里随机（种子驱动）、<see cref="Normal"/> 贪心最大化翻转数、
    /// <see cref="Hard"/> 极小极大 4 层。数值是稳定协议值（存档/回放），不要重排。
    /// </summary>
    public enum BoardGameDifficulty : byte
    {
        /// <summary>随机选一个合法点（由 <c>GameplayInitData.RandomSeed</c> 驱动，同种子同序列）。</summary>
        Easy = 0,

        /// <summary>贪心：选翻转棋子最多的合法点（同分取扫描序第一个）。</summary>
        Normal = 1,

        /// <summary>极小极大 4 层（角点 +30、角邻 -12 的位置权重）。</summary>
        Hard = 2
    }

    /// <summary>
    /// 黑白棋只读视图：渲染层与 HUD 每帧只读这里，不碰内核内部棋盘数组。
    /// <para>
    /// <b>坐标约定</b>：<c>x</c> 向右 <c>0..Size-1</c>，<c>y</c> 向下 <c>0..Size-1</c>
    /// （<c>(0, 0)</c> 是左上角，与屏幕习惯一致）；标准初始 4 子是黑 <c>(3, 4)/(4, 3)</c>、
    /// 白 <c>(3, 3)/(4, 4)</c>，黑先手。
    /// </para>
    /// <para>
    /// <b>为什么只有 <see cref="GetCell"/> 一条取数通道</b>：棋盘固定 8×8，单格查询足够画出整盘；
    /// 暴露"整盘快照/数组"会诱发每帧拷贝，也会让适配层绕过内核直接改盘。
    /// </para>
    /// <para>
    /// <b>跳过是自动的</b>：当前玩家无合法落子时内核自动跳过（见 <see cref="IsPassedLastTurn"/>），
    /// 因此只要 <see cref="IsFinished"/> 为 false，<see cref="CurrentPlayer"/> 一定有棋可下。
    /// </para>
    /// </summary>
    public interface IBoardGameReadView
    {
        /// <summary>棋盘边长（固定 8，即 8×8 共 64 格）。</summary>
        int Size { get; }

        /// <summary>
        /// 取 (x, y) 的格子；越界（含负数）返回 <see cref="BoardCell.Empty"/>，调用方不必先判界。
        /// </summary>
        BoardCell GetCell(int x, int y);

        /// <summary>黑子数（随落子/翻转实时维护，不是"每个子各算一次"的估算）。</summary>
        int BlackCount { get; }

        /// <summary>白子数。</summary>
        int WhiteCount { get; }

        /// <summary>当前该谁落子：1 = 黑，2 = 白。白方回合只能由 AI 落子（见 <see cref="IBoardGameInput.PlayAiTurn"/>）。</summary>
        int CurrentPlayer { get; }

        /// <summary>本局已成功落子的手数（被拒的输入不计，跳过不计；<see cref="IBoardGameInput.Restart"/> 归零）。</summary>
        int MoveCount { get; }

        /// <summary>对局是否结束：双方都无合法落子、或结算报告已被取回。</summary>
        bool IsFinished { get; }

        /// <summary>胜方：0 = 未结束或平局，1 = 黑胜，2 = 白胜。</summary>
        int Winner { get; }

        /// <summary>
        /// 最近一次换手是否发生了跳过：为 true 表示上一位该落子的一方无棋可下、回合被跳过
        /// （单方跳过与"双方都无子可落"的终局跳过都为 true）。正常落子换手后立即变回 false。
        /// </summary>
        bool IsPassedLastTurn { get; }

        /// <summary>最近一手落子的列；尚无落子（或刚重开）为 <c>-1</c>。</summary>
        int LastMoveX { get; }

        /// <summary>最近一手落子的行；尚无落子（或刚重开）为 <c>-1</c>。</summary>
        int LastMoveY { get; }
    }

    /// <summary>
    /// 黑白棋输入端口：适配层只能通过它提交意图，不能直接改棋盘。
    /// <para>对局结束后（含结算已取回）所有方法都是拒绝 / 空操作，不抛异常。</para>
    /// </summary>
    public interface IBoardGameInput
    {
        /// <summary>
        /// 当前玩家（仅黑方，即本地玩家）在 (x, y) 落子：越界、已有子、夹不住任何对方棋子都返回 false，
        /// 且<b>不换手、不改任何计数</b>。白方回合调用一律返回 false（白方必须走 <see cref="PlayAiTurn"/>）。
        /// </summary>
        bool TryPlace(int x, int y);

        /// <summary>
        /// 白方 AI 落子：只在 <see cref="IBoardGameReadView.CurrentPlayer"/> 为白时生效，黑方回合是空操作。
        /// 难度值大于 <see cref="BoardGameDifficulty.Hard"/> 时按 <see cref="BoardGameDifficulty.Normal"/> 处理。
        /// </summary>
        void PlayAiTurn(BoardGameDifficulty difficulty);

        /// <summary>
        /// 重开一局：棋盘回到初始 4 子、黑先手，<c>ProcessedSteps / PlayerMoves / AiMoves</c> 全部清零，
        /// 但 <c>PassCount</c> 作为会话级累计<b>保留</b>。
        /// </summary>
        void Restart();
    }
}

#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 黑白棋的 uGUI 皮肤：状态行（黑白子数 / 当前玩家 / 最近落子 / 胜负）+ 8×8 字符盘 +
    /// 8×8 格子按钮 + 一个"重新开始"按钮。
    /// <para>
    /// <b>为什么盘面用字符而不是 64 张棋子贴图</b>：一个 <see cref="Text"/> 就能表达整盘棋，
    /// 零池化、零 <c>Instantiate</c>；字符视图也正好让无头环境可以断言（同一份视图能打日志）。
    /// 格子按钮是<b>另一条通道</b>：它只负责"把手指点位翻译成 (x, y)"，顺带把同一个字符刷到按钮标题上，
    /// 让点得到的地方和看得到的地方是同一套坐标。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：合法性、翻转、自动跳过、胜负全部由内核判定；HUD 只负责
    /// "把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>坐标与行序</b>：内核的 <c>y</c> 向下（<c>(0,0)</c> 是左上角，见
    /// <see cref="IBoardGameReadView"/>），但字符盘按棋谱习惯<b>把 y = Size-1 画在第一行</b>
    /// （与 Tactics / 白棋的"底线在下"一致）。因此：
    /// <list type="bullet">
    /// <item>字符盘第 <c>row</c> 行画的是 <c>y = Size-1-row</c>；</item>
    /// <item><c>cellButtons</c> 的下标同样是 <c>row * Size + x</c>（行优先注入），
    /// 于是"最上面一排按钮"和"字符盘第一行"是同一批格子；</item>
    /// <item>点击回调再把 <c>row</c> 翻回内核的 <c>y</c>（见 <see cref="RaiseCellClicked"/>）。</item>
    /// </list>
    /// 两侧只要有一处漏了这次翻转，玩家点"看起来是角"的格子就会落到对角的另一头。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行、字符盘、64 个按钮标题都先比后写。对局在 <c>0.5</c> 秒一拍的节奏下推进，
    /// 稳态（等 AI 想棋时）不会触发任何字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoardGameHud : MonoBehaviour
    {
        /// <summary>棋盘边长（内核固定 8，与 <c>BoardGameRules.Size</c> 的约定一致）。</summary>
        private const int BoardSize = 8;

        /// <summary>格子总数（8×8）。</summary>
        private const int CellCount = BoardSize * BoardSize;

        /// <summary>字符盘一行的步长（8 格 + 1 个换行）。</summary>
        private const int RowStride = BoardSize + 1;

        /// <summary>字符盘缓冲长度：8 行 × 9，去掉最后一个换行。</summary>
        private const int BoardLength = (BoardSize * RowStride) - 1;

        /// <summary>黑子字符。</summary>
        private const char BlackChar = '●';

        /// <summary>白子字符。</summary>
        private const char WhiteChar = '○';

        /// <summary>空格字符。</summary>
        private const char EmptyChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点了某个格子；参数是内核口径的 (x, y)，<b>已经翻过行序</b>。</summary>
        public event Action<int, int>? OnCellClicked;

        /// <summary>状态行：第一行黑白子数与手数，第二行当前玩家 / 最近落子 / 胜负（结算后改写为结果文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符盘：8 行 × 8 列（● 黑 / ○ 白 / · 空），第一行是 <c>y = Size-1</c>。</summary>
        [SerializeField] private Text boardText = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>
        /// 64 个格子按钮，<b>行优先注入</b>：下标 = <c>显示行 * Size + x</c>，显示行 0 是字符盘第一行
        /// （<c>y = Size-1</c>）。未注入（空数组）时本类只刷字符盘，不崩也不报错。
        /// </summary>
        [SerializeField] private Button[] cellButtons = new Button[0];

        /// <summary>复用的字符盘缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请数组。</summary>
        private readonly char[] _board = new char[BoardLength];

        /// <summary>64 个按钮当前显示的字符（脏检查基准；初值 <c>'\0'</c> 保证首帧必然写一次）。</summary>
        private readonly char[] _cellChars = new char[CellCount];

        /// <summary>64 个按钮的标题控件（按钮本身没有文字，标题在子物体上）。</summary>
        private readonly Text?[] _cellLabels = new Text?[CellCount];

        /// <summary>
        /// 64 个按钮各自的点击监听器。<b>为什么要把委托存起来</b>：<c>UnityEvent.RemoveListener</c>
        /// 按"目标 + 方法"匹配，每次现造一个 lambda 会退订不掉，场景重载后一次点击触发两次。
        /// </summary>
        private readonly UnityEngine.Events.UnityAction?[] _cellHandlers = new UnityEngine.Events.UnityAction?[CellCount];

        /// <summary>复用的状态行缓冲：两行文本 + 数字，容量够用到底。</summary>
        private readonly StringBuilder _status = new StringBuilder(256);

        /// <summary>已写到控件上的字符盘（脏检查基准）。</summary>
        private string _lastBoard = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>格子按钮是否已解析并订阅（<c>Awake</c> 与首帧 <c>Render</c> 都可能先到，用标志兜住）。</summary>
        private bool _cellsBound;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后控制器仍可能继续推视图，
        /// 若每次都重写状态行，"对局结束 · …"会立刻被下一帧的实时计数盖掉。
        /// </summary>
        private bool _resultShown;

        /// <summary>
        /// 订阅按钮。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：控制器在自己的 <c>Awake</c> 里
        /// 就会建会话并首帧 <see cref="Render"/>，这里必须在此之前把标题解析出来；
        /// 两者都是 <c>Awake</c> 时谁先跑都不影响结果（<see cref="EnsureCells"/> 是幂等的）。
        /// </summary>
        private void Awake()
        {
            EnsureCells();

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Awake"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }

            if (cellButtons == null)
            {
                return;
            }

            int count = cellButtons.Length < CellCount ? cellButtons.Length : CellCount;
            for (int i = 0; i < count; i++)
            {
                Button? button = cellButtons[i];
                UnityEngine.Events.UnityAction? handler = _cellHandlers[i];
                if (button != null && handler != null)
                {
                    button.onClick.RemoveListener(handler);
                }
            }
        }

        /// <summary>
        /// 按只读视图刷新状态行、字符盘与格子按钮标题。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IBoardGameReadView view)
        {
            if (view == null)
            {
                return;
            }

            // 重开之后视图回到"未结束"：内部状态跟着复位，结果文案不再霸占状态行。
            if (_resultShown && !view.IsFinished)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
                _lastBoard = string.Empty;
            }

            EnsureCells();
            UpdateBoard(view);

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view));
            }
        }

        /// <summary>
        /// 显示结果文案（胜方 + 双方子数）。<b>只改状态行、不动字符盘</b>：
        /// 终局画面仍要看到最终盘面，否则玩家分不清"下完了"与"界面停了"。
        /// </summary>
        /// <param name="winner">胜方：0 平局、1 黑胜、2 白胜。</param>
        /// <param name="blackCount">黑子数。</param>
        /// <param name="whiteCount">白子数。</param>
        public void ShowResult(int winner, int blackCount, int whiteCount)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append("对局结束 · ").Append(WinnerLabel(winner))
                .Append(" · 黑 ").Append(blackCount)
                .Append(" · 白 ").Append(whiteCount)
                .Append(RowSeparator)
                .Append("点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 8×8 字符盘，并把同一批字符刷到 64 个按钮标题上。
        /// <para>
        /// <b>行序 Size-1 → 0</b>：缓冲第 0 行画 <c>y = Size-1</c>，最后一行画 <c>y = 0</c>。
        /// 内核的 <c>y</c> 是向下的，但棋类演示统一按"底线在下"的棋谱习惯显示
        /// （与 Tactics / 白棋一致）；按钮网格用同一套行序，点哪里和看哪里才不会错位。
        /// </para>
        /// <para>
        /// <b>为什么先铺满空位</b>：视图 <see cref="IBoardGameReadView.Size"/> 若小于 8（正常不会），
        /// 越过的行保持 <c>·</c> 而不是上一帧的残影；<see cref="IBoardGameReadView.GetCell"/> 本身
        /// 越界即返回 <see cref="BoardCell.Empty"/>，不必先判界。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateBoard(IBoardGameReadView view)
        {
            for (int i = 0; i < _board.Length; i++)
            {
                _board[i] = EmptyChar;
            }

            for (int row = 1; row < BoardSize; row++)
            {
                _board[(row * RowStride) - 1] = RowSeparator;
            }

            int size = view.Size;
            if (size > BoardSize)
            {
                size = BoardSize;
            }

            for (int row = 0; row < BoardSize; row++)
            {
                int y = (BoardSize - 1) - row;
                if (y >= size)
                {
                    continue;
                }

                for (int x = 0; x < size; x++)
                {
                    char glyph = GlyphOf(view.GetCell(x, y));
                    _board[(row * RowStride) + x] = glyph;
                    SetCellGlyph((row * BoardSize) + x, glyph);
                }
            }

            string board = new string(_board);
            if (board != _lastBoard)
            {
                _lastBoard = board;
                if (boardText != null)
                {
                    boardText.text = board;
                }
            }
        }

        /// <summary>把一个格子的字符写到对应按钮标题上（未变化就不碰控件）。</summary>
        /// <param name="index">按钮下标（<c>显示行 * Size + x</c>）。</param>
        /// <param name="glyph">要显示的字符。</param>
        private void SetCellGlyph(int index, char glyph)
        {
            if (index < 0 || index >= CellCount)
            {
                return;
            }

            if (_cellChars[index] == glyph)
            {
                return;
            }

            _cellChars[index] = glyph;

            Text? label = _cellLabels[index];
            if (label != null)
            {
                label.text = glyph.ToString();
            }
        }

        /// <summary>
        /// 解析 64 个按钮的标题并订阅点击（幂等：只有第一次真正执行）。
        /// <para>
        /// <b>为什么标题要走 <c>GetComponentInChildren</c></b>：同一物体上只能有一个 <c>Graphic</c>，
        /// 按钮本体是 <see cref="Image"/>，文字必然在子物体上；建场景脚本按这个结构建，
        /// 标题缺失时这里只是拿到 <c>null</c>，点击链路照旧工作。
        /// </para>
        /// </summary>
        private void EnsureCells()
        {
            if (_cellsBound)
            {
                return;
            }

            _cellsBound = true;

            if (cellButtons == null)
            {
                return;
            }

            int count = cellButtons.Length < CellCount ? cellButtons.Length : CellCount;
            for (int i = 0; i < count; i++)
            {
                Button? button = cellButtons[i];
                if (button == null)
                {
                    continue;
                }

                int index = i;
                UnityEngine.Events.UnityAction handler = () => RaiseCellClicked(index);

                _cellHandlers[i] = handler;
                button.onClick.AddListener(handler);
                _cellLabels[i] = button.GetComponentInChildren<Text>();
            }
        }

        /// <summary>
        /// 拼状态行：第一行黑白子数与已下手数，第二行当前玩家 / 最近落子 / 胜负。
        /// <b>为什么分两行</b>：一行塞下五段读数会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的往往正是"最近落子"这种最需要确认的读数。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>两行状态文本。</returns>
        private string BuildStatus(IBoardGameReadView view)
        {
            _status.Clear();
            _status.Append("黑 ").Append(view.BlackCount)
                .Append(" · 白 ").Append(view.WhiteCount)
                .Append(" · 已下 ").Append(view.MoveCount).Append(" 手")
                .Append(RowSeparator)
                .Append(TurnText(view))
                .Append(" · 最近落子 ").Append(LastMoveText(view))
                .Append(" · ").Append(ResultText(view));

            if (view.IsPassedLastTurn)
            {
                _status.Append(" · 上一手跳过");
            }

            return _status.ToString();
        }

        /// <summary>写状态行（文本未变就不碰控件，避免重复重建字形）。</summary>
        /// <param name="line">要显示的两行文本。</param>
        private void SetStatus(string line)
        {
            if (line == _lastStatus)
            {
                return;
            }

            _lastStatus = line;
            if (statusText != null)
            {
                statusText.text = line;
            }
        }

        /// <summary>当前该谁落子的说明（已终局时不再说"轮到谁"，避免同一行自相矛盾）。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>一句短文案。</returns>
        private static string TurnText(IBoardGameReadView view)
        {
            if (view.IsFinished)
            {
                return "对局结束";
            }

            return view.CurrentPlayer == (int)BoardCell.Black ? "轮到 黑方（你）" : "轮到 白方（AI）";
        }

        /// <summary>最近一手的坐标文案；尚无落子（含刚重开）时为"无"。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>(3, 4)</c> 的文本。</returns>
        private static string LastMoveText(IBoardGameReadView view)
        {
            if (view.LastMoveX < 0 || view.LastMoveY < 0)
            {
                return "无";
            }

            return "(" + view.LastMoveX + ", " + view.LastMoveY + ")";
        }

        /// <summary>胜负口径的文案：未结束报"进行中"，结束按 <see cref="IBoardGameReadView.Winner"/> 报黑白或平局。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>一句短文案。</returns>
        private static string ResultText(IBoardGameReadView view)
        {
            return view.IsFinished ? WinnerLabel(view.Winner) : "进行中";
        }

        /// <summary>胜方值的中文名（只用于展示，不参与任何运算）。</summary>
        /// <param name="winner">0 平局 / 1 黑胜 / 2 白胜。</param>
        /// <returns>中文标签。</returns>
        private static string WinnerLabel(int winner)
        {
            if (winner == (int)BoardCell.Black)
            {
                return "黑胜（你赢了）";
            }

            return winner == (int)BoardCell.White ? "白胜（AI 获胜）" : "平局";
        }

        /// <summary>格子状态到显示字符的映射（唯一的一处换算）。</summary>
        /// <param name="cell">格子状态。</param>
        /// <returns><c>●</c> / <c>○</c> / <c>·</c>。</returns>
        private static char GlyphOf(BoardCell cell)
        {
            switch (cell)
            {
                case BoardCell.Black:
                    return BlackChar;
                case BoardCell.White:
                    return WhiteChar;
                default:
                    return EmptyChar;
            }
        }

        /// <summary>转发"重新开始"。</summary>
        private void RaiseRestartClicked()
        {
            Action? handler = OnRestartClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>
        /// 把按钮下标抬成 <see cref="OnCellClicked"/> 事件，并<b>把显示行翻回内核的 y</b>：
        /// 显示行 0 是字符盘第一行 = <c>y = Size-1</c>，故 <c>y = (Size-1) - index / Size</c>。
        /// </summary>
        /// <param name="index">按钮下标（<c>显示行 * Size + x</c>）。</param>
        private void RaiseCellClicked(int index)
        {
            Action<int, int>? handler = OnCellClicked;
            if (handler == null)
            {
                return;
            }

            handler(index % BoardSize, (BoardSize - 1) - (index / BoardSize));
        }
    }
}

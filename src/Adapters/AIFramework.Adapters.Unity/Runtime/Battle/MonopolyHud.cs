#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 大富翁的 uGUI 皮肤：状态行（回合 / 当前玩家 / 现金 / 骰子 / 最终落点）+ 4 行 × 5 列字符盘 +
    /// 20 个格子按钮（点击 = 掷骰）+ "掷骰子 / 重新开始"两个按钮。
    /// <para>
    /// <b>为什么盘面用字符而不是 20 张贴图 / 20 个棋子物体</b>：一个 <see cref="Text"/> 就能表达整个棋盘，
    /// 零池化、零 <c>Instantiate</c>，同一份视图也能被无头环境断言（真机之外还能打日志核对）。
    /// 按钮是<b>另一条通道</b>：它只负责"把手指点位翻译成一次掷骰"，并把同一批字符刷到按钮标题上，
    /// 让点得到的地方和看得到的地方是同一套渲染。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：移动、买地、租金、税、机会卡、破产与胜负全部由内核
    /// （<c>MonopolyWorld</c>）决定；HUD 只做两件事——"把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。
    /// 这条边界一旦破了，UI 就会变成第二套规则实现，改内核而不改 UI 时两侧会静默分叉。
    /// </para>
    /// <para>
    /// <b>字符盘约定（唯一口径，改动请同步本注释与 <c>MonopolySceneBuilder</c>）</b>：
    /// <list type="number">
    /// <item>20 格排成 <b>4 行 × 5 列</b>，索引顺序 <b>行优先</b> 0→19（第 0 行 = 索引 0..4，
    /// 第 3 行 = 索引 15..19）。按钮网格用同一顺序注入，因此"第几行第几列"在字符盘与按钮上完全一致
    /// （与黑白棋不同，这里<b>不需要</b>行序翻转：大富翁的索引本身就是从左到右、从上到下的环形顺序）。</item>
    /// <item>每格占 <b>2 个字符</b>：第 1 个是<b>类型字符</b>，第 2 个是<b>标记位</b>（默认空格，
    /// 玩家 0 站位格写 <c>*</c>）。每格宽度固定，标记位不会让后面的列左右抖动。</item>
    /// <item>类型字符：<c>GO = ▣</c>；<c>Property</c> 无主 = <c>·</c>；<c>Property</c> 有主 = 等级数字
    /// <c>0/1/2/3</c>（<c>0</c> = 刚买入还没升级）；<c>Tax = T</c>；<c>Chance = ?</c>；
    /// <c>Jail = J</c>；<c>Bonus = ★</c>。</item>
    /// <item><b>为什么"有主"用等级数字而不是 <c>·</c></b>：只读视图里无主地产与"刚买入"的等级<b>都是 0</b>
    /// （契约：<see cref="MonopolyTileView.Level"/> 的 0 既表示无主也表示刚买入），唯一能区分两者的是
    /// <see cref="MonopolyTileView.Owner"/>。若把刚买入也画成 <c>·</c>，玩家买下地后盘面毫无反馈；
    /// 因此这里把"有主"渲染成等级数字，等级 0 就显示 <c>0</c>——<b>看得到数字 = 这块地已经有主</b>。
    /// 哪个玩家持有由状态行的"地产"统计给出（单字符格塞不下"所有者 + 等级"两个信息）。</item>
    /// <item><b>当前玩家棋子用 <c>@</c> 覆盖类型字符</b>（二选一里选了"覆盖"而不是"后缀 <c>*</c>"）：
    /// 覆盖后仍是一格两字符，列不会因为多了个后缀而整体右移；代价是该格的类型符号被遮住，
    /// 所以状态行另外用文字给出"最近落点 + 该格是什么"。<c>@</c> 落点 = 内核
    /// <see cref="IMonopolyReadView.CurrentPlayer"/>，即"接下来要掷骰的存活玩家"（掷骰是同步结算的，
    /// 掷完立刻换手，所以它不会停在"刚刚行动过的人"身上）。</item>
    /// <item>玩家 0（本地玩家）站位由 <c>Render</c> 的 <paramref name="selectedTile"/> 参数指定，
    /// 表现为标记位 <c>*</c> + 该格按钮底色变高亮（一个 <see cref="Text"/> 不能做局部着色，
    /// 字符盘靠标记位、按钮靠底色，两条通道指向同一格）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行、字符盘、20 个按钮标题、按钮底色都先比后写。自动演示 0.6 秒一拍，
    /// 稳态（等下一拍、或终局后静止）不会触发任何字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonopolyHud : MonoBehaviour
    {
        /// <summary>格子总数（契约固定 20，环形成环）。</summary>
        private const int TileCount = 20;

        /// <summary>字符盘行数（20 格 = 4 行 × 5 列）。</summary>
        private const int BoardRows = 4;

        /// <summary>字符盘列数。</summary>
        private const int BoardColumns = 5;

        /// <summary>每格字符宽度：类型字符 + 标记位。</summary>
        private const int CellWidth = 2;

        /// <summary>字符盘一行的步长（5 格 × 2 字符 + 1 个换行）。</summary>
        private const int RowStride = (BoardColumns * CellWidth) + 1;

        /// <summary>字符盘缓冲长度：4 行 × 11，去掉最后一个换行。</summary>
        private const int BoardLength = (BoardRows * RowStride) - 1;

        /// <summary>
        /// 名义回合上限（与内核 <c>MonopolyRules.MaxRounds</c> 的 40 一致，<b>只用于显示裁剪</b>，
        /// 不参与任何判定）。见 <see cref="RoundProgressText"/> 里关于"封顶瞬间 41"的口径说明。
        /// </summary>
        private const int MaxRoundNumber = 40;

        /// <summary>填充字符（未使用的格子位 / 标记位留白）。</summary>
        private const char BlankChar = ' ';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>起点格字符。</summary>
        private const char GoChar = '▣';

        /// <summary>无主地产字符（契约保证无主地产等级恒为 0）。</summary>
        private const char PropertyEmptyChar = '·';

        /// <summary>税务格字符。</summary>
        private const char TaxChar = 'T';

        /// <summary>机会格字符。</summary>
        private const char ChanceChar = '?';

        /// <summary>监狱格字符。</summary>
        private const char JailChar = 'J';

        /// <summary>奖励格字符。</summary>
        private const char BonusChar = '★';

        /// <summary>当前玩家棋子（覆盖该格类型字符）。</summary>
        private const char CurrentPieceChar = '@';

        /// <summary>玩家 0 站位格的标记位字符。</summary>
        private const char SelectedMarkChar = '*';

        /// <summary>玩家 0 站位格的高亮底色（金）；其余格子恢复注入时的底色。</summary>
        private static readonly Color SelectedColor = new Color(0.95f, 0.78f, 0.25f, 1f);

        /// <summary>点按钮或点任意格子：都表示"掷骰/推进一步"。</summary>
        public event Action? OnRollClicked;

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>状态行：4 行（回合+当前玩家+骰子 / 现金 / 最近落点 / 你的位置+地产统计）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符盘：4 行 × 5 列（每格 2 字符），约定见类型注释。</summary>
        [SerializeField] private Text boardText = null!;

        /// <summary>"掷骰子"按钮（等价于内核 <c>IMonopolyInput.TryRoll</c> 的一次提交）。</summary>
        [SerializeField] private Button rollButton = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>
        /// 20 个格子按钮，<b>行优先注入</b>：下标 = <c>行 * 5 + 列</c>，与字符盘索引一一对应。
        /// 未注入（空数组）时本类只刷字符盘，不崩也不报错。
        /// </summary>
        [SerializeField] private Button[] tileButtons = new Button[0];

        /// <summary>复用的字符盘缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请数组。</summary>
        private readonly char[] _board = new char[BoardLength];

        /// <summary>20 个按钮当前显示的字符（脏检查基准；初值 <c>'\0'</c> 保证首帧必然写一次）。</summary>
        private readonly char[] _tileChars = new char[TileCount];

        /// <summary>20 个按钮的标题控件（按钮本身没有文字，标题在子物体上）。</summary>
        private readonly Text?[] _tileLabels = new Text?[TileCount];

        /// <summary>20 个按钮的底图（高亮时改它的颜色）。</summary>
        private readonly Image?[] _tileImages = new Image?[TileCount];

        /// <summary>20 个按钮注入时的原始底色（取消高亮时恢复它，避免把主题色写死在 HUD 里）。</summary>
        private readonly Color[] _tileBaseColors = new Color[TileCount];

        /// <summary>20 个按钮各自的点击监听器。<b>为什么存起来</b>：<c>RemoveListener</c> 按"目标 + 方法"
        /// 匹配，每次现造一个 lambda 会退订不掉，场景重载后一次点击触发两次。</summary>
        private readonly UnityEngine.Events.UnityAction?[] _tileHandlers = new UnityEngine.Events.UnityAction?[TileCount];

        /// <summary>复用的状态行缓冲：4 行文本 + 数字，容量够用到底。</summary>
        private readonly StringBuilder _status = new StringBuilder(256);

        /// <summary>已写到控件上的字符盘（脏检查基准）。</summary>
        private string _lastBoard = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>按钮是否已解析并订阅（<c>Awake</c> 与首帧 <c>Render</c> 都可能先到，用标志兜住）。</summary>
        private bool _tilesBound;

        /// <summary>当前被高亮的格子（-1 = 还没渲染过；只用于避免每帧重设 20 个底色）。</summary>
        private int _highlightedTile = -1;

        /// <summary>掷骰按钮当前的可点状态（脏检查基准）。</summary>
        private bool _rollInteractable = true;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后控制器仍可能每帧推视图，
        /// 若每次都重写状态行，"对局结束 · …"会立刻被下一帧的实时读数盖掉。
        /// </summary>
        private bool _resultShown;

        /// <summary>最近一次渲染的总手数（结算文案要用；<see cref="ShowResult"/> 的签名里没有它）。</summary>
        private int _lastTurnIndex;

        /// <summary>最近一次渲染的显示回合数（已按 <see cref="MaxRoundNumber"/> 裁剪）。</summary>
        private int _lastRoundNumber;

        /// <summary>最近一次渲染的现金行（结算文案直接用，不重复扫玩家）。</summary>
        private string _lastCashLine = string.Empty;

        /// <summary>
        /// 订阅按钮。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：控制器在自己的 <c>Awake</c> 里
        /// 就会建会话并首帧 <see cref="Render"/>，这里必须在此之前把标题解析出来；
        /// 两者都是 <c>Awake</c> 时谁先跑都不影响结果（<see cref="EnsureTiles"/> 是幂等的）。
        /// </summary>
        private void Awake()
        {
            EnsureTiles();

            if (rollButton != null)
            {
                rollButton.onClick.AddListener(RaiseRollClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Awake"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (rollButton != null)
            {
                rollButton.onClick.RemoveListener(RaiseRollClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }

            if (tileButtons == null)
            {
                return;
            }

            int count = tileButtons.Length < TileCount ? tileButtons.Length : TileCount;
            for (int i = 0; i < count; i++)
            {
                Button? button = tileButtons[i];
                UnityEngine.Events.UnityAction? handler = _tileHandlers[i];
                if (button != null && handler != null)
                {
                    button.onClick.RemoveListener(handler);
                }
            }
        }

        /// <summary>
        /// 按只读视图刷新状态行、字符盘、按钮标题与高亮。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        /// <param name="selectedTile">要高亮的格子索引（控制器传玩家 0 的站位；越界会被忽略）。</param>
        public void Render(IMonopolyReadView view, int selectedTile)
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
                _highlightedTile = -1;
            }

            EnsureTiles();
            UpdateBoard(view, selectedTile);
            UpdateHighlight(selectedTile);
            SetRollInteractable(!view.IsFinished);

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view));
            }
        }

        /// <summary>
        /// 显示结果文案（胜负 + 胜者 + 收口回合 + 现金）。<b>只改状态行、不动字符盘</b>：
        /// 终局画面仍要看到最终盘面，否则玩家分不清"打完了"与"界面停了"。
        /// <para>
        /// <b>调用顺序</b>：必须在本局的最后一次 <see cref="Render"/> 之后调用——收口回合与现金读数
        /// 是 <see cref="Render"/> 时存下来的快照（本方法的签名只带胜负与胜者，拿不到视图）。
        /// 控制器在 <c>Roll → Render → 结算</c> 这条路径上调用，顺序天然满足。
        /// </para>
        /// </summary>
        /// <param name="isVictory">玩家 0（本地玩家）是否获胜（= <c>GameplayResult.IsVictory</c>）。</param>
        /// <param name="winnerPlayer">胜者玩家索引（0 基）；<c>-1</c> = 无人获胜。</param>
        public void ShowResult(bool isVictory, int winnerPlayer)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append("对局结束 · ")
                .Append(isVictory ? "你赢了（P1 获胜）" : WinnerText(winnerPlayer))
                .Append(RowSeparator)
                .Append("第 ").Append(_lastRoundNumber).Append(" 回合收口 · 共 ").Append(_lastTurnIndex).Append(" 手")
                .Append(RowSeparator);

            if (_lastCashLine.Length > 0)
            {
                _status.Append(_lastCashLine).Append(RowSeparator);
            }

            _status.Append("点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 4×5 字符盘，并把同一批字符刷到 20 个按钮标题上。
        /// <para>
        /// <b>行优先</b>：缓冲第 0 行画索引 0..4，最后一行画 15..19；按钮网格用同一顺序，
        /// 所以点哪里和看哪里不会错位（这里没有黑白棋那种"底线在下"的翻转）。
        /// </para>
        /// <para>
        /// <b>为什么先铺满空白</b>：视图 <see cref="IMonopolyReadView.TileCount"/> 若小于 20（正常不会），
        /// 越过的格子保持空白而不是上一帧的残影；<see cref="IMonopolyReadView.GetTile"/> 越界会抛异常，
        /// 因此这里严格按 <c>TileCount</c> 与 20 的较小值循环，不做越界探测。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="selectedTile">要高亮的格子索引（未知时传负数）。</param>
        private void UpdateBoard(IMonopolyReadView view, int selectedTile)
        {
            for (int i = 0; i < _board.Length; i++)
            {
                _board[i] = BlankChar;
            }

            for (int row = 1; row < BoardRows; row++)
            {
                _board[(row * RowStride) - 1] = RowSeparator;
            }

            int tileCount = view.TileCount;
            if (tileCount > TileCount)
            {
                tileCount = TileCount;
            }

            int currentPlayer = view.CurrentPlayer;

            for (int tile = 0; tile < tileCount; tile++)
            {
                char glyph = GlyphOf(view.GetTile(tile));

                // 当前玩家棋子覆盖类型字符（见类型注释：覆盖而不是后缀，保证每格仍是 2 字符）。
                if (tile == currentPlayer)
                {
                    glyph = CurrentPieceChar;
                }

                int offset = CellOffset(tile);
                _board[offset] = glyph;
                _board[offset + 1] = tile == selectedTile ? SelectedMarkChar : BlankChar;

                SetTileGlyph(tile, glyph);
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

        /// <summary>格索引 → 字符盘缓冲偏移（行优先，每格 2 字符）。</summary>
        /// <param name="tile">格索引（0..19）。</param>
        /// <returns>缓冲下标。</returns>
        private static int CellOffset(int tile)
        {
            return ((tile / BoardColumns) * RowStride) + ((tile % BoardColumns) * CellWidth);
        }

        /// <summary>把一个格子的字符写到对应按钮标题上（未变化就不碰控件）。</summary>
        /// <param name="tile">格索引（0..19）。</param>
        /// <param name="glyph">要显示的字符。</param>
        private void SetTileGlyph(int tile, char glyph)
        {
            if (tile < 0 || tile >= TileCount)
            {
                return;
            }

            if (_tileChars[tile] == glyph)
            {
                return;
            }

            _tileChars[tile] = glyph;

            Text? label = _tileLabels[tile];
            if (label != null)
            {
                label.text = glyph.ToString();
            }
        }

        /// <summary>
        /// 高亮玩家 0 站位格：被选中的格子底色改金、其余恢复注入时的原底色。
        /// <para>
        /// <b>为什么整轮重刷而不是只改两格</b>：只改"上一格 + 新一格"需要维护更多状态，而这 20 次写颜色
        /// 只发生在选中格变化时（掷一次骰一次），稳态一帧也不写。
        /// </para>
        /// </summary>
        /// <param name="selectedTile">要高亮的格子索引（负值或越界表示不高亮）。</param>
        private void UpdateHighlight(int selectedTile)
        {
            if (selectedTile < 0 || selectedTile >= TileCount)
            {
                selectedTile = -1;
            }

            if (selectedTile == _highlightedTile)
            {
                return;
            }

            _highlightedTile = selectedTile;

            for (int tile = 0; tile < TileCount; tile++)
            {
                Image? image = _tileImages[tile];
                if (image == null)
                {
                    continue;
                }

                image.color = tile == selectedTile ? SelectedColor : _tileBaseColors[tile];
            }
        }

        /// <summary>按对局状态切换掷骰按钮的可点性（终局后点它没有意义，但按钮留在原地便于对照结果）。</summary>
        /// <param name="interactable">是否可点。</param>
        private void SetRollInteractable(bool interactable)
        {
            if (interactable == _rollInteractable)
            {
                return;
            }

            _rollInteractable = interactable;
            if (rollButton != null)
            {
                rollButton.interactable = interactable;
            }
        }

        /// <summary>
        /// 拼状态行（4 行）。<b>为什么分四行</b>：一行塞下"回合 / 当前玩家 / 现金 / 骰子 / 落点"会在
        /// 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，而截断的往往正是玩家最想确认的落点与现金。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>四行状态文本。</returns>
        private string BuildStatus(IMonopolyReadView view)
        {
            _lastTurnIndex = view.TurnIndex;
            _lastRoundNumber = DisplayRound(view);
            _lastCashLine = BuildCashLine(view);

            _status.Clear();

            // 第 1 行：回合进度 / 当前该谁掷骰 / 最近一次骰子点数。
            _status.Append("回合 ").Append(RoundProgressText(view))
                .Append(" · ");
            if (view.IsFinished)
            {
                _status.Append("对局已结束");
            }
            else
            {
                _status.Append("轮到 ").Append(PlayerLabel(view.CurrentPlayer));
            }

            _status.Append(" · 骰子 ").Append(DiceText(view));
            _status.Append(RowSeparator);

            // 第 2 行：各玩家现金（破产位标出局——契约保证破产后现金恒为 0）。
            _status.Append(_lastCashLine);
            _status.Append(RowSeparator);

            // 第 3 行：最近一手的最终落点（口径见 LastMoveText）。
            _status.Append("最近：").Append(LastMoveText(view));
            _status.Append(RowSeparator);

            // 第 4 行：玩家 0 的站位（同时就是高亮格）+ 地产统计 + 机会次数。
            _status.Append("你的棋子在第 ").Append(view.GetPlayer(0).Position).Append(" 格（高亮）")
                .Append(" · 地产 ").Append(OwnedText(view));
            if (view.ChanceEvents > 0)
            {
                _status.Append(" · 机会 ").Append(view.ChanceEvents).Append(" 张");
            }

            return _status.ToString();
        }

        /// <summary>写状态行（文本未变就不碰控件，避免重复重建字形）。</summary>
        /// <param name="line">要显示的四行文本。</param>
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

        /// <summary>内核原始回合数按 40 裁剪后的显示值（口径见 <see cref="RoundProgressText"/>）。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>1..40 的显示回合数。</returns>
        private static int DisplayRound(IMonopolyReadView view)
        {
            int raw = view.RoundNumber;
            if (raw < 1)
            {
                return 1;
            }

            return raw > MaxRoundNumber ? MaxRoundNumber : raw;
        }

        /// <summary>
        /// 回合进度文本：<c>显示回合/40</c>，并在封顶时补"（已封顶）"。
        /// <para>
        /// <b>"封顶瞬间是 41"的口径（必须写清，否则 HUD 会显示"41/40"）</b>：
        /// <see cref="IMonopolyReadView.RoundNumber"/> 的定义是 <c>TurnIndex / PlayerCount + 1</c>（1 基）。
        /// 内核在 <c>TurnIndex == 40 × PlayerCount</c> 时收口，此刻公式恰好算出 <b>41</b>——
        /// 它是"40 个完整回合之后正要进入的第 41 个回合"，对局在同一拍就结束了。
        /// 因此 HUD 一律显示 <c>min(RoundNumber, 40)</c>，并在 <c>RoundNumber &gt; 40</c> 时标"（已封顶）"；
        /// 这与内核测试 <c>MaxRounds_ReportsFortyNominalRounds</c> 断言的 41 并不矛盾——
        /// 那一行锁的是<b>公式读数</b>，这里锁的是<b>给人看的进度</b>。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>12/40</c> 或 <c>40/40（已封顶）</c>。</returns>
        private static string RoundProgressText(IMonopolyReadView view)
        {
            int raw = view.RoundNumber;
            int shown = DisplayRound(view);
            return raw > MaxRoundNumber
                ? shown.ToString() + "/" + MaxRoundNumber + "（已封顶）"
                : shown.ToString() + "/" + MaxRoundNumber;
        }

        /// <summary>骰子文案；尚未掷骰（<see cref="IMonopolyReadView.LastDice"/> = 0）时报"未掷"。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>4</c> 或 <c>未掷</c>。</returns>
        private static string DiceText(IMonopolyReadView view)
        {
            return view.LastDice > 0 ? view.LastDice.ToString() : "未掷";
        }

        /// <summary>
        /// 最近一手的文案。<b>落点口径</b>：<see cref="IMonopolyReadView.LastMovedTo"/> 是
        /// <b>结算完成后的最终落点</b>，已经包含格效果位移——踩监狱会变成 0（起点）、
        /// 抽到"前进 3 格"会是前进后的格子。<b>不要拿它反推骰子步数</b>
        /// （<c>LastMovedTo ≠ (前位置 + LastDice) % 20</c> 是常态），所以这里同时把骰子和落点都写出来。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>P2 掷 4 → 落点 13（· 无主地产 340）</c>；尚未掷骰时为"尚未掷骰"。</returns>
        private static string LastMoveText(IMonopolyReadView view)
        {
            if (view.LastMovedPlayer < 0 || view.LastMovedTo < 0)
            {
                return "尚未掷骰";
            }

            return PlayerLabel(view.LastMovedPlayer) + " 掷 " + view.LastDice
                + " → 落点 " + view.LastMovedTo + "（" + TileLabel(view.GetTile(view.LastMovedTo)) + "）";
        }

        /// <summary>现金行：逐个存活/破产玩家报现金（破产者在契约里恒为 0，标"出局"避免被读成"刚好花光"）。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>现金 P1 1460 · P2 1540（出局）</c>。</returns>
        private static string BuildCashLine(IMonopolyReadView view)
        {
            var builder = new StringBuilder(96);
            builder.Append("现金 ");
            int count = view.PlayerCount;
            for (int index = 0; index < count; index++)
            {
                MonopolyPlayerView player = view.GetPlayer(index);
                if (index > 0)
                {
                    builder.Append(" · ");
                }

                builder.Append(PlayerLabel(index)).Append(' ').Append(player.Cash);
                if (player.IsBankrupt)
                {
                    builder.Append("（出局）");
                }
            }

            return builder.ToString();
        }

        /// <summary>地产统计行片段：每个玩家持有的地产块数（所有者身份的唯一展示位，字符盘只放等级）。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>形如 <c>P1 2 · P2 1</c>。</returns>
        private static string OwnedText(IMonopolyReadView view)
        {
            var builder = new StringBuilder(48);
            int count = view.PlayerCount;
            for (int index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(" · ");
                }

                builder.Append(PlayerLabel(index)).Append(' ').Append(view.GetPlayer(index).PropertyCount);
            }

            return builder.ToString();
        }

        /// <summary>玩家索引到展示名的唯一换算：<c>P1</c> = 索引 0（契约里的本地玩家），<c>P2</c> = 索引 1……
        /// 本地玩家额外标"（你）"，避免"P1 / 玩家0"两套编号在文案里打架。</summary>
        /// <param name="playerIndex">玩家索引（0 基）。</param>
        /// <returns>形如 <c>P1（你）</c> 或 <c>P2</c>。</returns>
        private static string PlayerLabel(int playerIndex)
        {
            return playerIndex == 0 ? "P1（你）" : "P" + (playerIndex + 1);
        }

        /// <summary>胜者文案（结算用）：<c>-1</c> 表示无人获胜（理论不可达，仅防御）。</summary>
        /// <param name="winnerPlayer">胜者索引。</param>
        /// <returns>一句短文案。</returns>
        private static string WinnerText(int winnerPlayer)
        {
            if (winnerPlayer < 0)
            {
                return "无人获胜";
            }

            return PlayerLabel(winnerPlayer) + " 获胜";
        }

        /// <summary>格子的文字说明（状态行用；与字符盘的符号表一一对应）。</summary>
        /// <param name="tile">格子快照。</param>
        /// <returns>形如 <c>T 税务</c> 或 <c>等级 2 地产（地价 320）</c>。</returns>
        private static string TileLabel(MonopolyTileView tile)
        {
            switch (tile.Kind)
            {
                case MonopolyTileKind.Go:
                    return "▣ 起点";
                case MonopolyTileKind.Tax:
                    return "T 税务 -100";
                case MonopolyTileKind.Chance:
                    return "? 机会";
                case MonopolyTileKind.Jail:
                    return "J 监狱（退回起点）";
                case MonopolyTileKind.Bonus:
                    return "★ 奖励 +150";
                default:
                    if (tile.Owner < 0)
                    {
                        return "· 无主地产 价 " + tile.Price;
                    }

                    return "等级 " + tile.Level + " 地产 价 " + tile.Price;
            }
        }

        /// <summary>格子状态到显示字符的映射（唯一的一处换算，约定见类型注释）。</summary>
        /// <param name="tile">格子快照。</param>
        /// <returns>字符盘与按钮标题共用的类型字符。</returns>
        private static char GlyphOf(MonopolyTileView tile)
        {
            switch (tile.Kind)
            {
                case MonopolyTileKind.Go:
                    return GoChar;
                case MonopolyTileKind.Tax:
                    return TaxChar;
                case MonopolyTileKind.Chance:
                    return ChanceChar;
                case MonopolyTileKind.Jail:
                    return JailChar;
                case MonopolyTileKind.Bonus:
                    return BonusChar;
                default:
                    return PropertyGlyph(tile);
            }
        }

        /// <summary>
        /// 地产字符：无主 <c>·</c>；有主显示等级数字（<c>0</c> = 刚买入还没升级）。
        /// <b>为什么用 Owner 而不是 Level 判"有没有主"</b>：见类型注释——两者在等级 0 时重叠，
        /// Owner 才是唯一可靠的拥有标记。
        /// </summary>
        /// <param name="tile">地产格快照。</param>
        /// <returns><c>·</c> 或 <c>0</c>..<c>3</c>。</returns>
        private static char PropertyGlyph(MonopolyTileView tile)
        {
            if (tile.Owner < 0)
            {
                return PropertyEmptyChar;
            }

            int level = tile.Level;
            if (level < 0)
            {
                level = 0;
            }

            if (level > 9)
            {
                level = 9;
            }

            return (char)('0' + level);
        }

        /// <summary>
        /// 解析 20 个按钮的标题、底图与点击监听（幂等：只有第一次真正执行）。
        /// <para>
        /// <b>为什么标题要走 <c>GetComponentInChildren</c> / 底图走 <c>GetComponent</c></b>：
        /// 同一物体上只能有一个 <c>Graphic</c>，按钮本体是 <see cref="Image"/>，
        /// 文字必然在子物体上；建场景脚本按这个结构建，标题/底图缺失时这里只拿到 <c>null</c>，
        /// 点击链路照旧工作。
        /// </para>
        /// </summary>
        private void EnsureTiles()
        {
            if (_tilesBound)
            {
                return;
            }

            _tilesBound = true;

            if (tileButtons == null)
            {
                return;
            }

            int count = tileButtons.Length < TileCount ? tileButtons.Length : TileCount;
            for (int i = 0; i < count; i++)
            {
                Button? button = tileButtons[i];
                if (button == null)
                {
                    continue;
                }

                int index = i;
                UnityEngine.Events.UnityAction handler = () => RaiseTileClicked(index);

                _tileHandlers[i] = handler;
                button.onClick.AddListener(handler);
                _tileLabels[i] = button.GetComponentInChildren<Text>();

                Image? image = button.GetComponent<Image>();
                _tileImages[i] = image;
                if (image != null)
                {
                    _tileBaseColors[i] = image.color;
                }
            }
        }

        /// <summary>转发"掷骰子"（按钮与任意格子按钮共用这一个出口）。</summary>
        private void RaiseRollClicked()
        {
            Action? handler = OnRollClicked;
            if (handler != null)
            {
                handler();
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
        /// 任意格子按钮的点击 → <see cref="OnRollClicked"/>。
        /// <b>为什么点格子也是掷骰</b>：内核的公开输入面只有 <c>TryRoll()</c>（移动目标由骰子决定，
        /// 玩家不选路），格子按钮如果"点了没用"就是骗人；让它等价于掷骰，既符合规则也不需要新契约。
        /// </summary>
        /// <param name="tile">被点格子的索引（只用于将来做"选中该格"的扩展，当前不参与逻辑）。</param>
        private void RaiseTileClicked(int tile)
        {
            if (tile < 0 || tile >= TileCount)
            {
                return;
            }

            RaiseRollClicked();
        }
    }
}

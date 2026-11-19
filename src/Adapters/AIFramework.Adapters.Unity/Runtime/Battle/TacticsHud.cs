#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 战棋的 uGUI 皮肤：状态行（回合 / 行动方 / 存活 / 选中 / 胜负）+ 8×8 字符盘 +
    /// 8×8 格子按钮 + "结束回合" / "重新开始" 两个按钮。
    /// <para>
    /// <b>为什么盘面用字符而不是 64 个棋子控件</b>：一个 <see cref="Text"/> 就能表达整盘棋
    /// （玩家 <c>A/B/C</c>、敌方 <c>a/b/c</c>、空格 <c>·</c>），零池化、零 <c>Instantiate</c>；
    /// 字符视图也正好让无头环境可以断言（同一份视图能打日志）。格子按钮是<b>另一条通道</b>：
    /// 它只负责"把手指点位翻译成 (x, y) 或 unitId"，顺带把同一个字符刷到按钮标题上，
    /// 让点得到的地方和看得到的地方是同一套坐标。
    /// </para>
    /// <para>
    /// <b>本类不含任何战棋规则</b>：能不能走、能不能打、扣多少血、谁胜谁负全部由内核判定；
    /// HUD 只负责"把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，
    /// UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>行序 <c>Height-1 → 0</c></b>：战棋内核的 <c>y</c> 是<b>向上</b>的
    /// （<c>y = 0</c> 是玩家底线，<c>y = Height-1</c> 是敌方底线，见 <see cref="ITacticsReadView"/>），
    /// 而屏幕是自上而下排行的。因此字符盘第 0 行画的是 <c>y = Height-1</c>（敌方那边），
    /// 最后一行画的是 <c>y = 0</c>（我方底线）——己方在下方、敌方在上方，与棋盘坐标同向。
    /// 按钮网格用同一套行序（下标 = <c>显示行 * 8 + x</c>），点击回调再把显示行翻回内核的 <c>y</c>
    /// （见 <see cref="RaiseCellClicked"/>）。两侧只要有一处漏了这次翻转，
    /// 玩家点"看起来在下面"的格子就会落到对角的另一头。
    /// </para>
    /// <para>
    /// <b>字母来源</b>：玩家单位 ID 是 1..3、敌方是 11..13（见 <see cref="TacticsUnitView.UnitId"/>），
    /// 因此 <c>A = 1</c>、<c>a = 11</c>。这是契约里写死的编号，不是本类的假设。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行、字符盘、64 个按钮标题都先比后写。自动演示是 0.5 秒一拍，
    /// 稳态（谁都没动的那几帧）不会触发任何字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticsHud : MonoBehaviour
    {
        /// <summary>棋盘边长（内核固定 8×8；同时是格子按钮的列数与行数）。</summary>
        private const int BoardSize = 8;

        /// <summary>格子总数（8×8），与 <c>cellButtons</c> 的注入长度一致。</summary>
        private const int CellCount = BoardSize * BoardSize;

        /// <summary>玩家阵营编码（0 = 玩家）。</summary>
        private const int PlayerTeam = 0;

        /// <summary>玩家单位 ID 基数：第 i 个玩家单位是 <c>1 + i</c>，画成 <c>A + i</c>。</summary>
        private const int PlayerUnitIdBase = 1;

        /// <summary>敌方单位 ID 基数：第 j 个敌方单位是 <c>11 + j</c>，画成 <c>a + j</c>。</summary>
        private const int EnemyUnitIdBase = 11;

        /// <summary>字母偏移上限（<c>A..Z</c> / <c>a..z</c>）；越界画 <c>?</c>，绝不抛异常。</summary>
        private const int MaxLetterOffset = 25;

        /// <summary>空格字符（未被占用的格子）。</summary>
        private const char EmptyChar = '·';

        /// <summary>无法映射成字母时的兜底字符（只会在内核改了 ID 编号时出现）。</summary>
        private const char UnknownChar = '?';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>字符盘缓冲初始容量：8 列 × 8 行 + 换行，一次分配够用到底。</summary>
        private const int BoardBuilderCapacity = 80;

        /// <summary>"回合结束"文案缓冲容量（两行中文 + 数字）。</summary>
        private const int StatusBuilderCapacity = 160;

        /// <summary>"没有单位"的哨兵值（与内核一致：无选中 / 空格 / 找不到都是 <c>-1</c>）。</summary>
        private const int NoUnitId = -1;

        /// <summary>点了"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点了"结束回合"。</summary>
        public event Action? OnEndTurnClicked;

        /// <summary>点了某个单位；参数是稳定 <c>unitId</c>（不是会因阵亡前移的 index）。</summary>
        public event Action<int>? OnUnitClicked;

        /// <summary>点了某个空格；参数是内核口径的 (x, y)，<b>已经翻过行序</b>。</summary>
        public event Action<int, int>? OnCellClicked;

        /// <summary>状态行：回合 / 行动方 / 存活数（第一行），选中单位 / 胜负（第二行）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符盘：8 行 × 8 列（玩家 <c>A/B/C</c> / 敌方 <c>a/b/c</c> / 空 <c>·</c>），第一行是 <c>y = Height-1</c>。</summary>
        [SerializeField] private Text boardText = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>"结束回合"按钮（手动试玩时用来推进敌方回合）。</summary>
        [SerializeField] private Button endTurnButton = null!;

        /// <summary>
        /// 64 个格子按钮，<b>行优先注入</b>：下标 = <c>显示行 * 8 + x</c>，显示行 0 是字符盘第一行
        /// （<c>y = 7</c>）。<b>为什么需要它</b>：<see cref="OnUnitClicked"/> / <see cref="OnCellClicked"/>
        /// 总得有一个点位来源，纯 <see cref="Text"/> 收不到点击；未注入（空数组）时本类只刷字符盘，
        /// 不崩也不报错——那时本场景就是"纯自动演示"，只有两个按钮可点。
        /// </summary>
        [SerializeField] private Button[] cellButtons = new Button[0];

        /// <summary>复用的字符盘缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请 <see cref="StringBuilder"/>。</summary>
        private readonly StringBuilder _board = new StringBuilder(BoardBuilderCapacity);

        /// <summary>复用的状态行缓冲（两行文本 + 数字）。</summary>
        private readonly StringBuilder _status = new StringBuilder(StatusBuilderCapacity);

        /// <summary>64 个按钮当前显示的字符（脏检查基准；初值 <c>'\0'</c> 保证首帧必然写一次）。</summary>
        private readonly char[] _cellChars = new char[CellCount];

        /// <summary>64 个按钮的标题控件（按钮本体是 <see cref="Image"/>，文字必然在子物体上）。</summary>
        private readonly Text?[] _cellLabels = new Text?[CellCount];

        /// <summary>
        /// 64 个按钮各自的点击监听器。<b>为什么要把委托存起来</b>：<c>UnityEvent.RemoveListener</c>
        /// 按"目标 + 方法"匹配，每次现造一个 lambda 会退订不掉，场景重载后一次点击触发两次。
        /// </summary>
        private readonly UnityEngine.Events.UnityAction?[] _cellHandlers = new UnityEngine.Events.UnityAction?[CellCount];

        /// <summary>最近一次渲染里 (x, y) 上的单位 ID（<see cref="NoUnitId"/> = 空格），点按钮时用来区分"点人"还是"点格"。</summary>
        private readonly int[] _cellUnitIds = new int[CellCount];

        /// <summary>已写到控件上的字符盘（脏检查基准）。</summary>
        private string _lastBoard = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>格子按钮是否已解析并订阅（<c>Awake</c> 与控制器首帧 <see cref="Render"/> 谁先到都用它兜住）。</summary>
        private bool _cellsBound;

        /// <summary>
        /// 是否已显示终局文案。<b>为什么需要它</b>：结算后控制器仍可能继续推视图，
        /// 若每次都重写状态行，"对局结束 · …"会立刻被下一帧的实时读数盖掉。
        /// </summary>
        private bool _resultShown;

        /// <summary>
        /// 订阅按钮。<b>为什么在 <c>Awake</c> 而非 <c>Start</c></b>：控制器在自己的 <c>Awake</c> 里就会
        /// 建会话并首帧 <see cref="Render"/>，这里必须在此之前把标题解析出来；
        /// 两者都是 <c>Awake</c> 时谁先跑都不影响结果（<see cref="EnsureCells"/> 是幂等的）。
        /// </summary>
        private void Awake()
        {
            EnsureCells();

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }

            if (endTurnButton != null)
            {
                endTurnButton.onClick.AddListener(RaiseEndTurnClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Awake"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }

            if (endTurnButton != null)
            {
                endTurnButton.onClick.RemoveListener(RaiseEndTurnClicked);
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
        /// <param name="selectedUnitId">当前选中单位 ID（<c>-1</c> 表示没选中）。</param>
        public void Render(ITacticsReadView view, int selectedUnitId)
        {
            if (view == null)
            {
                return;
            }

            int width = view.Width;
            int height = view.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // 重开之后视图回到"未结束"：内部状态跟着复位，终局文案不再霸占状态行。
            if (_resultShown && !view.IsFinished)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
                _lastBoard = string.Empty;
            }

            EnsureCells();
            RefreshBoard(view, width, height);

            if (endTurnButton != null)
            {
                endTurnButton.interactable = !view.IsFinished;
            }

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view, selectedUnitId));
            }
        }

        /// <summary>
        /// 显示终局文案（胜方 + 重新开始提示）。<b>只改状态行、不动字符盘</b>：
        /// 终局画面仍要看到最终盘面，否则玩家分不清"打完了"与"界面停了"。
        /// </summary>
        /// <param name="isVictory">内核结算口径的胜负（<see cref="GameplayResult.IsVictory"/>）。</param>
        /// <param name="victoryTeam">胜方阵营：<c>-1</c> 未分胜负、0 玩家、1 敌方（平局也记 1）。</param>
        public void ShowResult(bool isVictory, int victoryTeam)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append("对局结束 · ").Append(ResultLabel(isVictory, victoryTeam)).Append(RowSeparator)
                .Append("点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 8×8 字符盘，并把同一批字符刷到 64 个按钮标题上。
        /// <para>
        /// <b>行序 <c>Height-1 → 0</c></b>：缓冲第 0 行画 <c>y = Height-1</c>（敌方底线），
        /// 最后一行画 <c>y = 0</c>（我方底线）。内核的 <c>y</c> 向上，屏幕自上而下，
        /// 因此这是唯一能让"敌方在上、我方在下"与坐标同向的画法；按钮网格共用同一套行序。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="width">列数。</param>
        /// <param name="height">行数。</param>
        private void RefreshBoard(ITacticsReadView view, int width, int height)
        {
            _board.Clear();

            for (int row = 0; row < height; row++)
            {
                int y = (height - 1) - row;

                for (int x = 0; x < width; x++)
                {
                    int index = view.UnitAt(x, y);
                    char glyph;
                    int unitId;

                    if (index < 0 || index >= view.UnitCount)
                    {
                        glyph = EmptyChar;
                        unitId = NoUnitId;
                    }
                    else
                    {
                        TacticsUnitView unit = view.GetUnit(index);
                        glyph = GlyphOf(in unit);
                        unitId = unit.UnitId;
                    }

                    _board.Append(glyph);
                    SetCellGlyph(row, x, glyph);
                    SetCellUnit(row, x, unitId);
                }

                if (row < height - 1)
                {
                    _board.Append(RowSeparator);
                }
            }

            string board = _board.ToString();
            if (board != _lastBoard)
            {
                _lastBoard = board;
                if (boardText != null)
                {
                    boardText.text = board;
                }
            }
        }

        /// <summary>
        /// 单位快照到显示字符的映射（唯一的一处换算）：玩家 <c>A/B/C</c>、敌方 <c>a/b/c</c>。
        /// 空格不经过这里（由调用方直接给 <see cref="EmptyChar"/>）。
        /// </summary>
        /// <param name="unit">单位快照。</param>
        /// <returns>一个字符。</returns>
        private static char GlyphOf(in TacticsUnitView unit)
        {
            return unit.Team == PlayerTeam
                ? LetterOf('A', unit.UnitId - PlayerUnitIdBase)
                : LetterOf('a', unit.UnitId - EnemyUnitIdBase);
        }

        /// <summary>把"基数 + 偏移"翻成字母；偏移越界（内核换了编号）时画 <c>?</c>，不抛异常。</summary>
        /// <param name="first">基数首字母（<c>A</c> 或 <c>a</c>）。</param>
        /// <param name="offset">单位在所属阵营里的序号（0 起）。</param>
        /// <returns>字母或 <see cref="UnknownChar"/>。</returns>
        private static char LetterOf(char first, int offset)
        {
            if (offset < 0 || offset > MaxLetterOffset)
            {
                return UnknownChar;
            }

            return (char)(first + offset);
        }

        /// <summary>把一个格子的字符写到对应按钮标题上（未变化就不碰控件）。</summary>
        /// <param name="row">显示行（0 = 字符盘第一行 = <c>y = Height-1</c>）。</param>
        /// <param name="x">列。</param>
        /// <param name="glyph">要显示的字符。</param>
        private void SetCellGlyph(int row, int x, char glyph)
        {
            if (row < 0 || row >= BoardSize || x < 0 || x >= BoardSize)
            {
                return;
            }

            int index = (row * BoardSize) + x;
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
        /// 记录一个格子上站着谁（<see cref="NoUnitId"/> = 空格），供 <see cref="RaiseCellClicked"/>
        /// 区分"点到人"与"点到格"。与字形一起在 <see cref="RefreshBoard"/> 里刷新，因此点击永远基于<b>上一帧显示出来的</b>局面。
        /// </summary>
        /// <param name="row">显示行（0 = 字符盘第一行 = <c>y = Height-1</c>）。</param>
        /// <param name="x">列。</param>
        /// <param name="unitId">该格的单位 ID，空格为 <see cref="NoUnitId"/>。</param>
        private void SetCellUnit(int row, int x, int unitId)
        {
            if (row < 0 || row >= BoardSize || x < 0 || x >= BoardSize)
            {
                return;
            }

            _cellUnitIds[(row * BoardSize) + x] = unitId;
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

            for (int i = 0; i < _cellUnitIds.Length; i++)
            {
                _cellUnitIds[i] = NoUnitId;
            }

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
        /// 拼状态行：第一行回合 / 行动方 / 存活数，第二行选中单位 / 胜负。
        /// <b>为什么分两行</b>：一行塞下五段读数会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的往往正是"现在轮到谁"这种最需要确认的读数。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="selectedUnitId">当前选中单位 ID。</param>
        /// <returns>两行状态文本。</returns>
        private string BuildStatus(ITacticsReadView view, int selectedUnitId)
        {
            _status.Clear();
            _status.Append("回合 ").Append(view.TurnIndex)
                .Append(" · 行动方 ").Append(TeamLabel(view.CurrentTeam))
                .Append(" · 存活 ").Append(view.UnitCount).Append(" 个")
                .Append(RowSeparator)
                .Append("选中 ").Append(SelectionText(view, selectedUnitId))
                .Append(" · ").Append(view.IsFinished ? "对局结束" : "进行中");

            return _status.ToString();
        }

        /// <summary>写状态行（文本未变就不碰控件，避免每帧重建字形）。</summary>
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

        /// <summary>
        /// 选中单位的读数：<c>A（HP 20/20 · 攻 6）</c>；没选中为 <c>无</c>；
        /// ID 已不存在（刚刚阵亡）为 <c>无（已阵亡）</c>——这一条让"点完就死"的瞬间不会显示一个幽灵单位。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="selectedUnitId">选中 ID。</param>
        /// <returns>一句短文案。</returns>
        private static string SelectionText(ITacticsReadView view, int selectedUnitId)
        {
            if (selectedUnitId == NoUnitId)
            {
                return "无";
            }

            for (int i = 0; i < view.UnitCount; i++)
            {
                TacticsUnitView unit = view.GetUnit(i);
                if (unit.UnitId != selectedUnitId)
                {
                    continue;
                }

                return GlyphOf(in unit) + "（HP " + unit.Hp + "/" + unit.MaxHp + " · 攻 " + unit.Attack + "）";
            }

            return "无（已阵亡）";
        }

        /// <summary>阵营值的中文名（只用于展示，脱离内核枚举也要保持 0 = 玩家）。</summary>
        /// <param name="team">阵营值。</param>
        /// <returns>中文标签。</returns>
        private static string TeamLabel(int team)
        {
            return team == PlayerTeam ? "玩家" : "敌方";
        }

        /// <summary>
        /// 终局文案。
        /// <para>
        /// <b>为什么"失败"要带"或回合上限平局"</b>：内核在回合上限触发时把 <c>VictoryTeam</c> 记成 1（敌方占优）、
        /// 但 <c>GameplayResult.IsVictory</c> 仍是 false；<see cref="ITacticsReadView"/> 与
        /// <see cref="ShowResult"/> 拿到的就这两个值，<b>没有第三个信号能区分"被打死"还是"拖到平局"</b>，
        /// 因此只能合成一句。要分开显示的话，得让内核多暴露一个"结束原因"，那不是 HUD 能自己编的。
        /// </para>
        /// </summary>
        /// <param name="isVictory">内核结算口径的胜负。</param>
        /// <param name="victoryTeam">胜方阵营。</param>
        /// <returns>一句短文案。</returns>
        private static string ResultLabel(bool isVictory, int victoryTeam)
        {
            if (isVictory)
            {
                return "胜利 · 我方全歼敌军";
            }

            return victoryTeam == PlayerTeam ? "胜利 · 内核判玩家胜" : "失败（或回合上限平局）";
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

        /// <summary>转发"结束回合"。</summary>
        private void RaiseEndTurnClicked()
        {
            Action? handler = OnEndTurnClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>
        /// 把按钮下标抬成 <see cref="OnUnitClicked"/> 或 <see cref="OnCellClicked"/>，并
        /// <b>把显示行翻回内核的 y</b>：显示行 0 是字符盘第一行 = <c>y = Height-1</c>，
        /// 故 <c>y = (Height-1) - index / 8</c>（这里用固定的 8 而不是视图高度，是因为按钮网格恒为 8×8）。
        /// <para>
        /// <b>为什么靠 <see cref="_cellUnitIds"/> 分叉</b>：HUD 在最近一次 <see cref="Render"/> 里
        /// 已经知道每格站着谁，因此"点到人"和"点到空格"必须是两个事件——
        /// 前者交给控制器做"选中/攻击"，后者做"移动"。让控制器自己再查一遍视图也行，
        /// 但那样 HUD 就得把一个它已经掌握的坐标信息丢掉再让下游重建，纯属绕路。
        /// </para>
        /// </summary>
        /// <param name="index">按钮下标（<c>显示行 * 8 + x</c>）。</param>
        private void RaiseCellClicked(int index)
        {
            int x = index % BoardSize;
            int y = (BoardSize - 1) - (index / BoardSize);

            if (index >= 0 && index < CellCount)
            {
                int unitId = _cellUnitIds[index];
                if (unitId != NoUnitId)
                {
                    Action<int>? unitHandler = OnUnitClicked;
                    if (unitHandler != null)
                    {
                        unitHandler(unitId);
                    }

                    return;
                }
            }

            Action<int, int>? cellHandler = OnCellClicked;
            if (cellHandler != null)
            {
                cellHandler(x, y);
            }
        }
    }
}

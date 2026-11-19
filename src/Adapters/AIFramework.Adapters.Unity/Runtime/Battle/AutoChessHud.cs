#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 自走棋的 uGUI 皮肤：状态行 + 8×8 字符网格 + 三个操作按钮。
    /// <para>
    /// <b>为什么用字符网格而不是 64 个格子控件</b>：自走棋的信息量全在"谁站在哪"这一张图上；
    /// 用 <see cref="Text"/> 画一张等宽字符表，场景里只有一个控件、零池化、零 <c>Instantiate</c>，
    /// 内核换棋盘尺寸时也不需要重建任何层级。64 个格子控件要额外维护"格 → 单位槽位"的映射表，
    /// 那张表一旦与内核槽位不同步，画面与规则就会静默错位——这正是本仓极力避免的第二套状态。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：能不能上场、能不能开战、回合是否结束全部由内核判定；HUD 只负责
    /// "把只读数据翻译成字符/文本"与"把手指点位翻译成事件"，以及按视图状态决定按钮是否可点。
    /// 这条边界一旦破了，UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：网格文本与状态行都先比后写。稳态下每帧只有一次 <see cref="StringBuilder"/> 复用
    /// 与一次字符串比较，不触发 <see cref="Text"/> 的网格重建。
    /// </para>
    /// <para>
    /// <b>为什么"下一回合"按钮会改字</b>：控制器不接受额外参数，公开面里唯一的输入通道就是
    /// <see cref="Render"/>；结算后（<see cref="IAutoChessReadView.IsFinished"/>）本类自己把按钮文案
    /// 换成"重新开始"，控制器则把同一次点击解释成"重建会话"。文案与行为由同一个只读字段驱动，
    /// 不会出现"按钮写着重开、实际还在推进回合"。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AutoChessHud : MonoBehaviour
    {
        /// <summary>玩家阵营编码（<see cref="ChessUnitView.Owner"/> 的契约约定：0 = 玩家）。</summary>
        private const int PlayerOwner = 0;

        /// <summary>玩家单位符号。</summary>
        private const char PlayerChar = 'P';

        /// <summary>敌方单位符号。</summary>
        private const char EnemyChar = 'E';

        /// <summary>空格符号（中点，等宽字体的宽度与字母一致，读起来更像一张棋盘）。</summary>
        private const char EmptyChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary><see cref="_grid"/> 的初始容量：8×8 上限再加 7 个换行，一次分配够用到底。</summary>
        private const int GridBuilderCapacity = 96;

        /// <summary>未结算时"推进"按钮的文案。</summary>
        private const string NextRoundLabel = "下一回合";

        /// <summary>结算后同一个按钮的文案。</summary>
        private const string RestartLabel = "重新开始";

        /// <summary>点了"全部上场"。</summary>
        public event Action? OnPlaceClicked;

        /// <summary>点了"开战"。</summary>
        public event Action? OnStartBattleClicked;

        /// <summary>点了"下一回合"（结算后同一个按钮表示"重新开始"）。</summary>
        public event Action? OnNextRoundClicked;

        /// <summary>状态行：回合序号、双方基地血量、存活数、本轮阶段与胜负。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符网格文本（字号 20、居中；一个字符一格的纯文本棋盘）。</summary>
        [SerializeField] private Text gridText = null!;

        /// <summary>"全部上场"按钮。</summary>
        [SerializeField] private Button placeButton = null!;

        /// <summary>"开战"按钮。</summary>
        [SerializeField] private Button startBattleButton = null!;

        /// <summary>"下一回合"按钮（结算后改显示"重新开始"）。</summary>
        [SerializeField] private Button nextRoundButton = null!;

        /// <summary>复用的网格缓冲：每次 <see cref="Render"/> 清空重填，不新建 <see cref="StringBuilder"/>。</summary>
        private readonly StringBuilder _grid = new StringBuilder(GridBuilderCapacity);

        /// <summary>已写到控件上的网格文本（脏检查基准）。</summary>
        private string _lastGrid = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>"下一回合"按钮上的文字控件（<c>Start</c> 时从按钮子物体上取；为空时只改可点状态）。</summary>
        private Text? _nextRoundLabel;

        /// <summary>已写到"推进"按钮上的文案（脏检查基准）。</summary>
        private string _lastNextRoundLabel = NextRoundLabel;

        /// <summary>
        /// 把按钮点击接到转发方法上，并取回"推进"按钮的文字控件。
        /// <b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何 <c>Update</c> 之前跑完一次，
        /// 此时订阅不会与控制器首帧渲染抢顺序。
        /// </summary>
        private void Start()
        {
            if (placeButton != null)
            {
                placeButton.onClick.AddListener(RaisePlaceClicked);
            }

            if (startBattleButton != null)
            {
                startBattleButton.onClick.AddListener(RaiseStartBattleClicked);
            }

            if (nextRoundButton != null)
            {
                nextRoundButton.onClick.AddListener(RaiseNextRoundClicked);
                _nextRoundLabel = nextRoundButton.GetComponentInChildren<Text>();
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (placeButton != null)
            {
                placeButton.onClick.RemoveListener(RaisePlaceClicked);
            }

            if (startBattleButton != null)
            {
                startBattleButton.onClick.RemoveListener(RaiseStartBattleClicked);
            }

            if (nextRoundButton != null)
            {
                nextRoundButton.onClick.RemoveListener(RaiseNextRoundClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新网格、状态行与按钮可用性。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"棋盘炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IAutoChessReadView view)
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

            string grid = BuildGrid(view, width, height);
            if (grid != _lastGrid)
            {
                _lastGrid = grid;
                if (gridText != null)
                {
                    gridText.text = grid;
                }
            }

            // 存活数是逐槽过滤出来的，不能用 PlayerUnitCount / EnemyUnitCount 代替：
            // 那两个数报的是槽位数，阵亡单位要留到 EndRound 才清场。
            int slots = view.PlayerUnitCount + view.EnemyUnitCount;
            int playerAlive = 0;
            int enemyAlive = 0;
            int bench = 0;
            for (int i = 0; i < slots; i++)
            {
                ChessUnitView unit = view.GetUnit(i);
                if (!unit.Alive)
                {
                    continue;
                }

                if (unit.Owner == PlayerOwner)
                {
                    playerAlive++;

                    // 备战席坐标是 -1（阵亡单位保留阵亡坐标，所以前面已经用 Alive 过滤过）。
                    if (unit.X < 0)
                    {
                        bench++;
                    }
                }
                else
                {
                    enemyAlive++;
                }
            }

            SetStatus(BuildStatusLine(view, playerAlive, enemyAlive, bench));

            bool finished = view.IsFinished;

            // 只在"没结束 + 备战席有人"时可点：内核拒绝的点击不会改任何状态，点下去只会看起来像按钮坏了。
            SetInteractable(placeButton, !finished && bench > 0);
            SetInteractable(startBattleButton, !finished && !view.IsBattleResolved);
            SetInteractable(nextRoundButton, finished || view.IsBattleResolved);

            SetNextRoundLabel(finished ? RestartLabel : NextRoundLabel);
        }

        /// <summary>
        /// 拼状态行。<b>胜负为什么从血量判</b>：<see cref="IAutoChessReadView"/> 的公开面里没有 <c>IsVictory</c>，
        /// 而内核 <c>EndRound</c> 的收口顺序是"先看玩家血、再看敌方血"，因此 <c>PlayerHp &lt;= 0</c> 一定是失败。
        /// 这里只读两个字段，没有复制任何战斗规则。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="playerAlive">玩家存活槽位数。</param>
        /// <param name="enemyAlive">敌方存活槽位数。</param>
        /// <param name="bench">玩家备战席上的单位数。</param>
        /// <returns>一整行状态文本。</returns>
        private static string BuildStatusLine(IAutoChessReadView view, int playerAlive, int enemyAlive, int bench)
        {
            // 内核的 RoundIndex 从 0 开始；玩家看的是"第几回合"，因此只在显示层 +1，属性仍报内核原值。
            string line = "第 " + (view.RoundIndex + 1) + " 回合"
                + " · 我方 HP " + view.PlayerHp
                + " · 敌方 HP " + view.EnemyHp
                + " · 存活 " + playerAlive + "/" + enemyAlive;

            if (view.IsFinished)
            {
                string outcome = view.PlayerHp <= 0 ? "失败" : "胜利";
                return line + " · 对局结束：" + outcome + "（点『" + RestartLabel + "』再来一局）";
            }

            if (view.IsBattleResolved)
            {
                return line + " · 本轮已结算（点『" + NextRoundLabel + "』继续）";
            }

            return bench > 0
                ? line + " · 备战 " + bench + "（点『全部上场』布阵）"
                : line + " · 布阵中（点『开战』）";
        }

        /// <summary>
        /// 按"上到下"拼字符网格：行序从 <c>Height-1</c> 到 0（<c>y = 0</c> 是底行，玩家半场在下方）。
        /// 每格先问 <see cref="IAutoChessReadView.UnitAt"/> 拿槽位 index，再用
        /// <see cref="IAutoChessReadView.GetUnit"/> 的 <see cref="ChessUnitView.Owner"/> 决定 P / E。
        /// <para>
        /// <b>为什么走 UnitAt 而不是遍历槽位</b>：这一条把"占位表是唯一格子真相"钉死在表现层，
        /// 阵亡单位（占位已清、坐标还留着）自然不会出现在格子上，不需要 HUD 自己判生死。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="width">列数。</param>
        /// <param name="height">行数。</param>
        /// <returns>矩形字符块，每行 <paramref name="width"/> 个字符。</returns>
        private string BuildGrid(IAutoChessReadView view, int width, int height)
        {
            _grid.Clear();
            _grid.EnsureCapacity(((width + 1) * height) - 1);

            // 槽位总数只算一次：UnitAt 返回的 index 必须落在这个范围内，越界就按空格画，
            // 绝不让一次脏 index 把整个 HUD 抛异常（GetUnit 越界是 ArgumentOutOfRangeException）。
            int slots = view.PlayerUnitCount + view.EnemyUnitCount;

            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    _grid.Append(CharFor(view, x, y, slots));
                }

                if (y > 0)
                {
                    _grid.Append(RowSeparator);
                }
            }

            return _grid.ToString();
        }

        /// <summary>取单格符号：占位为空 → <see cref="EmptyChar"/>，否则按阵营给 P / E。</summary>
        /// <param name="view">只读视图。</param>
        /// <param name="x">列。</param>
        /// <param name="y">行。</param>
        /// <param name="slots">当前槽位总数。</param>
        /// <returns>该格的显示字符。</returns>
        private static char CharFor(IAutoChessReadView view, int x, int y, int slots)
        {
            int index = view.UnitAt(x, y);
            if (index < 0 || index >= slots)
            {
                return EmptyChar;
            }

            return view.GetUnit(index).Owner == PlayerOwner ? PlayerChar : EnemyChar;
        }

        /// <summary>写状态行（文本未变就不碰控件，避免每帧重建字形）。</summary>
        /// <param name="line">要显示的一整行。</param>
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

        /// <summary>改"推进"按钮的文案（未变就不碰控件）。</summary>
        /// <param name="label">目标文案。</param>
        private void SetNextRoundLabel(string label)
        {
            if (label == _lastNextRoundLabel)
            {
                return;
            }

            _lastNextRoundLabel = label;
            Text? target = _nextRoundLabel;
            if (target != null)
            {
                target.text = label;
            }
        }

        /// <summary>设按钮可点状态（值未变就不碰控件）。</summary>
        /// <param name="button">目标按钮。</param>
        /// <param name="interactable">是否可点。</param>
        private static void SetInteractable(Button? button, bool interactable)
        {
            if (button != null && button.interactable != interactable)
            {
                button.interactable = interactable;
            }
        }

        /// <summary>转发"全部上场"。</summary>
        private void RaisePlaceClicked()
        {
            Action? handler = OnPlaceClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"开战"。</summary>
        private void RaiseStartBattleClicked()
        {
            Action? handler = OnStartBattleClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"下一回合 / 重新开始"。</summary>
        private void RaiseNextRoundClicked()
        {
            Action? handler = OnNextRoundClicked;
            if (handler != null)
            {
                handler();
            }
        }
    }
}

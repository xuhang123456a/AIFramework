#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 解谜（推箱子）的 uGUI 皮肤：状态行 + 字符网格 + 三个操作按钮。
    /// <para>
    /// <b>为什么用字符网格而不是 64 个格子控件</b>：推箱子的信息量全在"图"上——墙、目标、箱子、
    /// 玩家四层叠加关系一眼可读；用 <see cref="Text"/> 画一张等宽字符表，场景里只有一个控件、
    /// 零池化、零 <c>Instantiate</c>，而且内核换关（尺寸变化）时不需要重建任何层级。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：能不能走、箱子是否归位全部由内核判定；HUD 只负责
    /// "把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：网格文本与状态行都先比后写。稳态下（自动解法在等间隔）每帧只有一次
    /// <see cref="StringBuilder"/> 复用与一次字符串比较，不触发 <c>Text</c> 的网格重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PuzzleHud : MonoBehaviour
    {
        /// <summary>墙。</summary>
        private const char WallChar = '#';

        /// <summary>目标点（其上没有箱子、也没有玩家）。</summary>
        private const char GoalChar = '.';

        /// <summary>箱子（落在普通地面）。</summary>
        private const char BoxChar = '$';

        /// <summary>玩家（站在普通地面）。</summary>
        private const char PlayerChar = '@';

        /// <summary>玩家站在目标点上。</summary>
        private const char PlayerOnGoalChar = '+';

        /// <summary>箱子已在目标点上。</summary>
        private const char BoxOnGoalChar = '*';

        /// <summary>地面（以及虚空：关卡未铺设的区域按空白显示，不伪造一堵墙）。</summary>
        private const char FloorChar = ' ';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary><see cref="_grid"/> 的初始容量：8×8 上限再加换行，一次分配够用到底。</summary>
        private const int GridBuilderCapacity = 96;

        /// <summary>点了"自动解一关"。</summary>
        public event Action? OnAutoSolveClicked;

        /// <summary>点了"重置本关"。</summary>
        public event Action? OnResetClicked;

        /// <summary>点了"下一关"。</summary>
        public event Action? OnNextLevelClicked;

        /// <summary>状态行：关卡进度、步数、箱子归位数（结算后改写为通关文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符网格文本（字号 20、居中；一个字符一格的纯文本棋盘）。</summary>
        [SerializeField] private Text gridText = null!;

        /// <summary>"自动解一关"按钮。</summary>
        [SerializeField] private Button autoSolveButton = null!;

        /// <summary>"重置本关"按钮。</summary>
        [SerializeField] private Button resetButton = null!;

        /// <summary>"下一关"按钮。</summary>
        [SerializeField] private Button nextLevelButton = null!;

        /// <summary>复用的网格缓冲：每次 <see cref="Render"/> 清空重填，不新建 <see cref="StringBuilder"/>。</summary>
        private readonly StringBuilder _grid = new StringBuilder(GridBuilderCapacity);

        /// <summary>已写到控件上的网格文本（脏检查基准）。</summary>
        private string _lastGrid = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示通关文案。<b>为什么需要它</b>：结算后控制器仍每帧推视图，
        /// 若每次都重写状态行，"通关！…"会立刻被"步数 3 · 箱子 1/1"盖掉。
        /// </summary>
        private bool _solved;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，此时订阅不会与控制器首帧渲染抢顺序。
        /// </summary>
        private void Start()
        {
            if (autoSolveButton != null)
            {
                autoSolveButton.onClick.AddListener(RaiseAutoSolveClicked);
            }

            if (resetButton != null)
            {
                resetButton.onClick.AddListener(RaiseResetClicked);
            }

            if (nextLevelButton != null)
            {
                nextLevelButton.onClick.AddListener(RaiseNextLevelClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (autoSolveButton != null)
            {
                autoSolveButton.onClick.RemoveListener(RaiseAutoSolveClicked);
            }

            if (resetButton != null)
            {
                resetButton.onClick.RemoveListener(RaiseResetClicked);
            }

            if (nextLevelButton != null)
            {
                nextLevelButton.onClick.RemoveListener(RaiseNextLevelClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新网格与状态行。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本关已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"棋盘炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IPuzzleReadView view)
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

            // 重置/换关后视图回到"未解出"：内部状态跟着复位，通关文案不再霸占状态行。
            if (_solved && !view.IsSolved)
            {
                _solved = false;
                _lastStatus = string.Empty;
            }

            if (!_solved)
            {
                SetStatus("步数 " + view.Moves
                    + " · 箱子 " + view.BoxesOnGoal + "/" + view.BoxCount
                    + (view.IsSolved ? " · 已解出" : string.Empty));
            }

            bool autoSolveAllowed = !view.IsSolved;
            if (autoSolveButton != null && autoSolveButton.interactable != autoSolveAllowed)
            {
                autoSolveButton.interactable = autoSolveAllowed;
            }
        }

        /// <summary>
        /// 显示通关文案并锁死"自动解一关"（同一关已经解出，再自动解只会被内核拒绝）。
        /// <b>只改文案、不动棋盘</b>：终局画面仍需看到最后一步的箱子位置，否则玩家看不出"怎么过的"。
        /// </summary>
        /// <param name="moves">本关实际步数（取自只读视图，不是解法长度）。</param>
        public void ShowSolved(int moves)
        {
            _solved = true;

            string line = "通关！用了 " + moves + " 步 · 点『下一关』继续";
            _lastStatus = line;
            if (statusText != null)
            {
                statusText.text = line;
            }

            if (autoSolveButton != null)
            {
                autoSolveButton.interactable = false;
            }
        }

        /// <summary>
        /// 按"上到下"拼字符网格：行序从 <c>Height-1</c> 到 0（<c>y = 0</c> 是底行）。
        /// 每格先问玩家、再问箱子、最后问地形——箱子压在目标点上时地形仍是 <see cref="PuzzleCell.Goal"/>，
        /// 三层叠加的判断顺序就是"谁的符号优先"。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="width">列数。</param>
        /// <param name="height">行数。</param>
        /// <returns>矩形字符块，每行 <paramref name="width"/> 个字符。</returns>
        private string BuildGrid(IPuzzleReadView view, int width, int height)
        {
            _grid.Clear();
            _grid.EnsureCapacity(((width + 1) * height) - 1);

            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    _grid.Append(CharFor(view, x, y));
                }

                if (y > 0)
                {
                    _grid.Append(RowSeparator);
                }
            }

            return _grid.ToString();
        }

        /// <summary>取单格符号：玩家 &gt; 箱子 &gt; 地形。</summary>
        /// <param name="view">只读视图。</param>
        /// <param name="x">列。</param>
        /// <param name="y">行。</param>
        /// <returns>该格的显示字符。</returns>
        private static char CharFor(IPuzzleReadView view, int x, int y)
        {
            PuzzleCell cell = view.GetCell(x, y);

            if (x == view.PlayerX && y == view.PlayerY)
            {
                return cell == PuzzleCell.Goal ? PlayerOnGoalChar : PlayerChar;
            }

            if (view.HasBox(x, y))
            {
                return cell == PuzzleCell.Goal ? BoxOnGoalChar : BoxChar;
            }

            switch (cell)
            {
                case PuzzleCell.Wall:
                    return WallChar;
                case PuzzleCell.Goal:
                    return GoalChar;
                default:
                    // Floor 与 Empty（虚空）都画空白：虚空既不可站也不可推，画成墙会误导玩家去撞。
                    return FloorChar;
            }
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

        /// <summary>转发"自动解一关"。</summary>
        private void RaiseAutoSolveClicked()
        {
            Action? handler = OnAutoSolveClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"重置本关"。</summary>
        private void RaiseResetClicked()
        {
            Action? handler = OnResetClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"下一关"。</summary>
        private void RaiseNextLevelClicked()
        {
            Action? handler = OnNextLevelClicked;
            if (handler != null)
            {
                handler();
            }
        }
    }
}

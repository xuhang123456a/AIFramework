#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 俄罗斯方块的 uGUI 皮肤：状态行（分数/消行/等级）+ 字符网格 + 一个"重新开始"按钮。
    /// <para>
    /// <b>为什么用字符网格而不是 200 个格子控件</b>：方块的棋盘固定 10×20，用 <see cref="Text"/> 画一张
    /// 等宽字符表，场景里只有一个控件、零池化、零 <c>Instantiate</c>；而且"整盘 200 格每帧都在变"
    /// 这件事，用 200 个 <see cref="Image"/> 去做就等于每帧 200 次脏标记，反而更贵。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：能不能左移、旋转是否成功、消了几行、得多少分全部由内核判定；
    /// HUD 只负责"把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，
    /// UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>行序</b>：方块的坐标约定是 <c>y = 0</c> 在<b>顶部</b>（出生点就在顶行，下落即 <c>y + 1</c>），
    /// 因此网格必须从 <c>y = 0</c> 画到 <c>y = Height-1</c>——这才是屏幕上"从上到下"的顺序。
    /// 若照抄推箱子 HUD 的 <c>Height-1 → 0</c>（那套内核 <c>y = 0</c> 在底部），整盘会上下颠倒：
    /// 方块看起来在往天上堆，出生行跑到屏幕最下面。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：网格文本与状态行都先比后写。稳态下每帧只有一次 <see cref="StringBuilder"/> 复用
    /// 与一次字符串比较，不触发 <see cref="Text"/> 的字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TetrisHud : MonoBehaviour
    {
        /// <summary>
        /// 已锁定方块的字符。<b>为什么是 <c>●</c> 而不是 <c>#</c></b>：方块游戏靠"实心块 vs 空点"的密度对比
        /// 一眼读出堆高；契约允许换成 <c>#</c>，内置字体若缺 U+25CF 改这一个常量即可。
        /// </summary>
        private const char FilledChar = '●';

        /// <summary>空格字符（未被占用的格子）。</summary>
        private const char EmptyChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary><see cref="_grid"/> 的初始容量：10 列 × 20 行再加换行，一次分配够用到底。</summary>
        private const int GridBuilderCapacity = 256;

        /// <summary>点了"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>状态行：分数、消行数、等级（结算后改写为终局文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符网格文本（字号 18、行高紧凑；一个字符一格的纯文本棋盘）。</summary>
        [SerializeField] private Text gridText = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>复用的网格缓冲：每次 <see cref="Render"/> 清空重填，不新建 <see cref="StringBuilder"/>。</summary>
        private readonly StringBuilder _grid = new StringBuilder(GridBuilderCapacity);

        /// <summary>已写到控件上的网格文本（脏检查基准）。</summary>
        private string _lastGrid = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示终局文案。<b>为什么需要它</b>：结算后控制器仍可能继续推视图，
        /// 若每次都重写状态行，"游戏结束 · 得分 N"会立刻被"分数 N · 消行 M · 等级 L"盖掉。
        /// </summary>
        private bool _gameOverShown;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，此时订阅不会与控制器首帧渲染抢顺序。
        /// </summary>
        private void Start()
        {
            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新网格与状态行。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"棋盘炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(ITetrisReadView view)
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

            // 重开之后视图回到"未判负"：内部状态跟着复位，终局文案不再霸占状态行。
            if (_gameOverShown && !view.IsGameOver)
            {
                _gameOverShown = false;
                _lastStatus = string.Empty;
            }

            if (!_gameOverShown)
            {
                SetStatus("分数 " + view.Score + " · 消行 " + view.ClearedLines + " · 等级 " + view.Level);
            }
        }

        /// <summary>
        /// 显示终局文案（分数取自只读视图，不是本类自己记的账）。
        /// <b>只改文案、不动棋盘</b>：终局画面仍需看到最后一格落到哪里，否则玩家看不出"是怎么死的"。
        /// </summary>
        /// <param name="score">本局最终分数。</param>
        public void ShowGameOver(int score)
        {
            _gameOverShown = true;

            string line = "游戏结束 · 得分 " + score + " · 点『重新开始』再来一局";
            _lastStatus = line;
            if (statusText != null)
            {
                statusText.text = line;
            }
        }

        /// <summary>
        /// 按"上到下"拼字符网格：行序从 <c>y = 0</c>（顶行，出生行）到 <c>y = Height-1</c>。
        /// 每行固定 <paramref name="width"/> 个字符，不补空格、不加边框——宽度由调用方给，
        /// HUD 不替内核决定棋盘多大。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="width">列数。</param>
        /// <param name="height">行数。</param>
        /// <returns>矩形字符块，每行 <paramref name="width"/> 个字符。</returns>
        private string BuildGrid(ITetrisReadView view, int width, int height)
        {
            _grid.Clear();
            _grid.EnsureCapacity(((width + 1) * height) - 1);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // CellAt 越界返回 -1，这里按"非 0 即方块"处理：真出界也只画成方块，不会抛异常。
                    _grid.Append(view.CellAt(x, y) != 0 ? FilledChar : EmptyChar);
                }

                if (y < height - 1)
                {
                    _grid.Append(RowSeparator);
                }
            }

            return _grid.ToString();
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

        /// <summary>转发"重新开始"。</summary>
        private void RaiseRestartClicked()
        {
            Action? handler = OnRestartClicked;
            if (handler != null)
            {
                handler();
            }
        }
    }
}

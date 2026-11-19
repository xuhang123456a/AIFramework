#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 跑酷的 uGUI 皮肤：状态行（距离/分数/速度/姿态）+ 一行 30 列的字符跑道 + 跳跃/下蹲/重新开始三个按钮。
    /// <para>
    /// <b>为什么跑道用字符而不是一排 <see cref="Image"/></b>：一个 <see cref="Text"/> 就能表达
    /// "前方 20 米有几个障碍、各是 Low 还是 High、玩家现在跳到哪儿了"，零池化、零 <c>Instantiate</c>；
    /// 而"每帧几个方块在动"用 <see cref="Image"/> 去做，等于每帧几次脏标记 + 位移动画，反而更贵。
    /// 字符跑道也正好让无头环境可以断言（同一份视图能直接打日志）。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：速度曲线、分数、碰撞窗、清障高度、跳跃/下蹲能否生效全部由内核判定；
    /// HUD 只负责"把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，
    /// UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>列与米的换算</b>：<c>1 列 = 1 米</c>，玩家固定在左起第 3 列（下标
    /// <see cref="PlayerColumn"/>）。障碍的列号 = 玩家列 + <c>round(X)</c>——
    /// 内核的 <see cref="RunnerObstacleView.X"/> 已经是"相对玩家"的坐标（玩家恒在 <c>x = 0</c>，
    /// 障碍从 <c>+20</c> 一路减到 <c>-2</c> 被回收），所以<b>不需要再减 <c>Distance</c></b>：
    /// <c>X ∈ [-2, 20]</c> 而 <c>Distance</c> 会一路涨到几百米，再减一次会让整条跑道永远空白。
    /// 这个映射恰好把可视范围铺满 30 列：<c>X = -2</c> → 第 0 列（最左，即将被回收）、
    /// <c>X = +20</c> → 第 22 列（出生点）。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行与跑道文本都先比后写。跑道的字符只在障碍跨过一整米时变，
    /// 稳态下不会每帧触发 <see cref="Text"/> 的字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunnerHud : MonoBehaviour
    {
        /// <summary>跑道的列数（需求固定 30 列，1 列 = 1 米）。</summary>
        private const int LaneColumns = 30;

        /// <summary>
        /// 玩家固定的列下标。<b>需求写的是"左起第 3 列"，中文计数从 1 开始，所以数组下标是 2。</b>
        /// 玩家身后只留 2 列，正好覆盖内核"越过 <c>x &lt; -2</c> 才回收"的那 1.4 米余量。
        /// </summary>
        private const int PlayerColumn = 2;

        /// <summary>地面姿态。</summary>
        private const char PlayerGroundChar = '@';

        /// <summary>跳跃姿态（<c>PlayerY &gt; 0</c>）。</summary>
        private const char PlayerJumpChar = '^';

        /// <summary>下蹲姿态（<c>IsSliding</c>）。</summary>
        private const char PlayerSlideChar = '_';

        /// <summary>Low（贴地）障碍：只能跳过去。</summary>
        private const char LowObstacleChar = '▲';

        /// <summary>High（空中）障碍：只能蹲下去。</summary>
        private const char HighObstacleChar = '▼';

        /// <summary>空位（跑道基线）。</summary>
        private const char EmptyChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点"跳跃"。</summary>
        public event Action? OnJumpPressed;

        /// <summary>点"下蹲"。</summary>
        public event Action? OnSlidePressed;

        /// <summary>状态行：第一行距离/分数/速度，第二行姿态（结算后改写为结果文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>跑道字符视图：一行 30 列。</summary>
        [SerializeField] private Text laneText = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>
        /// "跳跃"按钮。<b>为什么它不在需求列出的三个字段里却必须存在</b>：需求把公开面锁成
        /// 两个 <c>Text</c> + 一个 <c>Button</c> 加三个事件，而 <see cref="OnJumpPressed"/> 要有人抬起来；
        /// 事件没有按钮就是死代码。这两个字段是<b>私有实现细节</b>（公开成员一个没增删），
        /// 场景构建脚本会把它们与其它字段一起注入。
        /// </summary>
        [SerializeField] private Button jumpButton = null!;

        /// <summary>"下蹲"按钮（与 <see cref="jumpButton"/> 同一条理由）。</summary>
        [SerializeField] private Button slideButton = null!;

        /// <summary>复用的跑道缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请数组。</summary>
        private readonly char[] _lane = new char[LaneColumns];

        /// <summary>复用的状态行缓冲：两行文本 + 数字，容量够用到底。</summary>
        private readonly StringBuilder _status = new StringBuilder(128);

        /// <summary>已写到控件上的跑道文本（脏检查基准）。</summary>
        private string _lastLane = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后若还有人继续推视图（例如场景里第二个
        /// 调用方），"跑酷结束 · …"会立刻被实时读数盖掉；这个标记让结果文案直到重开为止都不被覆盖。
        /// </summary>
        private bool _resultShown;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，而此时控制器的 <c>Awake</c>（订阅事件的地方）已经执行过，
        /// 不会出现"第一次点击没人接"的空窗。
        /// </summary>
        private void Start()
        {
            if (jumpButton != null)
            {
                jumpButton.onClick.AddListener(RaiseJumpPressed);
            }

            if (slideButton != null)
            {
                slideButton.onClick.AddListener(RaiseSlidePressed);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (jumpButton != null)
            {
                jumpButton.onClick.RemoveListener(RaiseJumpPressed);
            }

            if (slideButton != null)
            {
                slideButton.onClick.RemoveListener(RaiseSlidePressed);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新状态行与跑道。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        /// <param name="speed">
        /// 当前速度（米/秒）。<b>为什么单独传</b>：<see cref="IRunnerReadView"/> 没有速度成员
        /// （它是 <c>RunnerWorld</c> 的类属性，不在进出门契约里），HUD 不该为了一个读数去引用内核具体类型。
        /// </param>
        public void Render(IRunnerReadView view, float speed)
        {
            if (view == null)
            {
                return;
            }

            // 重开之后视图回到"未结束"：内部状态跟着复位，结果文案不再霸占状态行。
            if (_resultShown && !view.IsGameOver)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
            }

            UpdateLane(view);

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view, speed));
            }
        }

        /// <summary>
        /// 显示结果文案（胜负 + 最终分数）。<b>只改状态行、不动跑道视图</b>：
        /// 终局画面仍要看到最后一段跑道，否则玩家分不清"撞上了"与"界面停了"。
        /// </summary>
        /// <param name="isVictory">内核给出的胜负（<c>Score ≥ 500</c> 即跑满 50 米为胜）。</param>
        /// <param name="score">本局最终分数（结算前抄下的快照）。</param>
        public void ShowResult(bool isVictory, int score)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append(isVictory ? "跑酷结束 · 胜利" : "跑酷结束 · 撞上障碍")
                .Append(" · 分数 ").Append(score)
                .Append(RowSeparator)
                .Append("点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 30 列跑道：先铺满基线 → 把未命中且还在可视范围内的障碍按 <c>1 列 = 1 米</c> 量化成列
        /// → 最后画玩家（玩家永远可见，不会被同一列上的障碍盖掉）。
        /// <para>
        /// <b>为什么玩家最后画</b>：碰撞瞬间障碍的 <c>|X| ≤ 0.6</c> 会落在玩家列或相邻列，
        /// 若让障碍覆盖玩家，那一帧正好看不出"我是在跳还是蹲"，而这恰恰是玩家最需要看到的反馈。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateLane(IRunnerReadView view)
        {
            for (int i = 0; i < _lane.Length; i++)
            {
                _lane[i] = EmptyChar;
            }

            int obstacleCount = view.ObstacleCount;
            for (int i = 0; i < obstacleCount; i++)
            {
                RunnerObstacleView obstacle = view.GetObstacle(i);

                // ObstacleId == 0 是 GetObstacle 越界时的默认值，不是合法障碍。
                if (obstacle.ObstacleId == 0)
                {
                    continue;
                }

                int column = PlayerColumn + ToColumnOffset(obstacle.X);
                if (column < 0 || column >= LaneColumns || column == PlayerColumn)
                {
                    continue;
                }

                _lane[column] = obstacle.Kind == RunnerObstacleKind.Low ? LowObstacleChar : HighObstacleChar;
            }

            _lane[PlayerColumn] = PlayerChar(view);

            string lane = new string(_lane);
            if (lane != _lastLane)
            {
                _lastLane = lane;
                if (laneText != null)
                {
                    laneText.text = lane;
                }
            }
        }

        /// <summary>
        /// 玩家当前姿态：跳跃（离地）→ <c>^</c>、下蹲 → <c>_</c>、地面 → <c>@</c>。
        /// <b>跳跃优先于下蹲</b>：内核里起跳会结束下蹲，两者不会同时为真；这里只是把优先级写明。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>一个姿态字符。</returns>
        private static char PlayerChar(IRunnerReadView view)
        {
            if (view.PlayerY > 0f)
            {
                return PlayerJumpChar;
            }

            return view.IsSliding ? PlayerSlideChar : PlayerGroundChar;
        }

        /// <summary>
        /// 把障碍的"相对玩家的横坐标（米）"量化成列偏移：<c>round(X)</c>，并夹在 ±64 内。
        /// <para>
        /// <b>为什么不用 <c>(int)X</c> 直接截断</b>：负数的截断是朝 0 取整（<c>-0.4 → 0</c>），
        /// 会让刚越过的障碍瞬间跳到玩家列上；四舍五入才是"最近的米"。
        /// </para>
        /// <para>
        /// <b>为什么要夹取</b>：<c>float.NaN</c> 与无穷值转 <c>int</c> 在 .NET 里是未定义结果，
        /// 夹到 ±64 之后所有越界值都稳定地落在跑道外（由调用方判界丢弃）。
        /// </para>
        /// </summary>
        /// <param name="relativeX">障碍相对玩家的横坐标（米）。</param>
        /// <returns>-64..64 的列偏移。</returns>
        private static int ToColumnOffset(float relativeX)
        {
            // 用否定比较：NaN 会走进这里被夹成 -64，避免 NaN 参与后续取整。
            if (!(relativeX > -64f))
            {
                return -64;
            }

            if (relativeX > 64f)
            {
                return 64;
            }

            return (int)Math.Floor(relativeX + 0.5f);
        }

        /// <summary>
        /// 拼状态行：第一行距离/分数/速度，第二行姿态。
        /// <b>为什么分两行</b>：四个读数塞一行会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的往往是"速度"这种最能解释"为什么这一跳没跳过去"的数字。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="speed">当前速度（米/秒）。</param>
        /// <returns>两行状态文本。</returns>
        private string BuildStatus(IRunnerReadView view, float speed)
        {
            _status.Clear();
            _status.Append("距离 ").Append(view.Distance.ToString("F1")).Append("m")
                .Append(" · 分数 ").Append(view.Score)
                .Append(" · 速度 ").Append(speed.ToString("F1")).Append("m/s")
                .Append(RowSeparator)
                .Append("状态 ").Append(PoseLabel(view));

            return _status.ToString();
        }

        /// <summary>
        /// 姿态的中文名（只用于展示，不参与任何运算）。
        /// 已结束（撞上）优先于姿态：那一帧玩家最需要知道的是"这局完了"，而不是"当时在跳"。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>姿态标签。</returns>
        private static string PoseLabel(IRunnerReadView view)
        {
            if (view.IsGameOver)
            {
                return "已撞上障碍";
            }

            if (view.PlayerY > 0f)
            {
                return "跳跃中";
            }

            return view.IsSliding ? "下蹲中" : "奔跑中";
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

        /// <summary>转发"跳跃"。</summary>
        private void RaiseJumpPressed()
        {
            Action? handler = OnJumpPressed;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"下蹲"。</summary>
        private void RaiseSlidePressed()
        {
            Action? handler = OnSlidePressed;
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
    }
}

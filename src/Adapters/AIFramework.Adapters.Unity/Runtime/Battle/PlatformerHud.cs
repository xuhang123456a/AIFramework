#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Platformer;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 平台跳跃的 uGUI 皮肤：状态行（关卡/分数/收集品/是否在地面/胜负）+ 20×10 字符简图 +
    /// 左移/右移/跳跃 + "重新开始" + "下一关"。
    /// <para>
    /// <b>为什么用字符简图而不是 8 个平台 <see cref="Image"/> + 8 个收集品控件</b>：平台跳跃的信息量全在
    /// "地形与目标的相对位置"上；一张等宽字符表就能表达"平台在哪、还剩哪几个收集品、人走到哪了"，
    /// 场景里只有一个 <see cref="Text"/>、零池化、零 <c>Instantiate</c>，而且内核算出的平台数/收集品数
    /// 随关卡变化时不需要重建任何层级。字符视图也正好让无头环境可以断言（同一份视图能打日志）。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：能不能起跳、有没有落地、收集品是否算已拿、什么时候通关全部由内核判定；
    /// HUD 只负责"把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，
    /// UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>量化口径</b>：世界坐标按关卡包围盒（所有平台 + 所有收集品的最小外接矩形）归一化到 20 列 × 10 行；
    /// 平台按"格心落在矩形里"落笔，收集品与玩家按"点落在哪一格"落笔。
    /// <b>为什么分轴归一化而不是单一比例尺</b>：内置三关的包围盒是 40~56 米宽、3~5 米高
    /// （地面 1 米 + 最高的浮台/收集品），真按"同一条比例尺塞进 20×10"只能占满 10 行里的 2~3 行，
    /// 纵向的高低差会全部挤成一条线——而"浮台比地面高多少"正是这张图要表达的东西。
    /// 分轴归一化后，图永远填满 20×10，代价是纵横比例不等（字符本身也不等宽，这一点在字符画里是可接受的）。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行与字符简图都先比后写。稳态下图只在玩家跨格/收集品消失时才真正变化，
    /// 不会每帧触发 <see cref="Text"/> 的字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlatformerHud : MonoBehaviour
    {
        /// <summary>简图列数（固定 20，与场景里的控件宽度对应）。</summary>
        private const int MapColumns = 20;

        /// <summary>简图行数（固定 10）。</summary>
        private const int MapRows = 10;

        /// <summary>一行在缓冲里的步长（20 列 + 1 个换行）。</summary>
        private const int MapStride = MapColumns + 1;

        /// <summary>整个简图缓冲的长度：10 行 × 21，去掉最后一个换行。</summary>
        private const int MapLength = (MapRows * MapStride) - 1;

        /// <summary>平台（实心方块）。</summary>
        private const char PlatformChar = '■';

        /// <summary>还没拿到的收集品（空心圆）。</summary>
        private const char CollectibleChar = '○';

        /// <summary>玩家（打在 AABB 左下角那一格）。</summary>
        private const char PlayerChar = '@';

        /// <summary>空地：既没有平台也没有目标。</summary>
        private const char EmptyChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点"下一关"。</summary>
        public event Action? OnNextLevelClicked;

        /// <summary>点"左移"。</summary>
        public event Action? OnLeftPressed;

        /// <summary>点"右移"。</summary>
        public event Action? OnRightPressed;

        /// <summary>点"跳跃"。</summary>
        public event Action? OnJumpPressed;

        /// <summary>状态行：关卡、分数、收集品、是否在地面、胜负（结算后改写为结果文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>字符简图：10 行 × 20 列（平台 ■ / 收集品 ○ / 玩家 @ / 空 ·）。</summary>
        [SerializeField] private Text mapText = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>"下一关"按钮。</summary>
        [SerializeField] private Button nextLevelButton = null!;

        /// <summary>"左移"按钮。</summary>
        [SerializeField] private Button leftButton = null!;

        /// <summary>"右移"按钮。</summary>
        [SerializeField] private Button rightButton = null!;

        /// <summary>"跳跃"按钮。</summary>
        [SerializeField] private Button jumpButton = null!;

        /// <summary>复用的简图缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请数组。</summary>
        private readonly char[] _map = new char[MapLength];

        /// <summary>复用的状态行缓冲：两行文本 + 数字，容量够用到底。</summary>
        private readonly StringBuilder _status = new StringBuilder(256);

        /// <summary>已写到控件上的简图（脏检查基准）。</summary>
        private string _lastMap = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后控制器仍可能继续推视图，
        /// 若每次都重写状态行，"通关！…"会立刻被下一帧的实时读数盖掉。
        /// </summary>
        private bool _resultShown;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，此时订阅不会与控制器首帧渲染抢顺序。
        /// </summary>
        private void Start()
        {
            if (leftButton != null)
            {
                leftButton.onClick.AddListener(RaiseLeftPressed);
            }

            if (rightButton != null)
            {
                rightButton.onClick.AddListener(RaiseRightPressed);
            }

            if (jumpButton != null)
            {
                jumpButton.onClick.AddListener(RaiseJumpPressed);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }

            if (nextLevelButton != null)
            {
                nextLevelButton.onClick.AddListener(RaiseNextLevelClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (leftButton != null)
            {
                leftButton.onClick.RemoveListener(RaiseLeftPressed);
            }

            if (rightButton != null)
            {
                rightButton.onClick.RemoveListener(RaiseRightPressed);
            }

            if (jumpButton != null)
            {
                jumpButton.onClick.RemoveListener(RaiseJumpPressed);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }

            if (nextLevelButton != null)
            {
                nextLevelButton.onClick.RemoveListener(RaiseNextLevelClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新字符简图与状态行。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IPlatformerReadView view)
        {
            if (view == null)
            {
                return;
            }

            // 重开/换关之后视图回到"未结束"：内部状态跟着复位，"通关！…"不再霸占状态行。
            if (_resultShown && !view.IsFinished)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
            }

            UpdateMap(view);

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view));
            }
        }

        /// <summary>
        /// 显示结果文案（胜负 + 本局得分）。<b>只改状态行、不动字符简图</b>：
        /// 终局画面仍要看到玩家最后停在哪、还差哪几个收集品，否则玩家分不清"打完了"与"界面停了"。
        /// </summary>
        /// <param name="isVictory">是否通关（到达终点并落地）。</param>
        /// <param name="score">本局最终得分。</param>
        public void ShowResult(bool isVictory, int score)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append(isVictory ? "通关！" : "挑战失败")
                .Append(" · 得分 ").Append(score)
                .Append(RowSeparator)
                .Append(isVictory ? "点『下一关』继续，或『重新开始』再打一遍" : "点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 20×10 字符简图：先铺满空地，再按"平台 → 收集品 → 玩家"的顺序覆盖。
        /// <para>
        /// <b>为什么是这个覆盖顺序</b>：平台是地板（先画），收集品要浮在平台上方才看得见（后画），
        /// 玩家永远在最上面（最后画）——三层叠加的判断顺序就是"谁的符号优先"。
        /// </para>
        /// <para>
        /// <b>为什么包围盒只算平台与收集品</b>：这是"关卡"的包围盒。玩家掉出世界时 <c>PlayerY</c> 会到
        /// <c>-10</c> 米，若把它也算进去，整张图会被压缩成一条线；现在它只是被夹到最底行，
        /// 正好表达"人掉下去了"。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateMap(IPlatformerReadView view)
        {
            for (int i = 0; i < _map.Length; i++)
            {
                _map[i] = EmptyChar;
            }

            for (int line = 0; line < MapRows - 1; line++)
            {
                _map[(line * MapStride) + MapColumns] = RowSeparator;
            }

            float minX;
            float maxX;
            float minY;
            float maxY;
            if (!TryGetLevelBounds(view, out minX, out maxX, out minY, out maxY))
            {
                PublishMap();
                return;
            }

            float spanX = maxX - minX;
            if (spanX <= 0f)
            {
                spanX = 1f;
            }

            float spanY = maxY - minY;
            if (spanY <= 0f)
            {
                spanY = 1f;
            }

            // 平台按"格心落在矩形里"落笔，而不是按矩形跨越的列/行区间整片填满。
            // 后者在 20 列跨 48 米时会把坑填平：4 米宽的坑只有 1.7 列，两侧边缘的取整一贴就接上了，
            // 而"哪里有坑"正是这张图最该说清的事（格心采样会如实留下空格）。
            float cellWidth = spanX / MapColumns;
            float cellHeight = spanY / MapRows;

            int platformCount = view.PlatformCount;
            for (int i = 0; i < platformCount; i++)
            {
                PlatformerView platform = view.GetPlatform(i);

                float left = platform.X;
                float right = platform.X + platform.Width;
                float bottom = platform.Y;
                float top = platform.Y + platform.Height;
                bool painted = false;

                for (int line = 0; line < MapRows; line++)
                {
                    int row = (MapRows - 1) - line;
                    float centerY = minY + ((row + 0.5f) * cellHeight);

                    if (centerY < bottom || centerY > top)
                    {
                        continue;
                    }

                    int offset = line * MapStride;
                    for (int column = 0; column < MapColumns; column++)
                    {
                        float centerX = minX + ((column + 0.5f) * cellWidth);
                        if (centerX < left || centerX > right)
                        {
                            continue;
                        }

                        _map[offset + column] = PlatformChar;
                        painted = true;
                    }
                }

                // 比一格还薄的地形按"顶面中点"落一格：宁可夸张一点，也不能让一块平台从图上整块消失。
                if (!painted)
                {
                    WriteCell(
                        platform.X + (platform.Width * 0.5f),
                        top,
                        PlatformChar,
                        minX,
                        spanX,
                        minY,
                        spanY);
                }
            }

            int collectibleCount = view.CollectibleCount;
            for (int i = 0; i < collectibleCount; i++)
            {
                CollectibleView collectible = view.GetCollectible(i);
                if (collectible.Collected)
                {
                    continue;
                }

                WriteCell(collectible.X, collectible.Y, CollectibleChar, minX, spanX, minY, spanY);
            }

            // 玩家用左沿 + 脚底这一格：视图给的就是 AABB 左下角，不额外假设玩家尺寸。
            WriteCell(view.PlayerX, view.PlayerY, PlayerChar, minX, spanX, minY, spanY);

            PublishMap();
        }

        /// <summary>把一格写成 <paramref name="value"/>（按世界坐标量化到行列）。</summary>
        /// <param name="x">世界横坐标（米）。</param>
        /// <param name="y">世界纵坐标（米）。</param>
        /// <param name="value">要写入的字符。</param>
        /// <param name="minX">包围盒左边界。</param>
        /// <param name="spanX">包围盒宽度。</param>
        /// <param name="minY">包围盒下边界。</param>
        /// <param name="spanY">包围盒高度。</param>
        private void WriteCell(
            float x, float y, char value, float minX, float spanX, float minY, float spanY)
        {
            int column = Quantize(x, minX, spanX, MapColumns);
            int row = Quantize(y, minY, spanY, MapRows);
            _map[(((MapRows - 1) - row) * MapStride) + column] = value;
        }

        /// <summary>把缓冲里的简图推给控件（文本未变就不碰控件，避免每帧重建字形）。</summary>
        private void PublishMap()
        {
            string map = new string(_map);
            if (map == _lastMap)
            {
                return;
            }

            _lastMap = map;
            if (mapText != null)
            {
                mapText.text = map;
            }
        }

        /// <summary>
        /// 计算关卡包围盒：所有平台（含底面）与所有收集品（含中心点）的最小外接矩形。
        /// <b>没有平台时返回 <c>false</c></b>（此时没有可画的地形，简图保持空白，而不是除零）。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="minX">左边界（米）。</param>
        /// <param name="maxX">右边界（米）。</param>
        /// <param name="minY">下边界（米）。</param>
        /// <param name="maxY">上边界（米）。</param>
        /// <returns>是否有至少一块平台。</returns>
        private static bool TryGetLevelBounds(
            IPlatformerReadView view, out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = float.MaxValue;
            maxX = float.MinValue;
            minY = float.MaxValue;
            maxY = float.MinValue;

            int platformCount = view.PlatformCount;
            for (int i = 0; i < platformCount; i++)
            {
                PlatformerView platform = view.GetPlatform(i);

                if (platform.X < minX)
                {
                    minX = platform.X;
                }

                if (platform.X + platform.Width > maxX)
                {
                    maxX = platform.X + platform.Width;
                }

                if (platform.Y < minY)
                {
                    minY = platform.Y;
                }

                if (platform.Y + platform.Height > maxY)
                {
                    maxY = platform.Y + platform.Height;
                }
            }

            int collectibleCount = view.CollectibleCount;
            for (int i = 0; i < collectibleCount; i++)
            {
                CollectibleView collectible = view.GetCollectible(i);

                if (collectible.X < minX)
                {
                    minX = collectible.X;
                }

                if (collectible.X > maxX)
                {
                    maxX = collectible.X;
                }

                if (collectible.Y < minY)
                {
                    minY = collectible.Y;
                }

                if (collectible.Y > maxY)
                {
                    maxY = collectible.Y;
                }
            }

            return platformCount > 0;
        }

        /// <summary>
        /// 把世界坐标量化成 <c>0..count-1</c> 的格子下标：<c>(value - min) / span × count</c>，再夹进区间。
        /// <para>
        /// <b>为什么用 <c>!(normalized &gt; 0)</c> 而不是 <c>normalized &lt;= 0</c></b>：<c>NaN</c> 参与任何比较
        /// 都是 false，只有这种写法能把它一起接到第一格；否则 <c>(int)NaN</c> 会把玩家画到图外。
        /// </para>
        /// </summary>
        /// <param name="value">世界坐标（米）。</param>
        /// <param name="min">包围盒下/左边界。</param>
        /// <param name="span">包围盒宽/高（&gt; 0）。</param>
        /// <param name="count">格子数。</param>
        /// <returns>格子下标（0..count-1）。</returns>
        private static int Quantize(float value, float min, float span, int count)
        {
            float normalized = (value - min) / span;

            if (!(normalized > 0f))
            {
                return 0;
            }

            if (normalized >= 1f)
            {
                return count - 1;
            }

            return (int)(normalized * count);
        }

        /// <summary>
        /// 拼状态行：第一行关卡/分数/收集品，第二行是否在地面 + 胜负。
        /// <b>为什么分两行</b>：五个读数塞一行会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的正是"是否在地面"这种操作时最需要看见的状态。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>两行状态文本。</returns>
        private string BuildStatus(IPlatformerReadView view)
        {
            _status.Clear();
            _status.Append("关卡 ").Append(LevelLabel(view))
                .Append(" · 分数 ").Append(view.Score)
                .Append(" · 收集 ").Append(view.CollectedCount).Append('/').Append(view.CollectibleCount)
                .Append(RowSeparator)
                .Append(view.IsOnGround ? "在地面" : "空中")
                .Append(" · ").Append(OutcomeLabel(view));

            return _status.ToString();
        }

        /// <summary>
        /// 关卡号。<b>为什么向具体类型取</b>：<see cref="IPlatformerReadView"/> 刻意没有关卡号——
        /// 它是"进场参数"（与 <see cref="AIFramework.Contracts.Gameplay.GameplayInitData.LevelId"/>
        /// 同源），不是局内状态；而状态行必须能显示"第几关"。因此这里只向
        /// <see cref="PlatformerWorld.LevelId"/> 要一个展示用编号（内核已夹取过），拿不到时显示 <c>?</c>，
        /// <b>绝不从平台数/收集品数反推关卡</b>——那是第二套关卡表。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>关卡号文本。</returns>
        private static string LevelLabel(IPlatformerReadView view)
        {
            PlatformerWorld? world = view as PlatformerWorld;
            return world != null ? world.LevelId.ToString() : "?";
        }

        /// <summary>胜负文案：未结束时是"进行中"，结束后按内核的判定给"已通关 / 挑战失败"。</summary>
        /// <param name="view">只读视图。</param>
        /// <returns>胜负标签。</returns>
        private static string OutcomeLabel(IPlatformerReadView view)
        {
            if (!view.IsFinished)
            {
                return "进行中";
            }

            return view.IsVictory ? "已通关" : "挑战失败";
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

        /// <summary>转发"重新开始"。</summary>
        private void RaiseRestartClicked()
        {
            Action? handler = OnRestartClicked;
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

        /// <summary>转发"左移"。</summary>
        private void RaiseLeftPressed()
        {
            Action? handler = OnLeftPressed;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"右移"。</summary>
        private void RaiseRightPressed()
        {
            Action? handler = OnRightPressed;
            if (handler != null)
            {
                handler();
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
    }
}

#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 肉鸽卡牌的 uGUI 皮肤：状态行（层数/节点种类/金币/卡组/能量/敌我 HP）+ 单行 8 格地图进度 +
    /// 5 行手牌 + "重开 / 前进 / 结束回合" + 5 个手牌按钮。
    /// <para>
    /// <b>为什么地图用一行字符</b>：地图是"8 层线性、每层一个节点"，一个 <see cref="Text"/> 就能表达
    /// 全部进度（<c>⚔✦$♥☠</c> 表示未进入的节点种类，已访问的用 <c>·</c> 覆盖），零池化、
    /// 零 <c>Instantiate</c>；换成 8 个 <see cref="Image"/> 节点只会每帧多 8 次脏标记。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：能不能前进、能不能出牌、伤害与格挡、商店与营地效果、胜负全部由内核判定；
    /// HUD 只负责"把只读数据翻译成文本/字符"和"把手指点位翻译成事件"。这条边界一旦破了，
    /// UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>手牌为什么只有 5 个固定按钮</b>：内核每回合抽 5 张（<c>HandSize</c>），所以下标 0..4 就是全部手牌；
    /// 固定按钮不需要池化，也不会出现"按钮比牌多/少"的错位。空位按钮置灰，点击事件本身不做任何过滤——
    /// 越界由内核返回 <c>false</c>。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行按字段快照比较、地图按字符缓冲比较、手牌按牌面字段比较，
    /// 三者都只在真变化时才重组字符串并写控件；稳态下（玩家不动）每帧零分配、零控件写入。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoguelikeHud : MonoBehaviour
    {
        /// <summary>地图槽位数（<c>RoguelikeNodeKind</c> 的 8 层线性地图；视图若更多则按需扩容）。</summary>
        private const int MapNodeSlots = 8;

        /// <summary>手牌槽位数（内核每回合抽 5 张，因此固定 5 个按钮与 5 行文本）。</summary>
        private const int HandSlots = 5;

        /// <summary>已访问节点的占位符（需求固定为 <c>·</c>）。</summary>
        private const char VisitedNodeChar = '·';

        /// <summary>未识别的节点种类（只为不让 HUD 抛异常，正常运行不会出现）。</summary>
        private const char UnknownNodeChar = '?';

        /// <summary>普通战斗节点符号。</summary>
        private const char BattleNodeChar = '⚔';

        /// <summary>精英战斗节点符号。</summary>
        private const char EliteNodeChar = '✦';

        /// <summary>商店节点符号。</summary>
        private const char ShopNodeChar = '$';

        /// <summary>营地节点符号。</summary>
        private const char RestNodeChar = '♥';

        /// <summary>Boss 节点符号。</summary>
        private const char BossNodeChar = '☠';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>状态行里的字段连接符（与其它 HUD 一致）。</summary>
        private const string Separator = " · ";

        /// <summary>空手牌槽位的占位文本。</summary>
        private const string EmptyCardText = "—";

        /// <summary>点"重开一轮"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点"前进"。</summary>
        public event Action? OnEnterNodeClicked;

        /// <summary>点"结束回合"。</summary>
        public event Action? OnEndTurnClicked;

        /// <summary>点了第 <paramref name="handIndex"/> 张手牌（下标 0..4，不是 <c>CardId</c>）。</summary>
        public event Action<int>? OnCardClicked;

        /// <summary>状态行：层数/节点种类/金币/卡组/能量/敌我 HP（两行；结算后改写为结果文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>地图进度行：单行 8 个字符（未访问显示节点种类，已访问显示 <c>·</c>）。</summary>
        [SerializeField] private Text mapText = null!;

        /// <summary>手牌文本：5 行，每行 <c>序号.名称(费/伤/挡)</c>。</summary>
        [SerializeField] private Text handText = null!;

        /// <summary>"重开一轮"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>"前进"按钮。</summary>
        [SerializeField] private Button enterNodeButton = null!;

        /// <summary>"结束回合"按钮。</summary>
        [SerializeField] private Button endTurnButton = null!;

        /// <summary>0 号手牌按钮。</summary>
        [SerializeField] private Button card0Button = null!;

        /// <summary>1 号手牌按钮。</summary>
        [SerializeField] private Button card1Button = null!;

        /// <summary>2 号手牌按钮。</summary>
        [SerializeField] private Button card2Button = null!;

        /// <summary>3 号手牌按钮。</summary>
        [SerializeField] private Button card3Button = null!;

        /// <summary>4 号手牌按钮。</summary>
        [SerializeField] private Button card4Button = null!;

        /// <summary>复用的地图字符缓冲（按 <see cref="IRoguelikeReadView.NodeCount"/> 使用前 N 格）。</summary>
        private char[] _map = new char[MapNodeSlots];

        /// <summary>已写到控件上的地图字符（脏检查基准）。</summary>
        private char[] _lastMap = new char[MapNodeSlots];

        /// <summary><see cref="_lastMap"/> 的有效长度；<c>-1</c> 表示还没有写过。</summary>
        private int _lastMapLength = -1;

        /// <summary>复用的状态行缓冲。</summary>
        private readonly StringBuilder _status = new StringBuilder(160);

        /// <summary>复用的手牌行缓冲。</summary>
        private readonly StringBuilder _hand = new StringBuilder(240);

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>已写到控件上的手牌文本（脏检查基准）。</summary>
        private string _lastHand = string.Empty;

        /// <summary>状态行的字段快照：任一字段变化才重组字符串。</summary>
        private int _snapDepth;
        private int _snapNodeIndex;
        private RoguelikeNodeKind _snapKind;
        private int _snapPlayerHp;
        private int _snapPlayerMaxHp;
        private int _snapGold;
        private int _snapDeckSize;
        private int _snapEnergy;
        private int _snapEnemyHp;
        private int _snapEnemyMaxHp;
        private bool _snapInBattle;
        private bool _snapHasNodes;

        /// <summary>状态行快照是否有效（首帧与重开后为 <c>false</c>，保证一定重建一次）。</summary>
        private bool _hasStatusSnapshot;

        /// <summary>手牌的牌面快照（下标 == 手牌槽位）；只在 <see cref="_handCacheCount"/> 内有效。</summary>
        private readonly int[] _handCacheIds = new int[HandSlots];

        /// <summary>手牌快照的另一半：每张牌的三个数值（费/伤/挡），按槽位 ×3 平铺。</summary>
        private readonly int[] _handCacheStats = new int[HandSlots * 3];

        /// <summary><see cref="_handCacheIds"/> / <see cref="_handCacheStats"/> 的有效张数；<c>-1</c> 表示无效。</summary>
        private int _handCacheCount = -1;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后控制器已停止推视图，但重开之后的
        /// <see cref="Render"/> 要能把状态行从"结果"切回实时读数（见 <see cref="Render"/>）。
        /// </summary>
        private bool _resultShown;

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

            if (enterNodeButton != null)
            {
                enterNodeButton.onClick.AddListener(RaiseEnterNodeClicked);
            }

            if (endTurnButton != null)
            {
                endTurnButton.onClick.AddListener(RaiseEndTurnClicked);
            }

            if (card0Button != null)
            {
                card0Button.onClick.AddListener(RaiseCard0Clicked);
            }

            if (card1Button != null)
            {
                card1Button.onClick.AddListener(RaiseCard1Clicked);
            }

            if (card2Button != null)
            {
                card2Button.onClick.AddListener(RaiseCard2Clicked);
            }

            if (card3Button != null)
            {
                card3Button.onClick.AddListener(RaiseCard3Clicked);
            }

            if (card4Button != null)
            {
                card4Button.onClick.AddListener(RaiseCard4Clicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }

            if (enterNodeButton != null)
            {
                enterNodeButton.onClick.RemoveListener(RaiseEnterNodeClicked);
            }

            if (endTurnButton != null)
            {
                endTurnButton.onClick.RemoveListener(RaiseEndTurnClicked);
            }

            if (card0Button != null)
            {
                card0Button.onClick.RemoveListener(RaiseCard0Clicked);
            }

            if (card1Button != null)
            {
                card1Button.onClick.RemoveListener(RaiseCard1Clicked);
            }

            if (card2Button != null)
            {
                card2Button.onClick.RemoveListener(RaiseCard2Clicked);
            }

            if (card3Button != null)
            {
                card3Button.onClick.RemoveListener(RaiseCard3Clicked);
            }

            if (card4Button != null)
            {
                card4Button.onClick.RemoveListener(RaiseCard4Clicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新状态行、地图行、手牌与按钮可用性。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IRoguelikeReadView view)
        {
            if (view == null)
            {
                return;
            }

            // 重开之后视图回到"未结束"：内部状态跟着复位，结果文案不再霸占状态行。
            if (_resultShown && !view.IsRunOver)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
                _hasStatusSnapshot = false;
            }

            UpdateMap(view);
            UpdateHand(view);
            UpdateButtons(view);

            if (!_resultShown)
            {
                UpdateStatus(view);
            }
        }

        /// <summary>
        /// 显示结果文案（胜负 + 本轮胜场）。<b>只改状态行、不动地图与手牌</b>：
        /// 终局画面仍要看到最后一张地图与最后一手牌，否则玩家分不清"打完了"与"界面停了"。
        /// </summary>
        /// <param name="isVictory">是否通关（打赢第 8 层 Boss）。</param>
        /// <param name="battlesWon">本轮打赢的战斗场数（内核累计值）。</param>
        public void ShowResult(bool isVictory, int battlesWon)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append(isVictory ? "通关！打穿了第 8 层 Boss" : "阵亡…run 结束")
                .Append(Separator).Append("本轮胜场 ").Append(battlesWon)
                .Append(RowSeparator)
                .Append("点『重开这一轮』再来一次");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画地图行：未访问节点画种类符号（<c>⚔✦$♥☠</c>），已访问节点一律画 <c>·</c>。
        /// <para>
        /// <b>为什么按 <see cref="IRoguelikeReadView.NodeCount"/> 而不是写死 8</b>：槽位数是内核口径，
        /// 地图扩容时 HUD 不该成为第二个需要同步改的地方；缓冲只在视图报出更长的地图时才扩容。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateMap(IRoguelikeReadView view)
        {
            int nodeCount = view.NodeCount;
            if (nodeCount < 0)
            {
                nodeCount = 0;
            }

            if (nodeCount > _map.Length)
            {
                _map = new char[nodeCount];
                _lastMap = new char[nodeCount];
                _lastMapLength = -1;
            }

            for (int i = 0; i < nodeCount; i++)
            {
                RoguelikeNodeView node = view.GetNode(i);
                _map[i] = node.Visited ? VisitedNodeChar : NodeChar(node.Kind);
            }

            if (MapUnchanged(nodeCount))
            {
                return;
            }

            Array.Copy(_map, _lastMap, nodeCount);
            _lastMapLength = nodeCount;

            if (mapText != null)
            {
                mapText.text = new string(_map, 0, nodeCount);
            }
        }

        /// <summary>地图字符缓冲是否与上次写下去的一致（长度也要一致）。</summary>
        /// <param name="nodeCount">本次有效字符数。</param>
        /// <returns>完全一致返回 <c>true</c>。</returns>
        private bool MapUnchanged(int nodeCount)
        {
            if (_lastMapLength != nodeCount)
            {
                return false;
            }

            for (int i = 0; i < nodeCount; i++)
            {
                if (_map[i] != _lastMap[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 重画手牌：固定 5 行，每行 <c>序号.名称(费/伤/挡)</c>，没有牌的槽位画 <c>—</c>。
        /// <b>为什么固定 5 行而不是按张数伸缩</b>：行数跳变会让整个 <see cref="Text"/> 的高度与行距变化，
        /// 玩家会看到其它行"上下乱跳"；固定槽位是稳定版式。
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateHand(IRoguelikeReadView view)
        {
            int handCount = view.HandCount;
            if (handCount < 0)
            {
                handCount = 0;
            }

            if (handCount > HandSlots)
            {
                handCount = HandSlots;
            }

            if (HandUnchanged(view, handCount))
            {
                return;
            }

            _hand.Clear();
            for (int slot = 0; slot < HandSlots; slot++)
            {
                if (slot > 0)
                {
                    _hand.Append(RowSeparator);
                }

                _hand.Append(slot + 1).Append(". ");

                if (slot < handCount)
                {
                    RoguelikeCardView card = view.GetHandCard(slot);
                    _hand.Append(card.Name)
                        .Append("(费").Append(card.Cost)
                        .Append("/伤").Append(card.Damage)
                        .Append("/挡").Append(card.Block)
                        .Append(')');
                    _handCacheIds[slot] = card.CardId;
                    _handCacheStats[slot * 3] = card.Cost;
                    _handCacheStats[(slot * 3) + 1] = card.Damage;
                    _handCacheStats[(slot * 3) + 2] = card.Block;
                }
                else
                {
                    _hand.Append(EmptyCardText);
                    _handCacheIds[slot] = 0;
                    _handCacheStats[slot * 3] = 0;
                    _handCacheStats[(slot * 3) + 1] = 0;
                    _handCacheStats[(slot * 3) + 2] = 0;
                }
            }

            _handCacheCount = handCount;

            string text = _hand.ToString();
            if (text == _lastHand)
            {
                return;
            }

            _lastHand = text;
            if (handText != null)
            {
                handText.text = text;
            }
        }

        /// <summary>
        /// 手牌是否与上次渲染的一致。<b>为什么比字段而不是比字符串</b>：比较字符串要先拼出来，
        /// 而这个方法每帧都跑；按牌面字段比较可以在稳态下完全不分配。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="handCount">本次有效张数（已夹到 0..<see cref="HandSlots"/>）。</param>
        /// <returns>一致返回 <c>true</c>。</returns>
        private bool HandUnchanged(IRoguelikeReadView view, int handCount)
        {
            if (_handCacheCount != handCount || _lastHand.Length == 0)
            {
                return false;
            }

            for (int slot = 0; slot < handCount; slot++)
            {
                RoguelikeCardView card = view.GetHandCard(slot);
                if (_handCacheIds[slot] != card.CardId
                    || _handCacheStats[slot * 3] != card.Cost
                    || _handCacheStats[(slot * 3) + 1] != card.Damage
                    || _handCacheStats[(slot * 3) + 2] != card.Block)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 重建状态行（只在字段快照变化时）：第一行层数/节点种类/金币/卡组，第二行能量/敌我 HP。
        /// <b>为什么分两行</b>：一行塞下 6 个读数会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的正是"敌方 HP"这种最需要看见的读数。
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateStatus(IRoguelikeReadView view)
        {
            int nodeCount = view.NodeCount;
            int nodeIndex = view.CurrentNodeIndex;
            bool hasNodes = nodeCount > 0;
            RoguelikeNodeKind kind = nodeIndex >= 0 && nodeIndex < nodeCount
                ? view.GetNode(nodeIndex).Kind
                : RoguelikeNodeKind.Battle;

            if (_hasStatusSnapshot
                && _snapDepth == view.Depth
                && _snapNodeIndex == nodeIndex
                && _snapKind == kind
                && _snapPlayerHp == view.PlayerHp
                && _snapPlayerMaxHp == view.PlayerMaxHp
                && _snapGold == view.Gold
                && _snapDeckSize == view.DeckSize
                && _snapEnergy == view.Energy
                && _snapEnemyHp == view.EnemyHp
                && _snapEnemyMaxHp == view.EnemyMaxHp
                && _snapInBattle == view.InBattle
                && _snapHasNodes == hasNodes)
            {
                return;
            }

            _snapDepth = view.Depth;
            _snapNodeIndex = nodeIndex;
            _snapKind = kind;
            _snapPlayerHp = view.PlayerHp;
            _snapPlayerMaxHp = view.PlayerMaxHp;
            _snapGold = view.Gold;
            _snapDeckSize = view.DeckSize;
            _snapEnergy = view.Energy;
            _snapEnemyHp = view.EnemyHp;
            _snapEnemyMaxHp = view.EnemyMaxHp;
            _snapInBattle = view.InBattle;
            _snapHasNodes = hasNodes;
            _hasStatusSnapshot = true;

            _status.Clear();
            _status.Append("层 ").Append(view.Depth).Append('/').Append(nodeCount)
                .Append(Separator).Append(nodeIndex >= 0 ? NodeLabel(kind) : "未出发")
                .Append(Separator).Append("金币 ").Append(view.Gold)
                .Append(Separator).Append("卡组 ").Append(view.DeckSize).Append(" 张")
                .Append(RowSeparator)
                .Append("能量 ").Append(view.Energy)
                .Append(Separator).Append("我方 ").Append(view.PlayerHp).Append('/').Append(view.PlayerMaxHp)
                .Append(Separator);

            if (view.InBattle)
            {
                _status.Append("敌方 ").Append(view.EnemyHp).Append('/').Append(view.EnemyMaxHp)
                    .Append(Separator).Append("手牌 ").Append(view.HandCount).Append(" 张");
            }
            else
            {
                _status.Append("敌方 —").Append(Separator).Append("点『前进』进入下一层");
            }

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 按视图刷新按钮可用性。<b>只做"有没有这张牌 / 在不在战斗"这种视图级判断，
        /// 不判断"这张牌打不打得出去"</b>——费用与能量是内核规则，复制一份就会两侧分叉；
        /// 玩家点下去之后由内核返回 <c>false</c>，界面如实反映结果即可。
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateButtons(IRoguelikeReadView view)
        {
            int handCount = view.HandCount;
            bool inBattle = view.InBattle && !view.IsRunOver;

            SetInteractable(card0Button, inBattle && handCount > 0);
            SetInteractable(card1Button, inBattle && handCount > 1);
            SetInteractable(card2Button, inBattle && handCount > 2);
            SetInteractable(card3Button, inBattle && handCount > 3);
            SetInteractable(card4Button, inBattle && handCount > 4);

            SetInteractable(enterNodeButton, !inBattle && !view.IsRunOver);
            SetInteractable(endTurnButton, inBattle);
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

        /// <summary>设置按钮可用性；值相同时不写（<c>Selectable.interactable</c> 的写会触发样式重绘）。</summary>
        /// <param name="button">目标按钮。</param>
        /// <param name="value">是否可点。</param>
        private static void SetInteractable(Button button, bool value)
        {
            if (button != null && button.interactable != value)
            {
                button.interactable = value;
            }
        }

        /// <summary>节点种类 → 地图字符（只用于展示，不参与任何运算）。</summary>
        /// <param name="kind">节点种类。</param>
        /// <returns>单字符。</returns>
        private static char NodeChar(RoguelikeNodeKind kind)
        {
            switch (kind)
            {
                case RoguelikeNodeKind.Battle:
                    return BattleNodeChar;
                case RoguelikeNodeKind.Elite:
                    return EliteNodeChar;
                case RoguelikeNodeKind.Shop:
                    return ShopNodeChar;
                case RoguelikeNodeKind.Rest:
                    return RestNodeChar;
                case RoguelikeNodeKind.Boss:
                    return BossNodeChar;
                default:
                    return UnknownNodeChar;
            }
        }

        /// <summary>节点种类 → 状态行里的中文标签（只用于展示）。</summary>
        /// <param name="kind">节点种类。</param>
        /// <returns>中文标签。</returns>
        private static string NodeLabel(RoguelikeNodeKind kind)
        {
            switch (kind)
            {
                case RoguelikeNodeKind.Battle:
                    return "战斗";
                case RoguelikeNodeKind.Elite:
                    return "精英";
                case RoguelikeNodeKind.Shop:
                    return "商店";
                case RoguelikeNodeKind.Rest:
                    return "营地";
                case RoguelikeNodeKind.Boss:
                    return "Boss";
                default:
                    return "未知";
            }
        }

        /// <summary>转发"重开一轮"。</summary>
        private void RaiseRestartClicked()
        {
            Action? handler = OnRestartClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"前进"。</summary>
        private void RaiseEnterNodeClicked()
        {
            Action? handler = OnEnterNodeClicked;
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

        /// <summary>转发 0 号手牌点击。</summary>
        private void RaiseCard0Clicked()
        {
            RaiseCardClicked(0);
        }

        /// <summary>转发 1 号手牌点击。</summary>
        private void RaiseCard1Clicked()
        {
            RaiseCardClicked(1);
        }

        /// <summary>转发 2 号手牌点击。</summary>
        private void RaiseCard2Clicked()
        {
            RaiseCardClicked(2);
        }

        /// <summary>转发 3 号手牌点击。</summary>
        private void RaiseCard3Clicked()
        {
            RaiseCardClicked(3);
        }

        /// <summary>转发 4 号手牌点击。</summary>
        private void RaiseCard4Clicked()
        {
            RaiseCardClicked(4);
        }

        /// <summary>把手牌下标抬成 <see cref="OnCardClicked"/> 事件。</summary>
        /// <param name="handIndex">手牌下标 0..4。</param>
        private void RaiseCardClicked(int handIndex)
        {
            Action<int>? handler = OnCardClicked;
            if (handler != null)
            {
                handler(handIndex);
            }
        }
    }
}

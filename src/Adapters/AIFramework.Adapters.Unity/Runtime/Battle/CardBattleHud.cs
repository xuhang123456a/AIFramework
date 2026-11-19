#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 卡牌战斗的 uGUI 皮肤：把 <see cref="ICardBattleReadView"/> 画到控件上，把点击抛成事件。
    /// <para>
    /// <b>本类不含任何规则</b>：能不能打、够不够能量、打了以后掉多少血，全部由内核判定；HUD 只负责
    /// "把只读数据翻译成文本"和"把手指点位翻译成索引"。这条边界一旦破了，UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>手牌池化（只增不减）</b>：每次 <c>Render</c> 都可能换一整手牌，逐帧 <c>Instantiate/Destroy</c>
    /// 会持续制造 GC 峰值（卡牌 UI 每帧刷新一次）。因此模板只在池子不够长时克隆，多出来的牌只
    /// <c>SetActive(false)</c>，永远不销毁；牌上的点击回调在克隆时一次性绑定，之后不再重复注册。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行只在数值真变化时重组字符串与写控件。稳态下（玩家不动）每帧零分配、零控件写入。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardBattleHud : MonoBehaviour
    {
        /// <summary>没有任何内核快照时显示的文字。</summary>
        private const string WaitingText = "等待内核…";

        /// <summary>状态行里"我方"之前的连接符（与设计稿一致）。</summary>
        private const string Separator = " · ";

        /// <summary>回合/能量/血量状态行（一行显示）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>手牌容器（池化出来的牌都挂在这里）。</summary>
        [SerializeField] private Transform handRoot = null!;

        /// <summary>手牌按钮模板（未激活，内含 Text）；池子不够时才克隆它。</summary>
        [SerializeField] private GameObject cardTemplate = null!;

        /// <summary>结束回合按钮。</summary>
        [SerializeField] private Button endTurnButton = null!;

        /// <summary>池化的手牌对象（下标 == 手牌索引，只增不减）。</summary>
        private readonly List<GameObject> _cardObjects = new List<GameObject>();

        /// <summary>与 <see cref="_cardObjects"/> 同长的文字控件缓存（模板里可能没有 Text，故可为 <c>null</c>）。</summary>
        private readonly List<Text?> _cardLabels = new List<Text?>();

        /// <summary>与 <see cref="_cardObjects"/> 同长的"上次画上去的牌面数据"，用来跳过重复写文本。</summary>
        private readonly List<CardViewData> _cardCache = new List<CardViewData>();

        /// <summary>与 <see cref="_cardObjects"/> 同长的缓存有效标记（新克隆的牌还没有缓存值）。</summary>
        private readonly List<bool> _cardCacheValid = new List<bool>();

        /// <summary>已写到控件上的状态行；与目标文本相同就不再写控件（脏检查基准）。</summary>
        private string _statusLine = string.Empty;

        /// <summary>是否已有过一次内核快照（为 <c>false</c> 时状态行显示"等待内核…"）。</summary>
        private bool _hasSnapshot;

        private int _turn;
        private int _energy;
        private int _playerHp;
        private int _playerBlock;
        private int _enemyHp;
        private int _handCount;
        private int _drawPile;
        private int _discardPile;

        /// <summary>结算后的胜负文案（"胜利"/"失败"）；空串表示还没结算。</summary>
        private string _outcome = string.Empty;

        /// <summary>点了第 index 张手牌。索引是手牌下标，不是 <c>CardId</c>。</summary>
        public event Action<int>? OnCardClicked;

        /// <summary>点了"结束回合"。</summary>
        public event Action? OnEndTurnClicked;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，此时订阅不会与控制器首帧渲染抢顺序；而 <c>Awake</c> 阶段
        /// <c>SerializedObject</c> 注入的引用在真机上虽然已就绪，但订阅失败时没有日志窗口可查。
        /// </summary>
        private void Start()
        {
            if (endTurnButton != null)
            {
                endTurnButton.onClick.AddListener(RaiseEndTurnClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (endTurnButton != null)
            {
                endTurnButton.onClick.RemoveListener(RaiseEndTurnClicked);
            }
        }

        /// <summary>
        /// 渲染一帧。<paramref name="view"/> 为 <c>null</c> 表示内核还没就绪（或本局已放弃），
        /// 此时只显示"等待内核…"并把手牌收起来——绝不伪造一份假状态。
        /// </summary>
        /// <param name="view">内核只读视图；可能为 <c>null</c>。</param>
        public void Render(ICardBattleReadView? view)
        {
            if (view == null)
            {
                _hasSnapshot = false;
                RebuildStatus();
                HideCardsFrom(0);
                return;
            }

            int turn = view.TurnIndex;
            int energy = view.Energy;
            int playerHp = view.PlayerHp;
            int playerBlock = view.PlayerBlock;
            int enemyHp = view.EnemyHp;
            int hand = view.HandCount;
            int draw = view.DrawPileCount;
            int discard = view.DiscardPileCount;

            if (!_hasSnapshot
                || turn != _turn
                || energy != _energy
                || playerHp != _playerHp
                || playerBlock != _playerBlock
                || enemyHp != _enemyHp
                || hand != _handCount
                || draw != _drawPile
                || discard != _discardPile)
            {
                _hasSnapshot = true;
                _turn = turn;
                _energy = energy;
                _playerHp = playerHp;
                _playerBlock = playerBlock;
                _enemyHp = enemyHp;
                _handCount = hand;
                _drawPile = draw;
                _discardPile = discard;
                RebuildStatus();
            }

            RenderHand(view);
        }

        /// <summary>
        /// 结算后把胜负追加到状态行（"… · 胜利" / "… · 失败"）。
        /// <b>只追加不覆盖</b>：终局画面仍需看到最后一回合的血量与能量，否则玩家无法理解"为什么输了"。
        /// </summary>
        /// <param name="result">内核结算报告。</param>
        public void ShowResult(GameplayResult result)
        {
            _outcome = result.IsVictory ? "胜利" : "失败";
            RebuildStatus();
        }

        /// <summary>
        /// 按当前快照重组状态行。<b>唯一写 <see cref="statusText"/> 的地方</b>：集中在一处，
        /// 才能保证"文本变化才写控件"这条脏检查不会被某条分支绕过。
        /// </summary>
        private void RebuildStatus()
        {
            string line;
            if (!_hasSnapshot)
            {
                line = WaitingText;
            }
            else
            {
                line = "回合 " + _turn
                    + Separator + "能量 " + _energy
                    + Separator + "我 " + _playerHp + "/" + _playerBlock
                    + Separator + "敌 " + _enemyHp
                    + Separator + "手 " + _handCount + "/抽 " + _drawPile + "/弃 " + _discardPile;
            }

            if (_outcome.Length > 0)
            {
                line = line + Separator + _outcome;
            }

            if (line == _statusLine)
            {
                return;
            }

            _statusLine = line;
            if (statusText != null)
            {
                statusText.text = line;
            }
        }

        /// <summary>
        /// 画手牌：池子不够就补，够就复用；多出来的隐藏。
        /// <para>
        /// 文本只在牌面数据变化时重写（<see cref="SameCard"/> 比较），因此"打出一张牌"只会真正重写剩余几张，
        /// 而不是整手牌每帧重写一遍。
        /// </para>
        /// </summary>
        private void RenderHand(ICardBattleReadView view)
        {
            if (handRoot == null || cardTemplate == null)
            {
                return;
            }

            int count = view.HandCount;
            int visible = 0;

            for (int i = 0; i < count; i++)
            {
                if (!view.TryGetHandCard(i, out CardViewData card))
                {
                    // 契约承诺越界返回 false 而不是抛异常；有可能 HandCount 与可取数量暂时不一致，
                    // 此时"能取几张画几张"，不让一行不一致的数据把整帧 UI 打挂。
                    break;
                }

                EnsureCard(i);
                visible = i + 1;

                GameObject item = _cardObjects[i];
                if (!item.activeSelf)
                {
                    item.SetActive(true);
                }

                if (_cardCacheValid[i] && SameCard(card, _cardCache[i]))
                {
                    continue;
                }

                _cardCache[i] = card;
                _cardCacheValid[i] = true;

                Text? label = _cardLabels[i];
                if (label != null)
                {
                    label.text = CardText(card);
                }
            }

            HideCardsFrom(visible);
        }

        /// <summary>把从 <paramref name="firstHidden"/> 起的所有池内手牌收起来（不销毁）。</summary>
        private void HideCardsFrom(int firstHidden)
        {
            for (int i = firstHidden; i < _cardObjects.Count; i++)
            {
                GameObject item = _cardObjects[i];
                if (item.activeSelf)
                {
                    item.SetActive(false);
                }
            }
        }

        /// <summary>
        /// 确保池子里有第 <paramref name="index"/> 张牌：没有就克隆模板、激活、缓存文字控件，
        /// 并<b>一次性</b>绑定点击回调。
        /// <para>
        /// <b>为什么先 <c>SetActive(true)</c> 再取 <c>Text</c></b>：模板是非激活的，克隆出来也是非激活的，
        /// 而 <c>GetComponentInChildren</c> 默认跳过未激活对象——顺序反了会永远取不到文字控件。
        /// </para>
        /// </summary>
        private void EnsureCard(int index)
        {
            while (_cardObjects.Count <= index)
            {
                // 捕获下标：牌在池里的位置就是它的手牌索引，且池子只增不减、顺序不变，所以绑定一次即永久有效。
                int handIndex = _cardObjects.Count;

                GameObject item = Instantiate(cardTemplate, handRoot);
                item.name = "Card_" + handIndex;
                item.SetActive(true);

                Text? label = item.GetComponentInChildren<Text>();
                Button? button = item.GetComponentInChildren<Button>();
                if (button != null)
                {
                    button.onClick.AddListener(() => OnCardClicked?.Invoke(handIndex));
                }

                _cardObjects.Add(item);
                _cardLabels.Add(label);
                _cardCache.Add(default);
                _cardCacheValid.Add(false);
            }
        }

        /// <summary>"点了结束回合"翻译成视图事件；能不能结束由内核说话。</summary>
        private void RaiseEndTurnClicked()
        {
            OnEndTurnClicked?.Invoke();
        }

        /// <summary>
        /// 牌面文案：<c>"1费 6伤 3甲"</c>；伤害或护甲为 0 时省略对应片段（0 值不占屏幕）。
        /// </summary>
        private static string CardText(CardViewData card)
        {
            string text = card.Cost + "费";
            if (card.Damage > 0)
            {
                text = text + " " + card.Damage + "伤";
            }

            if (card.Block > 0)
            {
                text = text + " " + card.Block + "甲";
            }

            return text;
        }

        /// <summary>
        /// 逐字段比较两张牌。<b>为什么不用 <c>Equals</c></b>：<see cref="CardViewData"/> 没有重写
        /// <c>Equals</c>，走 <c>ValueType.Equals</c> 会做反射式比较，在每帧手牌刷新里是纯浪费。
        /// </summary>
        private static bool SameCard(CardViewData a, CardViewData b)
        {
            return a.CardId == b.CardId
                && a.Cost == b.Cost
                && a.Damage == b.Damage
                && a.Block == b.Block;
        }
    }
}

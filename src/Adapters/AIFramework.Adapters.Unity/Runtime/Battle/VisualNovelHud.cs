#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 视觉小说的 uGUI 皮肤：说话人一行 + 正文一块 + 状态行 + 一个"继续/重新开始"按钮 +
    /// 最多 3 个选项按钮。
    /// <para>
    /// <b>本类不含任何剧情规则</b>：哪些选项可用（条件是否满足）、点下去会走到哪个节点、
    /// 故事是不是结束、结局 id 是多少，全部由内核 <c>VisualNovelWorld</c> 决定。
    /// HUD 只做两件事：把只读视图翻译成文本/按钮可见性，把手指点位翻译成
    /// <see cref="OnAdvanceClicked"/> / <see cref="OnChoiceClicked"/> 事件。
    /// 一旦在这里自己算一遍"条件不满足就隐藏"，改内核而不改 UI 时两侧就会静默分叉。
    /// </para>
    /// <para>
    /// <b>为什么选项按钮按下标而不是按文本绑定</b>：内核的 <c>ChoiceCount</c> /
    /// <c>GetChoice(i)</c> / <c>TryChoose(i)</c> 是<b>同一个可用序号</b>（条件不满足的选项不占位），
    /// 所以表现层只要铺 <c>0..ChoiceCount-1</c>，再把玩家点到的序号原样回传即可。
    /// 中间任何一次"自己重排/过滤"，都会让界面上的第 2 个按钮变成内核里的第 1 个选项。
    /// </para>
    /// <para>
    /// <b>为什么固定 3 个槽位</b>：内置演示剧本的 Choice 节点最多 3 个可用选项（开局 3 选 1、
    /// 汇合点含条件选项时也是 3 个）。槽位是场景里真实存在的按钮，不做池化/动态实例化；
    /// 若将来剧本选项更多，这里只显示前 3 个并保留 <c>VisualNovelController.Choose(index)</c>
    /// 这条公开入口——那是数据侧的扩容点，不是本类该偷偷兜的事。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：说话人、正文、状态行、推进按钮文案、每个选项的可见性与文案都先比后写。
    /// 控制器每帧都会推一次视图（跳转/自动播放都要时间轴），但只有真正换节点的那一帧会重建字形。
    /// </para>
    /// <para>
    /// <b>结局卡为什么能盖住逐帧渲染</b>：控制器每帧 <see cref="Render"/>，而结算发生在
    /// "进入 End 节点"的同一帧。若不拦住，<see cref="ShowEnding"/> 写好的结局卡会被下一帧的
    /// 节点正文洗掉。规则是：已显示结局且视图仍然结束 ⇒ 直接返回；视图回到未结束（重开）
    /// ⇒ 复位内部脏检查基准，让下一帧重新铺满。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VisualNovelHud : MonoBehaviour
    {
        /// <summary>选项槽位数（与场景里注入的 <c>choiceButtons</c> 长度一致）。</summary>
        private const int MaxChoiceSlots = 3;

        /// <summary>推进按钮默认文案（Line 节点：点一下看下一句）。</summary>
        private const string AdvanceLabel = "继续";

        /// <summary>结局后推进按钮文案（行为改成"重开一局"，由控制器在 <see cref="OnAdvanceClicked"/> 里分流）。</summary>
        private const string RestartLabel = "重新开始";

        /// <summary>点"继续 / 重新开始"（控制器按是否已结束决定是推进还是重建）。</summary>
        public event Action? OnAdvanceClicked;

        /// <summary>点了第几个<b>可用</b>选项；参数与内核 <c>GetChoice(i)</c> / <c>TryChoose(i)</c> 同序。</summary>
        public event Action<int>? OnChoiceClicked;

        /// <summary>说话人（角色名 / "旁白" / 结局标题所在行）。</summary>
        [SerializeField] private Text speakerText = null!;

        /// <summary>正文（一句台词、一段旁白，或结算后的结局卡）。</summary>
        [SerializeField] private Text bodyText = null!;

        /// <summary>状态行：当前节点 / 已进入步数 / 可用选项数 / 结局总数（已持标记位时追加）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>"继续"按钮（结局后改文案为"重新开始"，不换绑监听器）。</summary>
        [SerializeField] private Button advanceButton = null!;

        /// <summary>
        /// 3 个选项按钮（长度 = <see cref="MaxChoiceSlots"/>），<b>下标 = 内核可用选项序号</b>。
        /// 未用到的槽位由本类 <c>SetActive(false)</c> 隐藏；
        /// 未注入（空数组）时只刷文本，不崩也不报错。
        /// </summary>
        [SerializeField] private Button[] choiceButtons = new Button[MaxChoiceSlots];

        /// <summary>每个槽位当前显示的选项文案（脏检查基准；空串 = 该槽位不可见）。</summary>
        private readonly string[] _choiceLabels = new string[MaxChoiceSlots];

        /// <summary>每个槽位当前的可见性（脏检查基准，初值 false：首帧必然写一次 <c>SetActive</c>）。</summary>
        private readonly bool[] _choiceVisible = new bool[MaxChoiceSlots];

        /// <summary>每个选项按钮的标题控件（按钮本体是 <see cref="Image"/>，文字必然在子物体上）。</summary>
        private readonly Text?[] _choiceTexts = new Text?[MaxChoiceSlots];

        /// <summary>
        /// 每个选项按钮各自的点击监听器。<b>为什么要把委托存起来</b>：<c>UnityEvent.RemoveListener</c>
        /// 按"目标 + 方法"匹配，每次现造一个 lambda 会退订不掉，场景重载后一次点击触发两次。
        /// </summary>
        private readonly UnityEngine.Events.UnityAction?[] _choiceHandlers =
            new UnityEngine.Events.UnityAction?[MaxChoiceSlots];

        /// <summary>推进按钮的标题控件（结局时要把"继续"改成"重新开始"）。</summary>
        private Text? _advanceText;

        /// <summary>按钮是否已解析并订阅（<c>Awake</c> 与首帧 <see cref="Render"/> 都可能先到，用标志兜住）。</summary>
        private bool _buttonsBound;

        /// <summary>推进按钮当前可见性（脏检查基准；默认可见，与场景初始状态一致）。</summary>
        private bool _advanceVisible = true;

        /// <summary>推进按钮当前文案（脏检查基准）。</summary>
        private string _lastAdvanceLabel = string.Empty;

        /// <summary>已写到控件上的说话人（脏检查基准）。</summary>
        private string _lastSpeaker = string.Empty;

        /// <summary>已写到控件上的正文（脏检查基准）。</summary>
        private string _lastBody = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示结局卡。<b>为什么需要它</b>：控制器每帧渲染，而结算就发生在进入 End 的那一帧；
        /// 不拦的话结局卡会被下一帧的节点正文覆盖。
        /// </summary>
        private bool _endingShown;

        /// <summary>剧本里的结局总数（由控制器在 <see cref="Render"/> 里带进来，结局卡要显示"共 N 个"）。</summary>
        private int _endingCount;

        /// <summary>
        /// 订阅按钮。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：控制器在自己的 <c>Awake</c> 里
        /// 就建会话并首帧 <see cref="Render"/>，这里必须在此之前把标题解析出来并挂上监听；
        /// 两者都是 <c>Awake</c> 时谁先跑都不影响结果（<see cref="EnsureButtons"/> 是幂等的）。
        /// </summary>
        private void Awake()
        {
            EnsureButtons();

            if (advanceButton != null)
            {
                advanceButton.onClick.AddListener(RaiseAdvanceClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Awake"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (advanceButton != null)
            {
                advanceButton.onClick.RemoveListener(RaiseAdvanceClicked);
            }

            int slots = SlotCount();
            for (int i = 0; i < slots; i++)
            {
                Button? button = choiceButtons[i];
                UnityEngine.Events.UnityAction? handler = _choiceHandlers[i];
                if (button != null && handler != null)
                {
                    button.onClick.RemoveListener(handler);
                }
            }
        }

        /// <summary>
        /// 按只读视图刷新说话人 / 正文 / 选项按钮 / 推进按钮 / 状态行。
        /// <paramref name="view"/> 为 <c>null</c> 时原样返回（内核没就绪或本局已放弃），
        /// 不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        /// <param name="endingCount">剧本里的结局总数（只用于状态行与结局卡的"共 N 个"口径）。</param>
        public void Render(IVisualNovelReadView view, int endingCount)
        {
            if (view == null)
            {
                return;
            }

            if (_endingShown)
            {
                if (view.IsFinished)
                {
                    // 结局卡已经写好：逐帧渲染不许把它洗掉（见类型注释）。
                    return;
                }

                // 视图回到"未结束"= 重开：复位脏检查基准，下一句重新铺满。
                ResetEndingState();
            }

            _endingCount = endingCount;
            EnsureButtons();

            SetSpeaker(view.CurrentKind == VisualNovelNodeKind.Jump ? string.Empty : view.Speaker);
            SetBody(view.Text);
            RenderChoices(view);
            RenderAdvanceButton(view);
            SetStatus(BuildStatus(view, endingCount));
        }

        /// <summary>
        /// 显示结局卡：说话人写"结局"，正文写<b>结局</b>（在原正文后追加，保留进入 End 节点时那句
        /// 收尾旁白），隐藏全部选项，推进按钮改成"重新开始"。
        /// <para>
        /// <b>为什么参数只有 endingId + isVictory</b>：这是控制器结算后掌握的<b>全部</b>信息
        /// （结局文案与结局名的映射属于演示数据，见 <see cref="EndingLabel"/>）。
        /// 正式项目应由剧本节点自带文案，HUD 不该去猜。
        /// </para>
        /// </summary>
        /// <param name="endingId">结局 id（= End 节点 id）；<c>-1</c> = 悬空 Line/Jump 收尾的失败结局。</param>
        /// <param name="isVictory">是否走到显式 End 结局（内核结算报告的 <c>IsVictory</c>）。</param>
        public void ShowEnding(int endingId, bool isVictory)
        {
            EnsureButtons();
            _endingShown = true;

            string narrative = ReadBody();
            string endingLine = endingId >= 0
                ? "—— 结局 " + endingId + "：" + EndingLabel(endingId) + " ——"
                : "—— 结局：故事断在半路 ——";
            string verdict = isVictory ? "故事走到了一个结尾。" : "没有走到任何显式结局（剧本数据断链）。";

            SetSpeaker("结局");
            SetBody(narrative.Length == 0
                ? endingLine + "\n" + verdict + "\n点『重新开始』再看一遍。"
                : narrative + "\n\n" + endingLine + "\n" + verdict + "\n点『重新开始』再看一遍。");

            HideChoices();
            SetAdvance(true, RestartLabel);
            SetStatus("结局 " + endingId + " · 共 " + _endingCount + " 个结局 · "
                + (isVictory ? "通关" : "未通关") + " · 点『重新开始』回到开头");
        }

        /// <summary>
        /// 重画选项区：Choice 节点按 <c>0..ChoiceCount-1</c> 铺按钮（文案取 <c>GetChoice(i).Text</c>），
        /// 其余节点全部隐藏。<b>不在这里判条件</b>：可用序号本身就是内核过滤后的结果。
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void RenderChoices(IVisualNovelReadView view)
        {
            int slots = SlotCount();
            int available = view.CurrentKind == VisualNovelNodeKind.Choice ? view.ChoiceCount : 0;
            if (available > slots)
            {
                available = slots;
            }

            for (int i = 0; i < slots; i++)
            {
                bool visible = i < available;
                if (_choiceVisible[i] != visible)
                {
                    _choiceVisible[i] = visible;

                    Button? button = choiceButtons[i];
                    if (button != null)
                    {
                        button.gameObject.SetActive(visible);
                    }
                }

                if (!visible)
                {
                    continue;
                }

                string label = view.GetChoice(i).Text;
                if (label == _choiceLabels[i])
                {
                    continue;
                }

                _choiceLabels[i] = label;
                Text? text = _choiceTexts[i];
                if (text != null)
                {
                    text.text = label;
                }
            }
        }

        /// <summary>
        /// 推进按钮：只有 Line 节点可见（Choice 节点必须点选项，End 节点由结算接管）。
        /// 文案固定"继续"；结局时由 <see cref="ShowEnding"/> 改成"重新开始"。
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void RenderAdvanceButton(IVisualNovelReadView view)
        {
            SetAdvance(view.CurrentKind == VisualNovelNodeKind.Line, AdvanceLabel);
        }

        /// <summary>把 3 个选项按钮全部收起（结局 / 非 Choice 节点共用同一条路径）。</summary>
        private void HideChoices()
        {
            int slots = SlotCount();
            for (int i = 0; i < slots; i++)
            {
                _choiceLabels[i] = string.Empty;

                if (_choiceVisible[i])
                {
                    _choiceVisible[i] = false;

                    Button? button = choiceButtons[i];
                    if (button != null)
                    {
                        button.gameObject.SetActive(false);
                    }
                }
            }
        }

        /// <summary>
        /// 解析按钮标题并订阅点击（幂等：只有第一次真正执行）。
        /// <para>
        /// <b>为什么标题要走 <c>GetComponentInChildren</c></b>：同一物体上只能有一个 <c>Graphic</c>，
        /// 按钮本体是 <see cref="Image"/>，文字必然在子物体上；建场景脚本按这个结构建，
        /// 标题缺失时这里只是拿到 <c>null</c>，点击链路照旧工作。
        /// </para>
        /// </summary>
        private void EnsureButtons()
        {
            if (_buttonsBound)
            {
                return;
            }

            _buttonsBound = true;

            if (advanceButton != null)
            {
                _advanceText = advanceButton.GetComponentInChildren<Text>();
            }

            int slots = SlotCount();
            for (int i = 0; i < slots; i++)
            {
                Button? button = choiceButtons[i];
                if (button == null)
                {
                    continue;
                }

                int index = i;
                UnityEngine.Events.UnityAction handler = () => RaiseChoiceClicked(index);

                _choiceHandlers[i] = handler;
                button.onClick.AddListener(handler);
                _choiceTexts[i] = button.GetComponentInChildren<Text>();
            }
        }

        /// <summary>实际可用的选项槽位数（注入数组可能比 <see cref="MaxChoiceSlots"/> 短）。</summary>
        /// <returns>0..<see cref="MaxChoiceSlots"/>。</returns>
        private int SlotCount()
        {
            if (choiceButtons == null)
            {
                return 0;
            }

            return choiceButtons.Length < MaxChoiceSlots ? choiceButtons.Length : MaxChoiceSlots;
        }

        /// <summary>
        /// 拼状态行：当前节点 id、已进入步数、可用选项数、结局总数；拿到发奖标记位时追加掩码
        /// （条件选项的可见性完全由内核决定，这里只把读数照出来，方便确认"钥匙到手了没"）。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="endingCount">剧本里的结局总数。</param>
        /// <returns>一行状态文本。</returns>
        private static string BuildStatus(IVisualNovelReadView view, int endingCount)
        {
            string status = "节点 " + view.CurrentNodeId
                + " · 已进入 " + view.NodesVisited + " 步"
                + " · 可选项 " + view.ChoiceCount
                + " · 结局 " + endingCount + " 个";

            return view.Flags != 0 ? status + " · 标记 " + view.Flags : status;
        }

        /// <summary>写说话人（文本未变就不碰控件，避免重复重建字形）。</summary>
        /// <param name="speaker">说话人文本。</param>
        private void SetSpeaker(string speaker)
        {
            if (speaker == _lastSpeaker)
            {
                return;
            }

            _lastSpeaker = speaker;
            if (speakerText != null)
            {
                speakerText.text = speaker;
            }
        }

        /// <summary>写正文（未变即跳过）。</summary>
        /// <param name="body">正文文本。</param>
        private void SetBody(string body)
        {
            if (body == _lastBody)
            {
                return;
            }

            _lastBody = body;
            if (bodyText != null)
            {
                bodyText.text = body;
            }
        }

        /// <summary>写状态行（未变即跳过）。</summary>
        /// <param name="status">状态文本。</param>
        private void SetStatus(string status)
        {
            if (status == _lastStatus)
            {
                return;
            }

            _lastStatus = status;
            if (statusText != null)
            {
                statusText.text = status;
            }
        }

        /// <summary>设置推进按钮的可见性与文案（两处各自脏检查，互不牵连）。</summary>
        /// <param name="visible">是否可见。</param>
        /// <param name="label">按钮文案。</param>
        private void SetAdvance(bool visible, string label)
        {
            if (_advanceVisible != visible)
            {
                _advanceVisible = visible;

                if (advanceButton != null)
                {
                    advanceButton.gameObject.SetActive(visible);
                }
            }

            if (label == _lastAdvanceLabel)
            {
                return;
            }

            _lastAdvanceLabel = label;
            if (_advanceText != null)
            {
                _advanceText.text = label;
            }
        }

        /// <summary>读当前正文（结局卡要在它后面追加，保留进入 End 节点时那句收尾旁白）。</summary>
        /// <returns>正文控件上的文本；控件缺失时为空串。</returns>
        private string ReadBody()
        {
            Text? text = bodyText;
            return text != null ? text.text : string.Empty;
        }

        /// <summary>重开后的复位：清掉"已显示结局"与全部脏检查基准，让下一帧重新铺满。</summary>
        private void ResetEndingState()
        {
            _endingShown = false;
            _lastSpeaker = string.Empty;
            _lastBody = string.Empty;
            _lastStatus = string.Empty;
            _lastAdvanceLabel = string.Empty;
        }

        /// <summary>
        /// 结局 id 到结局名的映射——<b>这里是演示剧情文案</b>，与控制器里内置的 16 节点剧本一一对应
        /// （11 = 雨夜离别、13 = 里屋的门、14 = 抽屉里的字条、15 = 带钥匙回家）。
        /// 正式项目应由剧本数据把标题带进节点，HUD 不该内置任何剧情字典。
        /// </summary>
        /// <param name="endingId">结局 id。</param>
        /// <returns>结局名；未知 id 返回"未命名结局"。</returns>
        private static string EndingLabel(int endingId)
        {
            switch (endingId)
            {
                case 11:
                    return "雨夜离别";
                case 13:
                    return "里屋的门";
                case 14:
                    return "抽屉里的字条";
                case 15:
                    return "带钥匙回家";
                default:
                    return "未命名结局";
            }
        }

        /// <summary>转发"继续 / 重新开始"（控制器按当前是否已结束分流）。</summary>
        private void RaiseAdvanceClicked()
        {
            Action? handler = OnAdvanceClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发"点了第几个可用选项"（序号已是内核口径，不做任何换算）。</summary>
        /// <param name="index">可用选项序号。</param>
        private void RaiseChoiceClicked(int index)
        {
            Action<int>? handler = OnChoiceClicked;
            if (handler != null)
            {
                handler(index);
            }
        }
    }
}

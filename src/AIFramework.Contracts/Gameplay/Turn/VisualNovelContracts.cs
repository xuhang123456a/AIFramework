#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 视觉小说/文字冒险剧本节点种类。枚举值同时是存档/遥测的稳定协议值（<c>byte</c>），<b>不要重排</b>。
    /// <para>
    /// <b>推进语义</b>：<see cref="Line"/> / <see cref="Jump"/> 走
    /// <see cref="IVisualNovelInput.TryAdvance"/>；<see cref="Choice"/> 走
    /// <see cref="IVisualNovelInput.TryChoose"/>；<see cref="End"/> 进入即结束，之后不再接受任何输入。
    /// </para>
    /// </summary>
    public enum VisualNovelNodeKind : byte
    {
        /// <summary>台词/旁白：表现层显示说话人与文本，玩家点击（或自动播放）推进到 <c>NextNodeId</c>。</summary>
        Line = 0,

        /// <summary>选项分支：表现层列出当前可用选项，玩家点选后跳到该选项的 <c>TargetNodeId</c>。</summary>
        Choice = 1,

        /// <summary>跳转：不显示内容，表现层可自动调用 <see cref="IVisualNovelInput.TryAdvance"/> 跳过。</summary>
        Jump = 2,

        /// <summary>结局：进入即结束，<see cref="IVisualNovelReadView.EndingId"/> = 本节点 id。</summary>
        End = 3
    }

    /// <summary>
    /// 一个选项：显示文本 + 跳转节点 + 可选"条件标记"（如需要某 item 才出现）。
    /// <para>
    /// <b>条件语义（演示用轻量条件）</b>：<see cref="RequiredItemId"/> 为 0 表示无条件出现；
    /// 大于 0 时被解释为 <see cref="IVisualNovelReadView.Flags"/> 的位号
    /// （<c>1 &lt;&lt; (RequiredItemId &amp; 31)</c>，即只用了低 5 位，超出 32 的 item id 会回绕复用位）。
    /// 该位为 0 时选项<b>不出现在</b> <see cref="IVisualNovelReadView.ChoiceCount"/> /
    /// <see cref="IVisualNovelReadView.GetChoice"/> 的可用序列里。
    /// </para>
    /// <para>
    /// 正式项目应由业务切片（背包/任务/好感度）判定后生成剧本数据，而不是把条件压进位掩码；
    /// 这里保留位掩码只是为了让"选项带条件"在没有背包系统的前提下也能跑通无头测试。
    /// </para>
    /// </summary>
    public readonly struct VisualNovelChoice
    {
        public VisualNovelChoice(string text, int targetNodeId, int requiredItemId = 0)
        {
            Text = text;
            TargetNodeId = targetNodeId;
            RequiredItemId = requiredItemId;
        }

        /// <summary>显示文本（HUD 按钮文案）。</summary>
        public string Text { get; }

        /// <summary>选中后跳转的节点 id；必须在剧本中存在，否则构造期抛 <see cref="ArgumentException"/>。</summary>
        public int TargetNodeId { get; }

        /// <summary>条件标记：0 = 无条件；&gt; 0 = 需要的 flag 位（见类型注释的位映射）。</summary>
        public int RequiredItemId { get; }
    }

    /// <summary>
    /// 一个剧本节点（纯数据，内核构造时整段注入）。节点间用整数 id 相互引用，剧本本身不持有世界状态。
    /// <para>
    /// <b>字段口径</b>：
    /// <see cref="Kind"/> 决定本节点怎么被消费；
    /// <see cref="NextNodeId"/> 只对 <see cref="VisualNovelNodeKind.Line"/> / <see cref="VisualNovelNodeKind.Jump"/>
    /// 有意义（&lt; 0 表示没有下一节点——故事到此结束，但不算显式结局，见 <see cref="IVisualNovelReadView.EndingId"/>）；
    /// <see cref="Choices"/> 只对 <see cref="VisualNovelNodeKind.Choice"/> 有意义且不能为空；
    /// <see cref="GrantItemId"/> / <see cref="GrantCount"/> 对所有种类生效：进入节点时按"每节点只发一次"记账。
    /// </para>
    /// </summary>
    public readonly struct VisualNovelNode
    {
        public VisualNovelNode(
            int nodeId,
            VisualNovelNodeKind kind,
            string speaker,
            string text,
            int nextNodeId,
            VisualNovelChoice[]? choices,
            int grantItemId,
            int grantCount)
        {
            NodeId = nodeId;
            Kind = kind;
            Speaker = speaker;
            Text = text;
            NextNodeId = nextNodeId;
            Choices = choices;
            GrantItemId = grantItemId;
            GrantCount = grantCount;
        }

        /// <summary>节点 id：剧本内唯一、非负；<see cref="VisualNovelNodeKind.End"/> 节点的 id 同时是结局 id。</summary>
        public int NodeId { get; }

        /// <summary>节点种类。</summary>
        public VisualNovelNodeKind Kind { get; }

        /// <summary>说话人（角色名/旁白）；<see cref="VisualNovelNodeKind.Jump"/> 节点不使用。</summary>
        public string Speaker { get; }

        /// <summary>显示文本（正文/旁白）；<see cref="VisualNovelNodeKind.Jump"/> 节点不使用。</summary>
        public string Text { get; }

        /// <summary>
        /// <see cref="VisualNovelNodeKind.Line"/> / <see cref="VisualNovelNodeKind.Jump"/> 的下一个节点 id；
        /// <c>-1</c>（任何负数）表示没有下一节点，推进即结束故事。
        /// </summary>
        public int NextNodeId { get; }

        /// <summary><see cref="VisualNovelNodeKind.Choice"/> 的可选项清单；仅 Choice 节点要求非空。</summary>
        public VisualNovelChoice[]? Choices { get; }

        /// <summary>进入本节点时发放的物品 id；0 = 不发放。发放同时会置位对应 flag，供条件选项判定。</summary>
        public int GrantItemId { get; }

        /// <summary>发放数量；<see cref="GrantItemId"/> &gt; 0 时必须为正，否则构造期抛 <see cref="ArgumentException"/>。</summary>
        public int GrantCount { get; }
    }

    /// <summary>
    /// 视觉小说只读视图：Controller / HUD / 对话面板只读这里，不碰内核内部状态。
    /// <para>
    /// <b>为什么"选项"是过滤后的序列</b>：条件不满足的选项不占位。<see cref="ChoiceCount"/> 是当前
    /// <b>可用</b>选项数，<see cref="GetChoice"/> 的序号与 <see cref="IVisualNovelInput.TryChoose"/>
    /// 的入参完全同序——表现层直接把 <c>0..ChoiceCount-1</c> 铺成按钮，再把玩家点到的序号回传即可，
    /// 不需要自己重算条件（也避免"界面亮着但点了无效"）。
    /// </para>
    /// <para>
    /// <b>历史回看</b>：本视图只承载"当前节点 + 进度计数"，不保存已读文本队列。
    /// 对话回看（Log/Backlog）由表现层在每次渲染节点时自行缓存即可；内核不做重复存储。
    /// </para>
    /// </summary>
    public interface IVisualNovelReadView
    {
        /// <summary>当前节点 id（构造后即起始节点；存档/读档恢复后为快照里的节点）。</summary>
        int CurrentNodeId { get; }

        /// <summary>当前节点种类。</summary>
        VisualNovelNodeKind CurrentKind { get; }

        /// <summary>当前节点说话人（<see cref="VisualNovelNodeKind.Jump"/> 节点通常为空文本）。</summary>
        string Speaker { get; }

        /// <summary>当前节点正文/旁白文本。</summary>
        string Text { get; }

        /// <summary>
        /// 当前<b>可用</b>选项数：非 <see cref="VisualNovelNodeKind.Choice"/> 节点为 0；
        /// Choice 节点只统计条件满足的选项（条件语义见 <see cref="VisualNovelChoice.RequiredItemId"/>）。
        /// </summary>
        int ChoiceCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个<b>可用</b>选项（序号与 <see cref="ChoiceCount"/>、
        /// <see cref="IVisualNovelInput.TryChoose"/> 同序）。
        /// 越界（含负数、非 Choice 节点）抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        VisualNovelChoice GetChoice(int index);

        /// <summary>故事是否结束：进入 <see cref="VisualNovelNodeKind.End"/>、Line/Jump 无下一节点、或结算已取回。</summary>
        bool IsFinished { get; }

        /// <summary>
        /// 结局 id：到达 <see cref="VisualNovelNodeKind.End"/> 节点时置为该节点 id；<c>-1</c> = 未结束或
        /// 以"悬空 Line/Jump"收尾（数据断链），后者是失败的结局，结算报非胜利。
        /// </summary>
        int EndingId { get; }

        /// <summary>
        /// 已进入节点总数（含起始节点，故构造后为 1；重复进入同一节点重复计数）。
        /// 结算报告的 <c>StepCount</c> / <c>RemainingUnits</c> 均取本值。
        /// </summary>
        int NodesVisited { get; }

        /// <summary>
        /// 业务自定义标记位掩码（演示用轻量条件）。到达发奖节点时置位
        /// <c>1 &lt;&lt; (GrantItemId &amp; 31)</c>；条件选项按同一映射读取。
        /// </summary>
        int Flags { get; }
    }

    /// <summary>
    /// 视觉小说输入端口：Controller 只能通过它提交意图，不能直接改内核状态。
    /// <para>故事结束后（含结算已取回）所有方法都返回 false，不抛异常。</para>
    /// </summary>
    public interface IVisualNovelInput
    {
        /// <summary>
        /// 推进一个节点：<see cref="VisualNovelNodeKind.Line"/> / <see cref="VisualNovelNodeKind.Jump"/>
        /// 跳到 <c>NextNodeId</c>；<c>NextNodeId</c> 为负时结束故事（不算显式结局）。
        /// <para>
        /// 返回 true 表示状态发生了变化（移动到了下一节点，或故事就此结束）；
        /// 返回 false 表示当前节点不接受"推进"语义：Choice 节点（必须选选项）、End 节点、已结束（含结算已取回）。
        /// </para>
        /// </summary>
        bool TryAdvance();

        /// <summary>
        /// 选择第 <paramref name="index"/> 个<b>可用</b>选项（序号与
        /// <see cref="IVisualNovelReadView.ChoiceCount"/> / <see cref="IVisualNovelReadView.GetChoice"/> 同序），
        /// 成功后跳到该选项的 <c>TargetNodeId</c>。
        /// <para>
        /// 返回 false：当前节点不是 Choice、<paramref name="index"/> 越界（含负数）、
        /// 该选项条件本应不满足（防御分支）或故事已结束。失败一律不改任何状态。
        /// </para>
        /// </summary>
        bool TryChoose(int index);
    }

    /// <summary>
    /// 视觉小说存档快照（纯数据，4 个 int）：只承载"进度四元组"，由
    /// <c>VisualNovelWorld.CaptureState()</c> 生成、<c>VisualNovelWorld.TryRestoreState()</c> 消费。
    /// <para>
    /// <b>刻意不包含的东西</b>（读档语义边界，实现方在 <c>TryRestoreState</c> 的文档里同样声明）：
    /// 待发奖台账、发奖去重台账、Step 累计时间、结算是否已取回。即"读档回到某节点"只恢复进度，
    /// 不重放沿途节点进入副作用（不会补发读档点之前的物品，也不会因为回档重复发奖）。
    /// </para>
    /// <para>
    /// 因此显式 <see cref="VisualNovelNodeKind.End"/> 结局可完整往返；而以"悬空 Line/Jump"收尾的
    /// 结束态（<see cref="IVisualNovelReadView.EndingId"/> 仍为 -1）无法用本快照表达。
    /// </para>
    /// </summary>
    public readonly struct VisualNovelSaveState
    {
        public VisualNovelSaveState(int currentNodeId, int flags, int nodesVisited, int endingId)
        {
            CurrentNodeId = currentNodeId;
            Flags = flags;
            NodesVisited = nodesVisited;
            EndingId = endingId;
        }

        /// <summary>保存时的当前节点 id。</summary>
        public int CurrentNodeId { get; }

        /// <summary>保存时的标记位掩码。</summary>
        public int Flags { get; }

        /// <summary>保存时已进入节点总数。</summary>
        public int NodesVisited { get; }

        /// <summary>保存时的结局 id；<c>-1</c> = 未结束。</summary>
        public int EndingId { get; }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.VisualNovel
{
    /// <summary>
    /// 视觉小说/文字冒险内核（<see cref="GameplayKernelType.Turn"/> 卡带的新内容）：
    /// 纯数据剧本状态机 + 选项分支 + 存档快照，零引擎依赖、零随机、完全确定性。
    /// <para>
    /// <b>为什么复用 Turn 卡带</b>：对主板而言文字冒险也只是"另一个回合制会话"——进门拿
    /// <see cref="GameplayInitData"/>、出门交 <see cref="GameplayResult"/>；它的"回合"就是玩家的一次点击
    /// （推进/选择）。新增 <see cref="GameplayKernelType"/> 枚举值会强逼装配层与存档协议同步扩容，收益为零。
    /// 因此本内核<b>不新增枚举值、不做工厂注册</b>，适配层直接 <c>new VisualNovelWorld(...)</c> 持有会话。
    /// </para>
    /// <para>
    /// <b>剧本从哪来</b>：节点数组由调用方（关卡配置/表格导出/硬编码测试）在构造时整段注入；
    /// 内核只消费、不加载、不反序列化。校验与索引见 <see cref="VisualNovelScript"/>，
    /// 空剧本、重复 id、起点缺失、Choice 无选项/坏目标、Line/Jump 坏 NextNodeId、发奖字段不自洽
    /// 一律在构造期抛 <see cref="ArgumentException"/>（消息带节点 id），绝不让坏数据进运行期。
    /// </para>
    /// <para>
    /// <b>推进规则</b>：
    /// <list type="bullet">
    /// <item><see cref="TryAdvance"/>：Line/Jump 跳到 <see cref="VisualNovelNode.NextNodeId"/>；
    /// 该字段为负表示没有下一节点，故事就此结束（<b>不算显式结局</b>，<see cref="EndingId"/> 保持 -1）。</item>
    /// <item><see cref="TryChoose"/>：仅 Choice 节点可用；序号是 <b>可用</b>选项序号
    /// （条件不满足的选项不占位，与 <see cref="ChoiceCount"/> / <see cref="GetChoice"/> 同序）。</item>
    /// <item>进入 End 节点即 <see cref="IsFinished"/> = true、<see cref="EndingId"/> = 该节点 id；
    /// 之后所有输入返回 false，只有 <see cref="CollectResult"/> 能取回结算。</item>
    /// <item><see cref="NodesVisited"/> 进入即 +1（起始节点算第一次）；这是结算报告的
    /// <c>StepCount</c> / <c>RemainingUnits</c> 口径。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>发奖</b>：进入 <see cref="VisualNovelNode.GrantItemId"/> &gt; 0 的节点时记一笔
    /// <see cref="LootItem"/> 进待发奖台账，并置位 <c>1 &lt;&lt; (GrantItemId &amp; 31)</c> 标记供条件选项判定；
    /// <b>同一节点只发一次</b>（剧本可以成环，绕回来不会刷奖励）。发奖清单只在
    /// <see cref="CollectResult"/> 时随报告交还，读视图不暴露（避免表现层自行派奖）。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：回合制不按时间推进逻辑，<see cref="Step(float)"/> 只把 <c>deltaSeconds</c>
    /// 累加到 <see cref="ElapsedSeconds"/>（供自动播放/淡入淡出做时间轴），不改任何剧情状态；结束后为空操作。
    /// </para>
    /// <para>
    /// <b>存档</b>：<see cref="CaptureState"/> / <see cref="TryRestoreState"/> 只搬
    /// <see cref="VisualNovelSaveState"/> 的 4 个 int（进度四元组）；待发奖台账、发奖去重台账、
    /// <see cref="ElapsedSeconds"/>、结算是否已取回都不随快照迁移，语义边界见
    /// <see cref="TryRestoreState"/> 的注释。
    /// </para>
    /// <para>
    /// <b>确定性</b>：无随机源、无时间源，同一剧本 + 同一输入序列 ⇒ 读视图与结算报告完全一致；
    /// 构造期对剧本做了防御性拷贝，运行期外部改原数组也影响不到已开始的会话。
    /// </para>
    /// </summary>
    public sealed class VisualNovelWorld : IGameplaySession, IVisualNovelReadView, IVisualNovelInput
    {
        /// <summary>校验并索引过的剧本（运行期只按序号取节点）。</summary>
        private readonly VisualNovelScript _script;

        /// <summary>会话级运行期状态与存档快照搬运。</summary>
        private readonly VisualNovelState _state;

        /// <summary>进门数据里的关卡 id，只做战绩/日志口径（内核不按关卡做任何分支）。</summary>
        private readonly int _levelId;

        /// <summary>
        /// 用一段纯数据剧本开一局。
        /// </summary>
        /// <param name="init">
        /// 进门快照：只取 <see cref="GameplayInitData.LevelId"/> 作为战绩口径；
        /// <see cref="GameplayInitData.RandomSeed"/> 与 <see cref="GameplayInitData.UnitCount"/> 不使用
        /// （文字冒险无随机、无出战单位）。
        /// </param>
        /// <param name="script">剧本节点数组；为空/结构非法时抛 <see cref="ArgumentException"/>（消息带节点 id）。</param>
        /// <param name="startNodeId">起始节点 id，必须存在于剧本中（默认 0）。</param>
        public VisualNovelWorld(in GameplayInitData init, VisualNovelNode[] script, int startNodeId = 0)
        {
            _script = new VisualNovelScript(script, startNodeId);
            _state = new VisualNovelState(_script.Count);
            _levelId = init.LevelId;

            // 起始节点也算"进入"：发奖、访问计数、End 立即结束都在这里统一处理。
            _state.CurrentNodeIndex = _script.StartNodeIndex;
            EnterNode(_script.StartNodeIndex);
        }

        /// <summary>进门数据里的关卡 id（只做战绩/日志口径，不参与规则）。</summary>
        public int LevelId
        {
            get { return _levelId; }
        }

        /// <summary>本剧本的节点总数（诊断/存档校验用；运行期规则不需要它）。</summary>
        public int NodeCount
        {
            get { return _script.Count; }
        }

        /// <summary>
        /// <see cref="Step(float)"/> 累计的秒数（会话级诊断/表现层时间轴输入）。
        /// 与结算报告口径无关：报告的 <c>StepCount</c> 是 <see cref="NodesVisited"/>。
        /// </summary>
        public float ElapsedSeconds
        {
            get { return _state.ElapsedSeconds; }
        }

        /// <summary>当前节点 id。</summary>
        public int CurrentNodeId
        {
            get { return GetCurrentNode().NodeId; }
        }

        /// <summary>当前节点种类。</summary>
        public VisualNovelNodeKind CurrentKind
        {
            get { return GetCurrentNode().Kind; }
        }

        /// <summary>当前节点说话人（Jump 节点通常为空文本）。</summary>
        public string Speaker
        {
            get { return GetCurrentNode().Speaker; }
        }

        /// <summary>当前节点正文/旁白文本。</summary>
        public string Text
        {
            get { return GetCurrentNode().Text; }
        }

        /// <summary>
        /// 当前<b>可用</b>选项数：非 Choice 节点为 0；条件不满足的选项不占位。
        /// </summary>
        public int ChoiceCount
        {
            get
            {
                VisualNovelNode node = GetCurrentNode();
                if (node.Kind != VisualNovelNodeKind.Choice || node.Choices == null)
                {
                    return 0;
                }

                VisualNovelChoice[] choices = node.Choices;
                int count = 0;
                for (int i = 0; i < choices.Length; i++)
                {
                    if (IsAvailable(choices[i]))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>故事是否结束：进入 End、Line/Jump 无下一节点、或结算报告已取回。</summary>
        public bool IsFinished
        {
            get { return _state.IsFinished; }
        }

        /// <summary>结局 id；<c>-1</c> = 未结束或以"悬空 Line/Jump"收尾（失败结局）。</summary>
        public int EndingId
        {
            get { return _state.EndingId; }
        }

        /// <summary>已进入节点总数（含起始节点，构造后为 1；重复进入重复计数）。</summary>
        public int NodesVisited
        {
            get { return _state.NodesVisited; }
        }

        /// <summary>标记位掩码（发奖节点置位，条件选项按同一映射读取）。</summary>
        public int Flags
        {
            get { return _state.Flags; }
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 个<b>可用</b>选项；越界（含负数、非 Choice 节点）抛
        /// <see cref="ArgumentOutOfRangeException"/>。表现层按 <c>0..ChoiceCount-1</c> 循环即可。
        /// </summary>
        public VisualNovelChoice GetChoice(int index)
        {
            VisualNovelNode node = GetCurrentNode();
            if (node.Kind != VisualNovelNodeKind.Choice || node.Choices == null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "当前节点没有可用选项。");
            }

            VisualNovelChoice[] choices = node.Choices;
            int visibleIndex = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                VisualNovelChoice choice = choices[i];
                if (!IsAvailable(choice))
                {
                    continue;
                }

                if (visibleIndex == index)
                {
                    return choice;
                }

                visibleIndex++;
            }

            throw new ArgumentOutOfRangeException(
                nameof(index), index, "可用选项索引越界：当前可用选项数为 " + visibleIndex + "。");
        }

        /// <summary>
        /// 推进一个节点（仅供 Line/Jump）：跳到 <c>NextNodeId</c>；
        /// 无下一节点（负数）则结束故事（不算显式结局）。Choice/End/已结束时返回 false，且不改任何状态。
        /// </summary>
        public bool TryAdvance()
        {
            if (_state.IsFinished)
            {
                return false;
            }

            VisualNovelNode node = GetCurrentNode();
            switch (node.Kind)
            {
                case VisualNovelNodeKind.Line:
                case VisualNovelNodeKind.Jump:
                    if (node.NextNodeId < 0)
                    {
                        _state.Finished = true;
                        return true;
                    }

                    return MoveTo(node.NextNodeId);

                default:
                    // Choice 必须选选项才推进；End 进入时已结束（本分支实际不可达，防御保留）。
                    return false;
            }
        }

        /// <summary>
        /// 选择第 <paramref name="index"/> 个<b>可用</b>选项（序号与 <see cref="ChoiceCount"/> /
        /// <see cref="GetChoice"/> 同序，隐藏选项不占位），成功后跳到该选项的目标节点。
        /// 非 Choice 节点、序号越界、故事已结束一律返回 false 且不改任何状态。
        /// </summary>
        public bool TryChoose(int index)
        {
            if (_state.IsFinished || index < 0)
            {
                return false;
            }

            VisualNovelNode node = GetCurrentNode();
            if (node.Kind != VisualNovelNodeKind.Choice || node.Choices == null)
            {
                return false;
            }

            VisualNovelChoice[] choices = node.Choices;
            int visibleIndex = 0;
            int targetNodeId = -1;
            for (int i = 0; i < choices.Length; i++)
            {
                VisualNovelChoice choice = choices[i];
                if (!IsAvailable(choice))
                {
                    // 条件不满足的选项不占可用序号，也不能被选中。
                    continue;
                }

                if (visibleIndex == index)
                {
                    targetNodeId = choice.TargetNodeId;
                    break;
                }

                visibleIndex++;
            }

            // targetNodeId 在构造期已校验存在且非负；-1 只可能是"序号越界/无可用选项"。
            if (targetNodeId < 0)
            {
                return false;
            }

            return MoveTo(targetNodeId);
        }

        /// <summary>推演一步：只把 <paramref name="deltaSeconds"/> 累加到 <see cref="ElapsedSeconds"/>；结束后为空操作。</summary>
        public void Step(float deltaSeconds)
        {
            if (_state.IsFinished)
            {
                return;
            }

            _state.ElapsedSeconds += deltaSeconds;
        }

        /// <summary>
        /// 取回结算报告：故事未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>
        /// （会话所有权只交还一次）。
        /// <para>
        /// 报告口径：固定 <see cref="GameplayKernelType.Turn"/>；
        /// <see cref="GameplayResult.IsVictory"/> = <see cref="EndingId"/> &gt;= 0（走了显式 End 结局）；
        /// <see cref="GameplayResult.StepCount"/> = <see cref="NodesVisited"/>（score 口径同值）；
        /// <see cref="GameplayResult.RemainingUnits"/> = <see cref="NodesVisited"/>；
        /// 战利品 = 本局进入发奖节点累计的待发奖清单（无论胜利与否都发，按进入顺序）；
        /// 不触发任何 <see cref="GameplayTag"/>。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_state.ResultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_state.Finished)
            {
                throw new InvalidOperationException("故事尚未结束，禁止取回 GameplayResult。");
            }

            _state.ResultCollected = true;

            bool isVictory = _state.EndingId >= 0;
            IReadOnlyList<LootItem> loots = _state.PendingLoot.Count == 0
                ? Array.Empty<LootItem>()
                : _state.PendingLoot.ToArray();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _state.NodesVisited,
                _state.NodesVisited,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>
        /// 导出当前进度快照（4 个 int）。已结束、甚至已结算后调用都允许——快照只是只读复印，不改任何状态。
        /// </summary>
        public VisualNovelSaveState CaptureState()
        {
            return _state.Capture(GetCurrentNode().NodeId);
        }

        /// <summary>
        /// 从存档快照恢复进度四元组。
        /// <para>
        /// 返回 false（不改任何状态）的条件：结算报告已取回、当前节点 id 在剧本中不存在、
        /// <see cref="VisualNovelSaveState.NodesVisited"/> 为负、<see cref="VisualNovelSaveState.EndingId"/>
        /// 小于 -1、或 <c>EndingId &gt;= 0</c> 但与本节点不自洽（结局 id 必须等于当前节点 id 且当前节点是 End）。
        /// </para>
        /// <para>
        /// <b>恢复范围是"进度"而不是"整个世界状态"</b>：待发奖台账、发奖去重台账、
        /// <see cref="ElapsedSeconds"/>、结算是否已取回都不随快照迁移——读档不重放沿途节点进入副作用
        /// （不补发读档点之前的物品，也不会因为回档重复发奖）。显式 End 结局可完整往返；
        /// 以"悬空 Line/Jump"收尾的结束态没有结局 id，本快照无法表达，恢复后回到该节点未结束状态。
        /// </para>
        /// </summary>
        public bool TryRestoreState(in VisualNovelSaveState state)
        {
            if (_state.ResultCollected)
            {
                return false;
            }

            if (state.NodesVisited < 0 || state.EndingId < -1)
            {
                return false;
            }

            if (!_script.TryGetIndexByNodeId(state.CurrentNodeId, out int nodeIndex))
            {
                return false;
            }

            if (state.EndingId >= 0)
            {
                // 结局必然由"进入某个 End 节点"产生：id 必须等于该节点 id，否则是坏档。
                if (state.EndingId != state.CurrentNodeId
                    || _script[nodeIndex].Kind != VisualNovelNodeKind.End)
                {
                    return false;
                }
            }

            _state.Restore(nodeIndex, in state);
            return true;
        }

        /// <summary>当前节点（序号恒在界内：构造/推进/恢复三处都先校验过）。</summary>
        private VisualNovelNode GetCurrentNode()
        {
            return _script[_state.CurrentNodeIndex];
        }

        /// <summary>选项条件是否满足：无条件（0）恒真；否则查对应 flag 位（演示用轻量条件）。</summary>
        private bool IsAvailable(VisualNovelChoice choice)
        {
            return choice.RequiredItemId == 0 || _state.HasFlag(choice.RequiredItemId);
        }

        /// <summary>
        /// 进入指定节点：当前序号、访问计数、发奖（每节点一次）、End 结束判定一次做完。
        /// 只被构造、<see cref="TryAdvance"/>、<see cref="TryChoose"/> 三处调用（恢复走 <see cref="VisualNovelState.Restore"/>）。
        /// </summary>
        private void EnterNode(int nodeIndex)
        {
            VisualNovelNode node = _script[nodeIndex];
            _state.NodesVisited++;

            if (node.GrantItemId > 0 && !_state.GrantedNodes[nodeIndex])
            {
                _state.GrantedNodes[nodeIndex] = true;
                _state.RaiseFlag(node.GrantItemId);
                _state.PendingLoot.Add(new LootItem(node.GrantItemId, node.GrantCount));
            }

            if (node.Kind == VisualNovelNodeKind.End)
            {
                _state.Finished = true;
                _state.EndingId = node.NodeId;
            }
        }

        /// <summary>移动到指定节点 id；id 在构造期已校验存在，查不到只可能是不该发生的坏数据（返回 false 不动状态）。</summary>
        private bool MoveTo(int nodeId)
        {
            if (!_script.TryGetIndexByNodeId(nodeId, out int nodeIndex))
            {
                return false;
            }

            _state.CurrentNodeIndex = nodeIndex;
            EnterNode(nodeIndex);
            return true;
        }
    }
}

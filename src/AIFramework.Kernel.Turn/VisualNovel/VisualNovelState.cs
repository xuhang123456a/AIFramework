#nullable enable
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.VisualNovel
{
    /// <summary>
    /// 视觉小说的会话级运行期状态：当前节点序号、标记位、访问计数、结局与结算/发奖台账。
    /// <para>
    /// <b>为什么单独一个文件</b>：<see cref="VisualNovelWorld"/> 的主文件只写"规则与读视图"，
    /// 状态的字段与存档快照的搬运集中在这里；两处都改的时候不会互相踩行。
    /// </para>
    /// <para>
    /// <b>存档口径</b>：<see cref="Capture"/> 只导出
    /// <see cref="VisualNovelSaveState"/> 的 4 个 int；<see cref="GrantedNodes"/>、
    /// <see cref="PendingLoot"/>、<see cref="ElapsedSeconds"/>、<see cref="ResultCollected"/>
    /// 是会话级台账，<b>不随快照迁移</b>（见 <see cref="Restore"/> 注释）。
    /// </para>
    /// </summary>
    internal sealed class VisualNovelState
    {
        /// <summary>发奖去重台账，下标 = 脚本序号；一个节点只在第一次进入时发奖。</summary>
        internal readonly bool[] GrantedNodes;

        /// <summary>待发奖清单：到达 <c>CollectResult()</c> 时原样作为 <see cref="GameplayResult.Loots"/> 返回。</summary>
        internal readonly List<LootItem> PendingLoot;

        /// <summary>当前节点的脚本序号（不是 nodeId；对外的 <c>CurrentNodeId</c> 由剧本映射回来）。</summary>
        internal int CurrentNodeIndex;

        /// <summary>业务自定义标记位掩码（演示用轻量条件），发奖节点置位。</summary>
        internal int Flags;

        /// <summary>已进入节点总数（含起始节点；重复进入重复计数）。</summary>
        internal int NodesVisited;

        /// <summary>结局节点 id；<c>-1</c> = 还没有显式结局。</summary>
        internal int EndingId;

        /// <summary>是否已结束（到达 End 节点，或 Line/Jump 无下一节点）。</summary>
        internal bool Finished;

        /// <summary>结算报告是否已取回（取回后会话所有权交还，禁止再做任何推进/恢复）。</summary>
        internal bool ResultCollected;

        /// <summary><c>Step(float)</c> 累计的秒数（表现层自动播放/淡入淡出的时间轴输入）。</summary>
        internal float ElapsedSeconds;

        internal VisualNovelState(int nodeCount)
        {
            GrantedNodes = new bool[nodeCount];
            PendingLoot = new List<LootItem>(4);
            CurrentNodeIndex = 0;
            Flags = 0;
            NodesVisited = 0;
            EndingId = -1;
            Finished = false;
            ResultCollected = false;
            ElapsedSeconds = 0f;
        }

        /// <summary>故事是否结束（含结算报告已取回）。</summary>
        internal bool IsFinished
        {
            get { return Finished || ResultCollected; }
        }

        /// <summary>
        /// 条件选项的位映射（演示用轻量条件）：<paramref name="itemId"/> 的低 5 位决定 flag 位号，
        /// 超出 31 的 item id 会回绕复用（32 → 位 0、64 → 位 0），正式项目应替换为业务条件判定。
        /// </summary>
        internal static int FlagMask(int itemId)
        {
            return 1 << (itemId & 31);
        }

        /// <summary>该物品对应的 flag 位是否已置位（即"是否持有"）。</summary>
        internal bool HasFlag(int itemId)
        {
            return (Flags & FlagMask(itemId)) != 0;
        }

        /// <summary>置位该物品对应的 flag 位（发奖时调用；重复置位是幂等的）。</summary>
        internal void RaiseFlag(int itemId)
        {
            Flags |= FlagMask(itemId);
        }

        /// <summary>
        /// 导出存档快照：只带 4 个 int（当前节点 id 由调用方从剧本映射后传入）。
        /// 已结束、甚至已结算后调用都允许——快照只是当前进度的只读复印，不改任何状态。
        /// </summary>
        internal VisualNovelSaveState Capture(int currentNodeId)
        {
            return new VisualNovelSaveState(currentNodeId, Flags, NodesVisited, EndingId);
        }

        /// <summary>
        /// 就地恢复进度四元组。<paramref name="nodeIndex"/> 是调用方校验过的目标节点序号。
        /// <para>
        /// <b>不恢复</b>：<see cref="PendingLoot"/> / <see cref="GrantedNodes"/> / <see cref="ElapsedSeconds"/> /
        /// <see cref="ResultCollected"/>——读档只回到进度，不重放沿途节点进入副作用（不补发、不重复发奖）；
        /// 已结束标志由 <c>EndingId &gt;= 0</c> 推导，因此"悬空 Line/Jump 收尾"的结束态无法往返。
        /// </para>
        /// </summary>
        internal void Restore(int nodeIndex, in VisualNovelSaveState snapshot)
        {
            CurrentNodeIndex = nodeIndex;
            Flags = snapshot.Flags;
            NodesVisited = snapshot.NodesVisited;
            EndingId = snapshot.EndingId;
            Finished = snapshot.EndingId >= 0;
        }
    }
}

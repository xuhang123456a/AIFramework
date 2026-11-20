#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.VisualNovel
{
    /// <summary>
    /// 剧本的"校验 + 索引"层：构造期把纯数据 <see cref="VisualNovelNode"/> 数组收口成一份
    /// 内核可安全消费的剧本，运行期只按 <b>script index</b> 取节点，不做字典查找。
    /// <para>
    /// <b>为什么校验要做两轮</b>：节点之间的引用是前向自由的（0 → 10 → 3 很常见），
    /// 第一轮先把全部 id 登记进索引并做自身字段校验，第二轮才能判定"目标 id 是否存在"；
    /// 一轮到底会把合法的前向引用误判为坏数据。
    /// </para>
    /// <para>
    /// <b>防御性拷贝</b>：节点里带 <see cref="VisualNovelNode.Choices"/> 数组引用，构造时连节点数组
    /// 和每个选项数组都拷一份。剧本是内核构造时注入的纯数据，若调用方在运行期改原数组，
    /// 不应让一局已经开始的游戏走向另一种分支（确定性优先）。
    /// </para>
    /// <para>
    /// <b>错误口径</b>：所有结构错误统一抛 <see cref="ArgumentException"/>，消息里带出问题的节点 id
    /// （以及坏目标的 id），方便数据侧直接定位；不抛 <see cref="ArgumentNullException"/> 以免测试
    /// 必须按具体子类断言（空剧本也是 "剧本数据非法" 的一种）。
    /// </para>
    /// </summary>
    internal sealed class VisualNovelScript
    {
        /// <summary>校验并拷贝后的节点表；下标即运行期脚本序号。</summary>
        private readonly VisualNovelNode[] _nodes;

        /// <summary>节点 id → 脚本序号。仅构造期与读档恢复使用，逐帧路径不查它。</summary>
        private readonly Dictionary<int, int> _indexByNodeId;

        /// <summary>
        /// 校验整段剧本并建立索引。
        /// </summary>
        /// <param name="nodes">节点数组；null 与空数组都视为"空剧本"。</param>
        /// <param name="startNodeId">起始节点 id；必须存在于剧本中。</param>
        internal VisualNovelScript(VisualNovelNode[] nodes, int startNodeId)
        {
            if (nodes == null || nodes.Length == 0)
            {
                throw new ArgumentException("剧本为空：至少需要一个节点。", nameof(nodes));
            }

            _nodes = new VisualNovelNode[nodes.Length];
            _indexByNodeId = new Dictionary<int, int>(nodes.Length);

            // 第一轮：节点自身的字段校验 + id 唯一性 + 建立索引（同时做防御性拷贝）。
            for (int i = 0; i < nodes.Length; i++)
            {
                VisualNovelNode node = nodes[i];
                ValidateOwnFields(node);

                if (_indexByNodeId.ContainsKey(node.NodeId))
                {
                    throw new ArgumentException(
                        "节点 id 重复：" + node.NodeId + "。", nameof(nodes));
                }

                _indexByNodeId.Add(node.NodeId, i);
                _nodes[i] = CloneChoices(node);
            }

            // 第二轮：引用完整性（此时全部 id 都已登记，前向引用是合法的）。
            for (int i = 0; i < _nodes.Length; i++)
            {
                ValidateReferences(_nodes[i]);
            }

            if (!_indexByNodeId.TryGetValue(startNodeId, out int startIndex))
            {
                throw new ArgumentException(
                    "起始节点 " + startNodeId + " 不存在。", nameof(startNodeId));
            }

            StartNodeIndex = startIndex;
        }

        /// <summary>剧本节点总数（下标范围 <c>0..Count-1</c>）。</summary>
        internal int Count
        {
            get { return _nodes.Length; }
        }

        /// <summary>起始节点的脚本序号（构造期已校验存在）。</summary>
        internal int StartNodeIndex { get; }

        /// <summary>按脚本序号取节点（不做越界判断，调用方保证下标合法）。</summary>
        internal VisualNovelNode this[int index]
        {
            get { return _nodes[index]; }
        }

        /// <summary>按节点 id 查脚本序号；不存在返回 false。</summary>
        internal bool TryGetIndexByNodeId(int nodeId, out int index)
        {
            return _indexByNodeId.TryGetValue(nodeId, out index);
        }

        /// <summary>
        /// 节点自身字段校验：id 非负、发奖配置自洽、选项条件标记非负。
        /// 不校验引用（此时索引还没建完），引用在第二轮统一校验。
        /// </summary>
        private static void ValidateOwnFields(in VisualNovelNode node)
        {
            if (node.NodeId < 0)
            {
                throw new ArgumentException(
                    "节点 id 必须为非负整数：" + node.NodeId + "。");
            }

            if (node.GrantItemId < 0)
            {
                throw new ArgumentException(
                    "节点 " + node.NodeId + " 的 GrantItemId 不能为负：" + node.GrantItemId + "。");
            }

            if (node.GrantItemId > 0 && node.GrantCount <= 0)
            {
                throw new ArgumentException(
                    "节点 " + node.NodeId + " 声明了发奖但 GrantCount 不是正数："
                    + node.GrantCount + "。");
            }

            VisualNovelChoice[]? choices = node.Choices;
            if (choices == null)
            {
                return;
            }

            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i].RequiredItemId < 0)
                {
                    throw new ArgumentException(
                        "节点 " + node.NodeId + " 的第 " + i + " 个选项条件标记不能为负："
                        + choices[i].RequiredItemId + "。");
                }
            }
        }

        /// <summary>
        /// 引用校验：Choice 必须至少有一个选项且每个目标存在；Line/Jump 的 NextNodeId 若指向
        /// 非负 id 必须存在（负数表示"没有下一节点"，合法）。
        /// </summary>
        private void ValidateReferences(in VisualNovelNode node)
        {
            switch (node.Kind)
            {
                case VisualNovelNodeKind.Choice:
                    VisualNovelChoice[]? choices = node.Choices;
                    if (choices == null || choices.Length == 0)
                    {
                        throw new ArgumentException(
                            "Choice 节点 " + node.NodeId + " 没有任何选项。");
                    }

                    for (int i = 0; i < choices.Length; i++)
                    {
                        int target = choices[i].TargetNodeId;
                        if (!_indexByNodeId.ContainsKey(target))
                        {
                            throw new ArgumentException(
                                "节点 " + node.NodeId + " 的第 " + i + " 个选项目标节点 "
                                + target + " 不存在。");
                        }
                    }

                    break;

                case VisualNovelNodeKind.Line:
                case VisualNovelNodeKind.Jump:
                    if (node.NextNodeId >= 0 && !_indexByNodeId.ContainsKey(node.NextNodeId))
                    {
                        throw new ArgumentException(
                            "节点 " + node.NodeId + " 的 NextNodeId=" + node.NextNodeId + " 不存在。");
                    }

                    break;

                case VisualNovelNodeKind.End:
                    // 结局节点不使用 NextNodeId / Choices，指向哪都无所谓。
                    break;
            }
        }

        /// <summary>
        /// 拷贝节点；若带选项数组则连数组一起拷，避免调用方在运行期改原数组影响已开始的会话。
        /// </summary>
        private static VisualNovelNode CloneChoices(in VisualNovelNode node)
        {
            VisualNovelChoice[]? source = node.Choices;
            if (source == null)
            {
                return node;
            }

            var copy = new VisualNovelChoice[source.Length];
            Array.Copy(source, copy, source.Length);

            return new VisualNovelNode(
                node.NodeId,
                node.Kind,
                node.Speaker,
                node.Text,
                node.NextNodeId,
                copy,
                node.GrantItemId,
                node.GrantCount);
        }
    }
}

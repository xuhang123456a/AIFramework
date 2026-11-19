using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Core.Meta;
using Xunit;

namespace AIFramework.Core.Tests.Meta
{
    /// <summary>
    /// <see cref="RedDotTree"/> 行为契约：增量叠加与夹零、未知节点读 0、非法节点 ID 拒绝、
    /// Set / Clear 语义、<c>NodeCount</c> 只统计非 0 节点，以及 <see cref="RedDotTree.Register"/>
    /// 之后经真实 <see cref="CqeBus"/> 的命令 / 查询路由。
    /// </summary>
    public sealed class RedDotTreeTests
    {
        [Fact]
        public void Add_AccumulatesDeltaOnKnownNode()
        {
            RedDotTree tree = new RedDotTree();

            tree.Add(RedDotNodeIds.SignIn, 1);
            tree.Add(RedDotNodeIds.SignIn, 1);
            tree.Add(RedDotNodeIds.SignIn, 1);
            Assert.Equal(3, tree.Get(RedDotNodeIds.SignIn));

            tree.Add(RedDotNodeIds.SignIn, -2);
            Assert.Equal(1, tree.Get(RedDotNodeIds.SignIn));
        }

        [Fact]
        public void Add_WhenSumWouldGoNegative_ClampsAtZero()
        {
            RedDotTree tree = new RedDotTree();
            tree.Add(RedDotNodeIds.Mail, 2);

            tree.Add(RedDotNodeIds.Mail, -5);
            Assert.Equal(0, tree.Get(RedDotNodeIds.Mail));

            // 已经贴地后再减仍然是 0，不会绕回大正数。
            tree.Add(RedDotNodeIds.Mail, -1);
            Assert.Equal(0, tree.Get(RedDotNodeIds.Mail));
            Assert.Equal(0, tree.NodeCount);
        }

        [Fact]
        public void Add_ZeroDelta_LeavesValueAndNodeCountUntouched()
        {
            RedDotTree tree = new RedDotTree();
            tree.Add(RedDotNodeIds.Quest, 4);

            tree.Add(RedDotNodeIds.Quest, 0);

            Assert.Equal(4, tree.Get(RedDotNodeIds.Quest));
            Assert.Equal(1, tree.NodeCount);
        }

        [Fact]
        public void Get_UnknownOrOutOfRangeNode_ReturnsZero()
        {
            RedDotTree tree = new RedDotTree();
            tree.Add(RedDotNodeIds.SignIn, 7);

            // 从未写过的节点。
            Assert.Equal(0, tree.Get(RedDotNodeIds.Mail));

            // 越界节点：读路径是全函数，一律回 0，绝不抛异常。
            Assert.Equal(0, tree.Get(0));
            Assert.Equal(0, tree.Get(-3));
            Assert.Equal(0, tree.Get(RedDotTree.MaxNodeId + 1));
            Assert.Equal(0, tree.Get(int.MaxValue));
        }

        [Fact]
        public void Add_WithNonPositiveNodeId_Throws()
        {
            RedDotTree tree = new RedDotTree();

            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Add(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Add(-1, 1));
            Assert.Equal(0, tree.NodeCount);
        }

        [Fact]
        public void Add_WithNodeIdAboveLimit_Throws()
        {
            RedDotTree tree = new RedDotTree();

            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Add(RedDotTree.MaxNodeId + 1, 1));

            // 边界内的最大值本身必须可用。
            tree.Add(RedDotTree.MaxNodeId, 1);
            Assert.Equal(1, tree.Get(RedDotTree.MaxNodeId));
        }

        [Fact]
        public void Set_OverwritesInsteadOfAccumulating()
        {
            RedDotTree tree = new RedDotTree();

            tree.Add(RedDotNodeIds.Shop, 2);
            tree.Set(RedDotNodeIds.Shop, 9);
            Assert.Equal(9, tree.Get(RedDotNodeIds.Shop));

            tree.Set(RedDotNodeIds.Shop, 3);
            Assert.Equal(3, tree.Get(RedDotNodeIds.Shop));
        }

        [Fact]
        public void Set_NegativeValue_ClampsAtZero()
        {
            RedDotTree tree = new RedDotTree();
            tree.Set(RedDotNodeIds.Quest, 5);

            tree.Set(RedDotNodeIds.Quest, -4);

            Assert.Equal(0, tree.Get(RedDotNodeIds.Quest));
            Assert.Equal(0, tree.NodeCount);
        }

        [Fact]
        public void Set_WithOutOfRangeNodeId_Throws()
        {
            RedDotTree tree = new RedDotTree();

            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Set(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Set(RedDotTree.MaxNodeId + 1, 1));
        }

        [Fact]
        public void NodeCount_CountsOnlyNodesWithNonZeroValue()
        {
            RedDotTree tree = new RedDotTree();
            Assert.Equal(0, tree.NodeCount);

            tree.Add(RedDotNodeIds.SignIn, 2);
            tree.Add(RedDotNodeIds.Quest, 1);
            Assert.Equal(2, tree.NodeCount);

            // 2 -> 1 仍然非 0，节点数不变。
            tree.Add(RedDotNodeIds.SignIn, -1);
            Assert.Equal(2, tree.NodeCount);

            // 1 -> 0：节点熄灭。
            tree.Add(RedDotNodeIds.Quest, -1);
            Assert.Equal(1, tree.NodeCount);

            // 直接 Set 成 0 同样算熄灭。
            tree.Set(RedDotNodeIds.SignIn, 0);
            Assert.Equal(0, tree.NodeCount);

            // 非 0 -> 非 0 的覆盖不改变节点数。
            tree.Set(RedDotNodeIds.Mail, 9);
            tree.Set(RedDotNodeIds.Mail, 4);
            Assert.Equal(1, tree.NodeCount);
        }

        [Fact]
        public void Clear_ResetsEveryNodeAndNodeCount()
        {
            RedDotTree tree = new RedDotTree();
            tree.Add(RedDotNodeIds.SignIn, 3);
            tree.Set(RedDotNodeIds.Quest, 7);
            tree.Add(RedDotNodeIds.Mail, 1);
            Assert.Equal(3, tree.NodeCount);

            tree.Clear();

            Assert.Equal(0, tree.NodeCount);
            Assert.Equal(0, tree.Get(RedDotNodeIds.SignIn));
            Assert.Equal(0, tree.Get(RedDotNodeIds.Quest));
            Assert.Equal(0, tree.Get(RedDotNodeIds.Mail));

            // 清空后仍可正常写入，计数从 0 重新开始。
            tree.Add(RedDotNodeIds.SignIn, 1);
            Assert.Equal(1, tree.Get(RedDotNodeIds.SignIn));
            Assert.Equal(1, tree.NodeCount);
        }

        [Fact]
        public void Handle_Command_AppliesDelta()
        {
            RedDotTree tree = new RedDotTree();

            RedDotDeltaCommand plus = new RedDotDeltaCommand(RedDotNodeIds.SignIn, 3);
            tree.Handle(in plus);

            RedDotDeltaCommand minus = new RedDotDeltaCommand(RedDotNodeIds.SignIn, -1);
            tree.Handle(in minus);

            Assert.Equal(2, tree.Get(RedDotNodeIds.SignIn));
        }

        [Fact]
        public void Handle_Query_ReturnsSnapshotForRequestedNode()
        {
            RedDotTree tree = new RedDotTree();
            tree.Set(RedDotNodeIds.Quest, 5);

            RedDotQueryRequest known = new RedDotQueryRequest(RedDotNodeIds.Quest);
            RedDotSnapshot snapshot = tree.Handle(in known);

            Assert.Equal(RedDotNodeIds.Quest, snapshot.NodeId);
            Assert.Equal(5, snapshot.Count);

            // 未知节点：NodeId 原样带回，Count 为 0。
            RedDotQueryRequest unknown = new RedDotQueryRequest(RedDotNodeIds.Mail);
            RedDotSnapshot empty = tree.Handle(in unknown);

            Assert.Equal(RedDotNodeIds.Mail, empty.NodeId);
            Assert.Equal(0, empty.Count);
        }

        [Fact]
        public void Register_RoutesCommandAndQueryThroughCqeBus()
        {
            CqeBus bus = new CqeBus();
            RedDotTree tree = new RedDotTree();
            tree.Register(bus);

            RedDotDeltaCommand plusTwo = new RedDotDeltaCommand(RedDotNodeIds.Quest, 2);
            bus.Send(in plusTwo);

            RedDotDeltaCommand minusOne = new RedDotDeltaCommand(RedDotNodeIds.Quest, -1);
            bus.Send(in minusOne);

            RedDotQueryRequest query = new RedDotQueryRequest(RedDotNodeIds.Quest);
            RedDotSnapshot snapshot = bus.Query<RedDotQueryRequest, RedDotSnapshot>(in query);

            Assert.Equal(RedDotNodeIds.Quest, snapshot.NodeId);
            Assert.Equal(1, snapshot.Count);
            Assert.Equal(1, tree.Get(RedDotNodeIds.Quest));

            RedDotQueryRequest untouched = new RedDotQueryRequest(RedDotNodeIds.SignIn);
            RedDotSnapshot empty = bus.Query<RedDotQueryRequest, RedDotSnapshot>(in untouched);

            Assert.Equal(RedDotNodeIds.SignIn, empty.NodeId);
            Assert.Equal(0, empty.Count);
        }

        [Fact]
        public void Register_AddsTwoCommandAndOneQueryHandler()
        {
            CqeBus bus = new CqeBus();
            RedDotTree tree = new RedDotTree();

            tree.Register(bus);

            // 两条命令：RedDotDeltaCommand（增量）+ RedDotSetCommand（重登重算的绝对值）
            Assert.Equal(2, bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, bus.RegisteredQueryHandlerCount);
            Assert.Equal(0, bus.SubscriberCount);
        }

        [Fact]
        public void Register_WithNullBus_Throws()
        {
            RedDotTree tree = new RedDotTree();

            Assert.Throws<ArgumentNullException>(() => tree.Register(null!));
        }

        [Fact]
        public void Register_TwiceOnSameBus_Throws()
        {
            CqeBus bus = new CqeBus();
            RedDotTree tree = new RedDotTree();
            tree.Register(bus);

            Assert.Throws<InvalidOperationException>(() => tree.Register(bus));

            // 重复注册失败后总线状态不变：仍是 2 条命令（增量 + 设值）+ 1 条查询
            Assert.Equal(2, bus.RegisteredCommandHandlerCount);
            Assert.Equal(1, bus.RegisteredQueryHandlerCount);
        }

        [Fact]
        public void Add_OnWarmedPath_DoesNotAllocate()
        {
            RedDotTree tree = new RedDotTree();
            int nodeId = RedDotNodeIds.SignIn;

            // 预热：跑通数组下标、边界判断与分层编译。
            for (int i = 0; i < 100; i++)
            {
                tree.Add(nodeId, 1);
                tree.Add(nodeId, -1);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                tree.Add(nodeId, 1);
                tree.Set(nodeId, 0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(0L, allocated);
            Assert.Equal(0, tree.Get(nodeId));
            Assert.Equal(0, tree.NodeCount);
        }
    }
}

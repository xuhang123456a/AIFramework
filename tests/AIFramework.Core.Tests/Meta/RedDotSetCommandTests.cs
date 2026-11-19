using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Core.Meta;
using Xunit;

namespace AIFramework.Core.Tests.Meta
{
    /// <summary>
    /// 红点绝对值指令：重登/重连场景必须能用"设定值"覆盖脏基线，
    /// 否则同样的红点事件重放一次就翻倍（增量语义无法自我纠错）。
    /// </summary>
    public sealed class RedDotSetCommandTests
    {
        private static (CqeBus Bus, RedDotTree Tree) Create()
        {
            var bus = new CqeBus();
            var tree = new RedDotTree();
            tree.Register(bus);
            return (bus, tree);
        }

        [Fact]
        public void SetCommand_OverwritesValue()
        {
            (CqeBus bus, RedDotTree tree) = Create();

            var add = new RedDotDeltaCommand(RedDotNodeIds.Quest, 5);
            bus.Send(in add);

            var set = new RedDotSetCommand(RedDotNodeIds.Quest, 2);
            bus.Send(in set);

            Assert.Equal(2, tree.Get(RedDotNodeIds.Quest));
        }

        [Fact]
        public void SetCommand_NegativeValue_ClampsToZero()
        {
            (CqeBus bus, RedDotTree tree) = Create();

            var add = new RedDotDeltaCommand(RedDotNodeIds.Mail, 3);
            bus.Send(in add);
            var set = new RedDotSetCommand(RedDotNodeIds.Mail, -10);
            bus.Send(in set);

            Assert.Equal(0, tree.Get(RedDotNodeIds.Mail));
            Assert.Equal(0, tree.NodeCount);
        }

        [Fact]
        public void SetCommand_IsIdempotent()
        {
            (CqeBus bus, RedDotTree tree) = Create();

            for (int i = 0; i < 3; i++)
            {
                var set = new RedDotSetCommand(RedDotNodeIds.SignIn, 1);
                bus.Send(in set);
            }

            Assert.Equal(1, tree.Get(RedDotNodeIds.SignIn));
        }

        [Fact]
        public void SetCommand_ThenDelta_AccumulatesFromSetValue()
        {
            (CqeBus bus, RedDotTree tree) = Create();

            var set = new RedDotSetCommand(RedDotNodeIds.Shop, 4);
            bus.Send(in set);
            var add = new RedDotDeltaCommand(RedDotNodeIds.Shop, -1);
            bus.Send(in add);

            Assert.Equal(3, tree.Get(RedDotNodeIds.Shop));
        }

        [Fact]
        public void SetCommand_QueryReflectsImmediately()
        {
            (CqeBus bus, RedDotTree tree) = Create();

            var set = new RedDotSetCommand(RedDotNodeIds.Quest, 7);
            bus.Send(in set);

            var query = new RedDotQueryRequest(RedDotNodeIds.Quest);
            RedDotSnapshot snapshot = bus.Query<RedDotQueryRequest, RedDotSnapshot>(in query);

            Assert.Equal(7, snapshot.Count);
        }
    }
}

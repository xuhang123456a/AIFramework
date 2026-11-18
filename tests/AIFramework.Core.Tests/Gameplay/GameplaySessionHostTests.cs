using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Core.Gameplay;
using AIFramework.Kernel.Turn;
using Xunit;

namespace AIFramework.Core.Tests.Gameplay
{
    /// <summary>
    /// <see cref="GameplaySessionHost"/> 的进出门口契约：Begin 独占、EndAndCollect 结算即释放、
    /// 无会话结算抛异常，以及 <c>CollectResult</c> 自身抛异常时"原样冒泡且不丢会话"。
    /// </summary>
    public sealed class GameplaySessionHostTests
    {
        [Fact]
        public void Begin_CreatesSessionAndForwardsInitDataToFactory()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(7, 123456, 4);

            Assert.False(host.HasActiveSession);

            IGameplaySession session = host.Begin(GameplayKernelType.Turn, in init);

            Assert.Same(factory.Session, session);
            Assert.True(host.HasActiveSession);
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(GameplayKernelType.Turn, factory.LastType);
            Assert.Equal(7, factory.LastLevelId);
            Assert.Equal(123456, factory.LastRandomSeed);
            Assert.Equal(4, factory.LastUnitCount);
        }

        [Fact]
        public void Begin_WhileSessionIsActive_ThrowsWithoutTouchingFactory()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(1, 1, 1);

            host.Begin(GameplayKernelType.Turn, in init);
            Assert.Throws<InvalidOperationException>(() => host.Begin(GameplayKernelType.Turn, in init));

            Assert.Equal(1, factory.CreateCount);
            Assert.True(host.HasActiveSession);
        }

        [Fact]
        public void EndAndCollect_ReturnsResultAndClearsActiveSession()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(2, 2, 2);

            host.Begin(GameplayKernelType.Turn, in init);
            GameplayResult result = host.EndAndCollect();

            Assert.Same(factory.Session.Result, result);
            Assert.False(host.HasActiveSession);
            Assert.Equal(1, factory.Session.CollectCount);

            // 结算后可以再开新局。
            host.Begin(GameplayKernelType.Turn, in init);
            Assert.True(host.HasActiveSession);
            Assert.Equal(2, factory.CreateCount);
        }

        [Fact]
        public void EndAndCollect_WithoutActiveSession_Throws()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);

            Assert.Throws<InvalidOperationException>(() => host.EndAndCollect());
            Assert.Equal(0, factory.CreateCount);
        }

        [Fact]
        public void Release_DropsSessionAndAllowsBeginAgain()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(1, 1, 1);

            host.Begin(GameplayKernelType.Turn, in init);
            host.Release();

            Assert.False(host.HasActiveSession);

            host.Begin(GameplayKernelType.Turn, in init);
            Assert.True(host.HasActiveSession);
            Assert.Equal(2, factory.CreateCount);
        }

        [Fact]
        public void Current_ReflectsActiveSessionLifecycle()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(2, 2, 2);

            Assert.Null(host.Current);

            IGameplaySession session = host.Begin(GameplayKernelType.Turn, in init);
            Assert.Same(session, host.Current);

            host.EndAndCollect();
            Assert.Null(host.Current);

            host.Begin(GameplayKernelType.Turn, in init);
            Assert.NotNull(host.Current);

            host.Release();
            Assert.Null(host.Current);
        }

        [Fact]
        public void EndAndCollect_WhenCollectResultThrows_PropagatesSameExceptionAndKeepsSession()
        {
            FakeKernelFactory factory = new FakeKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(3, 3, 3);
            InvalidOperationException boom = new InvalidOperationException("collect failed on purpose");
            factory.Session.CollectException = boom;

            host.Begin(GameplayKernelType.Turn, in init);

            InvalidOperationException thrown =
                Assert.Throws<InvalidOperationException>(() => host.EndAndCollect());

            // 原样冒泡：不包装、不换类型。
            Assert.Same(boom, thrown);

            // 会话没有被提前丢弃，调用方还能决定 Release 还是继续等待结束。
            Assert.True(host.HasActiveSession);

            host.Release();
            Assert.False(host.HasActiveSession);
        }

        [Fact]
        public void EndAndCollect_WithUnfinishedTurnWorld_PropagatesAndKeepsSession()
        {
            TurnKernelFactory factory = new TurnKernelFactory();
            GameplaySessionHost host = new GameplaySessionHost(factory);
            GameplayInitData init = new GameplayInitData(1, 20241118, 2);

            host.Begin(GameplayKernelType.Turn, in init);

            InvalidOperationException thrown =
                Assert.Throws<InvalidOperationException>(() => host.EndAndCollect());

            // 具体实现抛的是 TurnWorld 自己的文案：宿主没有包装异常。
            Assert.Contains("尚未结束", thrown.Message);
            Assert.True(host.HasActiveSession);

            // 继续推进到结束再结算，仍然能正常出门。
            IGameplaySession session = factory.LastSession!;
            while (!session.IsFinished)
            {
                session.Step(1f);
            }

            GameplayResult result = host.EndAndCollect();
            Assert.True(result.IsVictory);
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(host.HasActiveSession);
        }

        // ── 假实现 ──────────────────────────────────────────────────────

        private sealed class FakeKernelFactory : IGameplayKernelFactory
        {
            public FakeSession Session { get; } = new FakeSession();

            public int CreateCount { get; private set; }

            public GameplayKernelType LastType { get; private set; }

            public int LastLevelId { get; private set; }

            public int LastRandomSeed { get; private set; }

            public int LastUnitCount { get; private set; }

            public IGameplaySession Create(GameplayKernelType type, in GameplayInitData init)
            {
                CreateCount++;
                LastType = type;
                LastLevelId = init.LevelId;
                LastRandomSeed = init.RandomSeed;
                LastUnitCount = init.UnitCount;
                return Session;
            }
        }

        private sealed class FakeSession : IGameplaySession
        {
            public FakeSession()
            {
                Result = new GameplayResult(
                    GameplayKernelType.Turn,
                    true,
                    3,
                    0,
                    Array.Empty<GameplayTag>(),
                    Array.Empty<LootItem>());
            }

            public GameplayResult Result { get; }

            public Exception? CollectException { get; set; }

            public int StepCount { get; private set; }

            public int CollectCount { get; private set; }

            public bool IsFinished => true;

            public void Step(float deltaSeconds)
            {
                StepCount++;
            }

            public GameplayResult CollectResult()
            {
                CollectCount++;

                if (CollectException != null)
                {
                    throw CollectException;
                }

                return Result;
            }
        }

        /// <summary>生产真实 <see cref="TurnWorld"/> 的工厂，用来验证真实内核的异常语义。</summary>
        private sealed class TurnKernelFactory : IGameplayKernelFactory
        {
            public IGameplaySession? LastSession { get; private set; }

            public IGameplaySession Create(GameplayKernelType type, in GameplayInitData init)
            {
                TurnWorld world = new TurnWorld(in init);
                LastSession = world;
                return world;
            }
        }
    }
}

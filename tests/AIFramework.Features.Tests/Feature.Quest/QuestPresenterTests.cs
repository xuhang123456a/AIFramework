using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Quest;
using Xunit;

namespace AIFramework.Features.Tests.Quest
{
    /// <summary>
    /// Presenter 验收：真实 <see cref="CqeBus"/> + 切片 <see cref="QuestHandler"/> + 文件内 <see cref="FakeQuestView"/>，
    /// 断言只落在"视图收到什么"、"总线发出什么"与"状态快照"三处，装配不碰共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class QuestPresenterTests
    {
        /// <summary>发奖原因标签（与切片内常量一致，写在断言里防止被无声改动）。</summary>
        private const int ClaimReasonTagId = 6201;

        /// <summary>战斗结束事件里的关卡号，Phase 1 不参与任务过滤，仅占位。</summary>
        private const int LevelId = 101;

        [Fact]
        public void Refresh_PushesEveryQuestInIdOrderWithTableRewards()
        {
            // 行序故意打乱：视图拿到的顺序必须由子表统一按 questId 升序决定。
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(1003, 5, 4303, 30),
                new QuestRowSpec(1001, 2, 4301, 10),
                new QuestRowSpec(1002, 1, 4302, 20)));

            harness.Presenter.Refresh();

            Assert.Equal(1, harness.View.RenderCount);
            QuestItemViewData[] quests = harness.View.LastQuests;
            Assert.Equal(3, quests.Length);

            Assert.Equal(1001, quests[0].QuestId);
            Assert.Equal(0, quests[0].Progress);
            Assert.Equal(2, quests[0].TargetCount);
            Assert.False(quests[0].Claimed);
            Assert.False(quests[0].Claimable);
            Assert.Equal(4301, quests[0].RewardItemId);
            Assert.Equal(10, quests[0].RewardCount);

            Assert.Equal(1002, quests[1].QuestId);
            Assert.Equal(1, quests[1].TargetCount);
            Assert.Equal(4302, quests[1].RewardItemId);
            Assert.Equal(20, quests[1].RewardCount);

            Assert.Equal(1003, quests[2].QuestId);
            Assert.Equal(5, quests[2].TargetCount);
            Assert.Equal(4303, quests[2].RewardItemId);
            Assert.Equal(30, quests[2].RewardCount);
        }

        [Fact]
        public void Refresh_ComputesClaimableForIncompleteClaimableAndClaimed()
        {
            // 三态一次覆盖：未完成（1/3）、已完成未领（1/1）、已领（1/1 → 领掉）。
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(5001, 3, 9001, 10),
                new QuestRowSpec(5002, 1, 9002, 20),
                new QuestRowSpec(5003, 1, 9003, 30)));

            harness.FinishGame();
            harness.Presenter.Claim(5003);
            harness.Presenter.Refresh();

            QuestItemViewData[] quests = harness.View.LastQuests;
            Assert.Equal(3, quests.Length);

            Assert.Equal(5001, quests[0].QuestId);
            Assert.Equal(1, quests[0].Progress);
            Assert.Equal(3, quests[0].TargetCount);
            Assert.False(quests[0].Claimed);
            Assert.False(quests[0].Claimable);

            Assert.Equal(5002, quests[1].QuestId);
            Assert.Equal(1, quests[1].Progress);
            Assert.Equal(1, quests[1].TargetCount);
            Assert.False(quests[1].Claimed);
            Assert.True(quests[1].Claimable);

            Assert.Equal(5003, quests[2].QuestId);
            Assert.True(quests[2].Claimed);
            Assert.False(quests[2].Claimable);
        }

        [Fact]
        public void Claim_OnClaimableQuest_GrantsRewardAndRefreshShowsClaimed()
        {
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(2001, 2, 9101, 5)));

            harness.FinishGame();
            harness.FinishGame();
            harness.Presenter.Refresh();
            Assert.True(harness.View.LastQuests[0].Claimable);

            harness.Presenter.Claim(2001);

            GrantItemCommand grant = Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Equal(9101, grant.ItemId);
            Assert.Equal(5, grant.Count);
            Assert.Equal(ClaimReasonTagId, grant.ReasonTagId);
            Assert.Single(harness.MessagesOf<QuestClaimedEvent>());
            Assert.True(harness.Handler.GetState(2001).Claimed);

            // 显式 Refresh 一次 + Claim 内部 Refresh 一次。
            Assert.Equal(2, harness.View.RenderCount);
            QuestItemViewData claimed = harness.View.LastQuests[0];
            Assert.Equal(2001, claimed.QuestId);
            Assert.Equal(2, claimed.Progress);
            Assert.Equal(2, claimed.TargetCount);
            Assert.True(claimed.Claimed);
            Assert.False(claimed.Claimable);
        }

        [Fact]
        public void Claim_Twice_SecondClaimDoesNotGrantAgain()
        {
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(2101, 1, 9111, 2)));

            harness.FinishGame();
            harness.Presenter.Claim(2101);
            int messagesAfterFirstClaim = harness.Messages.Count;

            harness.Presenter.Claim(2101);

            Assert.Equal(messagesAfterFirstClaim, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<QuestClaimedEvent>());
            Assert.True(harness.View.LastQuests[0].Claimed);
            Assert.False(harness.View.LastQuests[0].Claimable);
        }

        [Fact]
        public void Claim_OnUnfinishedQuest_DoesNotThrowAndKeepsProgress()
        {
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(3001, 2, 9201, 1)));

            harness.FinishGame();
            int messagesBeforeClaim = harness.Messages.Count;

            Exception? error = Record.Exception(() => harness.Presenter.Claim(3001));

            Assert.Null(error);
            Assert.Equal(messagesBeforeClaim, harness.Messages.Count);
            Assert.Empty(harness.MessagesOf<GrantItemCommand>());
            Assert.Empty(harness.MessagesOf<QuestClaimedEvent>());
            Assert.Equal(1, harness.Handler.GetState(3001).Progress);
            Assert.False(harness.Handler.GetState(3001).Claimed);

            // 拒绝后照常重建视图：进度不变、仍不可领。
            Assert.Equal(1, harness.View.RenderCount);
            Assert.Equal(1, harness.View.LastQuests[0].Progress);
            Assert.Equal(2, harness.View.LastQuests[0].TargetCount);
            Assert.False(harness.View.LastQuests[0].Claimable);
        }

        [Fact]
        public void Claim_OnUnknownQuest_DoesNotThrowAndLeavesListUntouched()
        {
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(3101, 1, 9301, 1)));

            harness.FinishGame();
            harness.Presenter.Refresh();
            int messagesBeforeClaim = harness.Messages.Count;

            Exception? error = Record.Exception(() => harness.Presenter.Claim(9999));

            Assert.Null(error);
            Assert.Equal(messagesBeforeClaim, harness.Messages.Count);
            Assert.Empty(harness.MessagesOf<GrantItemCommand>());
            Assert.False(harness.Handler.GetState(3101).Claimed);

            // 未知 ID 不进入视图：列表仍是唯一那条可领任务。
            Assert.Single(harness.View.LastQuests);
            Assert.Equal(3101, harness.View.LastQuests[0].QuestId);
            Assert.True(harness.View.LastQuests[0].Claimable);
        }

        [Fact]
        public void ClaimClicked_FromView_RoutesToPresenterClaim()
        {
            QuestPresenterHarness harness = QuestPresenterHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(4001, 1, 9401, 2)));

            harness.FinishGame();
            harness.Presenter.Refresh();

            harness.View.RaiseClaimClicked(4001);

            GrantItemCommand grant = Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Equal(9401, grant.ItemId);
            Assert.Equal(2, grant.Count);
            Assert.True(harness.Handler.GetState(4001).Claimed);
            Assert.True(harness.View.LastQuests[0].Claimed);
            Assert.False(harness.View.LastQuests[0].Claimable);
        }

        [Fact]
        public void Constructor_NullView_ThrowsArgumentNullException()
        {
            CqeBus bus = new CqeBus();

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => new QuestPresenter(bus, null!));

            Assert.Equal("view", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullBus_ThrowsArgumentNullException()
        {
            FakeQuestView view = new FakeQuestView();

            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => new QuestPresenter(null!, view));

            Assert.Equal("bus", exception.ParamName);
        }

        /// <summary>一条任务行的夹具定义（字节序与切片子表一致）。</summary>
        private readonly struct QuestRowSpec
        {
            internal QuestRowSpec(int questId, int targetCount, int rewardItemId, int rewardCount)
            {
                QuestId = questId;
                TargetCount = targetCount;
                RewardItemId = rewardItemId;
                RewardCount = rewardCount;
            }

            internal int QuestId { get; }

            internal int TargetCount { get; }

            internal int RewardItemId { get; }

            internal int RewardCount { get; }
        }

        /// <summary>
        /// 任务子表加载桩：按 Phase 1 夹具格式造字节（小端 <c>[int32 rowCount]</c> + 16 字节/行）。
        /// 只在本文件内使用，不改共享 <c>Fakes/</c>。
        /// </summary>
        private sealed class QuestTableStub : IBinaryTableLoader
        {
            internal const string TableName = "TbQuest";

            private const int HeaderBytes = 4;
            private const int RowBytes = 16;

            private readonly Dictionary<string, byte[]?> _tables;

            private QuestTableStub(Dictionary<string, byte[]?> tables)
            {
                _tables = tables;
            }

            internal static QuestTableStub WithRows(params QuestRowSpec[] rows)
            {
                Dictionary<string, byte[]?> tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                tables[TableName] = BuildPayload(rows);
                return new QuestTableStub(tables);
            }

            public byte[]? Load(string tableName)
            {
                byte[]? payload;
                return _tables.TryGetValue(tableName, out payload) ? payload : null;
            }

            private static byte[] BuildPayload(QuestRowSpec[] rows)
            {
                byte[] payload = new byte[HeaderBytes + (rows.Length * RowBytes)];
                WriteInt32LittleEndian(payload, 0, rows.Length);
                for (int i = 0; i < rows.Length; i++)
                {
                    int offset = HeaderBytes + (i * RowBytes);
                    WriteInt32LittleEndian(payload, offset, rows[i].QuestId);
                    WriteInt32LittleEndian(payload, offset + 4, rows[i].TargetCount);
                    WriteInt32LittleEndian(payload, offset + 8, rows[i].RewardItemId);
                    WriteInt32LittleEndian(payload, offset + 12, rows[i].RewardCount);
                }

                return payload;
            }

            private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
            {
                target[offset] = (byte)(value & 0xFF);
                target[offset + 1] = (byte)((value >> 8) & 0xFF);
                target[offset + 2] = (byte)((value >> 16) & 0xFF);
                target[offset + 3] = (byte)((value >> 24) & 0xFF);
            }
        }

        /// <summary>任务视图假实现：记录每次渲染的任务列表，并提供"人手点击领取"的触发入口。</summary>
        private sealed class FakeQuestView : IQuestView
        {
            internal FakeQuestView()
            {
                LastQuests = Array.Empty<QuestItemViewData>();
            }

            /// <inheritdoc />
            public event Action<int>? OnClaimClicked;

            /// <summary>渲染次数。</summary>
            internal int RenderCount { get; private set; }

            /// <summary>最近一次渲染的任务列表（拷贝副本，视图外不可回写）。</summary>
            internal QuestItemViewData[] LastQuests { get; private set; }

            /// <inheritdoc />
            public void RenderQuests(IReadOnlyList<QuestItemViewData> quests)
            {
                if (quests == null)
                {
                    throw new ArgumentNullException(nameof(quests));
                }

                QuestItemViewData[] copy = new QuestItemViewData[quests.Count];
                for (int i = 0; i < quests.Count; i++)
                {
                    copy[i] = quests[i];
                }

                RenderCount++;
                LastQuests = copy;
            }

            /// <summary>模拟点击某条任务的领取按钮，参数按视图契约回传任务 ID。</summary>
            internal void RaiseClaimClicked(int questId)
            {
                Action<int>? handler = OnClaimClicked;
                if (handler != null)
                {
                    handler(questId);
                }
            }
        }

        /// <summary>按发生顺序记录命令与事件的消息流水，用于断言"切片真实发出了什么、顺序如何"。</summary>
        private sealed class MessageRecorder
        {
            private readonly List<object> _entries;

            internal MessageRecorder()
            {
                _entries = new List<object>();
            }

            internal IReadOnlyList<object> Entries
            {
                get { return _entries; }
            }

            internal void Record(object message)
            {
                _entries.Add(message);
            }

            internal List<T> Of<T>()
                where T : struct
            {
                List<T> result = new List<T>();
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i] is T typed)
                    {
                        result.Add(typed);
                    }
                }

                return result;
            }
        }

        /// <summary>命令桩：走真实 <see cref="CqeBus"/> 派发，只记录不改状态。</summary>
        private sealed class CommandSpy<TCommand> : ICommandHandler<TCommand>
            where TCommand : struct, ICommand
        {
            private readonly MessageRecorder _recorder;

            internal CommandSpy(MessageRecorder recorder)
            {
                _recorder = recorder;
            }

            public void Handle(in TCommand command)
            {
                _recorder.Record(command);
            }
        }

        /// <summary>事件桩：走真实 <see cref="CqeBus"/> 派发，只记录。</summary>
        private sealed class EventSpy<TEvent> : IEventHandler<TEvent>
            where TEvent : struct, IEvent
        {
            private readonly MessageRecorder _recorder;

            internal EventSpy(MessageRecorder recorder)
            {
                _recorder = recorder;
            }

            public void Handle(in TEvent evt)
            {
                _recorder.Record(evt);
            }
        }

        /// <summary>装配：真实 <see cref="CqeBus"/> + 本文件私有桩 + 切片处理器 + Presenter。</summary>
        private sealed class QuestPresenterHarness
        {
            private readonly MessageRecorder _messages;

            private QuestPresenterHarness(
                CqeBus bus,
                MessageRecorder messages,
                QuestHandler handler,
                QuestPresenter presenter,
                FakeQuestView view)
            {
                Bus = bus;
                _messages = messages;
                Handler = handler;
                Presenter = presenter;
                View = view;
            }

            internal CqeBus Bus { get; }

            internal QuestHandler Handler { get; }

            internal QuestPresenter Presenter { get; }

            internal FakeQuestView View { get; }

            internal IReadOnlyList<object> Messages
            {
                get { return _messages.Entries; }
            }

            internal static QuestPresenterHarness Create(QuestTableStub loader)
            {
                CqeBus bus = new CqeBus();
                MessageRecorder messages = new MessageRecorder();

                // 切片发出的两条指令必须有处理器，否则真实总线会抛"未注册"——桩在这里同时兼作接收端。
                bus.RegisterCommandHandler<GrantItemCommand>(new CommandSpy<GrantItemCommand>(messages));
                bus.RegisterCommandHandler<RedDotDeltaCommand>(new CommandSpy<RedDotDeltaCommand>(messages));
                bus.Subscribe<QuestClaimedEvent>(new EventSpy<QuestClaimedEvent>(messages));

                QuestHandler handler = new QuestHandler(bus, loader);
                handler.Register(bus);

                FakeQuestView view = new FakeQuestView();
                QuestPresenter presenter = new QuestPresenter(bus, view);
                return new QuestPresenterHarness(bus, messages, handler, presenter, view);
            }

            internal void FinishGame()
            {
                Bus.Publish(new GameFinishedEvent(GameplayKernelType.Turn, LevelId, true));
            }

            internal List<T> MessagesOf<T>()
                where T : struct
            {
                return _messages.Of<T>();
            }
        }
    }
}

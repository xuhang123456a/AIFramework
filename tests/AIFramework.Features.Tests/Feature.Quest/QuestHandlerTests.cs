using System;
using System.Collections.Generic;
using System.IO;
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
    /// 任务切片业务规则验收：真实 <see cref="CqeBus"/> + 本文件内的私有桩（不触碰共享 <c>Fakes/</c>），
    /// 断言只落在"切片发出了什么消息"与"状态快照"两处。
    /// </summary>
    public sealed class QuestHandlerTests
    {
        /// <summary>发奖原因标签（与切片内常量一致，写在断言里防止被无声改动）。</summary>
        private const int ClaimReasonTagId = 6201;

        /// <summary>战斗结束事件里的关卡号，Phase 1 不参与任务过滤，仅占位。</summary>
        private const int LevelId = 101;

        [Fact]
        public void GameFinished_AdvancesEveryQuestAndEmitsOneProgressAndOneRedDotEach()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(5001, 2, 3101, 10),
                new QuestRowSpec(5002, 3, 3201, 1)));

            harness.FinishGame();

            Assert.Equal(1, harness.Handler.GetState(5001).Progress);
            Assert.Equal(1, harness.Handler.GetState(5002).Progress);
            Assert.False(harness.Handler.GetState(5001).Claimed);

            List<QuestProgressedEvent> progressed = harness.MessagesOf<QuestProgressedEvent>();
            Assert.Equal(2, progressed.Count);
            Assert.Equal(5001, progressed[0].QuestId);
            Assert.Equal(1, progressed[0].Progress);
            Assert.Equal(2, progressed[0].TargetCount);
            Assert.Equal(5002, progressed[1].QuestId);
            Assert.Equal(3, progressed[1].TargetCount);

            List<RedDotDeltaCommand> deltas = harness.MessagesOf<RedDotDeltaCommand>();
            Assert.Equal(2, deltas.Count);
            for (int i = 0; i < deltas.Count; i++)
            {
                Assert.Equal(RedDotNodeIds.Quest, deltas[i].NodeId);
                Assert.Equal(1, deltas[i].Delta);
            }

            Assert.Empty(harness.MessagesOf<GrantItemCommand>());
            Assert.Empty(harness.MessagesOf<QuestClaimedEvent>());
        }

        [Fact]
        public void GameFinished_CapsProgressAtTargetAndStopsEmitting()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(6001, 2, 3301, 5)));

            harness.FinishGame();
            harness.FinishGame();
            harness.FinishGame();

            // 目标 2：第三次事件不得把进度推到 3，也不得再广播/再点亮红点。
            Assert.Equal(2, harness.Handler.GetState(6001).Progress);
            Assert.Equal(2, harness.MessagesOf<QuestProgressedEvent>().Count);
            Assert.Equal(2, harness.MessagesOf<RedDotDeltaCommand>().Count);
        }

        [Fact]
        public void ClaimBeforeCompletion_IsRejectedWithoutAnyMessage()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(6101, 2, 4001, 3)));

            harness.FinishGame();
            int messagesBeforeClaim = harness.Messages.Count;

            harness.Claim(6101);

            Assert.Equal(messagesBeforeClaim, harness.Messages.Count);
            Assert.Empty(harness.MessagesOf<GrantItemCommand>());
            Assert.Empty(harness.MessagesOf<QuestClaimedEvent>());
            Assert.Single(harness.MessagesOf<RedDotDeltaCommand>());
            Assert.False(harness.Handler.GetState(6101).Claimed);
            Assert.Equal(1, harness.Handler.GetState(6101).Progress);
        }

        [Fact]
        public void ClaimAfterCompletion_GrantsRewardPublishesEventAndClearsRedDot()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(6201, 1, 4010, 7)));

            harness.FinishGame();
            harness.Claim(6201);

            // 顺序契约：进度事实、红点 +1、发奖指令、已领取事件、红点 -1。
            Assert.Equal(5, harness.Messages.Count);
            GrantItemCommand grant = Assert.IsType<GrantItemCommand>(harness.Messages[2]);
            Assert.Equal(4010, grant.ItemId);
            Assert.Equal(7, grant.Count);
            Assert.Equal(ClaimReasonTagId, grant.ReasonTagId);

            QuestClaimedEvent claimed = Assert.IsType<QuestClaimedEvent>(harness.Messages[3]);
            Assert.Equal(6201, claimed.QuestId);
            Assert.Equal(4010, claimed.RewardItemId);
            Assert.Equal(7, claimed.RewardCount);

            RedDotDeltaCommand redDot = Assert.IsType<RedDotDeltaCommand>(harness.Messages[4]);
            Assert.Equal(RedDotNodeIds.Quest, redDot.NodeId);
            Assert.Equal(-1, redDot.Delta);

            Assert.True(harness.Handler.GetState(6201).Claimed);
        }

        [Fact]
        public void ClaimTwice_SecondClaimIsRejected()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(6301, 1, 4020, 2)));

            harness.FinishGame();
            harness.Claim(6301);
            int messagesAfterFirstClaim = harness.Messages.Count;

            harness.Claim(6301);

            Assert.Equal(messagesAfterFirstClaim, harness.Messages.Count);
            Assert.Single(harness.MessagesOf<GrantItemCommand>());
            Assert.Single(harness.MessagesOf<QuestClaimedEvent>());
            Assert.True(harness.Handler.GetState(6301).Claimed);
        }

        [Fact]
        public void ClaimUnknownQuestId_IsRejectedSilentlyAndGetStateThrows()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(6401, 1, 4030, 1)));

            harness.Claim(9999);

            Assert.Empty(harness.Messages);
            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Handler.GetState(9999));
        }

        [Fact]
        public void ClaimedQuest_StopsProgressing()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(8001, 1, 4101, 1),
                new QuestRowSpec(8002, 3, 4102, 2)));

            harness.FinishGame();
            harness.Claim(8001);
            harness.FinishGame();

            Assert.Equal(1, harness.Handler.GetState(8001).Progress);
            Assert.True(harness.Handler.GetState(8001).Claimed);
            Assert.Equal(2, harness.Handler.GetState(8002).Progress);

            List<QuestProgressedEvent> progressed = harness.MessagesOf<QuestProgressedEvent>();
            Assert.Equal(3, progressed.Count);
            Assert.Equal(2, progressed[2].Progress);
            Assert.Equal(8002, progressed[2].QuestId);
        }

        [Fact]
        public void Query_ReturnsQuestViewDataAscendingByQuestId()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(7003, 5, 4303, 30),
                new QuestRowSpec(7001, 2, 4301, 10),
                new QuestRowSpec(7002, 1, 4302, 20)));

            harness.FinishGame();
            harness.Claim(7002);

            QuestListSnapshot snapshot = harness.QueryList();

            Assert.Equal(3, snapshot.Quests.Count);
            Assert.Equal(7001, snapshot.Quests[0].QuestId);
            Assert.Equal(7002, snapshot.Quests[1].QuestId);
            Assert.Equal(7003, snapshot.Quests[2].QuestId);

            // 未领取：进度 1 / 目标 2，奖励列回读自子表。
            Assert.Equal(1, snapshot.Quests[0].Progress);
            Assert.Equal(2, snapshot.Quests[0].TargetCount);
            Assert.False(snapshot.Quests[0].Claimed);
            Assert.Equal(4301, snapshot.Quests[0].RewardItemId);
            Assert.Equal(10, snapshot.Quests[0].RewardCount);

            // 已领取：进度封顶 1 / 目标 1，展示层据此渲染"已领取"。
            Assert.Equal(1, snapshot.Quests[1].Progress);
            Assert.Equal(1, snapshot.Quests[1].TargetCount);
            Assert.True(snapshot.Quests[1].Claimed);
            Assert.Equal(20, snapshot.Quests[1].RewardCount);

            Assert.Equal(5, snapshot.Quests[2].TargetCount);
            Assert.False(snapshot.Quests[2].Claimed);
        }

        [Fact]
        public void Register_WiresOneSubscriptionOneCommandHandlerAndQueryHandler()
        {
            QuestHarness harness = QuestHarness.Create(QuestTableStub.WithRows(
                new QuestRowSpec(9001, 1, 4401, 1)));

            // 2 个记录桩（GrantItemCommand / RedDotDeltaCommand）+ 切片自己注册的领取命令 = 3。
            Assert.Equal(3, harness.Bus.RegisteredCommandHandlerCount);

            // 查询处理器与事件订阅只可能来自切片（harness 只注册了命令桩与事件记录桩）。
            Assert.Equal(1, harness.Bus.RegisteredQueryHandlerCount);

            // 事件订阅共 3 个：2 个记录桩 + 切片自己订阅的 GameFinishedEvent。
            Assert.Equal(3, harness.Bus.SubscriberCount);
            Assert.Single(harness.QueryList().Quests);
        }

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new QuestHandler(new CqeBus(), QuestTableStub.WithoutTable()));

            Assert.Contains("TbQuest", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DeclaredRowCountMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 2 行、实际只写 1 行：长度校验必须挡住。
            QuestTableStub mismatched = QuestTableStub.WithPayload(
                QuestTableStub.BuildPayload(new QuestRowSpec[]
                {
                    new QuestRowSpec(5001, 1, 3101, 1),
                }, 2));

            Assert.Throws<InvalidDataException>(() => new QuestTables(mismatched));

            // 连 4 字节头部都不够：同样按非法数据拒绝。
            Assert.Throws<InvalidDataException>(() => new QuestTables(QuestTableStub.WithPayload(new byte[] { 1, 0 })));
        }

        [Fact]
        public void DuplicateQuestId_ThrowsInvalidDataException()
        {
            QuestTableStub duplicated = QuestTableStub.WithPayload(
                QuestTableStub.BuildPayload(new QuestRowSpec[]
                {
                    new QuestRowSpec(5001, 1, 3101, 1),
                    new QuestRowSpec(5001, 2, 3102, 2),
                }));

            Assert.Throws<InvalidDataException>(() => new QuestTables(duplicated));
        }

        [Fact]
        public void NonPositiveTargetCount_ThrowsInvalidDataException()
        {
            QuestTableStub zeroTarget = QuestTableStub.WithPayload(
                QuestTableStub.BuildPayload(new QuestRowSpec[]
                {
                    new QuestRowSpec(5001, 0, 3101, 1),
                }));

            Assert.Throws<InvalidDataException>(() => new QuestTables(zeroTarget));
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

            internal static QuestTableStub WithoutTable()
            {
                return new QuestTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
            }

            internal static QuestTableStub WithRows(params QuestRowSpec[] rows)
            {
                return WithPayload(BuildPayload(rows));
            }

            internal static QuestTableStub WithPayload(byte[]? payload)
            {
                Dictionary<string, byte[]?> tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                tables[TableName] = payload;
                return new QuestTableStub(tables);
            }

            internal static byte[] BuildPayload(QuestRowSpec[] rows)
            {
                return BuildPayload(rows, rows.Length);
            }

            /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度校验反例）。</summary>
            internal static byte[] BuildPayload(QuestRowSpec[] rows, int declaredRowCount)
            {
                byte[] payload = new byte[HeaderBytes + (rows.Length * RowBytes)];
                WriteInt32LittleEndian(payload, 0, declaredRowCount);
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

            public byte[]? Load(string tableName)
            {
                byte[]? payload;
                return _tables.TryGetValue(tableName, out payload) ? payload : null;
            }

            private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
            {
                target[offset] = (byte)(value & 0xFF);
                target[offset + 1] = (byte)((value >> 8) & 0xFF);
                target[offset + 2] = (byte)((value >> 16) & 0xFF);
                target[offset + 3] = (byte)((value >> 24) & 0xFF);
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

        /// <summary>装配：真实 <see cref="CqeBus"/> + 本文件私有桩 + 切片 <see cref="QuestHandler"/>。</summary>
        private sealed class QuestHarness
        {
            private readonly MessageRecorder _messages;

            private QuestHarness(CqeBus bus, MessageRecorder messages, QuestHandler handler)
            {
                Bus = bus;
                _messages = messages;
                Handler = handler;
            }

            internal CqeBus Bus { get; }

            internal QuestHandler Handler { get; }

            internal IReadOnlyList<object> Messages
            {
                get { return _messages.Entries; }
            }

            internal static QuestHarness Create(QuestTableStub loader)
            {
                CqeBus bus = new CqeBus();
                MessageRecorder messages = new MessageRecorder();

                // 切片发出的两条指令必须有处理器，否则真实总线会抛"未注册"——桩在这里同时兼作接收端。
                bus.RegisterCommandHandler<GrantItemCommand>(new CommandSpy<GrantItemCommand>(messages));
                bus.RegisterCommandHandler<RedDotDeltaCommand>(new CommandSpy<RedDotDeltaCommand>(messages));
                bus.Subscribe<QuestProgressedEvent>(new EventSpy<QuestProgressedEvent>(messages));
                bus.Subscribe<QuestClaimedEvent>(new EventSpy<QuestClaimedEvent>(messages));

                QuestHandler handler = new QuestHandler(bus, loader);
                handler.Register(bus);
                return new QuestHarness(bus, messages, handler);
            }

            internal void FinishGame()
            {
                Bus.Publish(new GameFinishedEvent(GameplayKernelType.Turn, LevelId, true));
            }

            internal void Claim(int questId)
            {
                Bus.Send(new ClaimQuestRewardCommand(questId));
            }

            internal QuestListSnapshot QueryList()
            {
                return Bus.Query<QuestListQueryRequest, QuestListSnapshot>(new QuestListQueryRequest());
            }

            internal List<T> MessagesOf<T>()
                where T : struct
            {
                return _messages.Of<T>();
            }
        }
    }
}

using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Core.Cqe;
using AIFramework.Features.Guide;
using Xunit;

namespace AIFramework.Features.Tests.Guide
{
    /// <summary>
    /// 引导自动演示：脚本编解码、按延迟播放、命令到达总线、失败不中断。
    /// <para>为什么值得测：演示脚本会在 QA/录屏里反复跑，任何"少发一条/顺序错"都会让演示失真。</para>
    /// </summary>
    public sealed class GuideDemoScriptTests
    {
        private static GuideDemoScript SampleScript()
        {
            return new GuideDemoScript("demo", new[]
            {
                new GuideDemoStep(kind: 0, value: 0, targetId: 0, delayMs: 0),
                new GuideDemoStep(kind: 1, value: 1, targetId: 0, delayMs: 100),
                new GuideDemoStep(kind: 0, value: 1, targetId: 0, delayMs: 100),
            });
        }

        [Fact]
        public void Json_RoundTrips()
        {
            string json = GuideDemoScriptCodec.ToJson(SampleScript());

            Assert.True(GuideDemoScriptCodec.TryParse(json, out GuideDemoScript parsed, out string error), error);
            Assert.Equal("demo", parsed.Name);
            Assert.Equal(3, parsed.Steps.Length);
            Assert.Equal(0, parsed.Steps[0].Kind);
            Assert.Equal(1, parsed.Steps[1].Value);
            Assert.Equal(100, parsed.Steps[2].DelayMs);
        }

        [Fact]
        public void Json_EmptyName_Escaped()
        {
            var script = new GuideDemoScript("带\"引号\"的名字", new[] { new GuideDemoStep(0, 0, 0, 0) });

            Assert.True(GuideDemoScriptCodec.TryParse(GuideDemoScriptCodec.ToJson(script), out GuideDemoScript parsed, out _));
            Assert.Equal("带\"引号\"的名字", parsed.Name);
        }

        [Fact]
        public void Json_Broken_ReturnsFalse()
        {
            Assert.False(GuideDemoScriptCodec.TryParse("{ broken", out _, out string error));
            Assert.False(string.IsNullOrEmpty(error)); // 手写解析器给的是"缺 steps"这类明确原因，不强制措辞
        }

        [Fact]
        public void Json_Empty_ReturnsFalse()
        {
            Assert.False(GuideDemoScriptCodec.TryParse(null, out _, out _));
            Assert.False(GuideDemoScriptCodec.TryParse(string.Empty, out _, out _));
        }

        [Fact]
        public void Json_MissingField_ReturnsFalse()
        {
            Assert.False(GuideDemoScriptCodec.TryParse("{\"steps\":[{\"kind\":0}]}", out _, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void TotalDuration_SumsDelays()
        {
            Assert.Equal(200, SampleScript().TotalDurationMs);
        }

        [Fact]
        public void Player_ExecutesStepsWhenDue()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());

            Assert.Equal(1, player.Tick(0));
            Assert.Equal(0, player.Tick(99));
            Assert.Equal(1, player.Tick(1));
            Assert.Equal(1, player.Tick(100));
            Assert.True(player.IsFinished);
            Assert.Equal(3, player.ExecutedSteps);
        }

        [Fact]
        public void Player_FirstStepAtZeroDelay_RunsOnFirstTick()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(new GuideDemoScript("x", new[] { new GuideDemoStep(0, 0, 0, 0) }));

            Assert.Equal(1, player.Tick(1));
            Assert.Single(bus.Triggers);
        }

        [Fact]
        public void Player_LargeDelta_ExecutesAllSteps()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());

            Assert.Equal(3, player.Tick(10_000));
            Assert.True(player.IsFinished);
        }

        [Fact]
        public void Player_SendsCorrectCommands()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());
            player.Tick(10_000);

            Assert.Equal(2, bus.Triggers.Count);
            Assert.Single(bus.Advances);
            Assert.Equal(1, bus.Advances[0]);
        }

        [Fact]
        public void Player_SkipAndSkipAll_AreSent()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(new GuideDemoScript("x", new[]
            {
                new GuideDemoStep(2, 2, 0, 0),
                new GuideDemoStep(3, 0, 0, 0),
            }));

            player.Tick(1);
            Assert.Single(bus.Skips);
            Assert.Equal(1, bus.SkipAllCount);
        }

        [Fact]
        public void Player_UnknownKind_RecordsErrorAndContinues()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(new GuideDemoScript("x", new[]
            {
                new GuideDemoStep(99, 0, 0, 0),
                new GuideDemoStep(0, 0, 0, 0),
            }));

            player.Tick(1);

            Assert.Contains("未知动作类型", player.LastError);
            Assert.Equal(2, player.ExecutedSteps);
            Assert.Single(bus.Triggers);
        }

        [Fact]
        public void Player_Stop_PreventsFurtherExecution()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());
            player.Stop();

            Assert.Equal(0, player.Tick(10_000));
        }

        [Fact]
        public void Player_NegativeDelta_Ignored()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());

            Assert.Equal(0, player.Tick(-100));
            Assert.Equal(0, player.ExecutedSteps);
        }

        [Fact]
        public void Player_NotStarted_TickReturnsZero()
        {
            var player = new GuideDemoPlayer(new RecordingBus());

            Assert.Equal(0, player.Tick(1000));
            Assert.False(player.IsFinished);
        }

        [Fact]
        public void Player_Restart_ResetsProgress()
        {
            var bus = new RecordingBus();
            var player = new GuideDemoPlayer(bus);
            player.Start(SampleScript());
            player.Tick(10_000);

            player.Start(SampleScript());

            Assert.False(player.IsFinished);
            Assert.Equal(0, player.ExecutedSteps);
        }

        [Fact]
        public void Player_NullBus_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new GuideDemoPlayer(null!));
        }

        [Fact]
        public void Step_NegativeDelay_ClampedToZero()
        {
            Assert.Equal(0, new GuideDemoStep(0, 0, 0, -50).DelayMs);
        }

        /// <summary>记录型总线：只接住演示播放器会发的四类命令。</summary>
        private sealed class RecordingBus : ICqeBus
        {
            public List<GuideTriggerCommand> Triggers { get; } = new List<GuideTriggerCommand>();

            public List<int> Advances { get; } = new List<int>();

            public List<int> Skips { get; } = new List<int>();

            public int SkipAllCount { get; private set; }

            public void Send<TCommand>(in TCommand command)
                where TCommand : struct, ICommand
            {
                if (command is GuideTriggerCommand trigger)
                {
                    Triggers.Add(trigger);
                }
                else if (command is GuideAdvanceCommand advance)
                {
                    Advances.Add(advance.StepId);
                }
                else if (command is GuideSkipCommand skip)
                {
                    Skips.Add(skip.StepId);
                }
                else if (command is GuideSkipAllCommand)
                {
                    SkipAllCount++;
                }
            }

            public TResult Query<TQuery, TResult>(in TQuery query)
                where TQuery : struct, IQuery<TResult>
            {
                throw new System.NotImplementedException();
            }

            public void Publish<TEvent>(in TEvent evt)
                where TEvent : struct, IEvent
            {
            }

            public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
                where TCommand : struct, ICommand
            {
            }

            public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
                where TQuery : struct, IQuery<TResult>
            {
            }

            public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
                where TEvent : struct, IEvent
            {
            }
        }
    }
}

using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using Xunit;
using Xunit.Abstractions;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleServices.RunSelfCheck"/> 的验收：它是对外宣称"外围服务真的接上了"的唯一凭据，
    /// 因此断言的不是"跑了不报错"，而是**三行内容本身**（音量夹取、BGM/音效播放、校验四态）。
    /// <para>是否接进 <c>ConsoleAppBootstrap</c> 由装配层决定；此处保证它随时可被独立调用。</para>
    /// </summary>
    public sealed class ConsoleServicesSelfCheckTests
    {
        private readonly ITestOutputHelper _output;

        public ConsoleServicesSelfCheckTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RunSelfCheck_PrintsVolumePlaybackAndValidationLines()
        {
            var writer = new CapturingWriter();

            ConsoleServices.RunSelfCheck(writer);

            foreach (string line in writer.Lines)
            {
                _output.WriteLine(line);
            }

            Assert.Equal(3, writer.Lines.Count);

            // 第 1 行：Master/Bgm 按设置值，Sfx 传 2f 必须被夹成 1.00，Voice 未设过保持默认 1.00。
            Assert.StartsWith("[Audio] 音量", writer.Lines[0]);
            Assert.Contains("Master=0.80", writer.Lines[0]);
            Assert.Contains("Bgm=0.60", writer.Lines[0]);
            Assert.Contains("Sfx=1.00", writer.Lines[0]);
            Assert.Contains("Voice=1.00", writer.Lines[0]);

            // 第 2 行：BGM 单例 + 两条 SFX 事件（顺序即调用顺序）。
            Assert.StartsWith("[Audio] 播放", writer.Lines[1]);
            Assert.Contains("BGM=#7", writer.Lines[1]);
            Assert.Contains("活跃音效=2", writer.Lines[1]);
            Assert.Contains("bgm#7, sfx#1001, sfx#1002", writer.Lines[1]);

            // 第 3 行：正常接受、重复指纹拒绝、离谱分数拒绝；被拒的不登记，所以台账只有 1 条。
            Assert.StartsWith("[Validate]", writer.Lines[2]);
            Assert.Contains("正常=Accepted", writer.Lines[2]);
            Assert.Contains("分=1200", writer.Lines[2]);
            Assert.Contains("重复=Rejected(duplicate)", writer.Lines[2]);
            Assert.Contains("离谱分数=Rejected(score exceeds plausible maximum)", writer.Lines[2]);
            Assert.Contains("已登记=1/256", writer.Lines[2]);
        }

        [Fact]
        public void RunSelfCheck_NullWriter_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ConsoleServices.RunSelfCheck(null!));
        }

        /// <summary>内存输出端口：不落盘、不依赖控制台编码。</summary>
        private sealed class CapturingWriter : IIOWriter
        {
            public List<string> Lines { get; } = new List<string>();

            public void WriteLine(string line)
            {
                Lines.Add(line);
            }
        }
    }
}

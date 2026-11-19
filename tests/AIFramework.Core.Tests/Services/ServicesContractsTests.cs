using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 外围服务契约（音频 / 服务端校验）的形状验收。
    /// <para>
    /// 这些断言看起来"只是属性赋值"，但它们保护的是**协议**：通道数值必须稠密（适配层用数组下标存音量）、
    /// 事件必须实现 <see cref="IEvent"/>（否则装配层广播不出去）、摘要是只读结构体且字段一一对应。
    /// 一旦有人给契约加字段或调整通道数值，这里会先炸，而不是等 Unity 侧播放串台才发现。
    /// </para>
    /// </summary>
    public sealed class ServicesContractsTests
    {
        [Fact]
        public void AudioChannel_IsContiguousFromZero_SoAdaptersCanIndexArrays()
        {
            Assert.Equal(0, (int)AudioChannel.Master);
            Assert.Equal(1, (int)AudioChannel.Bgm);
            Assert.Equal(2, (int)AudioChannel.Sfx);
            Assert.Equal(3, (int)AudioChannel.Voice);

            // 底层类型固定为 byte（网络/存档序列化依赖它的大小）。
            Assert.Equal(typeof(byte), System.Enum.GetUnderlyingType(typeof(AudioChannel)));
        }

        [Fact]
        public void AudioPlayRequestedEvent_CarriesClipAndChannel_AndIsBroadcastable()
        {
            var requested = new AudioPlayRequestedEvent(1001, AudioChannel.Sfx);

            Assert.Equal(1001, requested.ClipId);
            Assert.Equal(AudioChannel.Sfx, requested.Channel);
            Assert.IsAssignableFrom<IEvent>(requested);
        }

        [Fact]
        public void MatchSummary_RoundTripsAllSixFields()
        {
            var summary = new MatchSummary(
                kernelType: 2,
                levelId: 7,
                randomSeed: 20261120,
                frameCount: 7200,
                stateFingerprint: 0x0BADF00DL,
                score: 72000);

            Assert.Equal(2, summary.KernelType);
            Assert.Equal(7, summary.LevelId);
            Assert.Equal(20261120, summary.RandomSeed);
            Assert.Equal(7200, summary.FrameCount);
            Assert.Equal(0x0BADF00DL, summary.StateFingerprint);
            Assert.Equal(72000, summary.Score);
        }

        [Fact]
        public void MatchSummary_Default_IsAllZero_SoUninitializedStructsFailValidationLoudly()
        {
            MatchSummary summary = default;

            Assert.Equal(0, summary.KernelType);
            Assert.Equal(0, summary.FrameCount);
            Assert.Equal(0L, summary.StateFingerprint);
            Assert.Equal(0, summary.Score);
        }

        [Fact]
        public void NetworkValidationResult_HasStableUnderlyingValues()
        {
            // 数值会被写进日志与本地缓存，顺序不能悄悄改。
            Assert.Equal(0, (byte)NetworkValidationResult.Accepted);
            Assert.Equal(1, (byte)NetworkValidationResult.Rejected);
            Assert.Equal(2, (byte)NetworkValidationResult.Unavailable);
            Assert.Equal(3, (byte)NetworkValidationResult.Timeout);
        }
    }
}

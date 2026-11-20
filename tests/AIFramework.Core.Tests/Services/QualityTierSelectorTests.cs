using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="QualityTierSelector"/> 的行为契约验收：滑动窗口、升降档、迟滞带、冷却、非法样本、
    /// 档位→配置映射，以及"只有真的变了才调端口"。
    /// <para>
    /// <b>用例口径</b>：窗口与冷却都显式给成小数字（2~5 帧），这样每条用例只推演十几次采样就能穷举边界，
    /// 又不至于因为窗口太小失去代表性——所有分支（窗口未满、整窗慢、迟滞带、冷却内/外、降档优先、重置）
    /// 都与默认 120/300 帧走同一段代码。
    /// </para>
    /// <para>
    /// <b>关键边界</b>：阈值比较是<b>严格</b>的（<c>&lt;</c> 与 <c>&gt;</c>），所以"恰好 40fps"不降档、
    /// "恰好 50fps"不升档；"单次卡顿"不降档（必须整窗每一帧都慢）；<c>0</c>/<c>NaN</c>/<c>±Infinity</c>
    /// 既不进窗口也不推进冷却。
    /// </para>
    /// <para>
    /// <b>关于冷却覆盖降档</b>：任务描述里降档条款没有写冷却，但总则明确"任何变化后 <c>cooldownFrames</c>
    /// 帧内不再变化"。本套用例按总则验收（见 <see cref="Cooldown_BlocksConsecutiveDowngrades"/>）：
    /// 若允许降档绕开冷却，一个持续慢的窗口会在降档后的下一帧立刻再降一档，两帧内从 High 掉到 Low。
    /// </para>
    /// </summary>
    public sealed class QualityTierSelectorTests
    {
        /// <summary>40ms/帧 = 25fps，低于默认 40fps 的降档线。</summary>
        private const double SlowFrameMilliseconds = 40d;

        /// <summary>10ms/帧 = 100fps，高于默认 55fps 的升档线。</summary>
        private const double FastFrameMilliseconds = 10d;

        /// <summary>20ms/帧 = 50fps，落在 40~55 的迟滞带里：两个方向都不该动。</summary>
        private const double MidZoneFrameMilliseconds = 20d;

        /// <summary>25ms/帧 = 恰好 40fps：严格比较下不算"慢"。</summary>
        private const double ExactlyBadFpsFrameMilliseconds = 25d;

        /// <summary>一个慢帧（用于验证"单次卡顿不降档"）。</summary>
        private const double HitchFrameMilliseconds = 100d;

        [Fact]
        public void Constructor_NullPort_Throws()
        {
            // 端口为 null 属于装配期编程错误：没有落地目标，任何档位决策都没有意义。
            Assert.Throws<ArgumentNullException>(() => new QualityTierSelector(null!));
        }

        [Fact]
        public void Constructor_ZeroWindow_ClampsToOneFrame()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 0, cooldownFrames: 0);

            // 窗口被夹到 1：喂一帧慢帧就填满窗口，立刻可决策。
            Assert.True(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
        }

        [Fact]
        public void Constructor_NegativeWindow_ClampsToOneFrame()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: -7, cooldownFrames: 0);

            // 负窗口若原样参与分配会直接抛（数组长度非法）；夹到 1 后行为退化为"每次采样都可决策"。
            Assert.True(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
        }

        [Fact]
        public void Constructor_HugeWindow_IsClampedAndStaysUndecided()
        {
            // 误填 int.MaxValue 时按原值分配两个数组会直接 OOM；夹到上限后行为等价于"永不决策"。
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: int.MaxValue, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 500);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, selector.ChangeCount);
            Assert.Equal(0, port.ApplyCount);
            Assert.Equal(SlowFrameMilliseconds, selector.AverageFrameMs, 9);
        }

        [Fact]
        public void InitialState_IsMediumWithEmptyWindow()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0d, selector.AverageFrameMs);
            Assert.Equal(0, selector.ChangeCount);

            // 构造期不产生任何副作用：端口一次都没被调用（否则"变化才 Apply"的语义就废了）。
            Assert.Equal(0, port.ApplyCount);
        }

        [Fact]
        public void Sample_WindowNotFull_DoesNotDecide()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            // 3 帧慢帧还差一帧：开局加载的坏帧不能当"设备不行"的证据。
            Feed(selector, SlowFrameMilliseconds, 3);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, port.ApplyCount);
            Assert.Equal(SlowFrameMilliseconds, selector.AverageFrameMs, 9);

            // 第 4 帧填满窗口 → 立刻降档。
            Assert.True(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
        }

        [Fact]
        public void Sample_SustainedSlowFrames_DowngradesOneTier()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 4);

            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
            Assert.Equal(1, port.ApplyCount);
            Assert.Equal(QualityTier.Low, port.CurrentTier);
        }

        [Fact]
        public void Sample_SustainedSlowFrames_AtLow_StopsAtLow()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 4);
            Feed(selector, SlowFrameMilliseconds, 40);

            // Low 是下界：再慢也不存在"更低档"，端口不应该被反复调用（幂等压力在这里体现）。
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
            Assert.Equal(1, port.ApplyCount);
        }

        [Fact]
        public void Sample_RepeatedDowngrades_ReachLowAndStop()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            // 先靠两帧快帧升到 High。
            Feed(selector, FastFrameMilliseconds, 2);
            Assert.Equal(QualityTier.High, selector.CurrentTier);

            // 再整窗慢帧：High → Medium → Low，然后停住。
            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);

            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);

            Feed(selector, SlowFrameMilliseconds, 10);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);

            Assert.Equal(3, selector.ChangeCount);
        }

        [Fact]
        public void Sample_SustainedFastFrames_UpshiftsOneTier()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            Feed(selector, FastFrameMilliseconds, 4);

            Assert.Equal(QualityTier.High, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
            Assert.Equal(QualityTier.High, port.CurrentTier);
        }

        [Fact]
        public void Sample_SustainedFastFrames_AtHigh_StopsAtHigh()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            Feed(selector, FastFrameMilliseconds, 4);
            Feed(selector, FastFrameMilliseconds, 40);

            Assert.Equal(QualityTier.High, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
            Assert.Equal(1, port.ApplyCount);
        }

        [Fact]
        public void Sample_MidZoneFrames_DoNotChangeTier()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            // 50fps：高于降档线、低于升档线 → 迟滞带内保持不动（这是防"画质闪烁"的核心）。
            Feed(selector, MidZoneFrameMilliseconds, 40);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, selector.ChangeCount);
            Assert.Equal(0, port.ApplyCount);
        }

        [Fact]
        public void Sample_SingleHitchInWindow_DoesNotDowngrade()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 4, cooldownFrames: 0);

            // 3 帧 100fps + 1 帧 10fps：均值 32.5ms ≈ 30.8fps，已经低于 40fps，
            // 但"整窗每一帧都慢"不成立 → 不降档。一次 GC/加载卡顿不该让玩家永久失去画质。
            Feed(selector, FastFrameMilliseconds, 3);
            selector.Sample(HitchFrameMilliseconds);

            Assert.True(selector.AverageFrameMs > 1000d / 40d);
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, port.ApplyCount);
        }

        [Fact]
        public void Sample_InvalidFrameTimes_AreIgnoredAndNotCounted()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            // 0 / 负数 / NaN / ±Inf 都不是"测到的一帧"：忽略且不进窗口。
            selector.Sample(0d);
            selector.Sample(-16d);
            selector.Sample(double.NaN);
            selector.Sample(double.PositiveInfinity);
            selector.Sample(double.NegativeInfinity);

            Assert.Equal(0d, selector.AverageFrameMs);
            Assert.Equal(0, selector.ChangeCount);

            // 若上面任何一个被计数，窗口早就"满了"，这一帧就会触发降档。
            Assert.False(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(SlowFrameMilliseconds, selector.AverageFrameMs, 9);

            // 第二个有效帧才填满窗口 → 此时才降档。
            Assert.True(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
        }

        [Fact]
        public void Sample_InvalidFrames_DoNotAdvanceCooldown()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 1, cooldownFrames: 3);

            // 先升到 High（一次有效变化，冷却从 0 重新起算）。
            Assert.True(selector.Sample(FastFrameMilliseconds));
            Assert.Equal(QualityTier.High, selector.CurrentTier);

            // 冻结/失焦期间的"假帧"不能推进冷却，否则恢复后第一帧就能把档位再改一次。
            selector.Sample(0d);
            selector.Sample(double.NaN);
            selector.Sample(double.PositiveInfinity);

            // 一帧真慢帧：冷却计数只到 1（< 3），必须仍然停在 High。
            Assert.False(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.High, selector.CurrentTier);
        }

        [Fact]
        public void Sample_ExactBadFps_DoesNotDowngrade()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 1, cooldownFrames: 0);

            // 25ms = 恰好 40fps：规则是 "avg fps < badFps"，等号不降档（否则阈值线会随浮点抖动随机触发）。
            selector.Sample(ExactlyBadFpsFrameMilliseconds);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, port.ApplyCount);
        }

        [Fact]
        public void Sample_ExactGoodEnoughFps_DoesNotUpgrade()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 1, goodEnoughFps: 50, badFps: 40, cooldownFrames: 0);

            // 20ms = 恰好 50fps：规则是 "avg fps > goodEnoughFps"，等号不升档。
            selector.Sample(MidZoneFrameMilliseconds);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(0, port.ApplyCount);
        }

        [Fact]
        public void Sample_NonPositiveThresholds_DisableThatDirection()
        {
            // badFps <= 0：没有任何帧会被判"慢" → 永不降档（good 设成 1000 让升档也不触发）。
            var slowPort = new FakeQualityPort();
            var neverDown = new QualityTierSelector(slowPort, windowFrames: 2, goodEnoughFps: 1000, badFps: 0, cooldownFrames: 0);
            Feed(neverDown, SlowFrameMilliseconds, 20);
            Assert.Equal(QualityTier.Medium, neverDown.CurrentTier);

            // goodEnoughFps <= 0：任何可测帧率都算"够好" → 窗口一满就升档（用 50fps 的帧避开降档分支）。
            var midPort = new FakeQualityPort();
            var alwaysUp = new QualityTierSelector(midPort, windowFrames: 2, goodEnoughFps: 0, badFps: 40, cooldownFrames: 0);
            Feed(alwaysUp, MidZoneFrameMilliseconds, 2);
            Assert.Equal(QualityTier.High, alwaysUp.CurrentTier);
        }

        [Fact]
        public void Sample_OverlappingThresholds_DownshiftWins()
        {
            var port = new FakeQualityPort();
            // 阈值配反（升档线 30 < 降档线 40）：同一帧同时满足两个方向时，保帧率优先。
            var selector = new QualityTierSelector(port, windowFrames: 1, goodEnoughFps: 30, badFps: 40, cooldownFrames: 0);

            // 35ms ≈ 28.6fps：< 40（降档）且 > 30（升档）。
            selector.Sample(35d);

            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(QualityTier.Low, port.LastApplied.Tier);
        }

        [Fact]
        public void Sample_ReturnsTrueOnlyWhenTierChanges()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Assert.False(selector.Sample(SlowFrameMilliseconds));  // 窗口未满
            Assert.True(selector.Sample(SlowFrameMilliseconds));   // Medium → Low
            Assert.False(selector.Sample(SlowFrameMilliseconds));  // Low 止步
            Assert.False(selector.Sample(FastFrameMilliseconds));  // 窗口里还有慢帧，均值 40fps
            Assert.True(selector.Sample(FastFrameMilliseconds));   // Low → Medium
            Assert.Equal(2, selector.ChangeCount);
        }

        [Fact]
        public void AverageFrameMs_AveragesCurrentWindow()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 3, cooldownFrames: 0);

            Feed(selector, 10d, 3);
            Assert.Equal(10d, selector.AverageFrameMs, 9);

            Feed(selector, 20d, 3);
            Assert.Equal(20d, selector.AverageFrameMs, 9);
        }

        [Fact]
        public void AverageFrameMs_DropsEvictedFrames()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, 10d, 2);
            Assert.Equal(10d, selector.AverageFrameMs, 9);

            // 窗口只保留最近 2 帧：旧的 10ms 必须被淘汰，否则均值会被历史样本拖住、永远升不上去。
            selector.Sample(40d);
            Assert.Equal(25d, selector.AverageFrameMs, 9);

            selector.Sample(40d);
            Assert.Equal(40d, selector.AverageFrameMs, 9);
        }

        [Fact]
        public void Cooldown_BlocksUpshiftUntilCooldownElapses()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 5);

            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);

            // 变化后 4 个快帧：均值已经 100fps，但冷却没满，不许升。
            Feed(selector, FastFrameMilliseconds, 4);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);

            // 第 5 个快帧：冷却恰好满（>= cooldownFrames）→ 放行，升一档。
            Assert.True(selector.Sample(FastFrameMilliseconds));
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(2, selector.ChangeCount);
        }

        [Fact]
        public void Cooldown_BlocksConsecutiveDowngrades()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 5);

            // 升到 High（变化 1），随后持续慢帧：降档同样受冷却约束。
            Feed(selector, FastFrameMilliseconds, 2);
            Assert.Equal(QualityTier.High, selector.CurrentTier);

            Feed(selector, SlowFrameMilliseconds, 5);
            Assert.Equal(QualityTier.Medium, selector.CurrentTier); // 窗口 2 + 冷却 5 后才降第一档

            // 冷却内的下一帧虽然整窗还是慢帧，也不许继续掉档（否则两帧内就从 High 掉到 Low）。
            Assert.False(selector.Sample(SlowFrameMilliseconds));
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);

            Feed(selector, SlowFrameMilliseconds, 4);
            Assert.Equal(QualityTier.Low, selector.CurrentTier); // 冷却再次满 → 掉第二档
            Assert.Equal(3, selector.ChangeCount);
        }

        [Fact]
        public void Reset_ClearsWindowAndChangeCount_KeepsTier()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(1, port.ApplyCount);

            selector.Reset();

            // 档位是玩家已生效的现状，Reset 只重置"测量"，不回滚画质、也不额外调端口。
            Assert.Equal(QualityTier.Low, selector.CurrentTier);
            Assert.Equal(QualityTier.Low, selector.CurrentProfile.Tier);
            Assert.Equal(0d, selector.AverageFrameMs);
            Assert.Equal(0, selector.ChangeCount);
            Assert.Equal(1, port.ApplyCount);
        }

        [Fact]
        public void Reset_RequiresFullWindowAgain()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 2);
            selector.Reset();

            // 重置后窗口为空：一帧快帧不够决策。
            Assert.False(selector.Sample(FastFrameMilliseconds));
            Assert.Equal(QualityTier.Low, selector.CurrentTier);

            // 第二帧填满窗口 → 升档（冷却也被重置为"已满"，不用再等 0 帧之外的东西）。
            Assert.True(selector.Sample(FastFrameMilliseconds));
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
            Assert.Equal(1, selector.ChangeCount);
        }

        [Fact]
        public void Reset_ClearsCooldown()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 20);

            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(QualityTier.Low, selector.CurrentTier);

            // 不重置的话，接下来 20 帧内都动不了；Reset 把冷却归零，两帧快帧就能升档。
            selector.Reset();
            Feed(selector, FastFrameMilliseconds, 2);

            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
        }

        [Fact]
        public void ChangeCount_AccumulatesEveryChange()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 1, cooldownFrames: 0);

            selector.Sample(SlowFrameMilliseconds); // Medium → Low
            selector.Sample(FastFrameMilliseconds); // Low → Medium
            selector.Sample(FastFrameMilliseconds); // Medium → High
            selector.Sample(SlowFrameMilliseconds); // High → Medium

            Assert.Equal(4, selector.ChangeCount);
            Assert.Equal(4, port.ApplyCount);
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
        }

        [Fact]
        public void Apply_IsCalledOnlyWhenTierChanges()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 2);
            Assert.Equal(1, port.ApplyCount);

            // 已经是 Low：后续 20 帧慢帧一次都不该再调端口（幂等 + 不无脑重写引擎设置）。
            Feed(selector, SlowFrameMilliseconds, 20);
            Assert.Equal(1, port.ApplyCount);

            Feed(selector, FastFrameMilliseconds, 2);
            Assert.Equal(2, port.ApplyCount);
            Assert.Equal(QualityTier.Medium, selector.CurrentTier);
        }

        [Fact]
        public void Apply_ReceivesProfileOfNewTier()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 2);

            QualityProfile applied = port.LastApplied;
            Assert.Equal(QualityTier.Low, applied.Tier);
            Assert.Equal(30, applied.TargetFrameRate);
            Assert.Equal(200, applied.ParticleBudget);
            Assert.Equal(0, applied.ShadowQualityLevel);

            Feed(selector, FastFrameMilliseconds, 2);

            applied = port.LastApplied;
            Assert.Equal(QualityTier.Medium, applied.Tier);
            Assert.Equal(60, applied.TargetFrameRate);
            Assert.Equal(500, applied.ParticleBudget);
            Assert.Equal(1, applied.ShadowQualityLevel);
        }

        [Fact]
        public void Port_CurrentTierTracksSelector()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 1, cooldownFrames: 0);

            selector.Sample(SlowFrameMilliseconds);
            Assert.Equal(selector.CurrentTier, port.CurrentTier);

            selector.Sample(FastFrameMilliseconds);
            Assert.Equal(selector.CurrentTier, port.CurrentTier);

            selector.Sample(FastFrameMilliseconds);
            Assert.Equal(selector.CurrentTier, port.CurrentTier);
        }

        [Fact]
        public void CurrentProfile_InitialIsMediumProfile()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2);

            QualityProfile profile = selector.CurrentProfile;

            // 三档映射是硬编码的产品默认值：这里逐字段锁死，防止有人"顺手调一下"改了玩家可感知的档位。
            Assert.Equal(QualityTier.Medium, profile.Tier);
            Assert.Equal(60, profile.TargetFrameRate);
            Assert.Equal(500, profile.ParticleBudget);
            Assert.Equal(1, profile.ShadowQualityLevel);
        }

        [Fact]
        public void CurrentProfile_AfterDowngrade_IsLowProfile()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, SlowFrameMilliseconds, 2);

            QualityProfile profile = selector.CurrentProfile;
            Assert.Equal(QualityTier.Low, profile.Tier);
            Assert.Equal(30, profile.TargetFrameRate);
            Assert.Equal(200, profile.ParticleBudget);
            Assert.Equal(0, profile.ShadowQualityLevel);
        }

        [Fact]
        public void CurrentProfile_AfterUpgrade_IsHighProfile()
        {
            var port = new FakeQualityPort();
            var selector = new QualityTierSelector(port, windowFrames: 2, cooldownFrames: 0);

            Feed(selector, FastFrameMilliseconds, 2);

            QualityProfile profile = selector.CurrentProfile;
            Assert.Equal(QualityTier.High, profile.Tier);
            Assert.Equal(60, profile.TargetFrameRate);
            Assert.Equal(1000, profile.ParticleBudget);
            Assert.Equal(2, profile.ShadowQualityLevel);
        }

        [Fact]
        public void QualityProfile_NegativeFieldsClampToZero()
        {
            // 负值只可能来自配置错误：0 在三个字段上分别是"不限制 / 不生成 / 关阴影"。
            var profile = new QualityProfile(QualityTier.High, targetFrameRate: -1, particleBudget: -2, shadowQualityLevel: -3);

            Assert.Equal(0, profile.TargetFrameRate);
            Assert.Equal(0, profile.ParticleBudget);
            Assert.Equal(0, profile.ShadowQualityLevel);

            // 合法值原样保留（含大于 2 的阴影等级：上界是平台知识，契约层不夹）。
            var custom = new QualityProfile(QualityTier.Medium, 120, 2000, 5);
            Assert.Equal(120, custom.TargetFrameRate);
            Assert.Equal(2000, custom.ParticleBudget);
            Assert.Equal(5, custom.ShadowQualityLevel);
        }

        [Fact]
        public void QualityTier_HasStableByteValues()
        {
            // 数值进日志/埋点/本地存档：顺序只允许追加，不允许调整。
            Assert.Equal(0, (byte)QualityTier.Low);
            Assert.Equal(1, (byte)QualityTier.Medium);
            Assert.Equal(2, (byte)QualityTier.High);
            Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(QualityTier)));
        }

        /// <summary>喂 <paramref name="frames"/> 帧同样的耗时（只为让用例短一点，不做任何断言）。</summary>
        private static void Feed(QualityTierSelector selector, double frameMilliseconds, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                selector.Sample(frameMilliseconds);
            }
        }

        /// <summary>
        /// 假端口：记录每次 <c>Apply</c> 的配置，并像真实现一样把 <see cref="CurrentTier"/> 同步过去。
        /// <para>它不模拟平台限制（不夹阴影等级、不映射引擎 API）——那些属于适配层，不在 Core 的验收范围。</para>
        /// </summary>
        private sealed class FakeQualityPort : IQualitySettingsPort
        {
            private readonly List<QualityProfile> _applied = new List<QualityProfile>();

            public FakeQualityPort(QualityTier initialTier = QualityTier.Medium)
            {
                CurrentTier = initialTier;
            }

            public QualityTier CurrentTier { get; private set; }

            public int ApplyCount
            {
                get { return _applied.Count; }
            }

            /// <summary>最近一次被应用的配置；还没有应用过时为 <c>default</c>（用例不会在这种状态下读它）。</summary>
            public QualityProfile LastApplied
            {
                get { return _applied.Count == 0 ? default : _applied[_applied.Count - 1]; }
            }

            public void Apply(in QualityProfile profile)
            {
                CurrentTier = profile.Tier;
                _applied.Add(profile);
            }
        }
    }
}

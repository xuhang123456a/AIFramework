#nullable enable
using System;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="BattleAudioCueSelector"/> 的行为契约验收：四条事实规则（发射/受击/格挡/死亡）、
    /// 同帧合并、每帧优先级上限、同类节流、缓冲截断、负输入降级、<c>Reset</c> 的计数口径。
    /// <para>
    /// <b>为什么这些用例值得单独写</b>：音效桥的输入是"内核状态帧差"而不是事件流，规则一旦写错，
    /// 只会在真机上表现为"打斗没声音 / 声音糊成一片 / 死了人没响"，且无法用回放指纹抓到；
    /// 这里用纯内存用例把每条规则与边界钉死，适配层就只剩"填数字"这一件必须真机验证的事。
    /// </para>
    /// <para>
    /// <b>默认参数</b>：<c>minSecondsBetweenSameCue = 0.05</c>、<c>maxCuesPerFrame = 2</c>。
    /// 需要"不节流"的用例显式传 <c>0f</c>，避免用例之间互相干扰。
    /// </para>
    /// </summary>
    public sealed class BattleAudioCueSelectorTests
    {
        [Fact]
        public void Update_FirstFrameWithProjectiles_EmitsSingleShot()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            int[] cues = Run(selector, new BattleAudioFrame(4, 3, 0, 0, 0), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Shot, cues[0]);
            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(0, selector.SuppressedCount);
        }

        [Fact]
        public void Update_ManyProjectilesInSameFrame_MergeIntoSingleShot()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // 一次齐射 5 发：世界只给了"数量变多"这一个事实，听感上必须是"一声发射"而不是机枪。
            int[] cues = Run(selector, new BattleAudioFrame(4, 5, 0, 0, 0), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Shot, cues[0]);
            Assert.Equal(1, selector.EmittedCount);
        }

        [Fact]
        public void Update_ProjectilesUnchanged_EmitsNothing()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            Run(selector, new BattleAudioFrame(4, 2, 0, 0, 0), 0.016f);
            int[] cues = Run(selector, new BattleAudioFrame(4, 2, 0, 0, 0), 0.016f);

            Assert.Empty(cues);
            Assert.Equal(1, selector.EmittedCount);
        }

        [Fact]
        public void Update_ProjectilesDecreased_DoesNotEmitShot()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            Run(selector, new BattleAudioFrame(4, 5, 0, 0, 0), 0.016f);

            // 命中/出界回收让数量变少，这不是"发射"。
            int[] cues = Run(selector, new BattleAudioFrame(4, 1, 0, 0, 0), 0.016f);

            Assert.Empty(cues);
        }

        [Fact]
        public void Update_DamageTaken_EmitsHit()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 0, 7, 0), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Hit, cues[0]);
        }

        [Fact]
        public void Update_ShieldAbsorbedDamage_EmitsShieldInsteadOfHit()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // 当帧护盾吃到了攻击：只发 Shield，绝不再发 Hit（两声响会听成"格挡没挡住"）。
            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 2, 10, 0), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Shield, cues[0]);
        }

        [Fact]
        public void Update_ShieldActiveWithoutDamage_EmitsNothing()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // 护盾"在架"但这一帧没吃到任何攻击：格挡音不该空放。
            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 3, 0, 0), 0.016f);

            Assert.Empty(cues);
            Assert.Equal(0, selector.EmittedCount);
        }

        [Fact]
        public void Update_Deaths_EmitsSingleDeath()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // 同帧死 3 个也只发一条：帧差只能得到"死了 3 个"，分不出先后。
            int[] cues = Run(selector, new BattleAudioFrame(3, 0, 0, 0, 3), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Death, cues[0]);
        }

        [Fact]
        public void Update_ShieldAndDeathSameFrame_EmitsInPriorityOrder()
        {
            var selector = new BattleAudioCueSelector(0f, 2);

            int[] cues = Run(selector, new BattleAudioFrame(3, 0, 1, 4, 1), 0.016f);

            // 输出顺序 = 优先级顺序：Death → Shield → Hit → Shot。
            Assert.Equal(2, cues.Length);
            Assert.Equal(BattleAudioCues.Death, cues[0]);
            Assert.Equal(BattleAudioCues.Shield, cues[1]);
        }

        [Fact]
        public void Update_CapOne_DeathWinsOverHit()
        {
            var selector = new BattleAudioCueSelector(0f, 1);

            int[] cues = Run(selector, new BattleAudioFrame(3, 0, 0, 5, 1), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Death, cues[0]);
            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_CapTwo_DropsShotWhenDeathAndHitBothFire()
        {
            var selector = new BattleAudioCueSelector(0f, 2);
            Run(selector, new BattleAudioFrame(4, 0, 0, 0, 0), 0.016f);

            // 同一帧：有人死了 + 有人掉血 + 有新弹出膛，一帧只允许两条 → 最低优先级的 Shot 被丢弃。
            int[] cues = Run(selector, new BattleAudioFrame(3, 2, 0, 5, 1), 0.016f);

            Assert.Equal(2, cues.Length);
            Assert.Equal(BattleAudioCues.Death, cues[0]);
            Assert.Equal(BattleAudioCues.Hit, cues[1]);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_DefaultMaxCuesPerFrame_IsTwo()
        {
            var selector = new BattleAudioCueSelector();
            Run(selector, new BattleAudioFrame(4, 0, 0, 0, 0), 0.016f);

            int[] cues = Run(selector, new BattleAudioFrame(3, 2, 0, 5, 1), 0.016f);

            // 默认构造：一帧最多 2 条（死+受击），发射被丢弃。
            Assert.Equal(2, cues.Length);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_SameCueWithinWindow_IsSuppressed()
        {
            var selector = new BattleAudioCueSelector(0.1f, 4);

            int[] first = Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);
            int[] second = Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.05f);

            Assert.Single(first);
            Assert.Empty(second);
            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_SameCueAtWindowBoundary_EmitsAgain()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);

            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);

            // 恰好走完 0.05 秒：冷却归零即视作"可以再响"，边界不能多关一帧。
            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.05f);

            Assert.Single(cues);
            Assert.Equal(2, selector.EmittedCount);
        }

        [Fact]
        public void Update_ZeroDelta_DoesNotAdvanceThrottle()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);

            // delta = 0 的帧（同一逻辑帧被多个驱动源重复喂）不应把冷却往前推。
            for (int i = 0; i < 3; i++)
            {
                Assert.Empty(Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0f));
            }

            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(3, selector.SuppressedCount);
        }

        [Fact]
        public void Update_NegativeDelta_IsTreatedAsZero()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);

            // 时间倒流不能把冷却"越减越多"：负 delta 按 0 处理，本帧仍处于冷却中。
            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), -1f);

            Assert.Empty(cues);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_ThrottleIsPerCueKind()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);

            // Hit 还在冷却，但 Shot 是另一条 cue，不该被连坐。
            int[] cues = Run(selector, new BattleAudioFrame(4, 3, 0, 5, 0), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Shot, cues[0]);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_ThrottledHighPriorityCue_DoesNotConsumeFrameSlot()
        {
            var selector = new BattleAudioCueSelector(0.05f, 1);
            Run(selector, new BattleAudioFrame(3, 0, 0, 5, 1), 0.016f);

            // 上一帧 Death 已响过：本帧 Death 被节流，名额要让给 Hit，否则"该响的没响"。
            int[] cues = Run(selector, new BattleAudioFrame(3, 0, 0, 5, 1), 0.016f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Hit, cues[0]);
            Assert.Equal(2, selector.EmittedCount);

            // 抑制两条：上一帧被上限吃掉的 Hit，以及这一帧被节流的 Death。
            Assert.Equal(2, selector.SuppressedCount);
        }

        [Fact]
        public void Update_ZeroMaxCuesPerFrame_SuppressesEveryCandidate()
        {
            var selector = new BattleAudioCueSelector(0f, 0);
            Run(selector, new BattleAudioFrame(4, 0, 0, 0, 0), 0.016f);

            int[] cues = Run(selector, new BattleAudioFrame(3, 2, 0, 5, 1), 0.016f);

            Assert.Empty(cues);
            Assert.Equal(0, selector.EmittedCount);

            // 三类候选（Death / Hit / Shot）全被上限吃掉。
            Assert.Equal(3, selector.SuppressedCount);
        }

        [Fact]
        public void Update_BufferSmallerThanCap_TruncatesWithoutOverflow()
        {
            var selector = new BattleAudioCueSelector(0f, 4);
            Span<int> tiny = stackalloc int[1];

            int count = selector.Update(new BattleAudioFrame(3, 0, 0, 5, 1), 0.016f, tiny);

            Assert.Equal(1, count);
            Assert.Equal(BattleAudioCues.Death, tiny[0]);
            Assert.Equal(1, selector.EmittedCount);

            // 写不进去的那条按抑制处理，绝不越界写。
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void Update_EmptyBuffer_EmitsNothingWithoutThrowing()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            int count = selector.Update(new BattleAudioFrame(3, 0, 0, 5, 1), 0.016f, Span<int>.Empty);

            Assert.Equal(0, count);
            Assert.Equal(0, selector.EmittedCount);
            Assert.Equal(2, selector.SuppressedCount);
        }

        [Fact]
        public void Update_NegativeCounts_AreTreatedAsZero()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // 视图/端口异常读回负数时不能抛，也不能凭空造出 cue。
            int[] cues = Run(selector, new BattleAudioFrame(-1, -2, -3, -4, -5), 0.016f);

            Assert.Empty(cues);
            Assert.Equal(0, selector.EmittedCount);
            Assert.Equal(0, selector.SuppressedCount);
        }

        [Fact]
        public void Update_AliveUnitsAlone_NeverEmitsCue()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            // AliveUnits 只是快照里的现场温度：阵亡必须由适配层算好放进 Deaths，
            // 否则"人数变少"会被静默当成没有事发生（这条边界要写死在契约里）。
            int[] cues = Run(selector, new BattleAudioFrame(2, 0, 0, 0, 0), 0.016f);

            Assert.Empty(cues);
        }

        [Fact]
        public void Reset_ClearsFrameDiffAndThrottle_ButKeepsLifetimeCounters()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 0, 0), 0.016f);
            Run(selector, new BattleAudioFrame(4, 2, 0, 0, 0), 0.016f);
            Assert.Equal(1, selector.EmittedCount);

            selector.Reset();

            // Reset 之后：飞行物基线回到 0（同样 2 发也算"变多"），冷却清空，计数不回退。
            int[] cues = Run(selector, new BattleAudioFrame(4, 2, 0, 0, 0), 0f);

            Assert.Single(cues);
            Assert.Equal(BattleAudioCues.Shot, cues[0]);
            Assert.Equal(2, selector.EmittedCount);
        }

        [Fact]
        public void Reset_DoesNotClearSuppressedCount()
        {
            var selector = new BattleAudioCueSelector(0.05f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0f);

            selector.Reset();

            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(1, selector.SuppressedCount);
        }

        [Fact]
        public void EmittedCount_CountsCuesNotFrames()
        {
            var selector = new BattleAudioCueSelector(0f, 4);

            for (int i = 0; i < 5; i++)
            {
                Run(selector, new BattleAudioFrame(4, 1, 0, 0, 0), 0.016f);
            }

            // 只有第一帧是"发射"，其余四帧状态没变。
            Assert.Equal(1, selector.EmittedCount);
            Assert.Equal(0, selector.SuppressedCount);
        }

        [Fact]
        public void Update_LongIdleDelta_ReopensThrottleWindow()
        {
            var selector = new BattleAudioCueSelector(1f, 4);
            Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.016f);

            Assert.Empty(Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.5f));

            int[] cues = Run(selector, new BattleAudioFrame(4, 0, 0, 5, 0), 0.5f);

            Assert.Single(cues);
            Assert.Equal(2, selector.EmittedCount);
        }

        [Fact]
        public void BattleAudioCues_IdsAreDistinctAndStable()
        {
            // cue id 是与资源约定 <c>Resources/Audio/&lt;id&gt;</c> 的接口，改动等于换掉整套音效资源。
            Assert.Equal(1001, BattleAudioCues.Shot);
            Assert.Equal(1002, BattleAudioCues.Hit);
            Assert.Equal(1003, BattleAudioCues.Shield);
            Assert.Equal(1004, BattleAudioCues.Death);
        }

        /// <summary>喂一帧并把 cue 抄成数组（测试里允许分配，选择器内部不允许）。</summary>
        /// <param name="selector">被测选择器。</param>
        /// <param name="frame">本帧快照。</param>
        /// <param name="deltaSeconds">距上一帧的秒数。</param>
        /// <returns>本帧实际发出的 cue id 序列（按优先级顺序）。</returns>
        private static int[] Run(BattleAudioCueSelector selector, BattleAudioFrame frame, float deltaSeconds)
        {
            Span<int> buffer = stackalloc int[4];
            int count = selector.Update(in frame, deltaSeconds, buffer);

            int[] cues = new int[count];
            for (int i = 0; i < count; i++)
            {
                cues[i] = buffer[i];
            }

            return cues;
        }
    }
}

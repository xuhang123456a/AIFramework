using AIFramework.Contracts.Input;
using AIFramework.Core.Input;
using Xunit;

namespace AIFramework.Core.Tests.Input
{
    /// <summary>
    /// <see cref="ScriptedInputSource"/> 的行为契约验收：边沿只出一次、轴取后清零、held 跨帧持续、
    /// 定长溢出丢最老、非法动作值不被吞成别的动作、连续 Poll 幂等。
    /// <para>
    /// <b>为什么这些用例值得单独写</b>：它是无头测试/回放/PlayMode 用例"喂输入"的唯一入口。
    /// 一旦边沿重复出、轴不清零或非法枚举被移位截断成合法动作，内核测出来的行为就是错的——
    /// 而且现象是"跳跃偶尔连跳两次/下蹲变成跳"这种只能靠猜的 bug。
    /// </para>
    /// </summary>
    public sealed class ScriptedInputSourceTests
    {
        [Fact]
        public void EmptySource_Poll_ReturnsEmptyFrame()
        {
            var source = new ScriptedInputSource();

            InputFrame frame = source.Poll();

            Assert.Equal(0, frame.PressedMask);
            Assert.Equal(0f, frame.Horizontal);
            Assert.Equal(0f, frame.Vertical);
            Assert.False(frame.IsPressed(InputAction.Jump));
            Assert.Equal(0, source.PendingCount);
        }

        [Fact]
        public void Press_EmitsEdgeOnlyOnce()
        {
            var source = new ScriptedInputSource();
            source.Press(InputAction.Jump);

            InputFrame first = source.Poll();
            InputFrame second = source.Poll();

            Assert.True(first.IsPressed(InputAction.Jump));
            Assert.False(second.IsPressed(InputAction.Jump));
            Assert.Equal(0, second.PressedMask);
        }

        [Fact]
        public void Press_SameActionTwice_CollapsesIntoOneBit()
        {
            var source = new ScriptedInputSource();
            source.Press(InputAction.Jump);
            source.Press(InputAction.Jump);
            source.Press(InputAction.Slide);

            InputFrame frame = source.Poll();

            // 位掩码天然去重：按两次跳跃与按一次在"本帧按下"语义下等价。
            int expected = (1 << (int)InputAction.Jump) | (1 << (int)InputAction.Slide);
            Assert.Equal(expected, frame.PressedMask);
            Assert.True(frame.IsPressed(InputAction.Jump));
            Assert.True(frame.IsPressed(InputAction.Slide));
            Assert.False(frame.IsPressed(InputAction.Primary));
        }

        [Fact]
        public void Poll_ClearsAxisAfterReading()
        {
            var source = new ScriptedInputSource();
            source.SetAxis(0.75f, -0.5f);

            InputFrame first = source.Poll();
            InputFrame second = source.Poll();

            Assert.Equal(0.75f, first.Horizontal);
            Assert.Equal(-0.5f, first.Vertical);
            Assert.Equal(0f, second.Horizontal);
            Assert.Equal(0f, second.Vertical);
        }

        [Fact]
        public void Axis_OutOfRangeOrNotFinite_IsNormalizedByContract()
        {
            var source = new ScriptedInputSource();

            source.SetAxis(2.5f, float.NaN);
            InputFrame clamped = source.Poll();
            Assert.Equal(1f, clamped.Horizontal);
            Assert.Equal(0f, clamped.Vertical);

            source.SetAxis(float.NegativeInfinity, -3f);
            InputFrame sanitized = source.Poll();
            Assert.Equal(0f, sanitized.Horizontal);
            Assert.Equal(-1f, sanitized.Vertical);
        }

        [Fact]
        public void Held_SurvivesMultiplePolls()
        {
            var source = new ScriptedInputSource();
            source.SetHeld(InputAction.Slide, true);

            source.Poll();
            source.Poll();

            Assert.True(source.IsHeld(InputAction.Slide));

            source.SetHeld(InputAction.Slide, false);
            Assert.False(source.IsHeld(InputAction.Slide));
        }

        [Fact]
        public void Held_IsIndependentFromEdgeQueue()
        {
            var source = new ScriptedInputSource();
            source.Press(InputAction.Jump);
            source.SetHeld(InputAction.Jump, true);

            InputFrame first = source.Poll();
            Assert.True(first.IsPressed(InputAction.Jump));
            Assert.True(source.IsHeld(InputAction.Jump));

            // Poll 只清边沿，不清按住状态。
            InputFrame second = source.Poll();
            Assert.False(second.IsPressed(InputAction.Jump));
            Assert.True(source.IsHeld(InputAction.Jump));
        }

        [Fact]
        public void CapacityOverflow_DropsOldestAndKeepsNewest()
        {
            var source = new ScriptedInputSource(3);
            source.Press(InputAction.Jump);
            source.Press(InputAction.Slide);
            source.Press(InputAction.Primary);

            source.Press(InputAction.Secondary);

            Assert.Equal(3, source.Capacity);
            Assert.Equal(3, source.PendingCount);
            Assert.Equal(1, source.DroppedCount);

            InputFrame frame = source.Poll();

            // 最老的 Jump 被淘汰，最新的三个还在。
            Assert.False(frame.IsPressed(InputAction.Jump));
            Assert.True(frame.IsPressed(InputAction.Slide));
            Assert.True(frame.IsPressed(InputAction.Primary));
            Assert.True(frame.IsPressed(InputAction.Secondary));
        }

        [Fact]
        public void CapacityOverflow_CountsEveryDroppedEdgeAndDoesNotGrowPending()
        {
            var source = new ScriptedInputSource(2);

            for (int i = 0; i < 6; i++)
            {
                source.Press((i % 2 == 0) ? InputAction.Jump : InputAction.Slide);
            }

            Assert.Equal(2, source.Capacity);
            Assert.Equal(2, source.PendingCount);
            Assert.Equal(4, source.DroppedCount);
        }

        [Fact]
        public void InvalidAction_IsRejectedWithoutTouchingQueue()
        {
            var source = new ScriptedInputSource(2);
            source.Press(InputAction.Jump);

            // 底层是 byte，200 与 5（枚举外）都能强转进来；1 << 200 会被截断成 1 << 8，
            // 若不判上界就会变成"没人认识的位"甚至冒充合法动作。
            source.Press((InputAction)200);
            source.Press((InputAction)5);
            source.SetHeld((InputAction)200, true);

            Assert.Equal(3, source.RejectedCount);
            Assert.Equal(1, source.PendingCount);
            Assert.False(source.IsHeld((InputAction)200));

            InputFrame frame = source.Poll();
            Assert.Equal(1 << (int)InputAction.Jump, frame.PressedMask);
            Assert.False(frame.IsPressed((InputAction)200));
            Assert.False(frame.IsPressed((InputAction)5));
        }

        [Fact]
        public void LifetimeCounters_SurvivePoll()
        {
            var source = new ScriptedInputSource(1);
            source.Press(InputAction.Jump);
            source.Press(InputAction.Slide);
            source.Press((InputAction)200);

            InputFrame first = source.Poll();
            Assert.True(first.IsPressed(InputAction.Slide));

            // Poll 只清空队列，不清生命周期计数（排查信息不能被"清空一下"洗掉）。
            Assert.Equal(1, source.DroppedCount);
            Assert.Equal(1, source.RejectedCount);

            InputFrame second = source.Poll();
            Assert.Equal(0, second.PressedMask);
            Assert.Equal(1, source.DroppedCount);
            Assert.Equal(1, source.RejectedCount);
        }

        [Fact]
        public void Poll_WithoutNewInput_RepeatsEmptyFrame()
        {
            var source = new ScriptedInputSource();
            source.Press(InputAction.Pause);

            InputFrame first = source.Poll();
            InputFrame second = source.Poll();
            InputFrame third = source.Poll();

            Assert.True(first.IsPressed(InputAction.Pause));
            Assert.Equal(0, second.PressedMask);
            Assert.Equal(0, third.PressedMask);
            Assert.Equal(second.PressedMask, third.PressedMask);
            Assert.Equal(second.Horizontal, third.Horizontal);
            Assert.Equal(second.Vertical, third.Vertical);
        }

        [Fact]
        public void PendingCount_IsZeroRightAfterPoll()
        {
            var source = new ScriptedInputSource();
            source.Press(InputAction.Jump);

            Assert.Equal(1, source.PendingCount);

            source.Poll();

            Assert.Equal(0, source.PendingCount);
        }

        [Fact]
        public void Constructor_NonPositiveCapacity_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ScriptedInputSource(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ScriptedInputSource(-1));
        }

        [Fact]
        public void DefaultCapacity_MatchesDocumentedValue()
        {
            var source = new ScriptedInputSource();

            Assert.Equal(ScriptedInputSource.DefaultCapacity, source.Capacity);
            Assert.Equal(16, source.Capacity);
        }

        [Fact]
        public void Source_IsUsableThroughPortInterface()
        {
            IInputSource source = new ScriptedInputSource();

            // 端口只暴露 Poll/IsHeld；场景路由只认接口，不认具体类型。
            InputFrame frame = source.Poll();
            Assert.Equal(0, frame.PressedMask);
            Assert.False(source.IsHeld(InputAction.Pause));
        }
    }
}

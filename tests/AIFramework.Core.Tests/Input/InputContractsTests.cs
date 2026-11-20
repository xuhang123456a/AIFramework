using System;
using AIFramework.Contracts.Input;
using Xunit;

namespace AIFramework.Core.Tests.Input
{
    /// <summary>
    /// 输入契约的形状验收：动作枚举数值稠密且底层是 <c>byte</c>、位掩码布局与枚举一一对应、
    /// 非法动作值不会被移位截断成别的动作。
    /// <para>
    /// 这些断言保护的是**协议**：动作号会进日志/回放，掩码布局会被各输入源与场景路由直接按
    /// <c>1 &lt;&lt; (int)action</c> 拼装。顺序悄悄一变，历史回放就会按错误的位驱动玩法。
    /// </para>
    /// </summary>
    public sealed class InputContractsTests
    {
        private static readonly InputAction[] AllActions =
        {
            InputAction.Jump,
            InputAction.Slide,
            InputAction.Primary,
            InputAction.Secondary,
            InputAction.Pause
        };

        [Fact]
        public void InputAction_HasContiguousByteValues()
        {
            Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(InputAction)));

            Assert.Equal(0, (int)InputAction.Jump);
            Assert.Equal(1, (int)InputAction.Slide);
            Assert.Equal(2, (int)InputAction.Primary);
            Assert.Equal(3, (int)InputAction.Secondary);
            Assert.Equal(4, (int)InputAction.Pause);

            // 枚举项数与契约里的 ActionCount 必须一致，否则越界判定会漏掉最后一个动作。
            Assert.Equal(InputFrame.ActionCount, AllActions.Length);
        }

        [Fact]
        public void InputFrame_BitLayoutMatchesEnumOrder()
        {
            for (int i = 0; i < AllActions.Length; i++)
            {
                InputAction action = AllActions[i];
                var frame = new InputFrame(1 << (int)action, 0f, 0f);

                Assert.True(frame.IsPressed(action));

                // 只允许命中自己那一位。
                Assert.Equal(1 << i, frame.PressedMask);
                for (int j = 0; j < AllActions.Length; j++)
                {
                    if (j != i)
                    {
                        Assert.False(frame.IsPressed(AllActions[j]));
                    }
                }
            }
        }

        [Fact]
        public void InputFrame_InvalidAction_IsNotPressed()
        {
            var allPressed = new InputFrame(-1, 0f, 0f);

            foreach (InputAction action in AllActions)
            {
                Assert.True(allPressed.IsPressed(action));
            }

            // byte 枚举能强转进 5/200；1 << 5 会落到不存在的位，1 << 200 会被截断成 1 << 8。
            Assert.False(allPressed.IsPressed((InputAction)5));
            Assert.False(allPressed.IsPressed((InputAction)200));
            Assert.False(allPressed.IsPressed((InputAction)255));
        }

        [Fact]
        public void InputFrame_Default_IsEmptyAndZeroAxes()
        {
            InputFrame frame = default;

            Assert.Equal(0, frame.PressedMask);
            Assert.Equal(0f, frame.Horizontal);
            Assert.Equal(0f, frame.Vertical);
            Assert.False(frame.IsPressed(InputAction.Jump));
        }

        [Fact]
        public void InputFrame_NormalizesAxesAtConstruction()
        {
            var frame = new InputFrame(0, 3f, float.NaN);

            Assert.Equal(1f, frame.Horizontal);
            Assert.Equal(0f, frame.Vertical);

            var negative = new InputFrame(0, -1.5f, float.PositiveInfinity);
            Assert.Equal(-1f, negative.Horizontal);
            Assert.Equal(0f, negative.Vertical);
        }
    }
}

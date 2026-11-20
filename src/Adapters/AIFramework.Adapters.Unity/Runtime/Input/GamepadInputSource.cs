#nullable enable
using AIFramework.Contracts.Input;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 手柄输入源：把旧版 Input Manager 的手柄按键/摇杆翻译成统一动作。
    /// <para>
    /// <b>键位映射（锁死）</b>：<c>JoystickButton0</c>（多数手柄的 A / ✕）→ <see cref="InputAction.Jump"/>、
    /// <c>JoystickButton1</c>（B / ○）→ <see cref="InputAction.Slide"/>、
    /// <c>JoystickButton7</c>（Start）→ <see cref="InputAction.Pause"/>；
    /// <c>Horizontal</c> / <c>Vertical</c> 轴 → <see cref="InputFrame.Horizontal"/> / <see cref="InputFrame.Vertical"/>。
    /// </para>
    /// <para>
    /// <b>为什么按键用 <c>GetKey</c> 自己做边沿</b>：手柄按键在旧版 Input 里没有 <c>GetKeyDown</c> 按设备区分
    /// 的稳定写法（不同平台的手柄名字映射不一致），这里自己记上一帧状态来产生边沿——
    /// 代价是一个布尔字段，收益是"跨平台按键语义不再依赖 Input Manager 的轴配置"。
    /// </para>
    /// <para>
    /// <b>为什么单独成类而不是塞进 <see cref="KeyboardInputSource"/></b>：手柄与键鼠的"边沿来源"完全不同，
    /// 合成一个类会让"按住键的同时摇杆回中"这类状态互相污染；分开后由
    /// <see cref="CompositeInputSource"/> 做"边沿取或、轴取绝对值较大者"的合并，语义清楚且可单测。
    /// </para>
    /// <para>
    /// <b>真机备注</b>：本类只覆盖最常见的 3 个按键。不同手柄（Xbox / DualShock / Switch Pro）的按键编号有差异，
    /// 完整映射应做成资产或配置；当前阶段先保证"手柄能推进演示"，不为未验证的设备写死映射表。
    /// </para>
    /// </summary>
    public sealed class GamepadInputSource : IInputSource
    {
        /// <summary>旧版 Input Manager 的横向轴名。</summary>
        private const string HorizontalAxisName = "Horizontal";

        /// <summary>旧版 Input Manager 的纵向轴名。</summary>
        private const string VerticalAxisName = "Vertical";

        /// <summary>摇杆死区：小于该值视为"没推"（手柄零点漂移通常 &lt; 0.2）。</summary>
        private const float AxisDeadzone = 0.2f;

        private bool _jumpHeld;
        private bool _slideHeld;
        private bool _pauseHeld;

        /// <summary>拉取本帧手柄边沿与双轴（边沿由上一帧状态对比得出）。</summary>
        /// <returns>本帧快照。</returns>
        public InputFrame Poll()
        {
            bool jumpNow = Input.GetKey(KeyCode.JoystickButton0);
            bool slideNow = Input.GetKey(KeyCode.JoystickButton1);
            bool pauseNow = Input.GetKey(KeyCode.JoystickButton7);

            int mask = 0;
            if (jumpNow && !_jumpHeld)
            {
                mask |= BitOf(InputAction.Jump);
            }

            if (slideNow && !_slideHeld)
            {
                mask |= BitOf(InputAction.Slide);
            }

            if (pauseNow && !_pauseHeld)
            {
                mask |= BitOf(InputAction.Pause);
            }

            _jumpHeld = jumpNow;
            _slideHeld = slideNow;
            _pauseHeld = pauseNow;

            return new InputFrame(mask, ReadAxis(HorizontalAxisName), ReadAxis(VerticalAxisName));
        }

        /// <summary>某动作当前是否按住（与 <see cref="Poll"/> 同一批键）。</summary>
        /// <param name="action">待查询动作。</param>
        /// <returns>按住返回 <c>true</c>；未映射动作返回 <c>false</c>。</returns>
        public bool IsHeld(InputAction action)
        {
            switch (action)
            {
                case InputAction.Jump:
                    return Input.GetKey(KeyCode.JoystickButton0);

                case InputAction.Slide:
                    return Input.GetKey(KeyCode.JoystickButton1);

                case InputAction.Pause:
                    return Input.GetKey(KeyCode.JoystickButton7);

                default:
                    return false;
            }
        }

        private static float ReadAxis(string axisName)
        {
            float axis = Input.GetAxisRaw(axisName);
            return axis > -AxisDeadzone && axis < AxisDeadzone ? 0f : axis;
        }

        private static int BitOf(InputAction action)
        {
            return 1 << (int)action;
        }
    }
}

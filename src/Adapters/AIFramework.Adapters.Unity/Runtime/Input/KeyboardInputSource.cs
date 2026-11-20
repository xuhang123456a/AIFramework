#nullable enable
using AIFramework.Contracts.Input;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 键鼠输入源：把 <c>UnityEngine.Input</c> 的键盘/旧版输入轴翻译成统一动作。
    /// <para>
    /// <b>键位映射（锁死）</b>：Space → <see cref="InputAction.Jump"/>、S / ↓ → <see cref="InputAction.Slide"/>、
    /// Enter → <see cref="InputAction.Primary"/>、Esc → <see cref="InputAction.Secondary"/>、
    /// P → <see cref="InputAction.Pause"/>；A / D 或 ← / → → <see cref="InputFrame.Horizontal"/>。
    /// </para>
    /// <para>
    /// <b>为什么方向键既读按键又回落到 <c>GetAxisRaw("Horizontal")</c></b>：按键是确定行为（不依赖工程里的
    /// Input Manager 配置是否被删改），而旧版 Input Manager 默认把"手柄左摇杆 X / 方向键"都映射到
    /// <c>Horizontal</c> 轴——回落一次就顺带把手柄左摇杆接进同一份抽象，不必再写一个 GamepadInputSource。
    /// 两者同时有输入时以键盘为准（数字 ±1 比摇杆值更可预期）。
    /// </para>
    /// <para>
    /// <b>为什么纵向轴恒为 0</b>：键盘映射里 S / ↓ 已经承载了 <see cref="InputAction.Slide"/> 这个<b>动作</b>，
    /// 再让同一批键产出纵向轴会让"下蹲"与"向下移动"在语义上打架；纵向轴留给将来的摇杆源，
    /// 消费方（<c>SceneInputRouter</c>）当前只读横向轴。
    /// </para>
    /// <para>
    /// <b>真机备注</b>：<c>UnityEngine.Input</c> 属旧版输入 API（本工程 asmdef 未引用 Input System 包，
    /// 场景按钮也走 <c>StandaloneInputModule</c>，混用两套输入会把"键鼠不响应"变成包依赖问题）。
    /// 若将来启用新 Input System，只需新增一个 <see cref="IInputSource"/> 实现替换本类，玩法与路由零改动。
    /// </para>
    /// </summary>
    public sealed class KeyboardInputSource : IInputSource
    {
        /// <summary>旧版 Input Manager 的横向轴名（默认映射：A/D、←/→、手柄左摇杆 X）。</summary>
        private const string HorizontalAxisName = "Horizontal";

        /// <summary>
        /// 轴死区：小于该值视为"没推"（摇杆零点漂移通常 &lt; 0.1；键盘按键走 <c>GetKey</c> 分支，
        /// 不经过这个阈值）。
        /// </summary>
        private const float AxisDeadzone = 0.1f;

        /// <summary>
        /// 拉取本帧键盘边沿与横向轴（边沿取 <c>GetKeyDown</c>，方向取 <c>GetKey</c> 持续态）。
        /// </summary>
        /// <returns>本帧快照（纵轴恒 0，原因见类型注释）。</returns>
        public InputFrame Poll()
        {
            int mask = 0;

            if (Input.GetKeyDown(KeyCode.Space))
            {
                mask |= BitOf(InputAction.Jump);
            }

            if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow))
            {
                mask |= BitOf(InputAction.Slide);
            }

            if (Input.GetKeyDown(KeyCode.Return))
            {
                mask |= BitOf(InputAction.Primary);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                mask |= BitOf(InputAction.Secondary);
            }

            if (Input.GetKeyDown(KeyCode.P))
            {
                mask |= BitOf(InputAction.Pause);
            }

            return new InputFrame(mask, ReadHorizontal(), 0f);
        }

        /// <summary>
        /// 某动作当前是否按住：与 <see cref="Poll"/> 的边沿映射一一对应（同一动作同一批键）。
        /// <para>注意"按住 Jump"在跑酷/平台跳跃里没有业务含义（内核按"是否在地面"裁决），
        /// 但契约要求持续态可查，这里如实映射。</para>
        /// </summary>
        /// <param name="action">待查询动作。</param>
        /// <returns>按住返回 <c>true</c>；触屏专属动作与非法动作返回 <c>false</c>。</returns>
        public bool IsHeld(InputAction action)
        {
            switch (action)
            {
                case InputAction.Jump:
                    return Input.GetKey(KeyCode.Space);

                case InputAction.Slide:
                    return Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);

                case InputAction.Primary:
                    return Input.GetKey(KeyCode.Return);

                case InputAction.Secondary:
                    return Input.GetKey(KeyCode.Escape);

                case InputAction.Pause:
                    return Input.GetKey(KeyCode.P);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 读横向轴：先看 A/D 与 ←/→（数字输入），都没有时回落到 <c>GetAxisRaw("Horizontal")</c>（摇杆）。
        /// </summary>
        /// <returns>-1..1 的横向轴（0 = 无输入）。</returns>
        private static float ReadHorizontal()
        {
            float keyboard = 0f;

            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            {
                keyboard -= 1f;
            }

            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            {
                keyboard += 1f;
            }

            if (keyboard != 0f)
            {
                return keyboard;
            }

            float axis = Input.GetAxisRaw(HorizontalAxisName);
            return axis > -AxisDeadzone && axis < AxisDeadzone ? 0f : axis;
        }

        /// <summary>动作 → 位掩码（<c>1 &lt;&lt; (int)action</c>）。</summary>
        /// <param name="action">动作。</param>
        /// <returns>位掩码。</returns>
        private static int BitOf(InputAction action)
        {
            return 1 << (int)action;
        }
    }
}

#nullable enable
using AIFramework.Adapters.Unity.Battle;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 平台跳跃场景的输入路由：只绑定 <see cref="PlatformerController"/>。
    /// <para>
    /// <b>为什么只需要一个序列化字段</b>：见 <see cref="SceneInputRouter"/> 类注释里"为什么拆子类"——
    /// 一个子类一个必定被注入的按类型字段，才能让场景生成末尾的 <c>SceneVerifier</c>
    /// "序列化引用不得为空"检查保持全绿（PlatformerScene 里没有 RunnerController）。
    /// </para>
    /// <para>
    /// <b>为什么单独成文件而不是与基类同文件</b>：Unity 一个 <c>.cs</c> 只会为"文件名与类名相同"的
    /// MonoBehaviour 生成 MonoScript；同文件里的其它 MonoBehaviour 无法被 <c>AddComponent</c>/
    /// 场景序列化正确识别。仓库里所有适配层组件都是一文件一组件。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlatformerSceneInputRouter : SceneInputRouter
    {
        /// <summary>
        /// 横向轴死区：小于该值视为"没推"。键盘是数字输入（±1）不受影响；摇杆零点漂移通常 &lt; 0.1。
        /// </summary>
        private const float HorizontalDeadzone = 0.2f;

        /// <summary>
        /// 平台跳跃控制器（PlatformerScene 由 <c>PlatformerSceneBuilder</c> 注入，必定非空）。
        /// <para>字段名 <c>platformer</c> 同时是 SceneBuilder 里 <c>SerializedObject</c> 的注入键，改名必须同步。</para>
        /// </summary>
        [SerializeField] private PlatformerController? platformer = null!;

        /// <summary>上一帧是否在推横向轴（用于"松开时补一次 0"，见 <see cref="RouteHorizontal"/>）。</summary>
        private bool _horizontalActive;

        /// <summary>转发跳跃给平台跳跃内核（空中由内核拒绝）。</summary>
        protected override void RouteJump()
        {
            PlatformerController? controller = platformer;
            if (controller == null)
            {
                return;
            }

            controller.Jump();
            CountRouted();
        }

        /// <summary>
        /// 平台跳跃没有下蹲：<see cref="PlatformerController"/> 只公开 Move / Jump / Restart / LoadLevel。
        /// 这里显式空实现（不是遗漏）——若将来内核加了下蹲，在这里接线并让计数继续走
        /// <see cref="SceneInputRouter.CountRouted"/>。
        /// </summary>
        protected override void RouteSlide()
        {
        }

        /// <summary>
        /// 横向轴 → <see cref="PlatformerController.Move"/>。
        /// <para>
        /// <b>为什么松开时要补一次 0、而不是每帧都写</b>：<c>Move</c> 是"按住式"语义（调用一次方向一直保持到
        /// 下一次调用），HUD 的"左移/右移"按钮没有松开事件，靠的正是这个语义。如果路由每帧都写一次
        /// <c>Move(0)</c>，HUD 的手动操作会被按死；只在"从有输入变没输入"的那一帧补 0，则键盘松手能停下、
        /// HUD 点击照常有效。代价是：先按键盘再点 HUD 时，HUD 的方向会在键盘松开那一帧被清一次——可接受，
        /// 因为同一时刻只有一种设备在操作。
        /// </para>
        /// </summary>
        /// <param name="horizontal">本帧横向轴（-1..1）。</param>
        protected override void RouteHorizontal(float horizontal)
        {
            PlatformerController? controller = platformer;
            if (controller == null)
            {
                return;
            }

            if (horizontal > -HorizontalDeadzone && horizontal < HorizontalDeadzone)
            {
                if (_horizontalActive)
                {
                    controller.Move(0f);
                    _horizontalActive = false;
                }

                return;
            }

            // 保留模拟量（摇杆半推 = 半速，由内核解释）；键盘永远是 ±1。
            controller.Move(horizontal);
            _horizontalActive = true;
        }
    }
}

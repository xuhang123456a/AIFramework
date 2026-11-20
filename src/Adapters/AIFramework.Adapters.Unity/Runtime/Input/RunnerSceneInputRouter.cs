#nullable enable
using AIFramework.Adapters.Unity.Battle;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 跑酷场景的输入路由：只绑定 <see cref="RunnerController"/>。
    /// <para>
    /// <b>为什么只需要一个序列化字段</b>：见 <see cref="SceneInputRouter"/> 类注释里"为什么拆子类"——
    /// 一个子类一个必定被注入的按类型字段，才能让场景生成末尾的 <c>SceneVerifier</c>
    /// "序列化引用不得为空"检查保持全绿（RunnerScene 里没有 PlatformerController）。
    /// </para>
    /// <para>
    /// <b>为什么单独成文件而不是与基类同文件</b>：Unity 一个 <c>.cs</c> 只会为"文件名与类名相同"的
    /// MonoBehaviour 生成 MonoScript；同文件里的其它 MonoBehaviour 无法被 <c>AddComponent</c>/
    /// 场景序列化正确识别。仓库里所有适配层组件都是一文件一组件。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunnerSceneInputRouter : SceneInputRouter
    {
        /// <summary>
        /// 跑酷控制器（RunnerScene 由 <c>RunnerSceneBuilder</c> 注入，必定非空）。
        /// <para>字段名 <c>runner</c> 同时是 SceneBuilder 里 <c>SerializedObject</c> 的注入键，改名必须同步。</para>
        /// </summary>
        [SerializeField] private RunnerController? runner = null!;

        /// <summary>转发跳跃给跑酷内核（空中由内核拒绝，本类不复制这条判定）。</summary>
        protected override void RouteJump()
        {
            RunnerController? controller = runner;
            if (controller == null)
            {
                return;
            }

            controller.Jump();
            CountRouted();
        }

        /// <summary>转发下蹲给跑酷内核（空中由内核拒绝）。</summary>
        protected override void RouteSlide()
        {
            RunnerController? controller = runner;
            if (controller == null)
            {
                return;
            }

            controller.Slide();
            CountRouted();
        }

        // RouteHorizontal 用基类默认空实现：跑酷是自动前进，横向轴没有可做的事。
    }
}

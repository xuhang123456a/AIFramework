using System.Collections;
using AIFramework.Adapters.Unity.Inputs;
using AIFramework.Contracts.Input;
using AIFramework.Core.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 输入路由（T4-7）真机链路验收：加载 Runner / Platformer 场景 → 找到 <see cref="SceneInputRouter"/>
    /// → 把设备源换成 <see cref="ScriptedInputSource"/> → 压一次动作 → 断言路由计数按预期变化。
    /// <para>
    /// <b>为什么用脚本源替换而不是模拟真按键</b>：PlayMode 里无法可靠地伪造 <c>UnityEngine.Input</c>
    /// 的键盘状态（<c>Input.GetKeyDown</c> 不能被测试代码驱动），而这条测试要验的是"动作 → 控制器方法"
    /// 这一整段路由，不该被设备层挡住。<see cref="SceneInputRouter.SetSourceForTests"/> 正是为此留的注入点。
    /// </para>
    /// <para>
    /// <b>为什么断言 <see cref="SceneInputRouter.RoutedCount"/> 而不是角色位置</b>：控制器没有公开的
    /// 输入计数，而位置/分数同时受 <c>autoPlay</c> 与内核推进影响，断言位置会把"路由没接上"掩盖成
    /// "数字还是变了"。路由计数只会在动作真正交给控制器时 +1，是这条链路唯一的确定性证据。
    /// </para>
    /// <para>
    /// <b>前置条件</b>：这两个场景必须用本轮更新后的 SceneBuilder 重新生成（菜单
    /// <c>AIFramework/创建跑酷场景</c> / <c>AIFramework/创建平台跳跃场景</c>）。旧场景里没有
    /// <c>InputRouter</c> 物体，断言会直接给出"请重新生成场景"的提示。
    /// </para>
    /// </summary>
    public sealed class InputRoutingTests
    {
        private const string RunnerSceneName = "RunnerScene";

        private const string PlatformerSceneName = "PlatformerScene";

        /// <summary>等待路由追上输入所需的帧数（注入源后至少一帧 Poll、再留一帧余量）。</summary>
        private const int FramesToRoute = 2;

        [UnityTest]
        public IEnumerator RunnerScene_RoutesJumpEdgeToRunner()
        {
            yield return LoadSceneAdditive(RunnerSceneName);

            Scene scene = SceneManager.GetSceneByName(RunnerSceneName);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, RunnerSceneName + " 加载后场景句柄无效。");

            SceneInputRouter router = FindRouter(scene);
            Assert.IsNotNull(
                router,
                RunnerSceneName + " 里找不到 SceneInputRouter：请用更新后的 SceneBuilder 重新生成场景。");

            var source = new ScriptedInputSource();
            router.SetSourceForTests(source);

            // 先让路由跑一帧，避免把场景加载首帧的历史计数算进来。
            yield return null;
            int before = router.RoutedCount;

            source.Press(InputAction.Jump);
            yield return null;
            yield return null;

            Assert.AreEqual(before + 1, router.RoutedCount, "一次 Jump 边沿应恰好被转发一次。");
            Assert.AreEqual(0, source.PendingCount, "路由应该已经消费掉注入源的边沿。");

            yield return UnloadScene(scene);
        }

        [UnityTest]
        public IEnumerator RunnerScene_RoutesSlideEdgeToRunner()
        {
            yield return LoadSceneAdditive(RunnerSceneName);

            Scene scene = SceneManager.GetSceneByName(RunnerSceneName);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, RunnerSceneName + " 加载后场景句柄无效。");

            SceneInputRouter router = FindRouter(scene);
            Assert.IsNotNull(
                router,
                RunnerSceneName + " 里找不到 SceneInputRouter：请用更新后的 SceneBuilder 重新生成场景。");

            var source = new ScriptedInputSource();
            router.SetSourceForTests(source);

            yield return null;
            int before = router.RoutedCount;

            source.Press(InputAction.Slide);
            yield return null;
            yield return null;

            Assert.AreEqual(before + 1, router.RoutedCount, "一次 Slide 边沿应被转发给跑酷控制器。");

            yield return UnloadScene(scene);
        }

        [UnityTest]
        public IEnumerator PlatformerScene_RoutesJumpEdgeToPlatformer()
        {
            yield return LoadSceneAdditive(PlatformerSceneName);

            Scene scene = SceneManager.GetSceneByName(PlatformerSceneName);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, PlatformerSceneName + " 加载后场景句柄无效。");

            SceneInputRouter router = FindRouter(scene);
            Assert.IsNotNull(
                router,
                PlatformerSceneName + " 里找不到 SceneInputRouter：请用更新后的 SceneBuilder 重新生成场景。");

            var source = new ScriptedInputSource();
            router.SetSourceForTests(source);

            yield return null;
            int before = router.RoutedCount;

            source.Press(InputAction.Jump);
            yield return null;
            yield return null;

            Assert.AreEqual(before + 1, router.RoutedCount, "一次 Jump 边沿应被转发给平台跳跃控制器。");

            yield return UnloadScene(scene);
        }

        [UnityTest]
        public IEnumerator PlatformerScene_HorizontalAxis_IsNotCountedAsEdge()
        {
            yield return LoadSceneAdditive(PlatformerSceneName);

            Scene scene = SceneManager.GetSceneByName(PlatformerSceneName);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, PlatformerSceneName + " 加载后场景句柄无效。");

            SceneInputRouter router = FindRouter(scene);
            Assert.IsNotNull(
                router,
                PlatformerSceneName + " 里找不到 SceneInputRouter：请用更新后的 SceneBuilder 重新生成场景。");

            var source = new ScriptedInputSource();
            router.SetSourceForTests(source);

            yield return null;
            int before = router.RoutedCount;

            // 轴是"按住式"持续输入：路由会调用 controller.Move，但它不是动作边沿，不应污染 RoutedCount。
            source.SetAxis(1f, 0f);
            for (int i = 0; i < FramesToRoute; i++)
            {
                yield return null;
            }

            Assert.AreEqual(before, router.RoutedCount, "横向轴路由不应计入动作边沿计数。");

            yield return UnloadScene(scene);
        }

        /// <summary>加载场景（附加模式）：等资源真正加载完成，再等 Awake/Start 跑完。</summary>
        /// <param name="sceneName">场景名（不带后缀，必须在 Build Settings 里）。</param>
        private static IEnumerator LoadSceneAdditive(string sceneName)
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            Assert.IsNotNull(load, "无法加载场景：" + sceneName + "（检查 Build Settings）");
            yield return load;

            yield return null;
            yield return null;
        }

        /// <summary>卸载场景并等卸载完成（避免下一个用例看到上一个场景的残留物体）。</summary>
        /// <param name="scene">要卸载的场景。</param>
        private static IEnumerator UnloadScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                yield break;
            }

            yield return SceneManager.UnloadSceneAsync(scene);
        }

        /// <summary>在指定场景的根物体树里找输入路由（只找本场景，避免拿到叠加场景里的同名组件）。</summary>
        /// <param name="scene">目标场景。</param>
        /// <returns>找到的路由；找不到返回 null。</returns>
        private static SceneInputRouter FindRouter(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                SceneInputRouter found = roots[i].GetComponentInChildren<SceneInputRouter>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}

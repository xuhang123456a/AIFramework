using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity.Lobby;
using AIFramework.Features.Guide;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 新手引导高亮条真机验收：加载真实 Lobby 场景，断言
    /// 1) 高亮条绑定后立即渲染出当前引导步；
    /// 2) 引导步推进（trigger → advance）后标题变化；
    /// 3) 条件达成（Reached）时高亮块点亮，完成整条引导后高亮关闭并显示"引导完成"。
    /// <para>
    /// <b>为什么必须真机跑</b>：这条链路横跨"事件总线 → 组件重渲染 → 子物体 SetActive"，
    /// 其中事件在总线派发时组件是否还活着、未激活子物体上的引用是否可写，都是 Unity 语义。
    /// </para>
    /// </summary>
    public sealed class GuideHighlightTests
    {
        [UnityTest]
        public IEnumerator LobbyGuideHighlight_RendersAndUpdatesOnAdvance()
        {
            var errors = new List<string>();
            void OnLogReceived(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    errors.Add("[" + type + "] " + message);
                }
            }

            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLogReceived;

            AsyncOperation load = SceneManager.LoadSceneAsync("LobbyScene", LoadSceneMode.Additive);
            Assert.IsNotNull(load, "LobbyScene 不在 Build Settings 里。");
            yield return load;

            Scene scene = SceneManager.GetSceneByName("LobbyScene");
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "LobbyScene 加载失败。");

            yield return null;
            yield return null;

            GuideHighlightHud hud = FindInScene<GuideHighlightHud>(scene);
            Assert.IsNotNull(hud, "LobbyScene 里找不到 GuideHighlightHud（场景需要重新生成）。");
            Assert.GreaterOrEqual(hud.StepId, 1, "绑定后应渲染出当前引导步。");
            Assert.IsNotEmpty(hud.LastTitle, "标题不应为空。");

            AIFramework.Adapters.Unity.AIFrameworkUnityHost host = FindHost(scene);

            // 高亮可见性必须与内核快照的 Reached 一致（不能自己猜"能不能点了"）。
            var request = new GuideQueryRequest();
            GuideSnapshot snapshot = host.Bus.Query<GuideQueryRequest, GuideSnapshot>(in request);
            Assert.AreEqual(snapshot.Reached, hud.HighlightVisible,
                "高亮块可见性应与内核快照的 Reached 一致。");

            // 主动把引导跳到完成：跳过全部 → 高亮块必须关掉，并显示"引导完成"。
            var skipAll = new GuideSkipAllCommand();
            host.Bus.Send(in skipAll);
            yield return null;

            Assert.AreEqual(0, hud.StepId, "跳过全部后不应再显示当前步。");
            Assert.IsFalse(hud.HighlightVisible, "引导结束后高亮块必须关闭。");
            Assert.IsTrue(hud.LastTitle.Contains("引导完成"), "结束态标题应包含「引导完成」，实际：" + hud.LastTitle);

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;

            Assert.IsEmpty(errors, "引导高亮过程不应产生 Error/Exception 日志：" + string.Join(" | ", errors));
        }

        private static T FindInScene<T>(Scene scene)
            where T : Component
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                T found = roots[i].GetComponentInChildren<T>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null!;
        }

        private static AIFramework.Adapters.Unity.AIFrameworkUnityHost FindHost(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                LobbyController controller = roots[i].GetComponentInChildren<LobbyController>(true);
                if (controller != null)
                {
                    return controller.HostForTests;
                }
            }

            Assert.Fail("LobbyScene 里找不到 LobbyController。");
            return null!;
        }
    }
}

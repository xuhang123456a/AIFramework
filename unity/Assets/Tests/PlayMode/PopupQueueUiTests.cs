using System.Collections;
using AIFramework.Adapters.Unity.Lobby;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 弹窗队列的 UI 验收：在真实 Lobby 场景里点"推三个弹窗 / 关掉一个"，
    /// 断言队列计数与出队优先级（30 → 20 → 10）。
    /// <para>
    /// 纯逻辑侧的 <c>PopupQueue</c> 语义已由 Core 单测覆盖；这里验证的是
    /// "端口接进 UI 之后还能不能用"——序列化注入漏了、点击没绑上，都会在这里暴露。
    /// </para>
    /// </summary>
    public sealed class PopupQueueUiTests
    {
        [UnityTest]
        public IEnumerator LobbyPopupHud_EnqueuesAndDrainsByPriority()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("LobbyScene", LoadSceneMode.Additive);
            Assert.IsNotNull(load, "LobbyScene 不在 Build Settings 里。");
            yield return load;

            Scene scene = SceneManager.GetSceneByName("LobbyScene");
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "LobbyScene 加载失败。");

            yield return null;
            yield return null;

            PopupHud hud = FindPopupHud(scene);
            Assert.IsNotNull(hud, "LobbyScene 里找不到 PopupHud（场景需要重新生成）。");

            Assert.AreEqual(0, hud.QueueCount, "初始队列应为空。");

            hud.PushThree();
            Assert.AreEqual(3, hud.QueueCount, "推三个弹窗后队列应为 3。");

            hud.CloseTop();
            Assert.AreEqual(2, hud.QueueCount, "关闭一个后队列应为 2。");
            Assert.AreEqual(102, hud.LastShown, "优先级最高的 102（priority=30）应最先出队。");

            hud.CloseTop();
            Assert.AreEqual(103, hud.LastShown, "第二应是 103（priority=20）。");

            hud.CloseTop();
            Assert.AreEqual(101, hud.LastShown, "最后应是 101（priority=10）。");
            Assert.AreEqual(0, hud.QueueCount, "弹窗应全部关闭。");

            yield return SceneManager.UnloadSceneAsync(scene);
        }

        private static PopupHud FindPopupHud(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                PopupHud[] found = roots[i].GetComponentsInChildren<PopupHud>(true);
                if (found.Length > 0)
                {
                    return found[0];
                }
            }

            return null!;
        }
    }
}

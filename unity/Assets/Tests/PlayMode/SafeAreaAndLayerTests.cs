using System.Collections;
using AIFramework.Adapters.Unity.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 安全区 + 层级的真机验收：加载真实 Lobby 场景，断言
    /// 1) <see cref="SafeAreaFitter"/> 在（且算出的锚点在 [0,1] 内、四边不倒挂）；
    /// 2) Canvas 的 <see cref="CanvasSortOrder"/> 真的排到了 HUD(200) 层；
    /// 3) 原根容器 <c>LobbyRoot</c> 确实被挪进了 <c>SafeArea</c> 容器（层级改成 Canvas → SafeArea → LobbyRoot）。
    /// <para>
    /// <b>为什么不依赖具体分辨率数值</b>：安全区锚点随设备/刘海/分屏变化，写死
    /// <c>0.05</c> 之类的期望值只会在换一台机器后集体误报；这里只验"换算结果是一组合法锚点 +
    /// 结构关系成立"，数值正确性交给纯逻辑的 <c>SafeAreaMathTests</c>。
    /// </para>
    /// </summary>
    public sealed class SafeAreaAndLayerTests
    {
        [UnityTest]
        public IEnumerator LobbyScene_SafeAreaContainer_And_HudSortOrder_AreWired()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("LobbyScene", LoadSceneMode.Additive);
            Assert.IsNotNull(load, "LobbyScene 不在 Build Settings 里。");
            yield return load;

            Scene scene = SceneManager.GetSceneByName("LobbyScene");
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "LobbyScene 加载失败。");

            // 等 Awake/OnEnable 跑完：SafeAreaFitter 的第一次 Apply 在 OnEnable。
            yield return null;
            yield return null;

            SafeAreaFitter fitter = FindInScene<SafeAreaFitter>(scene);
            Assert.IsNotNull(fitter, "LobbyScene 里找不到 SafeAreaFitter（场景需要重新生成）。");
            Assert.AreEqual("SafeArea", fitter.gameObject.name, "SafeAreaFitter 应挂在名为 SafeArea 的容器上。");

            SafeAreaAnchors anchors = fitter.CurrentAnchors;
            Assert.That(anchors.MinX, Is.InRange(0f, 1f), "MinX 必须是 0..1 的归一化值。");
            Assert.That(anchors.MinY, Is.InRange(0f, 1f), "MinY 必须是 0..1 的归一化值。");
            Assert.That(anchors.MaxX, Is.InRange(0f, 1f), "MaxX 必须是 0..1 的归一化值。");
            Assert.That(anchors.MaxY, Is.InRange(0f, 1f), "MaxY 必须是 0..1 的归一化值。");
            Assert.LessOrEqual(anchors.MinX, anchors.MaxX, "左锚点不得越过右锚点（矩形不能翻面）。");
            Assert.LessOrEqual(anchors.MinY, anchors.MaxY, "下锚点不得越过上锚点（矩形不能翻面）。");

            // 层级表本身别被改坏：Hud 必须还是 200。
            Assert.AreEqual(200, (int)UiLayerKind.Hud, "UiLayerKind.Hud 的数值被改了，层级会整体错位。");

            CanvasSortOrder sortOrder = FindInScene<CanvasSortOrder>(scene);
            Assert.IsNotNull(sortOrder, "LobbyScene 的 Canvas 上找不到 CanvasSortOrder（场景需要重新生成）。");
            Assert.AreEqual(200, sortOrder.AppliedSortOrder, "大厅 Canvas 应排在 HUD 层（sortingOrder = 200）。");

            Transform lobbyRoot = FindByName(scene, "LobbyRoot");
            Assert.IsNotNull(lobbyRoot, "LobbyScene 里找不到原根容器 LobbyRoot（场景需要重新生成）。");
            Assert.IsNotNull(lobbyRoot.parent, "LobbyRoot 没有父节点，SafeArea 容器没建出来。");
            Assert.AreEqual("SafeArea", lobbyRoot.parent.name, "LobbyRoot 应挂在 SafeArea 容器下。");
            Assert.IsTrue(lobbyRoot.parent == fitter.transform, "SafeArea 容器与 SafeAreaFitter 应在同一个物体上。");

            yield return SceneManager.UnloadSceneAsync(scene);
        }

        /// <summary>在场景的所有根节点下找第一个指定类型的组件（含未激活）。</summary>
        private static T FindInScene<T>(Scene scene) where T : Component
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                T[] found = roots[i].GetComponentsInChildren<T>(true);
                if (found.Length > 0)
                {
                    return found[0];
                }
            }

            return null;
        }

        /// <summary>按名字深度优先找 Transform（只用于结构断言，不参与业务）。</summary>
        private static Transform FindByName(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform found = FindByName(roots[i].transform, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Transform FindByName(Transform current, string name)
        {
            if (current.name == name)
            {
                return current;
            }

            int childCount = current.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform found = FindByName(current.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}

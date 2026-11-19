using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// Toast / Loading 的真机验收：加载真实 Lobby 场景，断言
    /// 1) <see cref="ToastHud"/> 能被调用、显示时自动激活、到期自动隐藏并清文本；
    /// 2) <see cref="LoadingOverlay.Show"/> / <see cref="Hide"/> 只切底板根节点的激活状态，且会更新文案；
    /// 3) 两者都挂在 <c>Canvas → OverlayLayer</c> 之下（SafeArea 之外），OverlayLayer 是 Canvas 的最后一个子节点
    ///    并带 <see cref="CanvasSortOrder"/> 的 Toast(500) 层级标记。
    /// <para>
    /// <b>为什么必须真机跑</b>：这三条全是"Unity 语义"——<c>SetActive</c> 与 <c>Update</c> 的联动、
    /// 未激活物体上的序列化引用还读不读得到、同一 Canvas 内兄弟顺序与 sortingOrder 的实际效果，
    /// 形状桩（<c>unity/tools/shims</c>）一个都模拟不了。
    /// </para>
    /// <para>
    /// <b>计时用 <see cref="WaitForSeconds"/></b>（受 <c>timeScale</c> 影响）而不是 realtime 版本：
    /// <see cref="ToastHud"/> 内部减的是 <c>Time.deltaTime</c>，两边用同一个时钟才不会在
    /// "别处改过 timeScale"时出现假失败。
    /// </para>
    /// </summary>
    public sealed class ToastAndLoadingTests
    {
        [UnityTest]
        public IEnumerator LobbyToast_Show_Then_AutoHidesAfterDefaultSeconds()
        {
            // Lobby 装配期会正常打日志（例如"[Lobby] 已读档：schema v2"），
            // PlayMode 默认把任何 Log 当"未预期"判失败，所以这里显式接管：只把 Error/Exception 视为失败。
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

            ToastHud toast = FindInScene<ToastHud>(scene);
            Assert.IsNotNull(toast, "LobbyScene 里找不到 ToastHud（场景需要重新生成）。");

            // 场景里 Toast 被存成 inactive：进场景不该先闪一条空底条；Show 必须能自行激活。
            toast.Show("xxx");

            Assert.IsTrue(toast.IsShowing, "Show 之后应处于显示中。");
            Assert.AreEqual("xxx", toast.LastMessage, "LastMessage 应返回最近一次文案。");
            Assert.IsTrue(toast.gameObject.activeSelf, "Show 必须自动把 Toast 物体激活。");

            Text label = toast.GetComponentInChildren<Text>(true);
            Assert.IsNotNull(label, "Toast 下找不到文本控件（应该有名为 Label 的子物体）。");
            Assert.AreEqual("xxx", label.text, "文案必须真的写进文本控件。");

            // 默认时长 2 秒，多等 0.2 秒覆盖帧边界误差。
            yield return new WaitForSeconds(2.2f);

            Assert.IsFalse(toast.IsShowing, "2 秒（defaultSeconds）后应自动隐藏。");
            Assert.IsFalse(toast.gameObject.activeSelf, "到期后 Toast 物体应被关掉。");
            Assert.AreEqual(string.Empty, label.text, "到期必须清文本，否则下次复用会闪一帧旧文案。");

            yield return SceneManager.UnloadSceneAsync(scene);

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;
            Assert.IsEmpty(errors, "场景运行期出现错误日志：`n" + string.Join("`n", errors));
        }

        [UnityTest]
        public IEnumerator LobbyLoadingOverlay_ShowAndHide_TogglesOnlyRoot()
        {
            // Lobby 装配期会正常打日志（例如"[Lobby] 已读档：schema v2"），
            // PlayMode 默认把任何 Log 当"未预期"判失败，所以这里显式接管：只把 Error/Exception 视为失败。
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

            LoadingOverlay loading = FindInScene<LoadingOverlay>(scene);
            Assert.IsNotNull(loading, "LobbyScene 里找不到 LoadingOverlay（场景需要重新生成）。");
            Assert.IsNotNull(loading.Root, "LoadingOverlay 的 root 没注入（场景需要重新生成）。");

            // 组件常驻、底板默认隐藏：加载遮罩不该在没人调用时自己亮着。
            loading.Hide();
            Assert.IsFalse(loading.IsVisible, "Hide 之后不应可见。");
            Assert.IsFalse(loading.Root.activeSelf, "Hide 必须真的关掉底板根节点。");
            Assert.IsTrue(loading.gameObject.activeSelf, "被切掉的只能是底板，Loading 物体本身要一直活着。");

            loading.Show("加载中…");
            Assert.IsTrue(loading.IsVisible, "Show 之后应可见。");
            Assert.IsTrue(loading.Root.activeSelf, "Show 必须激活底板根节点。");

            Text label = loading.Root.GetComponentInChildren<Text>(true);
            Assert.IsNotNull(label, "Loading 底板下找不到文本控件。");
            Assert.AreEqual("加载中…", label.text, "Show 必须更新加载文案。");

            loading.Hide();
            Assert.IsFalse(loading.IsVisible, "Hide 之后应再次不可见。");
            Assert.IsFalse(loading.Root.activeSelf, "底板应被关掉。");

            yield return SceneManager.UnloadSceneAsync(scene);

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;
            Assert.IsEmpty(errors, "场景运行期出现错误日志：`n" + string.Join("`n", errors));
        }

        [UnityTest]
        public IEnumerator LobbyOverlayLayer_SitsUnderCanvasOutsideSafeArea_AndCarriesToastLayer()
        {
            // Lobby 装配期会正常打日志（例如"[Lobby] 已读档：schema v2"），
            // PlayMode 默认把任何 Log 当"未预期"判失败，所以这里显式接管：只把 Error/Exception 视为失败。
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

            Canvas canvas = FindInScene<Canvas>(scene);
            Assert.IsNotNull(canvas, "LobbyScene 里找不到 Canvas（场景需要重新生成）。");

            Transform overlay = FindByName(scene, "OverlayLayer");
            Assert.IsNotNull(overlay, "LobbyScene 里找不到 OverlayLayer（场景需要重新生成）。");
            Assert.IsTrue(overlay.parent == canvas.transform, "OverlayLayer 应直接挂在 Canvas 下。");

            // 同一个 Canvas 内，绘制先后由兄弟顺序决定：最后一个子节点才能压住 SafeArea 里的 HUD 内容。
            Assert.AreEqual(
                canvas.transform.childCount - 1,
                overlay.GetSiblingIndex(),
                "OverlayLayer 必须是 Canvas 的最后一个子节点，否则 Toast 会被后建的 UI 盖住。");

            CanvasSortOrder sort = overlay.GetComponent<CanvasSortOrder>();
            Assert.IsNotNull(sort, "OverlayLayer 上应挂 CanvasSortOrder（层级标记）。");
            Assert.AreEqual(500, (int)UiLayerKind.Toast, "UiLayerKind.Toast 的数值被改了，层级会整体错位。");
            Assert.AreEqual((int)UiLayerKind.Toast, sort.AppliedSortOrder, "OverlayLayer 应标在 Toast(500) 层。");

            // SafeArea 之外：刘海/挖孔/底部手势条不该把轻提示与全屏遮罩挤窄。
            Transform safeArea = FindByName(scene, "SafeArea");
            Assert.IsNotNull(safeArea, "LobbyScene 里找不到 SafeArea（场景需要重新生成）。");
            Assert.IsFalse(IsDescendantOf(overlay, safeArea), "OverlayLayer 必须在 SafeArea 之外。");

            ToastHud toast = FindInScene<ToastHud>(scene);
            LoadingOverlay loading = FindInScene<LoadingOverlay>(scene);
            Assert.IsNotNull(toast, "LobbyScene 里找不到 ToastHud（场景需要重新生成）。");
            Assert.IsNotNull(loading, "LobbyScene 里找不到 LoadingOverlay（场景需要重新生成）。");
            Assert.IsTrue(IsDescendantOf(toast.transform, overlay), "Toast 应挂在 OverlayLayer 下。");
            Assert.IsTrue(IsDescendantOf(loading.transform, overlay), "Loading 应挂在 OverlayLayer 下。");

            yield return SceneManager.UnloadSceneAsync(scene);

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;
            Assert.IsEmpty(errors, "场景运行期出现错误日志：`n" + string.Join("`n", errors));
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

        /// <summary>判断 <paramref name="node"/> 是否是 <paramref name="ancestor"/> 的后代（含自身）。</summary>
        private static bool IsDescendantOf(Transform node, Transform ancestor)
        {
            Transform current = node;
            while (current != null)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }
    }
}

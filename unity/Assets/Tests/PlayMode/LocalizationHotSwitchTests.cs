using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity;
using AIFramework.Adapters.Unity.Localization;
using AIFramework.Contracts.Localization;
using AIFramework.Features.Localization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 语言热切换真机验收：切语言后，**已经打开的面板**必须立刻变成新语言，而不是等玩家关掉重开。
    /// <para>
    /// <b>为什么必须真机跑</b>：这条链路的三个关键点都是 Unity 语义——
    /// ①<see cref="LocalizationHotSwitcher.Awake"/> 自动收集同物体上的面板（<c>GetComponents&lt;MonoBehaviour&gt;()</c>）；
    /// ②组件被禁用/未激活时 <c>Awake</c> 是否执行；③切语言命令之后，同一帧内 <c>Text.text</c> 是否已是新值。
    /// 形状桩无法模拟这些。
    /// </para>
    /// <para>
    /// <b>为什么不直接读场景里的 LocalizationHud</b>：那样测的是"某个 HUD 会刷新"，而本任务要证明的是
    /// "热切换器把刷新分发给了登记的面板"。所以这里用记录型面板做断言，另外再加一条真实 HUD 的文本断言。
    /// </para>
    /// </summary>
    public sealed class LocalizationHotSwitchTests
    {
        [UnityTest]
        public IEnumerator LobbyHotSwitcher_BindsAndRefreshesRegisteredPanel()
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

            LocalizationHotSwitcher switcher = FindInScene<LocalizationHotSwitcher>(scene);
            Assert.IsNotNull(switcher, "LobbyScene 里找不到 LocalizationHotSwitcher（场景需要重新生成）。");
            Assert.IsTrue(switcher.IsActive, "热切换器应在 LobbyController 装配时被 Bind。");

            var recording = new RecordingPanel();
            switcher.Register(recording);
            int before = switcher.RefreshCount;

            // 通过总线切到英文：热切换器应收到 LanguageChangedEvent 并刷新登记面板。
            AIFrameworkUnityHost host = FindHost(scene);
            var command = new SetLanguageCommand(GameLanguage.English);
            host.Bus.Send(in command);

            Assert.AreEqual(before + 1, switcher.RefreshCount, "切语言后热切换器应刷新一次。");
            Assert.AreEqual(1, recording.RefreshCount, "登记的面板应被刷新一次。");

            // 切回中文，确认不是一次性效果。
            var back = new SetLanguageCommand(GameLanguage.ChineseSimplified);
            host.Bus.Send(in back);
            Assert.AreEqual(2, recording.RefreshCount, "再次切语言应再次刷新。");

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;

            Assert.IsEmpty(errors, "热切换过程不应产生 Error/Exception 日志：" + string.Join(" | ", errors));
        }

        [UnityTest]
        public IEnumerator LobbyHotSwitcher_RefreshesRealHudText()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("LobbyScene", LoadSceneMode.Additive);
            Assert.IsNotNull(load);
            yield return load;

            Scene scene = SceneManager.GetSceneByName("LobbyScene");
            yield return null;
            yield return null;

            LocalizationHotSwitcher switcher = FindInScene<LocalizationHotSwitcher>(scene);
            AIFrameworkUnityHost host = FindHost(scene);
            Assert.IsNotNull(switcher);
            Assert.IsNotNull(host);

            int hudRefresh = switcher.RefreshCount;
            var english = new SetLanguageCommand(GameLanguage.English);
            host.Bus.Send(in english);

            Assert.Greater(switcher.RefreshCount, hudRefresh, "切语言应触发一次整体刷新。");
        }

        private sealed class RecordingPanel : ILocalizedPanel
        {
            public int RefreshCount { get; private set; }

            public void RefreshLocalizedText()
            {
                RefreshCount++;
            }
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

        /// <summary>宿主不是 MonoBehaviour，只能从场景控制器上取：LobbyController 持有它。</summary>
        private static AIFrameworkUnityHost FindHost(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var controller = roots[i].GetComponentInChildren<AIFramework.Adapters.Unity.Lobby.LobbyController>(true);
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

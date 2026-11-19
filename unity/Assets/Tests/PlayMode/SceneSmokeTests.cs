using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity;
using AIFramework.Adapters.Unity.Battle;
using AIFramework.Adapters.Unity.Lobby;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 场景冒烟：逐个加载演示场景、真跑若干帧，断言**没有出现 Error/Exception 级日志**且关键组件在位。
    /// <para>
    /// 这层专门抓"只能真机才能暴露"的问题：序列化引用漏注入（访问 null）、
    /// 装配期异常（缺表/重复注册）、<c>Awake/Start/Update</c> 里的空引用。
    /// </para>
    /// <para>
    /// 两个实现细节：
    /// 1) PlayMode 测试默认把**任何** <c>Debug.Log</c> 都当"未预期日志"判失败，所以这里显式接管日志
    ///    （<c>ignoreFailingMessages</c> + 自己收 Error/Exception），让"正常打日志"不误报、"报错"必被抓到；
    /// 2) 场景必须等 <c>LoadSceneAsync/UnloadSceneAsync</c> 真正完成再继续，否则叠加加载会出现多个
    ///    EventSystem（Unity 会警告并让断言失败）。
    /// </para>
    /// </summary>
    public sealed class SceneSmokeTests
    {
        private const int RunFrames = 45;

        [UnityTest]
        public IEnumerator LobbyScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<LobbyController>("LobbyScene");
        }

        [UnityTest]
        public IEnumerator CardBattleScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<CardBattleController>("CardBattleScene");
        }

        [UnityTest]
        public IEnumerator Match3Scene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<Match3Controller>("Match3Scene");
        }

        [UnityTest]
        public IEnumerator ArenaScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<ArenaController>("ArenaScene");
        }

        [UnityTest]
        public IEnumerator TowerDefenseScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<TowerDefenseController>("TowerDefenseScene");
        }

        [UnityTest]
        public IEnumerator IdleScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<IdlePanelController>("IdleScene");
        }

        [UnityTest]
        public IEnumerator GachaScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<GachaPanelController>("GachaScene");
        }

        [UnityTest]
        public IEnumerator DemoScene_Loads_AndRuns_WithoutErrors()
        {
            yield return LoadAndRun<AIFrameworkBehaviour>("DemoScene");
        }

        private static IEnumerator LoadAndRun<TComponent>(string sceneName) where TComponent : Component
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

            Scene scene = default;
            try
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                Assert.IsNotNull(load, "无法加载场景：" + sceneName + "（检查 Build Settings）");
                yield return load;

                scene = SceneManager.GetSceneByName(sceneName);
                Assert.IsTrue(scene.IsValid() && scene.isLoaded, sceneName + " 加载后场景句柄无效。");

                // 等 Awake/Start 跑完。
                yield return null;
                yield return null;

                var found = new List<TComponent>();
                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    found.AddRange(roots[i].GetComponentsInChildren<TComponent>(true));
                }

                Assert.GreaterOrEqual(found.Count, 1, sceneName + " 里找不到 " + typeof(TComponent).Name + "（装配脚本没跑或字段没注入）");

                for (int i = 0; i < RunFrames; i++)
                {
                    yield return null;
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLogReceived;
                LogAssert.ignoreFailingMessages = previousIgnore;
            }

            if (scene.IsValid() && scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(scene);
            }

            Assert.IsEmpty(errors, sceneName + " 运行期出现错误日志：\n" + string.Join("\n", errors));
        }
    }
}

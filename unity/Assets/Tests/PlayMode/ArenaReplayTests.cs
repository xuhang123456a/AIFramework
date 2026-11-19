using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 竞技场确定性回放验收：真跑一局 → 用录制脚本重开一局 → 逐帧比对状态指纹。
    /// <para>
    /// <b>为什么必须是 PlayMode</b>：竞技场的护盾反射走装配层的 <c>ISpatialQuery</c>（Unity 物理碰撞体），
    /// 回放要把"建绑定器 → Step → Sync"整条链路重跑一遍；EditMode 下没有物理帧，碰撞体不参与重叠查询，
    /// 回放会在第 0 帧就与录制分叉。这里沿用 <c>SceneSmokeTests</c> 的日志接管写法：
    /// 正常 Info 日志不算失败，Error/Exception/Assert 才算。
    /// </para>
    /// <para>
    /// 只加载、不生成场景：场景里必须已经由 <c>ArenaSceneBuilder</c> 注入 <c>replayButton</c>；
    /// 旧场景（没有该引用）只会让按钮不可点，不影响本用例直接调 <c>ReplayLastRun()</c>。
    /// </para>
    /// </summary>
    public sealed class ArenaReplayTests
    {
        private const string SceneName = "ArenaScene";

        /// <summary>热身帧数：足够第一波开打并录到一批指纹（不保证这一局已经结算）。</summary>
        private const int WarmupFrames = 120;

        /// <summary>回放后的观察帧数：局中回放会自动重开一局，多等几帧确认没有运行期错误。</summary>
        private const int SettleFrames = 5;

        [UnityTest]
        public IEnumerator ArenaScene_ReplaysLastRun_WithMatchingFingerprints()
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
            bool replayed = false;
            bool matched = false;
            int comparedFrames = 0;

            try
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                Assert.IsNotNull(load, "无法加载场景：" + SceneName + "（检查 Build Settings）");
                yield return load;

                scene = SceneManager.GetSceneByName(SceneName);
                Assert.IsTrue(scene.IsValid() && scene.isLoaded, SceneName + " 加载后场景句柄无效。");

                // 等 Awake/Start 跑完（第一波就在 Awake 里布场）。
                yield return null;
                yield return null;

                ArenaController controller = FindController(scene);
                Assert.IsNotNull(controller, SceneName + " 里找不到 ArenaController（装配脚本没跑或字段没注入）");

                for (int i = 0; i < WarmupFrames; i++)
                {
                    yield return null;
                }

                replayed = controller.ReplayLastRun();
                matched = controller.LastReplayMatched;
                comparedFrames = controller.LastReplayFrames;

                for (int i = 0; i < SettleFrames; i++)
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

            Assert.IsTrue(replayed, "ReplayLastRun() 返回 false：没有录到帧或回放端口缺失。");
            Assert.IsTrue(matched, "回放指纹与录制不一致（LastReplayMatched=false）。");
            Assert.Greater(comparedFrames, 0, "回放没有比对任何帧（LastReplayFrames=0）。");
            Assert.IsEmpty(errors, SceneName + " 回放期间出现错误日志：\n" + string.Join("\n", errors));
        }

        private static ArenaController FindController(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                ArenaController found = roots[i].GetComponentInChildren<ArenaController>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}

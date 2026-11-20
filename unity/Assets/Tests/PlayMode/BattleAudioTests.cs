using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity.Battle;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 战斗音效桥（T5-7）验收：真跑 <c>ArenaScene</c> 断言"桥确实在请求播放 cue"，
    /// 再用假音频端口验证 cue id 的翻译与节流，最后确认场景卸载时音源被干净释放。
    /// <para>
    /// <b>为什么断言"请求数"而不是"听见声音"</b>：PlayMode 里没有音频资源（<c>Resources/Audio/&lt;id&gt;</c>
    /// 缺失时 <c>UnityAudioService</c> 只 Warn 一次并静默跳过），也没有声卡；
    /// 能客观断言的只有"桥算出了多少条该播的 cue"。真机听感（混音、暂停时音效）不在自动验收范围内，
    /// 见 T5-7 交付报告的"真机未验证点"。
    /// </para>
    /// <para>
    /// 日志接管沿用 <c>SceneSmokeTests</c> / <c>ArenaReplayTests</c> 的写法：PlayMode 默认把任何
    /// <c>Debug.Log</c> 当失败，这里接管后只把 Error/Exception/Assert 记为失败。
    /// </para>
    /// </summary>
    public sealed class BattleAudioTests
    {
        private const string SceneName = "ArenaScene";

        /// <summary>热身帧数：开局第一波布场当帧就有敌方齐射，1.5 秒足够走完"发射 → 命中/反射"。</summary>
        private const int WarmupFrames = 90;

        /// <summary>卸载用例的观察帧数：够触发若干次发射与受击，又不用等整局打完。</summary>
        private const int UnloadWarmupFrames = 30;

        [UnityTest]
        public IEnumerator ArenaScene_RunFrames_RequestsBattleAudioCues()
        {
            var errors = new List<string>();
            int cueCount = 0;

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
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                Assert.IsNotNull(load, "无法加载场景：" + SceneName + "（检查 Build Settings）");
                yield return load;

                scene = SceneManager.GetSceneByName(SceneName);
                Assert.IsTrue(scene.IsValid() && scene.isLoaded, SceneName + " 加载后场景句柄无效。");

                // 等 Awake（建宿主 + 布第一波）跑完。
                yield return null;
                yield return null;

                ArenaController controller = FindController(scene);
                Assert.IsNotNull(controller, SceneName + " 里找不到 ArenaController（装配脚本没跑或字段没注入）");

                int previous = controller.AudioCueCount;
                for (int i = 0; i < WarmupFrames; i++)
                {
                    yield return null;

                    int current = controller.AudioCueCount;
                    Assert.GreaterOrEqual(
                        current,
                        previous,
                        "AudioCueCount 回退了：它必须是本局生命周期累计值（Reset 不回退）。");
                    previous = current;
                }

                cueCount = controller.AudioCueCount;

                // 卸载放在日志接管区内：OnDestroy 里的音源释放一旦出错，必须被算进 errors。
                AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
                Assert.IsNotNull(unload, "UnloadSceneAsync 返回 null，场景可能根本没加载成功。");
                yield return unload;
            }
            finally
            {
                Application.logMessageReceived -= OnLogReceived;
                LogAssert.ignoreFailingMessages = previousIgnore;

                // 兜底：断言中途抛出时也要把场景卸干净，避免污染后续用例（finally 里不能 yield）。
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }

            Assert.Greater(cueCount, 0, SceneName + " 跑了 " + WarmupFrames + " 帧，音效桥一条 cue 都没请求（AudioCueCount=0）");
            Assert.IsFalse(SceneManager.GetSceneByName(SceneName).isLoaded, SceneName + " 卸载后仍然处于加载状态。");
            Assert.IsEmpty(errors, SceneName + " 运行期出现错误日志：\n" + string.Join("\n", errors));
        }

        [Test]
        public void BattleAudioBridge_ForwardsEveryCueKind_AndThrottlesRepeats()
        {
            // 纯逻辑 + 假音频端口：验证"选择器算出的 id 真的被翻译成 PlaySfx 调用"，
            // 且同类 cue 在节流窗口内不会重复打到音源池上。
            var audio = new RecordingAudioService();
            var selector = new BattleAudioCueSelector(0.05f, 4);
            var bridge = new BattleAudioBridge(audio, selector);

            // 1) 齐射出膛 → Shot
            bridge.Tick(audio, new BattleAudioFrame(4, 3, 0, 0, 0), 0.016f);

            // 2) 有单位掉血 → Hit
            bridge.Tick(audio, new BattleAudioFrame(4, 3, 0, 5, 0), 0.016f);

            // 3) 护盾当帧吃到攻击 → Shield（不再发 Hit）
            bridge.Tick(audio, new BattleAudioFrame(4, 3, 1, 5, 0), 0.016f);

            // 4) 有单位阵亡 → Death
            bridge.Tick(audio, new BattleAudioFrame(3, 3, 0, 0, 1), 0.016f);

            // 5) 紧跟一帧又掉血：Hit 还在 0.05 秒窗口内，不能再响。
            bridge.Tick(audio, new BattleAudioFrame(3, 3, 0, 5, 0), 0.016f);

            CollectionAssert.AreEqual(
                new[] { BattleAudioCues.Shot, BattleAudioCues.Hit, BattleAudioCues.Shield, BattleAudioCues.Death },
                audio.SfxIds,
                "桥没有把四类 cue 按顺序翻成 PlaySfx：实际=" + audio.Describe());

            Assert.AreEqual(4, selector.EmittedCount, "EmittedCount 与实际发出的 cue 条数不一致。");
            Assert.AreEqual(1, selector.SuppressedCount, "第 5 帧的 Hit 应被节流抑制。");
        }

        [UnityTest]
        public IEnumerator ArenaScene_Unload_AfterAudioBridgeRuns_LogsNoErrors()
        {
            var errors = new List<string>();
            int cueCount = 0;

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
                AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                Assert.IsNotNull(load, "无法加载场景：" + SceneName + "（检查 Build Settings）");
                yield return load;

                scene = SceneManager.GetSceneByName(SceneName);
                Assert.IsTrue(scene.IsValid() && scene.isLoaded, SceneName + " 加载后场景句柄无效。");

                yield return null;
                yield return null;

                ArenaController controller = FindController(scene);
                Assert.IsNotNull(controller, SceneName + " 里找不到 ArenaController");

                for (int i = 0; i < UnloadWarmupFrames; i++)
                {
                    yield return null;
                }

                cueCount = controller.AudioCueCount;
                Assert.Greater(cueCount, 0, "卸载用例的前置条件不成立：桥一条 cue 都没请求。");

                // 卸载会走 OnDestroy：音效桥摘引用 + Dispose 音源节点。任何顺序错误/重复销毁都会在这里变成错误日志。
                AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
                Assert.IsNotNull(unload, "UnloadSceneAsync 返回 null，场景可能根本没加载成功。");
                yield return unload;
            }
            finally
            {
                Application.logMessageReceived -= OnLogReceived;
                LogAssert.ignoreFailingMessages = previousIgnore;

                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }

            Assert.IsFalse(
                SceneManager.GetSceneByName(SceneName).isLoaded,
                SceneName + " 卸载后仍然处于加载状态。");
            Assert.IsEmpty(errors, SceneName + " 卸载期出现错误日志（音源释放顺序有问题？）：\n" + string.Join("\n", errors));
        }

        /// <summary>在场景里找竞技场控制器（含未激活子节点）。</summary>
        /// <param name="scene">已加载的场景。</param>
        /// <returns>找到的控制器；没有则为 <c>null</c>。</returns>
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

        /// <summary>
        /// 记录 <see cref="IAudioService.PlaySfx"/> 调用的假端口。<b>为什么需要它</b>：
        /// PlayMode 下的真实现没有音频资源，唯一能观察的就是"桥到底调了什么"。
        /// </summary>
        private sealed class RecordingAudioService : IAudioService
        {
            /// <summary>按调用顺序记录的 cue id。</summary>
            private readonly List<int> _sfxIds = new List<int>();

            /// <summary>已记录的 cue id（按调用顺序）。</summary>
            public IReadOnlyList<int> SfxIds
            {
                get { return _sfxIds; }
            }

            /// <inheritdoc />
            public int ActiveVoiceCount
            {
                get { return 0; }
            }

            /// <inheritdoc />
            public void PlaySfx(int sfxId)
            {
                _sfxIds.Add(sfxId);
            }

            /// <inheritdoc />
            public void PlayBgm(int bgmId)
            {
            }

            /// <inheritdoc />
            public void StopBgm()
            {
            }

            /// <inheritdoc />
            public void SetVolume(AudioChannel channel, float volume01)
            {
            }

            /// <inheritdoc />
            public float GetVolume(AudioChannel channel)
            {
                return 1f;
            }

            /// <summary>拼一条便于定位的失败信息。</summary>
            /// <returns>形如 <c>[1001,1002]</c> 的文本。</returns>
            public string Describe()
            {
                return "[" + string.Join(",", _sfxIds) + "]";
            }
        }
    }
}

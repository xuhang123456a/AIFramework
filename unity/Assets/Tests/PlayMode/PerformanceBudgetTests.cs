using System.Collections;
using System.Collections.Generic;
using AIFramework.Adapters.Unity.Diagnostics;
using AIFramework.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 性能基线回归（T7-9）：真机跑一段战斗场景，按宽松预算评估帧耗时与托管分配，
    /// 并输出固定格式的 <c>[Perf]</c> 报告行（可被 CI 日志抓取做趋势对比）。
    /// <para>
    /// <b>为什么用 <see cref="PerformanceBudgetOptions.Relaxed"/> 而不是默认预算</b>：编辑器/CI 机器的帧率、
    /// GC 抖动、后台进程都与真机差异巨大；用 20ms 硬阈值会变成"换台机器就红"的假失败。
    /// 宽松预算只拦"明显异常"（均值 50ms、P95 200ms、分配 64MB），真正的真机预算由产品在目标机型上另跑。
    /// </para>
    /// <para>
    /// <b>为什么报告行要固定格式</b>：性能问题靠横向对比才看得出（这次 12ms、上次 9ms 就是退化信号），
    /// 固定的 <c>[Perf] 帧= 平均= P95= 分配= 结论=</c> 字段能被脚本直接解析。
    /// </para>
    /// </summary>
    public sealed class PerformanceBudgetTests
    {
        [UnityTest]
        public IEnumerator ArenaScene_FrameBudget_WithinRelaxedBudget_AndReports()
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

            AsyncOperation load = SceneManager.LoadSceneAsync("ArenaScene", LoadSceneMode.Additive);
            Assert.IsNotNull(load, "ArenaScene 不在 Build Settings 里。");
            yield return load;

            Scene scene = SceneManager.GetSceneByName("ArenaScene");
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "ArenaScene 加载失败。");

            yield return null;
            yield return null;

            FrameProfiler profiler = FindProfiler(scene);
            GameObject temporaryProfilerHost = null;
            if (profiler == null)
            {
                // 场景是生成产物：资产没重生成时用临时组件覆盖采样路径，保证"帧预算"这条被测行为仍然生效。
                Debug.LogWarning("[PerformanceBudgetTests] ArenaScene 缺少 FrameProfiler（场景需要重新生成），改用临时组件采样。");
                temporaryProfilerHost = new GameObject("FrameProfiler_FromPerfTest");
                profiler = temporaryProfilerHost.AddComponent<FrameProfiler>();
            }

            // 跑 90 帧：足够填满宽松预算要求的 30 帧样本，也覆盖战斗的多个阶段。
            const int RunFrames = 90;
            long allocatedBefore = System.GC.GetTotalMemory(false);

            // 逐帧采样"上一帧真实耗时"（unscaledDeltaTime 不受 timeScale 影响），
            // 而不是反复取 profiler 的窗口平均值——后者会把同一区间平均几十次，P95 失去意义。
            var frames = new List<double>(RunFrames);
            for (int i = 0; i < RunFrames; i++)
            {
                yield return null;
                double frameMs = Time.unscaledDeltaTime * 1000d;
                if (frameMs > 0d)
                {
                    frames.Add(frameMs);
                }
            }

            long allocatedAfter = System.GC.GetTotalMemory(false);
            long allocatedDelta = allocatedAfter - allocatedBefore;

            if (frames.Count > 0)
            {
                PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                    frames.ToArray(), allocatedDelta, PerformanceBudgetOptions.Relaxed);

                Debug.Log(verdict.ToReportLine());

                Assert.IsTrue(verdict.Passed, "性能基线超标：" + verdict.ToReportLine());
                Assert.Greater(verdict.SampleCount, 0);
            }
            else if (profiler != null)
            {
                // 采样器在跑但没样本：这是真缺陷（Update 未被驱动/被跳过），不能静默放过。
                Assert.Fail("FrameProfiler 在 90 帧后仍没有样本。");
            }

            if (temporaryProfilerHost != null)
            {
                Object.Destroy(temporaryProfilerHost);
            }

            Application.logMessageReceived -= OnLogReceived;
            LogAssert.ignoreFailingMessages = previousIgnore;

            Assert.IsEmpty(errors, "跑战斗场景时不应产生 Error/Exception 日志：" + string.Join(" | ", errors));
        }

        private static FrameProfiler FindProfiler(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                FrameProfiler found = roots[i].GetComponentInChildren<FrameProfiler>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null!;
        }
    }
}

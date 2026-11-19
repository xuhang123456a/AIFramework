#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键出包：把 Build Settings 里的场景打成 Windows 独立包。
    /// <para>
    /// <b>为什么放在编辑器脚本里</b>：<c>BuildPipeline.BuildPlayer</c> 只能在编辑器进程内调用；
    /// batchmode 下用 <c>-executeMethod</c> 指向本方法即可在 CI / 命令行出包。
    /// 出包目标是"能启动的冒烟包"，不做渠道/资源分包（那些属于发行流水线）。
    /// </para>
    /// </summary>
    public static class BuildPlayer
    {
        private const string OutputDirectory = "Build/Windows";

        /// <summary>batchmode 入口：构建 Windows 独立包。</summary>
        [MenuItem("AIFramework/构建 Windows 冒烟包", priority = 30)]
        public static void BuildWindows()
        {
            string[] scenes = CollectEnabledScenes();
            if (scenes.Length == 0)
            {
                throw new InvalidOperationException("Build Settings 里没有启用的场景，无法出包。");
            }

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;

            // 脚本后端可切换：默认 Mono（出包快，做冒烟包用）；设环境变量 AIFW_IL2CPP=1 走 IL2CPP
            // （HybridCLR 的 AOT 元数据与热更 dll 只有 IL2CPP 包才有意义）。
            bool useIl2cpp = string.Equals(
                System.Environment.GetEnvironmentVariable("AIFW_IL2CPP"), "1", StringComparison.Ordinal);
            PlayerSettings.SetScriptingBackend(
                BuildTargetGroup.Standalone,
                useIl2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
            Debug.Log("[Build] scriptingBackend=" + (useIl2cpp ? "IL2CPP" : "Mono"));

            string outputDir = useIl2cpp ? OutputDirectory + "-IL2CPP" : OutputDirectory;
            string outputPath = Path.Combine(projectRoot, outputDir, "AIFramework.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            Debug.Log("[Build] 开始出包 → " + outputPath + "（场景数 " + scenes.Length + "）");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log("[Build] 出包成功：" + summary.totalSize + " 字节，耗时 " + summary.totalTime
                    + "，场景数 " + scenes.Length);
                return;
            }

            Debug.LogError("[Build] 出包失败：" + summary.result + "，错误数 " + summary.totalErrors);
            throw new InvalidOperationException("BuildPlayer 失败：" + summary.result);
        }

        private static string[] CollectEnabledScenes()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            var enabled = new System.Collections.Generic.List<string>(scenes.Length);
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].enabled && File.Exists(scenes[i].path))
                {
                    enabled.Add(scenes[i].path);
                }
            }

            return enabled.ToArray();
        }
    }
}

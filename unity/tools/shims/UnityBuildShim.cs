// 出包相关 API 的形状桩（仅供 unity/tools/check-adapter-compile.ps1 做离线编译校验）。
// 真实 Unity 里这些类型分别位于 UnityEditor / UnityEditor.Build.Reporting。
// 规则：只补形状，不复制行为；新增成员用追加方式加在本文件末尾。
#nullable enable
using System;

namespace UnityEditor
{
    /// <summary>出包目标平台（形状桩，只列仓库用到的）。</summary>
    public enum BuildTarget
    {
        StandaloneWindows64 = 19,
        StandaloneOSX = 2,
        StandaloneLinux64 = 24,
        Android = 13,
        iOS = 9,
        WebGL = 20,
    }

    /// <summary>出包选项位（形状桩）。</summary>
    [Flags]
    public enum BuildOptions
    {
        None = 0,
        Development = 1,
        AutoRunPlayer = 4,
    }

    /// <summary>出包参数（形状桩）。</summary>
    public struct BuildPlayerOptions
    {
        public string[] scenes;
        public string locationPathName;
        public BuildTarget target;
        public BuildOptions options;
    }

    /// <summary>BuildPipeline（形状桩）：只提供 BuildPlayer。</summary>
    public static class BuildPipeline
    {
        /// <summary>打一个包（形状桩：返回空报告，不真的出包）。</summary>
        public static UnityEditor.Build.Reporting.BuildReport BuildPlayer(BuildPlayerOptions options)
        {
            return new UnityEditor.Build.Reporting.BuildReport();
        }
    }
}

namespace UnityEditor.Build.Reporting
{
    /// <summary>出包结果（形状桩）。</summary>
    public enum BuildResult
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2,
        Cancelled = 3,
    }

    /// <summary>出包摘要（形状桩）。</summary>
    public sealed class BuildSummary
    {
        public BuildResult result;
        public ulong totalSize;
        public TimeSpan totalTime;
        public int totalErrors;
    }

    /// <summary>出包报告（形状桩）。</summary>
    public sealed class BuildReport
    {
        public BuildSummary summary = new BuildSummary();
    }
}

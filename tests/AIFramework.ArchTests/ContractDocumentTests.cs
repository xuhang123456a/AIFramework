using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace AIFramework.ArchTests
{
    /// <summary>
    /// 切片边界契约卡守卫：每个 <c>src/AIFramework.Features/Feature.*</c> 目录必须有 <c>CONTRACT.md</c>，
    /// 且内容不超过 60 行。<c>Interactions/</c> 是双边收口区，不是切片，跳过。
    /// 路径一律由 <see cref="CallerFilePathAttribute"/> 在编译期注入并回溯仓根，
    /// 严禁使用 <c>Environment.CurrentDirectory</c>（dotnet test 的工作目录在 bin/ 下）。
    /// </summary>
    public sealed class ContractDocumentTests
    {
        /// <summary>CONTRACT.md 的内容行数上限（空行不计入内容行）。</summary>
        private const int MaxContractContentLines = 60;

        private const string ContractFileName = "CONTRACT.md";
        private const string FeatureDirectoryPrefix = "Feature.";
        private const string InteractionsDirectoryName = "Interactions";

        [Fact]
        public void FeatureSliceScan_IsNotEmpty_AndSkipsInteractions()
        {
            string[] sliceDirectories = GetFeatureSliceDirectories();

            // 扫描静默失效（目录名规则变了、路径回溯错了）必须直接失败，不能 0 目录还绿。
            Assert.NotEmpty(sliceDirectories);

            foreach (string sliceDirectory in sliceDirectories)
            {
                string directoryName = Path.GetFileName(sliceDirectory);
                Assert.StartsWith(FeatureDirectoryPrefix, directoryName, StringComparison.Ordinal);
                Assert.NotEqual(InteractionsDirectoryName, directoryName);
            }
        }

        [Fact]
        public void EveryFeatureSlice_HasContractDocument()
        {
            foreach (string sliceDirectory in GetFeatureSliceDirectories())
            {
                string contractPath = Path.Combine(sliceDirectory, ContractFileName);
                Assert.True(
                    File.Exists(contractPath),
                    "切片缺少边界契约卡：" + ToRepoRelativePath(contractPath));
            }
        }

        [Fact]
        public void EveryContractDocument_StaysWithinLineBudget()
        {
            foreach (string sliceDirectory in GetFeatureSliceDirectories())
            {
                string contractPath = Path.Combine(sliceDirectory, ContractFileName);
                Assert.True(
                    File.Exists(contractPath),
                    "切片缺少边界契约卡，无法校验行数：" + ToRepoRelativePath(contractPath));

                int contentLineCount = CountContentLines(contractPath);
                Assert.True(
                    contentLineCount <= MaxContractContentLines,
                    "契约卡超出行数预算：" + ToRepoRelativePath(contractPath)
                    + " 内容行 " + contentLineCount + " > " + MaxContractContentLines);
            }
        }

        /// <summary>
        /// 编译期拿到本文件绝对路径，向上两级即仓根：tests/AIFramework.ArchTests → tests → 仓根。
        /// </summary>
        private static string RepoRoot([CallerFilePath] string callerFile = "")
        {
            string? directory = Path.GetDirectoryName(callerFile);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("无法从 CallerFilePath 得到测试源文件目录：" + callerFile);
            }

            return Path.GetFullPath(Path.Combine(directory, "..", ".."));
        }

        private static string[] GetFeatureSliceDirectories()
        {
            string featuresRoot = Path.Combine(RepoRoot(), "src", "AIFramework.Features");
            Assert.True(Directory.Exists(featuresRoot), "Features 工程目录不存在，路径回溯已失效：" + featuresRoot);

            var sliceDirectories = new List<string>();
            // 这里用「全量子目录 + 前缀过滤」而不是 Windows 通配符 "Feature.*"：
            // Win32 对 "名字.*" 这类模式的匹配语义有历史坑，前缀比较是确定行为。
            foreach (string directory in Directory.GetDirectories(featuresRoot))
            {
                if (Path.GetFileName(directory).StartsWith(FeatureDirectoryPrefix, StringComparison.Ordinal))
                {
                    sliceDirectories.Add(directory);
                }
            }

            sliceDirectories.Sort(StringComparer.Ordinal);
            return sliceDirectories.ToArray();
        }

        /// <summary>内容行 = 去掉首尾空白后非空的行；纯空行是排版分隔，不计入契约卡内容预算。</summary>
        private static int CountContentLines(string path)
        {
            int count = 0;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Trim().Length > 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static string ToRepoRelativePath(string path)
        {
            string root = RepoRoot() + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.Ordinal)
                ? path.Substring(root.Length).Replace('\\', '/')
                : path;
        }
    }
}

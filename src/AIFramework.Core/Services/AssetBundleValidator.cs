using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>分包校验报告。</summary>
    public readonly struct AssetBundleReport
    {
        /// <summary>构造报告。</summary>
        /// <param name="bundleCount">分包数。</param>
        /// <param name="assetCount">资源数。</param>
        /// <param name="totalBytes">总体积。</param>
        /// <param name="issues">问题列表。</param>
        public AssetBundleReport(int bundleCount, int assetCount, long totalBytes, AssetBundleIssue[] issues)
        {
            BundleCount = bundleCount;
            AssetCount = assetCount;
            TotalBytes = totalBytes < 0 ? 0 : totalBytes;
            Issues = issues ?? new AssetBundleIssue[0];
        }

        /// <summary>分包数。</summary>
        public int BundleCount { get; }

        /// <summary>资源数。</summary>
        public int AssetCount { get; }

        /// <summary>总体积（字节）。</summary>
        public long TotalBytes { get; }

        /// <summary>问题列表。</summary>
        public AssetBundleIssue[] Issues { get; }

        /// <summary>错误数。</summary>
        public int ErrorCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Issues.Length; i++)
                {
                    if (Issues[i].IsError)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>提示数。</summary>
        public int WarningCount
        {
            get { return Issues.Length - ErrorCount; }
        }

        /// <summary>是否通过（无错误）。</summary>
        public bool Passed
        {
            get { return ErrorCount == 0; }
        }

        /// <summary>一行报告（口径固定）。</summary>
        public string ToReportLine()
        {
            return "[Bundle] 分包=" + BundleCount + " 资源=" + AssetCount
                + " 体积=" + (TotalBytes / 1024L) + "KB 错误=" + ErrorCount + " 提示=" + WarningCount
                + " 结论=" + (Passed ? "通过" : "不通过");
        }
    }

    /// <summary>
    /// 资源分包清单校验：名字唯一、依赖存在、无循环依赖、资源不重复、体积预算、依赖深度。
    /// <para>
    /// <b>为什么这些必须在校验期拦</b>：分包问题一旦发版才暴露，代价是玩家侧资源加载失败/白屏：
    /// 依赖写错在编辑器里可能"恰好"因为全资源都在本地而看不出来，出包后才炸；循环依赖会让打包直接失败；
    /// 资源被两个包同时收录会让包体翻倍且更新时相互覆盖。
    /// </para>
    /// <para>
    /// <b>为什么不追求"自动分包"</b>：分包策略是产品/运营决策（哪些内容一起下载），
    /// 工具只负责把"策略的后果"（体积、依赖深度、资源重复）算清楚并报警。
    /// </para>
    /// </summary>
    public static class AssetBundleValidator
    {
        /// <summary>单包体积告警阈值（默认 32 MiB）。</summary>
        public const long DefaultBundleSizeWarningBytes = 32L * 1024L * 1024L;

        /// <summary>依赖深度告警阈值（超过会让首包加载链路变长）。</summary>
        public const int MaxDependencyDepth = 4;

        /// <summary>校验分包清单。</summary>
        /// <param name="bundles">分包列表。</param>
        /// <param name="bundleSizeWarningBytes">单包体积告警阈值（&lt;0 用默认值）。</param>
        /// <returns>校验报告。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bundles"/> 为 null。</exception>
        public static AssetBundleReport Validate(IReadOnlyList<AssetBundleSpec> bundles, long bundleSizeWarningBytes = DefaultBundleSizeWarningBytes)
        {
            if (bundles == null)
            {
                throw new ArgumentNullException(nameof(bundles));
            }

            var issues = new List<AssetBundleIssue>(8);
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            var assetOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            long totalBytes = 0;
            int assetCount = 0;
            long sizeThreshold = bundleSizeWarningBytes < 0 ? DefaultBundleSizeWarningBytes : bundleSizeWarningBytes;

            for (int i = 0; i < bundles.Count; i++)
            {
                AssetBundleSpec bundle = bundles[i];

                if (bundle.Name.Length == 0)
                {
                    issues.Add(new AssetBundleIssue("bundle", "(空名)", "分包名不能为空", isError: true));
                    continue;
                }

                if (names.ContainsKey(bundle.Name))
                {
                    issues.Add(new AssetBundleIssue("bundle", bundle.Name, "分包名重复", isError: true));
                    continue;
                }

                names[bundle.Name] = i;
                totalBytes += bundle.SizeBytes;

                if (bundle.Assets.Length == 0)
                {
                    issues.Add(new AssetBundleIssue("bundle", bundle.Name, "分包没有任何资源", isError: false));
                }

                if (bundle.SizeBytes > sizeThreshold)
                {
                    issues.Add(new AssetBundleIssue("size", bundle.Name,
                        "包体 " + (bundle.SizeBytes / 1024L) + "KB 超过阈值 " + (sizeThreshold / 1024L) + "KB", isError: false));
                }

                for (int a = 0; a < bundle.Assets.Length; a++)
                {
                    string asset = bundle.Assets[a];
                    if (asset.Length == 0)
                    {
                        issues.Add(new AssetBundleIssue("asset", bundle.Name, "包含空资源路径", isError: true));
                        continue;
                    }

                    assetCount++;
                    if (assetOwners.TryGetValue(asset, out string? owner))
                    {
                        issues.Add(new AssetBundleIssue("asset", asset,
                            "资源同时被打进 " + owner + " 与 " + bundle.Name + "（包体重复、更新会相互覆盖）", isError: true));
                    }
                    else
                    {
                        assetOwners[asset] = bundle.Name;
                    }
                }
            }

            // 依赖存在性 + 自依赖
            for (int i = 0; i < bundles.Count; i++)
            {
                AssetBundleSpec bundle = bundles[i];
                if (bundle.Name.Length == 0)
                {
                    continue;
                }

                for (int d = 0; d < bundle.Dependencies.Length; d++)
                {
                    string dependency = bundle.Dependencies[d];
                    if (dependency.Length == 0)
                    {
                        issues.Add(new AssetBundleIssue("dependency", bundle.Name, "包含空依赖名", isError: true));
                        continue;
                    }

                    if (string.Equals(dependency, bundle.Name, StringComparison.Ordinal))
                    {
                        issues.Add(new AssetBundleIssue("dependency", bundle.Name, "依赖了自己", isError: true));
                        continue;
                    }

                    if (!names.ContainsKey(dependency))
                    {
                        issues.Add(new AssetBundleIssue("dependency", bundle.Name,
                            "依赖的分包不存在：" + dependency, isError: true));
                    }
                }
            }

            // 循环依赖 + 深度
            for (int i = 0; i < bundles.Count; i++)
            {
                string name = bundles[i].Name;
                if (name.Length == 0 || !names.ContainsKey(name))
                {
                    continue;
                }

                var path = new List<string>(MaxDependencyDepth + 2);
                if (HasCycle(bundles, names, name, path, new HashSet<string>(StringComparer.Ordinal)))
                {
                    issues.Add(new AssetBundleIssue("dependency", name,
                        "存在循环依赖：" + string.Join(" → ", path) + " → " + name, isError: true));
                    continue;
                }

                int depth = MaxDepth(bundles, names, name, new HashSet<string>(StringComparer.Ordinal));
                if (depth > MaxDependencyDepth)
                {
                    issues.Add(new AssetBundleIssue("dependency", name,
                        "依赖深度 " + depth + " 超过 " + MaxDependencyDepth + "（首包加载链路过长）", isError: false));
                }
            }

            return new AssetBundleReport(bundles.Count, assetCount, totalBytes, issues.ToArray());
        }

        private static bool HasCycle(
            IReadOnlyList<AssetBundleSpec> bundles,
            Dictionary<string, int> names,
            string current,
            List<string> path,
            HashSet<string> visiting)
        {
            if (!names.TryGetValue(current, out int index))
            {
                return false;
            }

            if (!visiting.Add(current))
            {
                path.Add(current);
                return true;
            }

            path.Add(current);
            AssetBundleSpec bundle = bundles[index];
            for (int i = 0; i < bundle.Dependencies.Length; i++)
            {
                if (HasCycle(bundles, names, bundle.Dependencies[i], path, visiting))
                {
                    return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(current);
            return false;
        }

        private static int MaxDepth(
            IReadOnlyList<AssetBundleSpec> bundles,
            Dictionary<string, int> names,
            string current,
            HashSet<string> seen)
        {
            if (!names.TryGetValue(current, out int index) || !seen.Add(current))
            {
                return 0;
            }

            int max = 0;
            AssetBundleSpec bundle = bundles[index];
            for (int i = 0; i < bundle.Dependencies.Length; i++)
            {
                int depth = 1 + MaxDepth(bundles, names, bundle.Dependencies[i], new HashSet<string>(seen, StringComparer.Ordinal));
                if (depth > max)
                {
                    max = depth;
                }
            }

            return max;
        }
    }
}

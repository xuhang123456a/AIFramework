using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 资源分包校验：重名/重复资源/缺失依赖/循环依赖/体积与深度告警。
    /// <para>这些错误在编辑器里往往"恰好能跑"，出包才炸，所以校验口径要能被独立断言。</para>
    /// </summary>
    public sealed class AssetBundleValidatorTests
    {
        private static AssetBundleSpec Bundle(string name, string[] assets, string[]? dependencies = null, long size = 1024)
        {
            return new AssetBundleSpec(name, assets, dependencies ?? Array.Empty<string>(), size);
        }

        [Fact]
        public void Validate_CleanManifest_Passes()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("core", new[] { "a.prefab", "b.prefab" }),
                Bundle("level1", new[] { "scene1.unity" }, new[] { "core" }),
            });

            Assert.True(report.Passed);
            Assert.Equal(2, report.BundleCount);
            Assert.Equal(3, report.AssetCount);
        }

        [Fact]
        public void Validate_DuplicateBundleName_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("core", new[] { "a" }),
                Bundle("core", new[] { "b" }),
            });

            Assert.False(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Scope == "bundle" && issue.Message.Contains("重复"));
        }

        [Fact]
        public void Validate_EmptyBundleName_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[] { Bundle(string.Empty, new[] { "a" }) });

            Assert.False(report.Passed);
        }

        [Fact]
        public void Validate_DuplicateAssetAcrossBundles_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "shared.prefab" }),
                Bundle("b", new[] { "shared.prefab" }),
            });

            Assert.False(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Scope == "asset" && issue.Message.Contains("资源同时被打进"));
        }

        [Fact]
        public void Validate_EmptyAssetPath_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[] { Bundle("a", new[] { string.Empty }) });

            Assert.False(report.Passed);
        }

        [Fact]
        public void Validate_MissingDependency_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "x" }, new[] { "no-such-bundle" }),
            });

            Assert.False(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Message.Contains("不存在"));
        }

        [Fact]
        public void Validate_SelfDependency_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "x" }, new[] { "a" }),
            });

            Assert.False(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Message.Contains("依赖了自己"));
        }

        [Fact]
        public void Validate_CycleDependency_IsError()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "x" }, new[] { "b" }),
                Bundle("b", new[] { "y" }, new[] { "a" }),
            });

            Assert.False(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Message.Contains("循环依赖"));
        }

        [Fact]
        public void Validate_DeepDependency_IsWarningOnly()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("d0", new[] { "a" }),
                Bundle("d1", new[] { "b" }, new[] { "d0" }),
                Bundle("d2", new[] { "c" }, new[] { "d1" }),
                Bundle("d3", new[] { "d" }, new[] { "d2" }),
                Bundle("d4", new[] { "e" }, new[] { "d3" }),
                Bundle("d5", new[] { "f" }, new[] { "d4" }),
            });

            Assert.True(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Scope == "dependency" && !issue.IsError && issue.Message.Contains("依赖深度"));
        }

        [Fact]
        public void Validate_OversizeBundle_IsWarningOnly()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(
                new[] { Bundle("huge", new[] { "x" }, size: 64L * 1024 * 1024) },
                bundleSizeWarningBytes: 32L * 1024 * 1024);

            Assert.True(report.Passed);
            Assert.Contains(report.Issues, issue => issue.Scope == "size" && !issue.IsError);
        }

        [Fact]
        public void Validate_EmptyBundle_IsWarningOnly()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[] { Bundle("empty", Array.Empty<string>()) });

            Assert.True(report.Passed);
            Assert.Contains(report.Issues, issue => !issue.IsError && issue.Message.Contains("没有任何资源"));
        }

        [Fact]
        public void Validate_TotalBytesSums()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "x" }, size: 100),
                Bundle("b", new[] { "y" }, size: 200),
            });

            Assert.Equal(300, report.TotalBytes);
        }

        [Fact]
        public void Validate_EmptyManifest_Passes()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(Array.Empty<AssetBundleSpec>());

            Assert.True(report.Passed);
            Assert.Equal(0, report.BundleCount);
            Assert.Equal(0, report.AssetCount);
        }

        [Fact]
        public void Validate_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => AssetBundleValidator.Validate(null!));
        }

        [Fact]
        public void Report_LineHasFixedFields()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("core", new[] { "a" }),
            });

            string line = report.ToReportLine();

            Assert.StartsWith("[Bundle] 分包=1", line, StringComparison.Ordinal);
            Assert.Contains("错误=", line);
            Assert.Contains("结论=通过", line);
        }

        [Fact]
        public void Report_CountsErrorsAndWarnings()
        {
            AssetBundleReport report = AssetBundleValidator.Validate(new[]
            {
                Bundle("a", new[] { "x" }),
                Bundle("a", new[] { "y" }),
            });

            Assert.Equal(1, report.ErrorCount);
            Assert.Equal(0, report.WarningCount);
        }

        [Fact]
        public void Issue_ToString_MarksErrorAndWarning()
        {
            var error = new AssetBundleIssue("bundle", "a", "x", isError: true);
            var warning = new AssetBundleIssue("size", "a", "x", isError: false);

            Assert.StartsWith("✗", error.ToString(), StringComparison.Ordinal);
            Assert.StartsWith("⚠", warning.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Spec_DefaultsToEmptyArrays()
        {
            var spec = new AssetBundleSpec(null!, null!, null!, -5);

            Assert.Equal(string.Empty, spec.Name);
            Assert.Empty(spec.Assets);
            Assert.Empty(spec.Dependencies);
            Assert.Equal(0, spec.SizeBytes);
        }
    }
}

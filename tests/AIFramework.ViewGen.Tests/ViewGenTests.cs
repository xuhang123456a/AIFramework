using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.ViewGen;
using Xunit;

namespace AIFramework.ViewGen.Tests
{
    /// <summary>
    /// 绑定描述解析与校验：写错的描述文件必须当场带着定位信息拒绝，而不是生成半成品代码。
    /// </summary>
    public sealed class BindingSpecTests
    {
        private const string ValidJson = @"{
  ""panel"": ""SignIn"",
  ""viewNamespace"": ""AIFramework.Features.DailySignIn"",
  ""summary"": ""签到面板"",
  ""bindings"": [
    { ""kind"": ""Event"", ""name"": ""ClaimClicked"", ""summary"": ""点领取"" },
    { ""kind"": ""EventWithInt"", ""name"": ""RetroactiveClicked"", ""parameter"": ""dayIndex"" },
    { ""kind"": ""List"", ""name"": ""Slots"", ""itemType"": ""SignInSlotViewData"" }
  ]
}";

        [Fact]
        public void Parse_FillsPanelNamesAndBindings()
        {
            PanelSpec spec = BindingSpecReader.Parse(ValidJson);

            Assert.Equal("SignIn", spec.Panel);
            Assert.Equal("ISignInView", spec.InterfaceName);
            Assert.Equal("SignInViewBinder", spec.BinderName);
            Assert.Equal(3, spec.Bindings.Count);
            Assert.Equal(BindingKind.Event, spec.Bindings[0].ResolvedKind);
            Assert.Equal(BindingKind.EventWithInt, spec.Bindings[1].ResolvedKind);
            Assert.Equal(BindingKind.List, spec.Bindings[2].ResolvedKind);
            Assert.Equal("SignInSlotViewData", spec.Bindings[2].ItemType);
        }

        [Fact]
        public void Parse_KindIsCaseInsensitive()
        {
            const string json = @"{
  ""panel"": ""Shop"",
  ""viewNamespace"": ""AIFramework.Features.Shop"",
  ""bindings"": [ { ""kind"": ""text"", ""name"": ""Price"" } ]
}";

            PanelSpec spec = BindingSpecReader.Parse(json);
            Assert.Equal(BindingKind.Text, spec.Bindings[0].ResolvedKind);
        }

        [Fact]
        public void Parse_MissingPanel_Throws()
        {
            const string json = @"{ ""viewNamespace"": ""A.B"", ""bindings"": [ { ""kind"": ""Event"", ""name"": ""X"" } ] }";
            Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
        }

        [Fact]
        public void Parse_InvalidPanelIdentifier_Throws()
        {
            const string json = @"{ ""panel"": ""Sign-In"", ""viewNamespace"": ""A.B"", ""bindings"": [ { ""kind"": ""Event"", ""name"": ""X"" } ] }";
            ViewGenException ex = Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
            Assert.Contains("合法类型名", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_UnknownKind_ThrowsWithHint()
        {
            const string json = @"{ ""panel"": ""SignIn"", ""viewNamespace"": ""A.B"", ""bindings"": [ { ""kind"": ""Button"", ""name"": ""X"" } ] }";
            ViewGenException ex = Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
            Assert.Contains("未知", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Button", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DuplicateName_Throws()
        {
            const string json = @"{
  ""panel"": ""SignIn"",
  ""viewNamespace"": ""A.B"",
  ""bindings"": [
    { ""kind"": ""Event"", ""name"": ""Claim"" },
    { ""kind"": ""Text"", ""name"": ""Claim"" }
  ]
}";
            ViewGenException ex = Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
            Assert.Contains("重复", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_ListWithoutItemType_Throws()
        {
            const string json = @"{ ""panel"": ""SignIn"", ""viewNamespace"": ""A.B"", ""bindings"": [ { ""kind"": ""List"", ""name"": ""Slots"" } ] }";
            ViewGenException ex = Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
            Assert.Contains("itemType", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EmptyBindings_Throws()
        {
            const string json = @"{ ""panel"": ""SignIn"", ""viewNamespace"": ""A.B"", ""bindings"": [] }";
            Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse(json));
        }

        [Fact]
        public void Parse_MalformedJson_Throws()
        {
            Assert.Throws<ViewGenException>(() => BindingSpecReader.Parse("{ not json"));
        }
    }

    /// <summary>
    /// 生成器本身：输出必须确定、包含预期成员，并且"磁盘上的生成物 = 描述文件"。
    /// </summary>
    public sealed class ViewCodeGeneratorTests : IDisposable
    {
        private readonly string _tempDirectory;

        public ViewCodeGeneratorTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "aiframework-viewgen-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        private static PanelSpec SampleSpec()
        {
            const string json = @"{
  ""panel"": ""SignIn"",
  ""viewNamespace"": ""AIFramework.Features.DailySignIn"",
  ""summary"": ""签到面板"",
  ""bindings"": [
    { ""kind"": ""Event"", ""name"": ""ClaimClicked"", ""summary"": ""点领取"" },
    { ""kind"": ""EventWithInt"", ""name"": ""RetroactiveClicked"", ""parameter"": ""dayIndex"" },
    { ""kind"": ""List"", ""name"": ""Slots"", ""itemType"": ""SignInSlotViewData"" },
    { ""kind"": ""Text"", ""name"": ""Title"" },
    { ""kind"": ""Visible"", ""name"": ""Badge"" }
  ]
}";
            return BindingSpecReader.Parse(json);
        }

        private ViewGenOptions Options()
        {
            return new ViewGenOptions
            {
                InterfacePath = Path.Combine(_tempDirectory, "ISignInView.g.cs"),
                BinderPath = Path.Combine(_tempDirectory, "SignInViewBinder.g.cs"),
                BinderNamespace = "AIFramework.Adapters.Unity.Views",
                SourcePath = "src/demo/SignIn.bindings.json"
            };
        }

        [Fact]
        public void Interface_ContainsEveryBindingMember()
        {
            string code = ViewCodeGenerator.GenerateInterface(SampleSpec(), "src/demo/SignIn.bindings.json");

            Assert.Contains("public interface ISignInView", code, StringComparison.Ordinal);
            Assert.Contains("event Action? OnClaimClicked;", code, StringComparison.Ordinal);
            Assert.Contains("event Action<int>? OnRetroactiveClicked;", code, StringComparison.Ordinal);
            Assert.Contains("void RenderSlots(IReadOnlyList<SignInSlotViewData> slots);", code, StringComparison.Ordinal);
            Assert.Contains("void RenderTitle(string text);", code, StringComparison.Ordinal);
            Assert.Contains("void RenderBadge(bool visible);", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Interface_IsDeterministicAndCarriesHeader()
        {
            string first = ViewCodeGenerator.GenerateInterface(SampleSpec(), "src/demo/SignIn.bindings.json");
            string second = ViewCodeGenerator.GenerateInterface(SampleSpec(), "src/demo/SignIn.bindings.json");

            Assert.Equal(first, second);
            Assert.Contains("// <auto-generated>", first, StringComparison.Ordinal);
            Assert.Contains("tools/viewgen", first, StringComparison.Ordinal);
            Assert.Contains("src/demo/SignIn.bindings.json", first, StringComparison.Ordinal);
        }

        [Fact]
        public void Binder_ContainsRaisersPoolAndPartialHooks()
        {
            string code = ViewCodeGenerator.GenerateBinder(SampleSpec(), Options());

            Assert.Contains("public sealed partial class SignInViewBinder : MonoBehaviour, ISignInView", code, StringComparison.Ordinal);
            Assert.Contains("public void RaiseClaimClicked()", code, StringComparison.Ordinal);
            Assert.Contains("public void RaiseRetroactiveClicked(int dayIndex)", code, StringComparison.Ordinal);
            Assert.Contains("partial void OnBindSlotsItem(GameObject item, SignInSlotViewData data);", code, StringComparison.Ordinal);
            Assert.Contains("partial void OnRenderTitleText(string text);", code, StringComparison.Ordinal);
            Assert.Contains("partial void OnRenderBadgeVisible(bool visible);", code, StringComparison.Ordinal);
            Assert.Contains("EnsureSlotsItems", code, StringComparison.Ordinal);
            Assert.Contains("[SerializeField] private Transform slotsContent = null!;", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Binder_DoesNotEmitBusinessBranches()
        {
            string code = ViewCodeGenerator.GenerateBinder(SampleSpec(), Options());

            // 皮肤层不允许出现业务判断：if 只可能来自"显隐/池化"这类纯展示逻辑，
            // 这里直接钉死"没有 if 包住业务语义"的写法代价太高，改为钉死两类最容易渗漏的关键字。
            Assert.DoesNotContain("ClaimDailySignInCommand", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ICqeBus", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Emit_WritesBothFiles()
        {
            ViewGenOptions options = Options();
            IReadOnlyList<string> written = ViewCodeGenerator.Emit(SampleSpec(), options);

            Assert.Equal(2, written.Count);
            Assert.True(File.Exists(options.InterfacePath));
            Assert.True(File.Exists(options.BinderPath));
            Assert.Contains("public interface ISignInView", File.ReadAllText(options.InterfacePath), StringComparison.Ordinal);
            Assert.Contains("SignInViewBinder", File.ReadAllText(options.BinderPath), StringComparison.Ordinal);
        }

        [Fact]
        public void FindStale_IsEmptyRightAfterEmit()
        {
            ViewGenOptions options = Options();
            ViewCodeGenerator.Emit(SampleSpec(), options);

            Assert.Empty(ViewCodeGenerator.FindStale(SampleSpec(), options));
        }

        [Fact]
        public void FindStale_DetectsHandEdit()
        {
            ViewGenOptions options = Options();
            ViewCodeGenerator.Emit(SampleSpec(), options);

            File.AppendAllText(options.InterfacePath, "// 手改一行\n");

            IReadOnlyList<string> stale = ViewCodeGenerator.FindStale(SampleSpec(), options);
            Assert.Single(stale);
            Assert.Equal(options.InterfacePath, stale[0]);
        }

        [Fact]
        public void FindStale_TreatsCrlfAsEqual()
        {
            ViewGenOptions options = Options();
            GenerationResult generated = ViewCodeGenerator.Generate(SampleSpec(), options);

            File.WriteAllText(options.InterfacePath, generated.InterfaceContent.Replace("\n", "\r\n"));
            File.WriteAllText(options.BinderPath, generated.BinderContent.Replace("\n", "\r\n"));

            Assert.Empty(ViewCodeGenerator.FindStale(SampleSpec(), options));
        }
    }

    /// <summary>
    /// 仓库金标准：真实描述文件与已提交的生成物必须一致。
    /// <para>这条用例替代"CI 记得跑 --check"：`dotnet test` 一跑就会发现有人手改 .g.cs 或改描述忘了重新生成。</para>
    /// </summary>
    public sealed class RepoGeneratedCodeTests
    {
        private const string BindingSpecRelativePath = "src/AIFramework.Features/Feature.DailySignIn/SignIn.bindings.json";

        [Fact]
        public void SignInBindings_GeneratedCodeIsUpToDate()
        {
            string repoRoot = FindRepoRoot();
            string specPath = Path.Combine(repoRoot, BindingSpecRelativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(specPath), "找不到绑定描述：" + specPath);

            PanelSpec spec = BindingSpecReader.Parse(File.ReadAllText(specPath));

            var options = new ViewGenOptions
            {
                InterfacePath = Path.Combine(
                    repoRoot, "src", "AIFramework.Features", "Feature.DailySignIn", "I" + spec.Panel + "View.g.cs"),
                BinderPath = Path.Combine(
                    repoRoot, "src", "Adapters", "AIFramework.Adapters.Unity", "Runtime", "Views", spec.BinderName + ".g.cs"),
                BinderNamespace = "AIFramework.Adapters.Unity.Views",
                SourcePath = BindingSpecRelativePath
            };

            IReadOnlyList<string> stale = ViewCodeGenerator.FindStale(spec, options);

            Assert.True(
                stale.Count == 0,
                "生成物与描述不一致，请执行 dotnet run --project tools/viewgen -- " + BindingSpecRelativePath
                + Environment.NewLine + "过期文件：" + string.Join(", ", stale));
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.ViewGen.Tests/RepoGeneratedCodeTests.cs 向上两级
            DirectoryInfo? directory = new FileInfo(callerFilePath).Directory;
            for (int i = 0; i < 2 && directory != null; i++)
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}

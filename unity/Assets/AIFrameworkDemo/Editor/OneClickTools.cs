#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 编辑器一键工具：把"生成全部场景 → 装配校验 → Build Settings 检查 → StreamingAssets 表检查"
    /// 串成一次点击，并把结果汇总成一段可复制的纯文本报告。
    /// <para>
    /// <b>为什么要有它</b>：场景全部由脚本生成，一次完整验收要依次点四个菜单
    /// （<c>生成全部场景</c> → <c>校验场景装配</c> → <c>把场景加入 Build Settings</c> → 手工确认 14 张表）。
    /// 顺序错一步或漏看一眼，现象都是"按 Play 没反应 / 跳转失败"，而且只有真机才看得见。
    /// 这里用一次点击固定顺序执行，并保证<b>单步失败不中断后续收集</b>——
    /// 生成炸了也照样把 Build Settings 与 StreamingAssets 的问题一次性列全，免得修一个跑一次。
    /// </para>
    /// <para>
    /// <b>为什么报告要能复制</b>：Unity 控制台条目多、跨域时不方便截屏。报告是一段纯文本，
    /// 搜索 <c>[OneClick]</c> 就能整段复制进 issue / 聊天窗口。
    /// </para>
    /// <para>
    /// <b>纯编辑器工具</b>：不引入任何运行时依赖，不进包体；只被 <c>AIFramework.Demo.Editor</c> 程序集编译。
    /// </para>
    /// </summary>
    public static class OneClickTools
    {
        /// <summary>场景目录（与各 SceneBuilder / <see cref="BootSceneBuilder"/> 保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>启动场景路径：Build Settings 索引 0 必须是它，否则发包后首帧没有可加载场景。</summary>
        private const string BootScenePath = SceneFolder + "/BootScene.unity";

        /// <summary>配置表二进制目录（StreamingAssets 下按子目录隔离，避免与未来其它资产混放）。</summary>
        private const string StreamingAssetFolder = "Assets/StreamingAssets/AIFrameworkDemo";

        /// <summary>场景总数：21 个（20 个玩法/演示场景 + Boot）。</summary>
        private const int ExpectedSceneCount = 21;

        /// <summary>日志/报告统一前缀：控制台搜索它即可把一次运行的输出整段捞出来。</summary>
        private const string LogPrefix = "[OneClick] ";

        /// <summary>报告首行标题。</summary>
        private const string ReportHeader = "AIFramework 编辑器自检报告";

        /// <summary>弹窗里最多列几条失败：<see cref="EditorUtility.DisplayDialog"/> 的窗口不可滚动，列多了会被截断。</summary>
        private const int MaxDialogFailureLines = 6;

        /// <summary>
        /// 14 张配置表文件名，与 <c>unity/Assets/StreamingAssets/AIFrameworkDemo</c> 下的实际文件一一对应。
        /// <para>
        /// <b>为什么写死名字而不是扫目录</b>：这个检查的目的是"该有的都在"，
        /// 扫目录只能回答"有多少张"，回答不了"缺的是哪一张"；缺表和空表正是这里要抓的现象。
        /// </para>
        /// </summary>
        private static readonly string[] ExpectedBinaryTables =
        {
            "TbBuilding.bin",
            "TbCard.bin",
            "TbGachaPool.bin",
            "TbIdleStage.bin",
            "TbLocalization.bin",
            "TbMail.bin",
            "TbMergeItem.bin",
            "TbQuest.bin",
            "TbShopItem.bin",
            "TbSignInReward.bin",
            "TbTower.bin",
            "TbTycoonBuilding.bin",
            "TbAchievement.bin",
            "TbSeasonPass.bin"
        };

        /// <summary>
        /// 一键校验全部：生成 21 个场景 + 装配校验 + Build Settings 检查 + StreamingAssets 表检查，
        /// 最后把通过项 / 失败项 / 警告项汇总成一条可复制的报告（弹窗给摘要，控制台给全文）。
        /// <para>
        /// 全部通过才打开 <c>BootScene.unity</c> 并退出 Play 模式；有失败项时保持现场不动，
        /// 免得掩盖"当前打开的场景被换掉"这一现象。
        /// </para>
        /// </summary>
        [MenuItem("AIFramework/一键校验全部", priority = 40)]
        public static void RunAll()
        {
            Debug.Log(LogPrefix + "开始一键校验：生成场景 → 装配校验 → Build Settings → StreamingAssets。");

            var report = new CheckReport();

            RunGenerateStep(report);
            RunVerifyStep(report);
            CheckBuildSettings(report);
            CheckStreamingAssetsTables(report, logEachTable: false);

            // 判定要在"打开 BootScene"之前做：打开失败本身也算一项失败，不能反过来影响是否该打开。
            if (report.AllPassed)
            {
                OpenBootScene(report);
            }
            else
            {
                report.Warnings.Add("存在失败项，已跳过打开 BootScene（修好后重跑本菜单）。");
            }

            Publish(report, "AIFramework 一键校验", showDialog: true);
        }

        /// <summary>只重跑第 1 步：生成全部场景与 Build Settings（局部返工时用，比跑全量快）。</summary>
        [MenuItem("AIFramework/工具/重新生成全部场景", priority = 41)]
        public static void RegenerateAllScenes()
        {
            Debug.Log(LogPrefix + "只跑第 1 步：生成全部场景。");

            var report = new CheckReport();
            RunGenerateStep(report);
            Publish(report, "生成全部场景", showDialog: false);
        }

        /// <summary>只跑第 2 步：装配校验，并把结果（含逐条失败原因）打印出来。</summary>
        [MenuItem("AIFramework/工具/只跑装配校验", priority = 42)]
        public static void VerifyAssemblyOnly()
        {
            Debug.Log(LogPrefix + "只跑第 2 步：装配校验。");

            var report = new CheckReport();
            RunVerifyStep(report);
            Publish(report, "装配校验", showDialog: false);
        }

        /// <summary>
        /// 只跑第 4 步：检查 StreamingAssets 下的 14 张表，<b>打印每张表的字节数</b>
        /// （空表在二进制层面和缺表一样致命，字节数是最直接的证据）。
        /// </summary>
        [MenuItem("AIFramework/工具/检查 StreamingAssets 表", priority = 43)]
        public static void CheckStreamingTablesOnly()
        {
            Debug.Log(LogPrefix + "只跑第 4 步：检查 StreamingAssets 表（逐张打印字节数）。");

            var report = new CheckReport();
            CheckStreamingAssetsTables(report, logEachTable: true);
            Publish(report, "StreamingAssets 表检查", showDialog: false);
        }

        // ------------------------------------------------------------------ 各步骤

        /// <summary>第 1 步：生成全部场景与 Build Settings。</summary>
        /// <param name="report">汇总容器（只追加，不抛出）。</param>
        private static void RunGenerateStep(CheckReport report)
        {
            try
            {
                GenerateAllScenes.Generate();
                report.Passed.Add("生成全部场景：GenerateAllScenes.Generate() 执行完成（"
                    + ExpectedSceneCount + " 个场景 + Build Settings）。");
            }
            catch (Exception ex)
            {
                // Generate() 末尾自己会调一次 SceneVerifier.VerifyAll()，所以"生成成功但校验失败"
                // 也会从这里抛出来。下一步会给出逐条明细，这里只记异常本身。
                report.Failed.Add("生成全部场景：" + ex.GetType().Name + "：" + SingleLine(ex.Message)
                    + "（注意：Generate() 内部末尾自带一次装配校验，校验失败也会从这一步抛出，明细见下一项）。");
            }
        }

        /// <summary>
        /// 第 2 步：装配校验。<see cref="SceneVerifier.VerifyAll"/> 失败时抛
        /// <see cref="InvalidOperationException"/>，其 message 是"标题 + 每条一行 <c>  - xxx</c>"的结构，
        /// 这里按行拆开还原成逐条失败项，便于报告里一眼看出坏在哪。
        /// </summary>
        /// <param name="report">汇总容器（只追加，不抛出）。</param>
        private static void RunVerifyStep(CheckReport report)
        {
            try
            {
                SceneVerifier.VerifyAll();
                report.Passed.Add("装配校验：SceneVerifier.VerifyAll() 通过（组件齐备、[SerializeField] 注入非空）。");
            }
            catch (Exception ex)
            {
                string[] lines = (ex.Message ?? string.Empty).Split('\n');
                int added = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!line.StartsWith("- ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    report.Failed.Add("装配校验：" + line.Substring(2).Trim());
                    added++;
                }

                if (added == 0)
                {
                    // 不是预期格式（例如反射/IO 异常）：整条消息当一条失败，别把信息丢掉。
                    report.Failed.Add("装配校验：" + ex.GetType().Name + "：" + SingleLine(ex.Message));
                }
            }
        }

        /// <summary>
        /// 第 3 步：检查 Build Settings 的三条硬性条件——场景数 ≥ 21、索引 0 必须是 <c>BootScene.unity</c>、无重复路径。
        /// <para>
        /// 另外把"未勾选 / 指向不存在的文件 / 空路径"记为<b>警告</b>而不计入失败：
        /// 它们不影响本菜单的判定语义，但都会让打包后在真机上才炸，值得提前看见。
        /// </para>
        /// </summary>
        /// <param name="report">汇总容器（只追加，不抛出）。</param>
        private static void CheckBuildSettings(CheckReport report)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes == null)
            {
                scenes = Array.Empty<EditorBuildSettingsScene>();
            }

            // 1) 数量
            if (scenes.Length >= ExpectedSceneCount)
            {
                report.Passed.Add("Build Settings 场景数：" + scenes.Length + " ≥ " + ExpectedSceneCount + "。");
            }
            else
            {
                report.Failed.Add("Build Settings 场景数不足：" + scenes.Length + " < " + ExpectedSceneCount
                    + "（可能场景文件没生成全，或菜单 AIFramework/把场景加入 Build Settings 没跑）。");
            }

            // 2) 索引 0 必须是 BootScene（发包后的启动场景）
            string first = scenes.Length > 0 ? NormalizeAssetPath(scenes[0].path) : string.Empty;
            if (scenes.Length > 0 && string.Equals(first, BootScenePath, StringComparison.Ordinal))
            {
                report.Passed.Add("Build Settings[0] = " + BootScenePath + "（索引 0 即启动场景）。");
            }
            else
            {
                report.Failed.Add("Build Settings[0] 不是 " + BootScenePath + "，实际："
                    + (scenes.Length > 0 ? first : "(列表为空)") + "。");
            }

            // 3) 无重复路径（Windows 文件系统不区分大小写，这里也按不区分比对）
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            for (int i = 0; i < scenes.Length; i++)
            {
                string path = NormalizeAssetPath(scenes[i].path);
                if (path.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(path))
                {
                    duplicates.Add(path);
                }
            }

            if (duplicates.Count == 0)
            {
                report.Passed.Add("Build Settings 无重复场景路径（" + seen.Count + " 条唯一路径）。");
            }
            else
            {
                report.Failed.Add("Build Settings 存在重复场景路径：" + string.Join("、", duplicates) + "。");
            }

            // 附加观察项：只记警告，不改变 AllPassed。
            for (int i = 0; i < scenes.Length; i++)
            {
                string path = NormalizeAssetPath(scenes[i].path);
                if (path.Length == 0)
                {
                    report.Warnings.Add("Build Settings[" + i + "] 路径为空。");
                    continue;
                }

                if (!scenes[i].enabled)
                {
                    report.Warnings.Add("Build Settings[" + i + "] 未勾选（打包不会包含）：" + path);
                }

                if (!File.Exists(ToAbsolutePath(path)))
                {
                    report.Warnings.Add("Build Settings[" + i + "] 指向不存在的文件：" + path);
                }
            }
        }

        /// <summary>
        /// 第 4 步：检查 StreamingAssets 下 14 张 <c>.bin</c> 是否存在且非空。
        /// </summary>
        /// <param name="report">汇总容器（只追加，不抛出）。</param>
        /// <param name="logEachTable">是否逐张打印字节数（单项菜单要，全量一键校验时只在报告里给区间）。</param>
        private static void CheckStreamingAssetsTables(CheckReport report, bool logEachTable)
        {
            string folder = ToAbsolutePath(StreamingAssetFolder);
            if (!Directory.Exists(folder))
            {
                report.Failed.Add("StreamingAssets 表目录不存在：" + StreamingAssetFolder + "（物理路径 " + folder + "）。");
                return;
            }

            int ok = 0;
            long minSize = long.MaxValue;
            long maxSize = 0;

            for (int i = 0; i < ExpectedBinaryTables.Length; i++)
            {
                string name = ExpectedBinaryTables[i];
                string assetPath = StreamingAssetFolder + "/" + name;
                string physicalPath = Path.Combine(folder, name);

                if (!File.Exists(physicalPath))
                {
                    report.Failed.Add("StreamingAssets 缺表：" + assetPath + "（未导出或文件名不符）。");
                    continue;
                }

                long size = new FileInfo(physicalPath).Length;
                if (size <= 0)
                {
                    report.Failed.Add("StreamingAssets 空表：" + assetPath + "（0 字节，加载必失败）。");
                    continue;
                }

                ok++;
                if (size < minSize)
                {
                    minSize = size;
                }

                if (size > maxSize)
                {
                    maxSize = size;
                }

                if (logEachTable)
                {
                    Debug.Log(LogPrefix + assetPath + " = " + size + " 字节。");
                }
            }

            if (ok == ExpectedBinaryTables.Length)
            {
                report.Passed.Add("StreamingAssets：" + ok + " 张 .bin 全部存在且非空（"
                    + minSize + "~" + maxSize + " 字节）。");
            }
            else
            {
                // 逐张的缺失/空表已记进失败项，这里补一条总账，让报告里能看出"到了几张"。
                report.Failed.Add("StreamingAssets：应到 " + ExpectedBinaryTables.Length
                    + " 张 .bin，实到 " + ok + " 张。");
            }

            string[] unexpected = ListUnexpectedTables(folder);
            if (unexpected.Length > 0)
            {
                report.Warnings.Add("StreamingAssets 目录存在清单外的 .bin：" + string.Join("、", unexpected)
                    + "（清单 ExpectedBinaryTables 可能需要同步）。");
            }
        }

        /// <summary>全部通过后的收尾：打开 <c>BootScene.unity</c> 并确保不在 Play 模式。</summary>
        /// <param name="report">汇总容器（只追加，不抛出）。</param>
        private static void OpenBootScene(CheckReport report)
        {
            // 正在 Play 模式下切场景，不同 Unity 版本行为不一致（可能直接拒绝）。
            // 先请求退出 Play，再尝试打开；真落不下去时下面的 catch 会把原因写进失败项。
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                report.Warnings.Add("执行时处于 Play 模式：已请求退出 Play，本次仍尝试打开 BootScene（若失败请退出后重跑）。");
            }

            try
            {
                EditorSceneManager.OpenScene(BootScenePath);
                EditorApplication.isPlaying = false;
                report.Passed.Add("已打开 " + BootScenePath + "，并确认未处于 Play 模式。");
            }
            catch (Exception ex)
            {
                report.Failed.Add("打开 BootScene 失败：" + ex.GetType().Name + "：" + SingleLine(ex.Message));
            }
        }

        // ------------------------------------------------------------------ 输出

        /// <summary>把报告写到控制台（失败/警告逐条单独打印以便高亮），并按需弹窗。</summary>
        /// <param name="report">汇总结果。</param>
        /// <param name="dialogTitle">弹窗标题。</param>
        /// <param name="showDialog">是否弹窗：一键校验要弹（人可能没盯控制台），单项重跑只打印。</param>
        private static void Publish(CheckReport report, string dialogTitle, bool showDialog)
        {
            // 先逐条打失败/警告：Unity 控制台会给它们错误/警告图标，CI 也能按条数统计。
            for (int i = 0; i < report.Failed.Count; i++)
            {
                Debug.LogError(LogPrefix + report.Failed[i]);
            }

            for (int i = 0; i < report.Warnings.Count; i++)
            {
                Debug.LogWarning(LogPrefix + report.Warnings[i]);
            }

            // 再打一整段完整报告：单条消息、带统一前缀，搜索 [OneClick] 可整段复制。
            Debug.Log(LogPrefix + report.ToText());

            if (showDialog && !IsBatchMode)
            {
                EditorUtility.DisplayDialog(dialogTitle, BuildDialogSummary(report), "好");
            }
            else if (showDialog)
            {
                // 批处理下弹模态框会等人点，CI 直接挂死；报告已经在上面输出，这里只是补一句说明。
                Debug.Log(LogPrefix + "批处理模式：跳过弹窗，完整报告见上方。");
            }
        }

        /// <summary>
        /// 是否运行在批处理模式（<c>Unity.exe -batchmode -executeMethod ...</c>）。
        /// <para>
        /// 用 <see cref="Environment.CommandLine"/> 判断而不是 <c>Application.isBatchMode</c>：
        /// 后者要 UnityEngine 侧的形状桩配合，而命令行参数从 Unity 5.x 起就没变过，
        /// 编辑器脚本读它既准确又不额外扩大桩面。
        /// </para>
        /// </summary>
        private static bool IsBatchMode
        {
            get
            {
                string commandLine = Environment.CommandLine;
                return commandLine != null
                    && commandLine.IndexOf("-batchmode", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>弹窗摘要：只给计数、结论与前几条失败（窗口不可滚动，必须短）。</summary>
        /// <param name="report">汇总结果。</param>
        /// <returns>可直接塞进 <see cref="EditorUtility.DisplayDialog"/> 的短文本。</returns>
        private static string BuildDialogSummary(CheckReport report)
        {
            var summary = new StringBuilder(256);
            summary.Append("通过 ").Append(report.Passed.Count)
                .Append(" 项 / 失败 ").Append(report.Failed.Count)
                .Append(" 项 / 警告 ").Append(report.Warnings.Count).Append(" 项\n");
            summary.Append(report.AllPassed ? "结论：全部通过。\n" : "结论：存在失败项。\n");

            int shown = 0;
            while (shown < report.Failed.Count && shown < MaxDialogFailureLines)
            {
                summary.Append("- ").Append(report.Failed[shown]).Append('\n');
                shown++;
            }

            if (report.Failed.Count > shown)
            {
                summary.Append("… 另有 ").Append(report.Failed.Count - shown).Append(" 项失败。\n");
            }

            summary.Append("\n完整报告已输出到控制台，搜索 [OneClick] 即可复制。");
            return summary.ToString();
        }

        // ------------------------------------------------------------------ 工具方法

        /// <summary>资产相对路径 → 磁盘绝对路径。</summary>
        /// <param name="assetRelativePath">以 <c>Assets/</c> 开头的资产路径。</param>
        /// <returns>绝对路径；无法定位时退回相对路径（编辑器当前目录即工程根）。</returns>
        private static string ToAbsolutePath(string assetRelativePath)
        {
            const string AssetsPrefix = "Assets/";
            string relative = assetRelativePath.StartsWith(AssetsPrefix, StringComparison.Ordinal)
                ? assetRelativePath.Substring(AssetsPrefix.Length)
                : assetRelativePath;

            // 编辑器里 Application.dataPath 就是 <工程>/Assets，因此不用再猜工程根目录
            // （BootSceneBuilder 直接用相对路径，前提是进程当前目录为工程根；这里不依赖那个前提）。
            string dataPath = Application.dataPath;
            if (dataPath == null || dataPath.Length == 0)
            {
                return relative;
            }

            return Path.Combine(dataPath, relative);
        }

        /// <summary>统一路径写法：反斜杠转正斜杠并去空白（Build Settings 里理论上只存正斜杠，但不写死假设）。</summary>
        /// <param name="assetPath">原始路径。</param>
        /// <returns>规范化后的路径；空输入返回空串。</returns>
        private static string NormalizeAssetPath(string assetPath)
        {
            if (assetPath == null || assetPath.Length == 0)
            {
                return string.Empty;
            }

            return assetPath.Replace('\\', '/').Trim();
        }

        /// <summary>列出目录里不在清单内的 <c>.bin</c>（清单过期或有人手工塞表时用来提醒）。</summary>
        /// <param name="absoluteFolder">目录绝对路径。</param>
        /// <returns>多余文件名（不含路径）；读取失败时返回空数组。</returns>
        private static string[] ListUnexpectedTables(string absoluteFolder)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(absoluteFolder, "*.bin", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                // 目录不可读不该让整份报告失败：逐张检查已经覆盖了"该有的在不在"。
                return Array.Empty<string>();
            }

            var unexpected = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (!IsExpectedTable(name))
                {
                    unexpected.Add(name);
                }
            }

            return unexpected.ToArray();
        }

        /// <summary>文件名是否在预期清单内。</summary>
        /// <param name="fileName">不含路径的文件名。</param>
        /// <returns>在清单内返回 <c>true</c>。</returns>
        private static bool IsExpectedTable(string fileName)
        {
            for (int i = 0; i < ExpectedBinaryTables.Length; i++)
            {
                if (string.Equals(ExpectedBinaryTables[i], fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>把多行异常消息压成一行（报告的一行只放一条失败，换行会破坏排版）。</summary>
        /// <param name="text">原始文本。</param>
        /// <returns>单行文本。</returns>
        private static string SingleLine(string? text)
        {
            if (text == null || text.Length == 0)
            {
                return string.Empty;
            }

            return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // ------------------------------------------------------------------ 汇总结构

        /// <summary>
        /// 一次校验的汇总结果：通过项 / 失败项 / 警告项，以及"能否整段复制"的纯文本报告。
        /// <para>
        /// <b>为什么用三个 List 而不是一个带状态的列表</b>：报告要分节排版，
        /// 而且"警告"与"失败"的判定语义不同（警告不影响 <see cref="AllPassed"/>），
        /// 分开放可以从结构上杜绝两者混在一起。
        /// </para>
        /// </summary>
        private sealed class CheckReport
        {
            public CheckReport()
            {
                Passed = new List<string>();
                Failed = new List<string>();
                Warnings = new List<string>();
            }

            /// <summary>通过的检查项（每条一句话，含实测值）。</summary>
            public List<string> Passed { get; }

            /// <summary>失败的检查项（会打印为错误日志）。</summary>
            public List<string> Failed { get; }

            /// <summary>警告项（不参与通过判定，但会让真机出问题）。</summary>
            public List<string> Warnings { get; }

            /// <summary>是否全部通过：只要有一条失败就为 <c>false</c>。</summary>
            public bool AllPassed
            {
                get { return Failed.Count == 0; }
            }

            /// <summary>拼成一段可复制的纯文本报告（LF 换行，粘进聊天/issue 都不会串行）。</summary>
            /// <returns>报告正文。</returns>
            public string ToText()
            {
                var text = new StringBuilder(1024);

                // 时间戳只服务于"这份报告是哪次跑出来的"：本类位于 AIFramework.Demo.Editor，
                // 不在 AIF003（纯逻辑程序集禁 DateTime.Now）的管辖范围。
                text.Append(ReportHeader)
                    .Append("（")
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("）\n");

                text.Append("结果：通过 ").Append(Passed.Count)
                    .Append(" 项 / 失败 ").Append(Failed.Count)
                    .Append(" 项 / 警告 ").Append(Warnings.Count)
                    .Append(AllPassed ? " 项 → 全部通过\n" : " 项 → 存在失败项\n");

                AppendSection(text, "通过项", Passed);
                AppendSection(text, "失败项", Failed);
                AppendSection(text, "警告项", Warnings);

                return text.ToString();
            }

            /// <summary>追加一节（空节也保留标题并写"（无）"，这样报告结构恒定、便于人眼比对）。</summary>
            /// <param name="text">目标缓冲。</param>
            /// <param name="title">节标题。</param>
            /// <param name="items">条目。</param>
            private static void AppendSection(StringBuilder text, string title, List<string> items)
            {
                text.Append("\n【").Append(title).Append("】(").Append(items.Count).Append(")\n");

                if (items.Count == 0)
                {
                    text.Append("  （无）\n");
                    return;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    text.Append("  ").Append(i + 1).Append(". ").Append(items[i]).Append('\n');
                }
            }
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Adapters.Unity.Battle;
using UnityEditor;
using UnityEngine;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 推箱子关卡网格编辑器：在编辑器里点格子画关卡，导出成 <see cref="PuzzleLevelJson"/> 定义的
    /// JSON 文件（<c>Assets/Resources/Levels/puzzle-&lt;id&gt;.json</c>）。
    /// <para>
    /// <b>为什么用 <see cref="EditorWindow"/> + OnGUI 而不是 UI Toolkit / Custom Editor</b>：
    /// 本工程所有编辑器工具都是 IMGUI 脚本（<c>UIBindExporter</c> / <c>OneClickTools</c>），
    /// 同一套范式便于 AI 批量改；而且网格编辑器只有"画格子 + 四个按钮"，UXML 带来的资产与
    /// 命名调试成本远高于收益。
    /// </para>
    /// <para>
    /// <b>为什么格式代码一行都不在本文件里</b>：解析、校验、序列化全部委托给
    /// <see cref="PuzzleLevelJson"/>（适配层 Runtime，纯 <see cref="string"/> 进出）。
    /// 编辑器与运行时共用同一份实现，"编辑器放行、运行时炸"这类分叉从结构上被排除；
    /// 本文件只负责网格状态、点击循环、EditorPrefs 与文件落盘。
    /// </para>
    /// <para>
    /// <b>状态为什么进 <see cref="EditorPrefs"/></b>：关卡是手工画出来的，窗口一关就丢等于逼人一次画完；
    /// 用 EditorPrefs 而不是 <c>ScriptableObject</c> 资产，是为了不往仓库塞中间态垃圾文件——
    /// 真正的产物只有导出的 JSON。
    /// </para>
    /// <para>
    /// <b>离线验不到的部分</b>：本类只在 Unity 编辑器里运行，<c>unity/tools/check-adapter-compile.ps1</c>
    /// 只能证明它对 <c>EditorWindow/GUILayout/EditorPrefs</c> 的形状调用正确；
    /// 窗口观感、点击循环手感、EditorPrefs 跨会话、导出后 <c>AssetDatabase.Refresh</c> 的表现必须真机看。
    /// </para>
    /// </summary>
    public sealed class GridLevelEditorWindow : EditorWindow
    {
        /// <summary>菜单路径（与 OneClickTools 的 priority 段错开：60 留给本工具）。</summary>
        private const string MenuPath = "AIFramework/工具/推箱子关卡编辑器";

        /// <summary>日志前缀：控制台搜它能把一次操作整段捞出来。</summary>
        private const string LogPrefix = "[GridLevelEditor] ";

        /// <summary>导出目录（资产相对路径）。</summary>
        private const string ExportFolderAsset = "Assets/Resources/Levels";

        /// <summary>默认网格边长（需求锁死 8×8）。</summary>
        private const int DefaultSide = 8;

        /// <summary>格子按钮边长（像素）。8 格 + 边距在 320 宽窗口内放得下。</summary>
        private const float CellSize = 32f;

        /// <summary>窗口最小尺寸（像素）。</summary>
        private const float MinWindowWidth = 340f;

        /// <summary>窗口最小高度（像素）：标题 + 两行工具 + 8×32 网格 + 图例 + 报告区。</summary>
        private const float MinWindowHeight = 460f;

        /// <summary>弹窗里最多列几条失败：<see cref="EditorUtility.DisplayDialog"/> 不可滚动。</summary>
        private const int MaxDialogErrorLines = 6;

        /// <summary>EditorPrefs：关卡 id。</summary>
        private const string PrefId = "AIFramework.GridLevelEditor.Id";

        /// <summary>EditorPrefs：网格宽。</summary>
        private const string PrefWidth = "AIFramework.GridLevelEditor.Width";

        /// <summary>EditorPrefs：网格高。</summary>
        private const string PrefHeight = "AIFramework.GridLevelEditor.Height";

        /// <summary>EditorPrefs：行优先字符网格（顶部在前）。</summary>
        private const string PrefTiles = "AIFramework.GridLevelEditor.Tiles";

        /// <summary>关卡 id（完整形态，例如 <c>puzzle-demo</c>）。</summary>
        private string _id = PuzzleLevelJson.DemoId;

        /// <summary>当前网格宽（1..8）。</summary>
        private int _width = DefaultSide;

        /// <summary>当前网格高（1..8）。</summary>
        private int _height = DefaultSide;

        /// <summary>行优先字符网格，第 0 行是<b>顶部</b>（与 JSON <c>tiles</c> 同序）。</summary>
        private char[] _tiles = CreateBlankTiles(DefaultSide, DefaultSide);

        /// <summary>最近一次操作/校验的完整报告（换行分隔），显示在窗口底部。</summary>
        private string _report = string.Empty;

        /// <summary>报告级别：通过=Info，失败=Error。</summary>
        private MessageType _reportType = MessageType.None;

        /// <summary>打开窗口（菜单入口）。</summary>
        [MenuItem(MenuPath, priority = 60)]
        public static void Open()
        {
            GridLevelEditorWindow window = EditorWindow.GetWindow<GridLevelEditorWindow>("推箱子关卡编辑器");
            window.minSize = new Vector2(MinWindowWidth, MinWindowHeight);
            window.Show();
        }

        /// <summary>
        /// 窗口启用时从 <see cref="EditorPrefs"/> 恢复状态。
        /// <b>为什么恢复失败要静默退回空白画布</b>：偏好数据可能来自旧版本（尺寸或字符集变了），
        /// 为此弹窗打断人没有意义——画布本身就是可覆盖的中间态。
        /// </summary>
        private void OnEnable()
        {
            _id = PuzzleLevelJson.NormalizeId(EditorPrefs.GetString(PrefId, PuzzleLevelJson.DemoId));
            _width = ClampSide(EditorPrefs.GetInt(PrefWidth, DefaultSide));
            _height = ClampSide(EditorPrefs.GetInt(PrefHeight, DefaultSide));

            char[]? restored = DecodeTiles(EditorPrefs.GetString(PrefTiles, string.Empty), _width, _height);
            _tiles = restored ?? CreateBlankTiles(_width, _height);
            _report = string.Empty;
            _reportType = MessageType.None;
        }

        /// <summary>窗口关闭时落一次盘（点击时也会存，这里兜住"改了 id 但没点格子就关窗"）。</summary>
        private void OnDisable()
        {
            SaveState();
        }

        /// <summary>画界面：标题 → 工具条 → 网格 → 图例 → 报告。</summary>
        private void OnGUI()
        {
            GUILayout.BeginVertical();

            EditorGUILayout.LabelField("推箱子关卡编辑器（最大 " + PuzzleLevelJson.MaxSide + "×" + PuzzleLevelJson.MaxSide
                + "，导出为 " + ExportFolderAsset + "/puzzle-<id>.json）");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("清空", GUILayout.Width(64f)))
            {
                ClearGrid();
            }

            if (GUILayout.Button("载入示例", GUILayout.Width(80f)))
            {
                LoadDemo();
            }

            if (GUILayout.Button("导出", GUILayout.Width(64f)))
            {
                ExportCurrent();
            }

            if (GUILayout.Button("验证", GUILayout.Width(64f)))
            {
                ValidateCurrent();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("关卡 ID", GUILayout.Width(56f));
            string editedId = EditorGUILayout.TextField(_id, GUILayout.Width(180f));
            if (!string.Equals(editedId, _id, StringComparison.Ordinal))
            {
                _id = editedId;
                SaveState();
            }

            EditorGUILayout.LabelField("尺寸 " + _width + "×" + _height);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            DrawGrid();
            GUILayout.Space(6f);

            EditorGUILayout.LabelField("点击格子循环：. 空地 → # 墙 → X 箱子 → O 目标 → * 箱子在目标 → @ 玩家 → 空地");
            EditorGUILayout.LabelField("统计：" + DescribeStatistics());

            if (_report.Length > 0)
            {
                GUILayout.Space(6f);
                EditorGUILayout.HelpBox(_report, _reportType);
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// 画网格：外层行数自上而下（与 JSON <c>tiles</c> 同序），内层列数自左向右；
        /// 每格按钮的背景色按字符区分，点一下走 <see cref="PuzzleLevelJson.NextTile"/> 的循环。
        /// </summary>
        private void DrawGrid()
        {
            for (int row = 0; row < _height; row++)
            {
                GUILayout.BeginHorizontal();
                for (int x = 0; x < _width; x++)
                {
                    int index = (row * _width) + x;
                    char tile = _tiles[index];

                    Color previous = GUI.backgroundColor;
                    GUI.backgroundColor = ColorOf(tile);
                    bool clicked = GUILayout.Button(SymbolOf(tile), GUILayout.Width(CellSize), GUILayout.Height(CellSize));
                    GUI.backgroundColor = previous;

                    if (clicked)
                    {
                        HandleCellClicked(index);
                    }
                }

                GUILayout.EndHorizontal();
            }
        }

        /// <summary>一格被点击：切到下一态、清掉旧报告、立即落偏好。</summary>
        /// <param name="index">行优先下标。</param>
        private void HandleCellClicked(int index)
        {
            _tiles[index] = PuzzleLevelJson.NextTile(_tiles[index]);
            _report = string.Empty;
            _reportType = MessageType.None;
            SaveState();
        }

        /// <summary>
        /// 清空：回到"四周是墙、内部是空地"的空白画布（<b>不是</b>全空地——那必然过不了边界墙校验）。
        /// <b>不弹窗</b>：清空是高频的草稿操作，每次都拦一个模态框会把画关卡变成点确认对话框。
        /// </summary>
        private void ClearGrid()
        {
            _width = DefaultSide;
            _height = DefaultSide;
            _tiles = CreateBlankTiles(_width, _height);
            SaveState();

            _report = "已清空为 " + _width + "×" + _height + " 空白画布（四周墙 + 内部空地）。";
            _reportType = MessageType.Info;
            Debug.Log(LogPrefix + _report);
        }

        /// <summary>
        /// 载入示例：<b>优先按磁盘路径直接读</b>（刚导出的文件不依赖导入时序），
        /// 读不到再退回 <c>Resources.Load&lt;TextAsset&gt;</c>（已在 Resources 里、但工作副本被挪走时仍能读）。
        /// </summary>
        private void LoadDemo()
        {
            string assetPath = ExportFolderAsset + "/" + PuzzleLevelJson.FileNameFor(PuzzleLevelJson.DemoId);
            string absolutePath = ToAbsolutePath(assetPath);

            string text;
            if (File.Exists(absolutePath))
            {
                text = File.ReadAllText(absolutePath);
            }
            else
            {
                TextAsset? asset = Resources.Load<TextAsset>(PuzzleLevelJson.ResourceNameFor(PuzzleLevelJson.DemoId));
                if (asset == null)
                {
                    PublishReport(false, "找不到示例关卡：" + assetPath + "（既不在磁盘上，也不在 Resources 里）。", null);
                    return;
                }

                text = asset.text;
            }

            if (!PuzzleLevelJson.TryParse(text, out PuzzleLevelData? level, out IReadOnlyList<string> errors)
                || level == null)
            {
                PublishReport(false, "示例关卡解析失败（" + assetPath + "）：", errors);
                return;
            }

            _id = level.Id;
            _width = level.Width;
            _height = level.Height;
            _tiles = level.CopyTiles();
            SaveState();

            PublishReport(true, "已载入示例：" + assetPath + "（" + _width + "×" + _height
                + "，箱子 " + level.BoxCount + "，目标 " + level.GoalCount + "）。", null);
        }

        /// <summary>验证当前画布：只报告、不写文件。</summary>
        private void ValidateCurrent()
        {
            IReadOnlyList<string> errors = PuzzleLevelJson.Validate(BuildLevel());
            if (errors.Count == 0)
            {
                PublishReport(true, "校验通过：" + DescribeStatistics() + "（可以导出）。", null);
                return;
            }

            PublishReport(false, "校验失败（" + errors.Count + " 项，未写任何文件）：", errors);
        }

        /// <summary>
        /// 导出当前画布：<b>先校验，任何一条不过就拒绝写文件</b>（并弹窗）。
        /// 写盘用 UTF-8 无 BOM，写完立即 <c>AssetDatabase.Refresh()</c>，让 <c>Resources.Load</c> 马上能取到。
        /// </summary>
        private void ExportCurrent()
        {
            PuzzleLevelData level = BuildLevel();
            IReadOnlyList<string> errors = PuzzleLevelJson.Validate(level);
            if (errors.Count > 0)
            {
                PublishReport(false, "导出被拒绝（" + errors.Count + " 项校验未通过），文件未改动：", errors);
                return;
            }

            string assetPath = ExportFolderAsset + "/" + PuzzleLevelJson.FileNameFor(level.Id);
            string absolutePath = ToAbsolutePath(assetPath);

            try
            {
                EnsureFolder(ExportFolderAsset);
                string? directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                File.WriteAllText(absolutePath, PuzzleLevelJson.Write(level), new UTF8Encoding(false));
                AssetDatabase.Refresh();
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is ArgumentException
                || exception is NotSupportedException)
            {
                PublishReport(false, "写文件失败（" + assetPath + "）：" + exception.Message, null);
                return;
            }

            PublishReport(true, "导出成功：" + assetPath + "（" + level.Width + "×" + level.Height
                + "，箱子 " + level.BoxCount + "，目标 " + level.GoalCount + "，玩家 (" + level.PlayerX + ","
                + level.PlayerY + ")）。", null);
        }

        /// <summary>用当前网格构造一份关卡数据（构造失败视为内部错误，报告出来而不是让窗口崩掉）。</summary>
        /// <returns>关卡数据；构造失败时返回一个 1×1 全墙的占位（必然校验失败，不会误导出）。</returns>
        private PuzzleLevelData BuildLevel()
        {
            try
            {
                return new PuzzleLevelData(PuzzleLevelJson.NormalizeId(_id), _width, _height, _tiles);
            }
            catch (ArgumentException exception)
            {
                PublishReport(false, "内部状态异常（网格尺寸与字符数组不一致）：" + exception.Message, null);
                return new PuzzleLevelData(PuzzleLevelJson.NormalizeId(_id), 1, 1, new[] { PuzzleLevelJson.WallChar });
            }
        }

        /// <summary>把一次操作结果同时写进控制台、窗口报告区，并按需弹窗。</summary>
        /// <param name="passed">是否通过。</param>
        /// <param name="headline">一句话结论。</param>
        /// <param name="errors">失败明细（可空）；逐条列出，满足"报告里列每条校验"。</param>
        private void PublishReport(bool passed, string headline, IReadOnlyList<string>? errors)
        {
            var builder = new StringBuilder(320);
            builder.Append(headline);
            if (errors != null && errors.Count > 0)
            {
                builder.Append('\n');
                for (int i = 0; i < errors.Count; i++)
                {
                    builder.Append("- ").Append(errors[i]).Append('\n');
                }
            }

            _report = builder.ToString().TrimEnd('\n');
            _reportType = passed ? MessageType.Info : MessageType.Error;

            Debug.Log(LogPrefix + (passed ? "通过：" : "失败：") + _report);

            if (!IsBatchMode)
            {
                EditorUtility.DisplayDialog("推箱子关卡编辑器", BuildDialogSummary(passed, headline, errors), "好");
            }
            else
            {
                Debug.Log(LogPrefix + "批处理模式：跳过弹窗，完整报告见上方。");
            }
        }

        /// <summary>弹窗摘要：只给结论与前几条错误（窗口不可滚动）。</summary>
        private static string BuildDialogSummary(bool passed, string headline, IReadOnlyList<string>? errors)
        {
            if (passed || errors == null || errors.Count == 0)
            {
                return headline + "\n\n完整报告见控制台（搜索 " + LogPrefix + "）。";
            }

            var builder = new StringBuilder(256);
            builder.Append(headline).Append('\n');
            int shown = 0;
            while (shown < errors.Count && shown < MaxDialogErrorLines)
            {
                builder.Append("- ").Append(errors[shown]).Append('\n');
                shown++;
            }

            if (errors.Count > shown)
            {
                builder.Append("… 另有 ").Append(errors.Count - shown).Append(" 项。\n");
            }

            builder.Append("\n完整报告见控制台（搜索 ").Append(LogPrefix).Append("）。");
            return builder.ToString();
        }

        /// <summary>状态行用的统计文本（玩家/箱子/目标个数，含 <c>*</c> 与 <c>+</c>）。</summary>
        private string DescribeStatistics()
        {
            int players = 0;
            int boxes = 0;
            int goals = 0;
            for (int i = 0; i < _tiles.Length; i++)
            {
                char tile = _tiles[i];
                if (PuzzleLevelJson.IsPlayer(tile))
                {
                    players++;
                }

                if (PuzzleLevelJson.IsBox(tile))
                {
                    boxes++;
                }

                if (PuzzleLevelJson.IsGoal(tile))
                {
                    goals++;
                }
            }

            return "玩家 " + players + " 个｜箱子 " + boxes + " 个｜目标 " + goals + " 个"
                + (players == 1 && boxes == goals && boxes >= 1 ? "（数量自洽）" : "（数量不自洽）");
        }

        /// <summary>把当前状态写进 <see cref="EditorPrefs"/>（每次改动立即写，关窗时再写一次兜底）。</summary>
        private void SaveState()
        {
            EditorPrefs.SetString(PrefId, PuzzleLevelJson.NormalizeId(_id));
            EditorPrefs.SetInt(PrefWidth, _width);
            EditorPrefs.SetInt(PrefHeight, _height);
            EditorPrefs.SetString(PrefTiles, new string(_tiles));
        }

        /// <summary>从偏好字符串还原网格；长度或字符不合法时返回 <c>null</c>（调用方退回空白画布）。</summary>
        private static char[]? DecodeTiles(string stored, int width, int height)
        {
            if (stored.Length != width * height)
            {
                return null;
            }

            for (int i = 0; i < stored.Length; i++)
            {
                if (!PuzzleLevelJson.IsKnown(stored[i]))
                {
                    return null;
                }
            }

            return stored.ToCharArray();
        }

        /// <summary>空白画布：四周墙 + 内部空地。</summary>
        private static char[] CreateBlankTiles(int width, int height)
        {
            var tiles = new char[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool border = row == 0 || row == height - 1 || x == 0 || x == width - 1;
                    tiles[(row * width) + x] = border ? PuzzleLevelJson.WallChar : PuzzleLevelJson.FloorChar;
                }
            }

            return tiles;
        }

        /// <summary>把任意边长夹到 1..8（偏好数据可能来自旧版本）。</summary>
        private static int ClampSide(int side)
        {
            if (side < 1)
            {
                return 1;
            }

            return side > PuzzleLevelJson.MaxSide ? PuzzleLevelJson.MaxSide : side;
        }

        /// <summary>
        /// 格子上显示的字：<c>.</c> 画成中点 <c>·</c>，让"空地"和"分隔符"在按钮里一眼可分；
        /// 其余字符原样显示，保证屏幕上的字符与 JSON 里的字符一一对应（避免自创符号造成误读）。
        /// </summary>
        private static string SymbolOf(char tile)
        {
            return tile == PuzzleLevelJson.FloorChar ? "\u00B7" : tile.ToString();
        }

        /// <summary>格子背景色：墙最暗、目标绿、箱子橙、玩家蓝，叠加态（<c>*</c>/<c>+</c>）更亮。</summary>
        private static Color ColorOf(char tile)
        {
            switch (tile)
            {
                case PuzzleLevelJson.WallChar:
                    return new Color(0.16f, 0.17f, 0.20f, 1f);
                case PuzzleLevelJson.FloorChar:
                    return new Color(0.36f, 0.36f, 0.39f, 1f);
                case PuzzleLevelJson.GoalChar:
                    return new Color(0.20f, 0.48f, 0.26f, 1f);
                case PuzzleLevelJson.BoxChar:
                    return new Color(0.62f, 0.42f, 0.16f, 1f);
                case PuzzleLevelJson.BoxOnGoalChar:
                    return new Color(0.28f, 0.68f, 0.32f, 1f);
                case PuzzleLevelJson.PlayerChar:
                    return new Color(0.18f, 0.40f, 0.72f, 1f);
                case PuzzleLevelJson.PlayerOnGoalChar:
                    return new Color(0.38f, 0.62f, 0.88f, 1f);
                default:
                    return new Color(0.80f, 0.20f, 0.20f, 1f);
            }
        }

        /// <summary>
        /// 是否运行在批处理模式（<c>Unity.exe -batchmode -executeMethod ...</c>）。
        /// 与 <c>OneClickTools</c> 同一写法：读 <see cref="Environment.CommandLine"/> 而不是
        /// <c>Application.isBatchMode</c>，免得为一行判断扩大 UnityEngine 形状桩面。
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

        /// <summary>资产相对路径 → 磁盘绝对路径（编辑器里 <c>Application.dataPath</c> 就是仓库的 <c>Assets</c>）。</summary>
        /// <param name="assetRelativePath">以 <c>Assets/</c> 开头的路径。</param>
        /// <returns>绝对路径；<c>dataPath</c> 不可用时退回相对路径。</returns>
        private static string ToAbsolutePath(string assetRelativePath)
        {
            const string AssetsPrefix = "Assets/";
            string relative = assetRelativePath.StartsWith(AssetsPrefix, StringComparison.Ordinal)
                ? assetRelativePath.Substring(AssetsPrefix.Length)
                : assetRelativePath;

            string dataPath = Application.dataPath;
            if (dataPath == null || dataPath.Length == 0)
            {
                return relative;
            }

            return Path.Combine(dataPath, relative);
        }

        /// <summary>确保资产目录存在（父目录不存在时递归创建）。</summary>
        /// <param name="path">资产相对路径，例如 <c>Assets/Resources/Levels</c>。</param>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path) ?? "Assets";
            parent = parent.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

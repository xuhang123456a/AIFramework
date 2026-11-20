#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 推箱子关卡 JSON（格式版本 1）的<b>唯一</b>解析 / 校验 / 序列化实现。
    /// <para>
    /// <b>为什么只吃 <see cref="string"/></b>：这份格式同时被两处使用——运行时
    /// （<see cref="PuzzleController"/> 从 <c>Resources</c> 拿到 <c>TextAsset.text</c>）与编辑器
    /// （关卡编辑器从磁盘读文本、导出时写文本）。一旦解析器依赖 <c>UnityEngine.TextAsset</c> 或
    /// <c>AssetDatabase</c>，它就无法在纯 .NET 命令行下验证；因此本类**不引用任何 Unity 类型**，
    /// 只做"字符串进、字符串出"，离屏工程可以原样拷贝本文件做回归。
    /// </para>
    /// <para>
    /// <b>字符表（与 JSON <c>tiles</c> 一一对应）</b>：<c>#</c> 墙、<c>.</c> 空地、<c>O</c> 目标、
    /// <c>X</c> 箱子、<c>@</c> 玩家、<c>*</c> 箱子在目标上、<c>+</c> 玩家在目标上。
    /// 注意内核 <c>PuzzleLevel</c> 自用的文本格式是<b>另一套</b>（<c>$</c> 箱子、空格地面），
    /// 两套字符在这里<b>不</b>互相兼容——本类是编辑器产物的唯一权威格式，别拿内核那套去凑。
    /// </para>
    /// <para>
    /// <b>坐标约定</b>：JSON 的 <c>tiles[0]</c> 是<b>棋盘顶部</b>那一行（人眼顺序），
    /// 而 <c>player.x/y</c> 与 <see cref="PuzzleLevelData.GetTile"/> 的 <c>y=0</c> 是<b>底行</b>
    /// （与 <c>IPuzzleReadView</c> 的坐标口径一致）。行序翻转只在解析/序列化各做一次，
    /// 校验与渲染都走统一坐标，避免"UI 上下颠倒但校验通过"。
    /// </para>
    /// <para>
    /// <b>为什么手写 JSON 而不引 System.Text.Json</b>：Unity 2022 的 netstandard2.1 运行时不带
    /// <c>System.Text.Json</c>，为一个字段固定的关卡文件引一整套反射式序列化并不划算，
    /// 而且手写解析能把"第几行第几列出错"报给人看。
    /// </para>
    /// </summary>
    public static class PuzzleLevelJson
    {
        /// <summary>格式版本。加字段或改语义必须同时升这个数字，否则老文件会被静默误读。</summary>
        public const int CurrentVersion = 1;

        /// <summary>单边最大尺寸：与内核 <c>PuzzleLevel.MaxSide</c> 对齐（8×8）。</summary>
        public const int MaxSide = 8;

        /// <summary>Resources 下的关卡子目录（<c>Resources.Load</c> 的 key 前缀）。</summary>
        public const string LevelsFolder = "Levels";

        /// <summary>关卡 id 前缀。文件名 = 归一化 id + <c>.json</c>，例如 <c>puzzle-demo.json</c>。</summary>
        public const string IdPrefix = "puzzle-";

        /// <summary>关卡文件后缀。</summary>
        public const string FileExtension = ".json";

        /// <summary>示例关卡的 id（完整 id 含前缀），对应 <c>Resources/Levels/puzzle-demo.json</c>。</summary>
        public const string DemoId = IdPrefix + "demo";

        /// <summary>墙：不可站立、不可推入。</summary>
        public const char WallChar = '#';

        /// <summary>空地：可站立、可放箱子。</summary>
        public const char FloorChar = '.';

        /// <summary>目标点：可站立；箱子停在其上即算归位。</summary>
        public const char GoalChar = 'O';

        /// <summary>箱子（在空地上）。</summary>
        public const char BoxChar = 'X';

        /// <summary>玩家（在空地上）。</summary>
        public const char PlayerChar = '@';

        /// <summary>箱子（已在目标点上）。</summary>
        public const char BoxOnGoalChar = '*';

        /// <summary>玩家（站在目标点上）。</summary>
        public const char PlayerOnGoalChar = '+';

        /// <summary>解析并校验一段关卡 JSON。<b>不抛异常</b>：语法错、字段缺失、语义非法都进 <paramref name="errors"/>。</summary>
        /// <param name="text">JSON 文本（<c>null</c>/空串也算失败，不会抛）。</param>
        /// <param name="level">解析成功且校验通过时为关卡数据，否则为 <c>null</c>。</param>
        /// <param name="errors">失败原因（每条一句人话）；成功时为空列表。</param>
        /// <returns>是否成功。</returns>
        public static bool TryParse(string? text, out PuzzleLevelData? level, out IReadOnlyList<string> errors)
        {
            var messages = new List<string>();

            if (text == null)
            {
                level = null;
                messages.Add("关卡 JSON 为 null。");
                errors = messages;
                return false;
            }

            if (text.Trim().Length == 0)
            {
                level = null;
                messages.Add("关卡 JSON 为空。");
                errors = messages;
                return false;
            }

            try
            {
                level = ParseRoot(text, messages);
            }
            catch (FormatException exception)
            {
                // 语法错（括号/逗号/引号/转义/未知字段）必须变成"校验失败"，
                // 绝不能让一个手改坏的 JSON 在 Awake 里把场景炸掉。
                level = null;
                messages.Add("JSON 解析失败：" + exception.Message);
            }

            if (messages.Count > 0 || level == null)
            {
                level = null;
                if (messages.Count == 0)
                {
                    messages.Add("关卡 JSON 解析结果为空。");
                }

                errors = messages;
                return false;
            }

            errors = messages;
            return true;
        }

        /// <summary>
        /// 校验一份关卡数据（编辑器"验证"按钮与运行时载入共用同一套规则，避免"编辑器放行、运行时炸"）。
        /// 规则：version=1、id 可做文件名、宽高 1..8、字符合法、恰好 1 个玩家、玩家不在墙内、
        /// 箱子数 == 目标数（含 <c>*</c>/<c>+</c>）、至少各 1 个、四周全是墙。
        /// </summary>
        /// <param name="level">关卡数据；<c>null</c> 记一条错误。</param>
        /// <returns>错误清单；空列表表示通过。</returns>
        public static IReadOnlyList<string> Validate(PuzzleLevelData? level)
        {
            var errors = new List<string>();
            if (level == null)
            {
                errors.Add("关卡数据为空。");
                return errors;
            }

            if (level.Id.Length == 0)
            {
                errors.Add("缺少关卡 id。");
            }
            else if (!IsSafeId(level.Id))
            {
                errors.Add("关卡 id \"" + level.Id + "\" 只能包含字母、数字、'-'、'_'（它会直接成为文件名）。");
            }

            if (level.Width < 1 || level.Width > MaxSide)
            {
                errors.Add("宽 " + level.Width + " 越界：必须落在 1.." + MaxSide + "。");
            }

            if (level.Height < 1 || level.Height > MaxSide)
            {
                errors.Add("高 " + level.Height + " 越界：必须落在 1.." + MaxSide + "。");
            }

            if (level.Width >= 1 && level.Width <= MaxSide && level.Height >= 1 && level.Height <= MaxSide)
            {
                AddUnknownTileErrors(level, errors);
            }

            if (level.PlayerCount != 1)
            {
                errors.Add("必须恰好 1 个玩家（" + PlayerChar + " 或 " + PlayerOnGoalChar + "），当前 " + level.PlayerCount + " 个。");
            }
            else if (level.GetTile(level.PlayerX, level.PlayerY) == WallChar)
            {
                // 字符语义下不可能出现；留着是因为未来若支持"双图层"（地形与实体分离），
                // 玩家字段可能指向墙格——这条显式断言会把那种静默错误挡住。
                errors.Add("玩家所在格不能是墙。");
            }

            if (level.BoxCount < 1)
            {
                errors.Add("至少要有 1 个箱子（" + BoxChar + " 或 " + BoxOnGoalChar + "）。");
            }

            if (level.GoalCount < 1)
            {
                errors.Add("至少要有 1 个目标点（" + GoalChar + "、" + BoxOnGoalChar + " 或 " + PlayerOnGoalChar + "）。");
            }

            if (level.BoxCount != level.GoalCount)
            {
                errors.Add("箱子 " + level.BoxCount + " 个与目标点 " + level.GoalCount + " 个数量不等"
                    + "（" + BoxOnGoalChar + " 同时计入箱子与目标，" + PlayerOnGoalChar + " 计入目标）。");
            }

            AddBorderErrors(level, errors);
            return errors;
        }

        /// <summary>
        /// 序列化成格式版本 1 的 JSON 文本（字段顺序固定，便于 diff；UTF-8 无 BOM 由调用方决定）。
        /// <b>调用前先跑 <see cref="Validate"/></b>：没有唯一玩家时这里会抛
        /// <see cref="InvalidOperationException"/>，因为写出一份自己都读不回来的文件没有意义。
        /// </summary>
        /// <param name="level">已通过校验的关卡数据。</param>
        /// <returns>JSON 文本（以换行结尾）。</returns>
        public static string Write(PuzzleLevelData level)
        {
            if (level == null)
            {
                throw new ArgumentNullException(nameof(level));
            }

            if (level.PlayerX < 0 || level.PlayerY < 0)
            {
                throw new InvalidOperationException("关卡没有唯一的玩家起点，禁止序列化（先跑 Validate）。");
            }

            var builder = new StringBuilder(256);
            builder.Append("{\n");
            builder.Append("  \"version\": ").Append(CurrentVersion).Append(",\n");
            builder.Append("  \"id\": \"").Append(Escape(level.Id)).Append("\",\n");
            builder.Append("  \"width\": ").Append(level.Width).Append(",\n");
            builder.Append("  \"height\": ").Append(level.Height).Append(",\n");
            builder.Append("  \"player\": { \"x\": ").Append(level.PlayerX)
                .Append(", \"y\": ").Append(level.PlayerY).Append(" },\n");
            builder.Append("  \"tiles\": [\n");

            string[] rows = level.GetRows();
            for (int row = 0; row < rows.Length; row++)
            {
                builder.Append("    \"").Append(Escape(rows[row])).Append('"');
                if (row < rows.Length - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("  ]\n}");
            builder.Append('\n');
            return builder.ToString();
        }

        /// <summary>
        /// 把用户输入的 id 归一化成完整 id：去空白；空串退回 <see cref="DemoId"/>；
        /// 没有 <see cref="IdPrefix"/> 前缀时自动补上。因此输入 <c>demo</c> 与 <c>puzzle-demo</c> 等价，
        /// 导出文件名永远是 <c>puzzle-&lt;裸 id&gt;.json</c>。
        /// </summary>
        /// <param name="rawId">原始输入，可为 <c>null</c>。</param>
        /// <returns>非空的完整 id。</returns>
        public static string NormalizeId(string? rawId)
        {
            string trimmed = rawId == null ? string.Empty : rawId.Trim();
            if (trimmed.Length == 0)
            {
                return DemoId;
            }

            return trimmed.StartsWith(IdPrefix, StringComparison.Ordinal) ? trimmed : IdPrefix + trimmed;
        }

        /// <summary>关卡文件名（不含目录），例如 <c>puzzle-demo.json</c>。</summary>
        /// <param name="id">裸 id 或完整 id。</param>
        /// <returns>文件名。</returns>
        public static string FileNameFor(string? id)
        {
            return NormalizeId(id) + FileExtension;
        }

        /// <summary><c>Resources.Load</c> 用的 key（不含扩展名），例如 <c>Levels/puzzle-demo</c>。</summary>
        /// <param name="id">裸 id 或完整 id。</param>
        /// <returns>资源 key。</returns>
        public static string ResourceNameFor(string? id)
        {
            return LevelsFolder + "/" + NormalizeId(id);
        }

        /// <summary>按数字关卡 ID 推出资源 key（与 <c>PuzzleController</c> 的内置关卡编号对应），例如 <c>Levels/puzzle-3</c>。</summary>
        /// <param name="levelId">关卡编号。</param>
        /// <returns>资源 key。</returns>
        public static string ResourceNameForLevel(int levelId)
        {
            return LevelsFolder + "/" + IdPrefix + levelId;
        }

        /// <summary>字符是否属于版本 1 的字符表。</summary>
        /// <param name="tile">待判字符。</param>
        /// <returns>是否合法。</returns>
        public static bool IsKnown(char tile)
        {
            switch (tile)
            {
                case WallChar:
                case FloorChar:
                case GoalChar:
                case BoxChar:
                case PlayerChar:
                case BoxOnGoalChar:
                case PlayerOnGoalChar:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>该格的地形是否是墙。</summary>
        /// <param name="tile">字符。</param>
        /// <returns>是否墙。</returns>
        public static bool IsWall(char tile)
        {
            return tile == WallChar;
        }

        /// <summary>该格是否带目标点（<c>O</c> / <c>*</c> / <c>+</c>）。</summary>
        /// <param name="tile">字符。</param>
        /// <returns>是否目标点。</returns>
        public static bool IsGoal(char tile)
        {
            return tile == GoalChar || tile == BoxOnGoalChar || tile == PlayerOnGoalChar;
        }

        /// <summary>该格是否有箱子（<c>X</c> / <c>*</c>）。</summary>
        /// <param name="tile">字符。</param>
        /// <returns>是否有箱子。</returns>
        public static bool IsBox(char tile)
        {
            return tile == BoxChar || tile == BoxOnGoalChar;
        }

        /// <summary>该格是否有玩家（<c>@</c> / <c>+</c>）。</summary>
        /// <param name="tile">字符。</param>
        /// <returns>是否有玩家。</returns>
        public static bool IsPlayer(char tile)
        {
            return tile == PlayerChar || tile == PlayerOnGoalChar;
        }

        /// <summary>
        /// 编辑器点击循环的下一态：空地 → 墙 → 箱子 → 目标 → 箱子在目标 → 玩家 → 空地。
        /// <c>+</c>（玩家在目标）不在循环里——工具条只提供 6 个状态；它只能由"载入"产生，
        /// 再点一次回到目标点（<b>保留</b>地面上的目标，而不是连目标一起抹掉）。
        /// </summary>
        /// <param name="current">当前字符。</param>
        /// <returns>下一态字符。</returns>
        public static char NextTile(char current)
        {
            switch (current)
            {
                case FloorChar:
                    return WallChar;
                case WallChar:
                    return BoxChar;
                case BoxChar:
                    return GoalChar;
                case GoalChar:
                    return BoxOnGoalChar;
                case BoxOnGoalChar:
                    return PlayerChar;
                case PlayerChar:
                    return FloorChar;
                case PlayerOnGoalChar:
                    return GoalChar;
                default:
                    return FloorChar;
            }
        }

        /// <summary>解析根对象并做全部语义校验；语法错抛 <see cref="FormatException"/>，语义错写进 <paramref name="errors"/>。</summary>
        private static PuzzleLevelData? ParseRoot(string text, List<string> errors)
        {
            var reader = new JsonReader(text);

            reader.SkipWhitespace();
            reader.Expect('{', "根对象的左花括号");

            bool hasVersion = false;
            int version = 0;
            string? id = null;
            int width = -1;
            int height = -1;
            bool hasPlayer = false;
            int declaredPlayerX = -1;
            int declaredPlayerY = -1;
            string[]? rows = null;

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            reader.SkipWhitespace();
            if (reader.TryReadChar('}'))
            {
                // 空对象：交给下面"缺字段"的检查统一报错。
            }
            else
            {
                while (true)
                {
                    reader.SkipWhitespace();
                    string key = reader.ReadString();
                    if (!seenKeys.Add(key))
                    {
                        throw reader.Error("字段 \"" + key + "\" 重复出现");
                    }

                    reader.SkipWhitespace();
                    reader.Expect(':', "字段名后的冒号");
                    reader.SkipWhitespace();

                    switch (key)
                    {
                        case "version":
                            version = reader.ReadInt();
                            hasVersion = true;
                            break;
                        case "id":
                            id = reader.ReadString();
                            break;
                        case "width":
                            width = reader.ReadInt();
                            break;
                        case "height":
                            height = reader.ReadInt();
                            break;
                        case "player":
                            ReadPlayerObject(reader, out declaredPlayerX, out declaredPlayerY);
                            hasPlayer = true;
                            break;
                        case "tiles":
                            rows = reader.ReadStringArray();
                            break;
                        default:
                            throw reader.Error("未知字段 \"" + key + "\"（版本 " + CurrentVersion
                                + " 只允许 version/id/width/height/player/tiles；加字段请升 version）");
                    }

                    reader.SkipWhitespace();
                    char separator = reader.ReadChar();
                    if (separator == '}')
                    {
                        break;
                    }

                    if (separator != ',')
                    {
                        throw reader.Error("字段之间必须用逗号分隔");
                    }
                }
            }

            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("根对象之后还有多余内容");
            }

            if (!hasVersion)
            {
                errors.Add("缺少字段 \"version\"。");
            }
            else if (version != CurrentVersion)
            {
                errors.Add("version 必须是 " + CurrentVersion + "，实际 " + version + "。");
            }

            if (id == null)
            {
                errors.Add("缺少字段 \"id\"。");
            }

            if (width < 1)
            {
                errors.Add("字段 \"width\" 缺失或不是正整数。");
            }
            else if (width > MaxSide)
            {
                errors.Add("width " + width + " 越界：最大 " + MaxSide + "。");
            }

            if (height < 1)
            {
                errors.Add("字段 \"height\" 缺失或不是正整数。");
            }
            else if (height > MaxSide)
            {
                errors.Add("height " + height + " 越界：最大 " + MaxSide + "。");
            }

            if (!hasPlayer)
            {
                errors.Add("缺少字段 \"player\"。");
            }

            if (rows == null)
            {
                errors.Add("缺少字段 \"tiles\"。");
            }

            bool sizeOk = width >= 1 && width <= MaxSide && height >= 1 && height <= MaxSide;
            if (rows != null && sizeOk)
            {
                if (rows.Length != height)
                {
                    errors.Add("tiles 行数 " + rows.Length + " 与 height " + height + " 不一致。");
                }

                int rowCount = Math.Min(rows.Length, height);
                for (int row = 0; row < rowCount; row++)
                {
                    string line = rows[row];
                    if (line.Length != width)
                    {
                        errors.Add("第 " + (row + 1) + " 行长度 " + line.Length + " 与 width " + width + " 不一致。");
                        continue;
                    }

                    for (int x = 0; x < line.Length; x++)
                    {
                        if (!IsKnown(line[x]))
                        {
                            errors.Add("第 " + (row + 1) + " 行第 " + (x + 1) + " 列是未知字符 '" + line[x] + "'。");
                        }
                    }
                }
            }

            if (errors.Count > 0 || rows == null || !sizeOk || id == null)
            {
                return null;
            }

            // 到这一步字段齐全且尺寸自洽，可以安全构造。
            char[] tiles = new char[width * height];
            for (int row = 0; row < height; row++)
            {
                string line = rows[row];
                if (line.Length != width)
                {
                    errors.Add("第 " + (row + 1) + " 行长度与 width 不一致。");
                    return null;
                }

                for (int x = 0; x < width; x++)
                {
                    tiles[(row * width) + x] = line[x];
                }
            }

            PuzzleLevelData data;
            try
            {
                data = new PuzzleLevelData(id, width, height, tiles);
            }
            catch (ArgumentException exception)
            {
                errors.Add("关卡数据构造失败：" + exception.Message);
                return null;
            }

            // player 字段必须与 tiles 里的玩家位置一致：两个来源写同一个事实，
            // 不一致说明有人手改过文件，放行只会让"渲染按 tiles、碰撞按 player"这种精神分裂上线。
            // 玩家数量不为 1 时跳过这条：那种情况由 Validate 报"必须恰好 1 个玩家"更准确，
            // 否则 0 个玩家会冒出一条让人摸不着头脑的"player 字段 (-1,-1) 不一致"。
            if (hasPlayer && data.PlayerCount == 1
                && (declaredPlayerX != data.PlayerX || declaredPlayerY != data.PlayerY))
            {
                errors.Add("player 字段 (x=" + declaredPlayerX + ", y=" + declaredPlayerY
                    + ") 与 tiles 中的玩家位置 (x=" + data.PlayerX + ", y=" + data.PlayerY + ") 不一致。");
            }

            foreach (string message in Validate(data))
            {
                errors.Add(message);
            }

            return errors.Count == 0 ? data : null;
        }

        /// <summary>读 <c>"player": { "x": int, "y": int }</c>。</summary>
        private static void ReadPlayerObject(JsonReader reader, out int x, out int y)
        {
            reader.Expect('{', "player 对象的左花括号");
            int foundX = int.MinValue;
            int foundY = int.MinValue;
            bool hasX = false;
            bool hasY = false;

            reader.SkipWhitespace();
            if (!reader.TryReadChar('}'))
            {
                while (true)
                {
                    reader.SkipWhitespace();
                    string key = reader.ReadString();
                    reader.SkipWhitespace();
                    reader.Expect(':', "player 字段名后的冒号");
                    reader.SkipWhitespace();

                    if (key == "x")
                    {
                        if (hasX)
                        {
                            throw reader.Error("player.x 重复出现");
                        }

                        foundX = reader.ReadInt();
                        hasX = true;
                    }
                    else if (key == "y")
                    {
                        if (hasY)
                        {
                            throw reader.Error("player.y 重复出现");
                        }

                        foundY = reader.ReadInt();
                        hasY = true;
                    }
                    else
                    {
                        throw reader.Error("player 只允许 x/y，出现未知字段 \"" + key + "\"");
                    }

                    reader.SkipWhitespace();
                    char separator = reader.ReadChar();
                    if (separator == '}')
                    {
                        break;
                    }

                    if (separator != ',')
                    {
                        throw reader.Error("player 字段之间必须用逗号分隔");
                    }
                }
            }

            if (!hasX || !hasY)
            {
                throw reader.Error("player 必须同时给出 x 与 y");
            }

            x = foundX;
            y = foundY;
        }

        /// <summary>逐格报未知字符（带上"从顶部数第几行"的人类坐标）。</summary>
        private static void AddUnknownTileErrors(PuzzleLevelData level, List<string> errors)
        {
            for (int row = 0; row < level.Height; row++)
            {
                for (int x = 0; x < level.Width; x++)
                {
                    char tile = level.GetTile(x, level.Height - 1 - row);
                    if (!IsKnown(tile))
                    {
                        errors.Add("第 " + (row + 1) + " 行第 " + (x + 1) + " 列是未知字符 '" + tile + "'。");
                    }
                }
            }
        }

        /// <summary>四周必须是墙：按"上/下/左/右"各汇总成一条，避免 8×8 空格列出 28 条噪音。</summary>
        private static void AddBorderErrors(PuzzleLevelData level, List<string> errors)
        {
            if (level.Width < 1 || level.Height < 1)
            {
                return;
            }

            var columns = new List<int>();
            for (int x = 0; x < level.Width; x++)
            {
                if (!IsWall(level.GetTile(x, level.Height - 1)))
                {
                    columns.Add(x + 1);
                }
            }

            if (columns.Count > 0)
            {
                errors.Add("边界墙缺失：顶部行（第 1 行）有 " + columns.Count + " 格不是墙（列 " + DescribeIndexes(columns) + "）。");
            }

            columns.Clear();
            for (int x = 0; x < level.Width; x++)
            {
                if (!IsWall(level.GetTile(x, 0)))
                {
                    columns.Add(x + 1);
                }
            }

            if (columns.Count > 0)
            {
                errors.Add("边界墙缺失：底部行（第 " + level.Height + " 行）有 " + columns.Count
                    + " 格不是墙（列 " + DescribeIndexes(columns) + "）。");
            }

            var rows = new List<int>();
            for (int y = 0; y < level.Height; y++)
            {
                if (!IsWall(level.GetTile(0, y)))
                {
                    rows.Add(level.Height - y);
                }
            }

            if (rows.Count > 0)
            {
                errors.Add("边界墙缺失：左列有 " + rows.Count + " 格不是墙（行 " + DescribeIndexes(rows) + "）。");
            }

            rows.Clear();
            for (int y = 0; y < level.Height; y++)
            {
                if (!IsWall(level.GetTile(level.Width - 1, y)))
                {
                    rows.Add(level.Height - y);
                }
            }

            if (rows.Count > 0)
            {
                errors.Add("边界墙缺失：右列有 " + rows.Count + " 格不是墙（行 " + DescribeIndexes(rows) + "）。");
            }
        }

        /// <summary>把下标列表压成 "2,4,5"（超过 8 个截断并补总数）。</summary>
        private static string DescribeIndexes(List<int> indexes)
        {
            const int MaxShown = 8;
            var builder = new StringBuilder();
            int shown = Math.Min(indexes.Count, MaxShown);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(indexes[i]);
            }

            if (indexes.Count > shown)
            {
                builder.Append(" 等 ").Append(indexes.Count).Append(" 处");
            }

            return builder.ToString();
        }

        /// <summary>id 是否会成为安全文件名（不检查是否为空，空由调用方单独报）。</summary>
        private static bool IsSafeId(string id)
        {
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool allowed = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '-'
                    || c == '_';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>JSON 字符串转义（tiles 与 id 都只含 ASCII，但转义表必须完整才能容纳将来的中文字段）。</summary>
        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 极简 JSON 读取器：只覆盖本格式需要的对象/字符串/整数/字符串数组。
        /// <b>刻意不支持浮点、布尔、null、注释与尾逗号</b>——关卡文件是机器产出的，
        /// 放宽语法只会让手改出来的错误更晚才被发现。
        /// </summary>
        private sealed class JsonReader
        {
            private readonly string _text;

            private int _index;

            internal JsonReader(string text)
            {
                _text = text;
                _index = 0;
            }

            internal bool AtEnd
            {
                get { return _index >= _text.Length; }
            }

            internal void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _index++;
                        continue;
                    }

                    break;
                }
            }

            internal char ReadChar()
            {
                if (_index >= _text.Length)
                {
                    throw Error("内容意外结束");
                }

                return _text[_index++];
            }

            internal bool TryReadChar(char expected)
            {
                if (_index < _text.Length && _text[_index] == expected)
                {
                    _index++;
                    return true;
                }

                return false;
            }

            internal void Expect(char expected, string what)
            {
                if (_index >= _text.Length || _text[_index] != expected)
                {
                    throw Error("期望" + what);
                }

                _index++;
            }

            internal string ReadString()
            {
                Expect('"', "字符串起始的双引号");
                var builder = new StringBuilder();

                while (true)
                {
                    char c = ReadChar();
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    char escape = ReadChar();
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            builder.Append(ReadUnicodeEscape());
                            break;
                        default:
                            throw Error("未知转义 \\" + escape);
                    }
                }
            }

            internal int ReadInt()
            {
                bool negative = false;
                if (_index < _text.Length && _text[_index] == '-')
                {
                    negative = true;
                    _index++;
                }

                int digits = 0;
                int value = 0;
                while (_index < _text.Length && _text[_index] >= '0' && _text[_index] <= '9')
                {
                    int digit = _text[_index] - '0';
                    if (value > (int.MaxValue - digit) / 10)
                    {
                        throw Error("整数过大");
                    }

                    value = (value * 10) + digit;
                    _index++;
                    digits++;
                }

                if (digits == 0)
                {
                    throw Error("期望整数字面量");
                }

                return negative ? -value : value;
            }

            internal string[] ReadStringArray()
            {
                Expect('[', "数组起始的左方括号");
                var items = new List<string>();

                SkipWhitespace();
                if (TryReadChar(']'))
                {
                    return items.ToArray();
                }

                while (true)
                {
                    SkipWhitespace();
                    items.Add(ReadString());
                    SkipWhitespace();
                    char separator = ReadChar();
                    if (separator == ']')
                    {
                        return items.ToArray();
                    }

                    if (separator != ',')
                    {
                        throw Error("数组元素之间必须用逗号分隔");
                    }
                }
            }

            internal FormatException Error(string message)
            {
                int line = 1;
                int column = 1;
                int limit = Math.Min(_index, _text.Length);
                for (int i = 0; i < limit; i++)
                {
                    if (_text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }

                return new FormatException(message + "（第 " + line + " 行第 " + column + " 列）");
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _text.Length)
                {
                    throw Error("\\u 转义不足 4 位十六进制");
                }

                int code = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = _text[_index + i];
                    int digit;
                    if (c >= '0' && c <= '9')
                    {
                        digit = c - '0';
                    }
                    else if (c >= 'a' && c <= 'f')
                    {
                        digit = c - 'a' + 10;
                    }
                    else if (c >= 'A' && c <= 'F')
                    {
                        digit = c - 'A' + 10;
                    }
                    else
                    {
                        throw Error("\\u 转义含非十六进制字符 '" + c + "'");
                    }

                    code = (code * 16) + digit;
                }

                _index += 4;
                return (char)code;
            }
        }
    }

    /// <summary>
    /// 一份已解析的关卡（不可变）：<c>tiles</c> 按<b>行优先、第 0 行在顶部</b>保存，
    /// 对外坐标一律走 <see cref="GetTile"/>（<c>y=0</c> 为底行）。
    /// <para>
    /// <b>为什么构造期就把统计量算好</b>：箱子/目标/玩家计数会被校验、导出报告、运行时日志
    /// 反复读取，每次遍历字符既慢又容易两处算法分叉；构造期一次扫描是唯一口径。
    /// </para>
    /// <para>
    /// <b>为什么构造不抛"缺玩家"</b>：编辑器允许画出中间态（还没放玩家），此时仍需要一个可校验的对象——
    /// 非法状态由 <see cref="PuzzleLevelJson.Validate"/> 报错，构造只负责"结构自洽"。
    /// </para>
    /// </summary>
    public sealed class PuzzleLevelData
    {
        /// <summary>行优先、顶部在前的字符网格（长度 = Width × Height）。</summary>
        private readonly char[] _tiles;

        /// <summary>构造并扫描统计量。</summary>
        /// <param name="id">关卡 id（完整形态，含 <c>puzzle-</c> 前缀；归一化由调用方做）。</param>
        /// <param name="width">宽（1..8）。</param>
        /// <param name="height">高（1..8）。</param>
        /// <param name="tilesTopDown">行优先字符数组，第 0 行在顶部。</param>
        /// <exception cref="ArgumentException">参数为 <c>null</c>、尺寸越界或数组长度不等于 <c>width*height</c>。</exception>
        public PuzzleLevelData(string id, int width, int height, char[] tilesTopDown)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            if (width < 1 || width > PuzzleLevelJson.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "宽必须落在 1.." + PuzzleLevelJson.MaxSide + "。");
            }

            if (height < 1 || height > PuzzleLevelJson.MaxSide)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "高必须落在 1.." + PuzzleLevelJson.MaxSide + "。");
            }

            if (tilesTopDown == null)
            {
                throw new ArgumentNullException(nameof(tilesTopDown));
            }

            if (tilesTopDown.Length != width * height)
            {
                throw new ArgumentException("tiles 长度必须等于 width*height。", nameof(tilesTopDown));
            }

            Id = id;
            Width = width;
            Height = height;
            _tiles = (char[])tilesTopDown.Clone();

            int playerCount = 0;
            int boxCount = 0;
            int goalCount = 0;
            int boxesOnGoal = 0;
            int playersOnGoal = 0;
            int playerX = -1;
            int playerY = -1;

            for (int row = 0; row < height; row++)
            {
                int y = height - 1 - row;
                for (int x = 0; x < width; x++)
                {
                    char tile = _tiles[(row * width) + x];
                    switch (tile)
                    {
                        case PuzzleLevelJson.GoalChar:
                            goalCount++;
                            break;
                        case PuzzleLevelJson.BoxChar:
                            boxCount++;
                            break;
                        case PuzzleLevelJson.PlayerChar:
                            playerCount++;
                            playerX = x;
                            playerY = y;
                            break;
                        case PuzzleLevelJson.BoxOnGoalChar:
                            boxCount++;
                            goalCount++;
                            boxesOnGoal++;
                            break;
                        case PuzzleLevelJson.PlayerOnGoalChar:
                            goalCount++;
                            playerCount++;
                            playersOnGoal++;
                            playerX = x;
                            playerY = y;
                            break;
                        default:
                            // 未知字符由 Validate 逐格报错；构造期不抛，才能让"验证"按钮一次列全所有问题。
                            break;
                    }
                }
            }

            PlayerCount = playerCount;
            BoxCount = boxCount;
            GoalCount = goalCount;
            BoxesOnGoal = boxesOnGoal;
            PlayersOnGoal = playersOnGoal;
            PlayerX = playerX;
            PlayerY = playerY;
        }

        /// <summary>关卡 id（完整形态，与导出文件名一致，例如 <c>puzzle-demo</c>）。</summary>
        public string Id { get; }

        /// <summary>宽（列数）。</summary>
        public int Width { get; }

        /// <summary>高（行数）。</summary>
        public int Height { get; }

        /// <summary>玩家所在列；玩家不唯一时为 -1。</summary>
        public int PlayerX { get; }

        /// <summary>玩家所在行（<c>y=0</c> 为底行）；玩家不唯一时为 -1。</summary>
        public int PlayerY { get; }

        /// <summary>玩家字符个数（含 <c>+</c>）。</summary>
        public int PlayerCount { get; }

        /// <summary>箱子总数（含 <c>*</c>）。</summary>
        public int BoxCount { get; }

        /// <summary>目标点总数（含 <c>*</c> 与 <c>+</c>）。</summary>
        public int GoalCount { get; }

        /// <summary>初始就在目标点上的箱子数。</summary>
        public int BoxesOnGoal { get; }

        /// <summary>初始就站在目标点上的玩家数（0 或 1）。</summary>
        public int PlayersOnGoal { get; }

        /// <summary>
        /// 取 (x, y) 的原始字符；<c>y=0</c> 为底行。越界返回 <see cref="PuzzleLevelJson.WallChar"/>
        /// （与内核 <c>IPuzzleReadView.GetCell</c> 的越界口径一致，调用方不必先判界）。
        /// </summary>
        /// <param name="x">列。</param>
        /// <param name="y">行（0 为底）。</param>
        /// <returns>该格字符。</returns>
        public char GetTile(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return PuzzleLevelJson.WallChar;
            }

            int row = Height - 1 - y;
            return _tiles[(row * Width) + x];
        }

        /// <summary>拷贝一份行优先字符数组（顶部在前）；调用方随意改，不影响本对象。</summary>
        /// <returns>字符数组副本。</returns>
        public char[] CopyTiles()
        {
            return (char[])_tiles.Clone();
        }

        /// <summary>按 JSON <c>tiles</c> 的行序（顶部在前）切成字符串数组。</summary>
        /// <returns>长度 = <see cref="Height"/> 的字符串数组。</returns>
        public string[] GetRows()
        {
            var rows = new string[Height];
            for (int row = 0; row < Height; row++)
            {
                rows[row] = new string(_tiles, row * Width, Width);
            }

            return rows;
        }
    }
}

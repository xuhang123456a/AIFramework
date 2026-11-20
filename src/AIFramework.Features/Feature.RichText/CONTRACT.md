# Feature.RichText 切片边界契约

## 1. 职责定位
邮件/公告富文本的**唯一净化入口**：白名单标记 → 可直接渲染的片段 + 被剥离/修正清单；绝不把不可控标记透传进 UI。
不查本地化表、不碰 UI、不落存档、不读时钟、不引用任何引擎类型。

## 2. 允许依赖
- 仅 BCL：本切片当前**不引用** `AIFramework.Contracts` 任何类型（Features 程序集整体只能引 Contracts，AIF004）。
- 禁止 `Core`/`Kernel.*`/`Adapters.*`、`UnityEngine`/`TMPro`、其他 `Features.*` 切片（AIF001/AIF002）。

## 3. 公开面（切片内契约，不进 Contracts）
- `RichTextParser.Parse(string?)` → `RichTextDocument`；`null`/空串 → `Empty`，**永不抛**。
- `RichTextDocument`：`Spans` / `Diagnostics` / `HasErrors` / `PlainText` / `ToSafeTmp()`。
- `RichTextSpan`：`Style` / `Text` / `SourceOffset` / `ColorRgb` / `FontSize`（后两项是样式负载，其他样式为 0）。
- `RichTextFormatter.ToSafeTmp(spans)` + 常量 `MaxDepth=8`、`MaxInputLength=4096`、`MinFontSize=10`、`MaxFontSize=200`。

## 4. 白名单（除此之外一律剥离）
| 标记 | 合法形式 | 负载 |
| --- | --- | --- |
| `b` / `i` / `u` | `<b>`…`</b>`，支持嵌套与自闭合 `<b/>`，不接受任何属性 | — |
| `color` | `<color=#RRGGBB>`（6 位十六进制，大小写均可） | `ColorRgb = 0xRRGGBB` |
| `size` | `<size=NN>`，NN 为 10..200 的十进制整数 | `FontSize = NN` |

- `#RRGGBBAA` / `#F00` / `red` / `#GGGGGG` → 剥离 + Error；`<size=9>` / `<size=201>` / `<size=abc>` → 剥离 + Error。
- `script` / `a href` / `img` / `div` … → 整段剥离（不转义成文本），中间可读文本保留；`<b foo="1">` 同样剥离 + Error。

## 5. 关键规则（测试已钉死）
1. 嵌套按**最内层**归一：`<b>a<i>b</i>c</b>` → Bold "a" / Italic "b" / Bold "c"（单值样式表达不了粗+斜，见 §7）。
2. 相邻同样式（样式+颜色+字号）片段合并，`SourceOffset` 取第一段位置；零长度片段不产出。
3. 未闭合标签到末尾自动闭合（Warning，`reason` 含"自动闭合"），文本一个字符都不丢；从内到外各记 1 条。
4. 孤立/错配闭合标签剥离 + Error（空栈 = "没有对应的开标签"，栈顶不同名 = "不匹配"）。
5. `<` 不构成标记时按普通字符输出（`1 < 2`、`a<b`）；实体 `&lt;` 等原样透传，由 UI 层自行处理。
6. 深度上限 8：第 9 层开标签剥离 + Error，其后同名闭合标签会按孤立闭合再记一条。
7. 长度上限 4096（UTF-16 code unit）：按输入截断，尾部不解析，记 1 条 Warning（offset 4096、tag 空串）。
8. 标签以**第一个** `>` 结束（属性区不解析引号）；标签名大小写不敏感，输出统一小写。

## 6. 诊断口径
- `Offset` = 源文本下标（标记起始的 `<`）；`Tag` = 标记原文（含尖括号与属性，便于对回配置）；`Reason` 只给人看，不做程序判定。
- `Kind`：`Error` = 内容被剥离（`HasErrors` 为 true）；`Warning` = 内容保留但被修正（自动闭合 / 截断）。
- 顺序：扫描期按源顺序，其后从内到外补自动闭合，最后补截断。

## 7. ToSafeTmp 决策（测试锁定）
- **不转义**文本里的 `<`：span 文本已是解析后的纯文本，`<` 只可能来自"不构成标记的字符"或透传实体；
  原样输出保住纯文本语义，且再解析不改变文本与样式（往返用例覆盖 11 组输入）。
- 颜色统一大写 `#RRGGBB`、字号夹到 10..200、颜色截到 24 位；解析产物本就在范围内，往返不受影响。
- 组合样式（粗+斜）在切片模型里降级为最内层样式；要组合效果由适配层决定，本切片不造组合标记。

## 8. 明确不做
- 不做本地化 key 解析（`textKey` → 文案是 Localization 切片职责）、不做打字机/逐字动画（UI 表现层）。
- 不支持图片/超链接/自定义 TMP 标签（避免注入与平台差异）；不反转义 HTML 实体。
- 不把标记白名单搬进 `Contracts`：只有跨切片共享的契约才放那里。

## 9. 已知缺口（真机未验证）
- TMP 实际支持的标签面与本白名单的差异、CJK 字体缺字、超长公告的换行/顶点预算均未在真机核对。

## 10. 验收命令
```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~RichText"
```

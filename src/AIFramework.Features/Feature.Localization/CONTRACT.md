# Feature.Localization 切片边界契约

## 1. 职责定位

Phase 1 本地化：按本切片子表查 `key + 语言` 文案、维护当前语言、切换语言并广播事实。
不含 TMP 字体/图集回退、语言持久化（存档）、系统语言探测、文案插值/复数、UI 弹窗与界面刷新。

查询规则（逐条锁死）：
1. 按 `CurrentLanguage` 查；缺该语言 → 回退 `defaultLanguage`；仍缺 → `LocalizedText(key, key, found: false)`。
2. 永不返回 null / 空串：`Text` 至少等于 `key`（唯一例外是调用方传空 key，原样回显空串）。
3. 查询路径零分配：只读构造期冻结的字典，不建临时集合、不拼字符串，只返回表内字符串引用。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理查询（In）**：`LocalizationQueryRequest(key)` → `LocalizedText(key, text, found)`；端口 `ILocalizationQuery` 在 Contracts
* **处理指令（In）**：`SetLanguageCommand(language)`
* **广播事件（Out）**：`LanguageChangedEvent(language)`（仅真实切换时发）
* **端口依赖（In）**：`ICqeBus`（构造注入，只用于广播）、`IBinaryTableLoader`（加载本切片子表）
* **UI 视图契约**：Phase 1 无 Presenter；UI 只读 `LocalizedText`，改语言只发 `SetLanguageCommand`

## 3. 独立子表（Config）

* 表名 `TbLocalization`，布局：小端 `[int32 keyCount]` + keyCount × { `[int32 keyLen]` + UTF-8 key + `[int32 langCount]` + langCount × { `[int32 lang]` + `[int32 textLen]` + UTF-8 text } }。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  不足 4 字节头部、截断、`keyCount`/`keyLen`/`langCount`/`textLen` 为负、`langCount = 0`、
  同一 `key + lang` 重复、`lang` 不在 0..2、尾部残留字节 → 抛 `InvalidDataException`。
* `keyCount = 0` 是合法配置（空表）：`Count = 0`、`KeyCount = 0`，所有查询回退 key，不是加载失败。
* 同 key 可拆多条（语言不重叠时合并）：`Count` = 条目数，`KeyCount` = 去重后 key 数；语言重叠即上述重复错误。

## 4. 行为规则

1. `CurrentLanguage` 初值 = 构造参数 `defaultLanguage`（默认 `ChineseSimplified`），同时充当回退语言。
2. `SetLanguageCommand`：未定义枚举值（负数/越界）→ 静默拒绝；与当前相同 → 静默忽略；合法且不同 → 改状态后 `Publish(LanguageChangedEvent)`。
3. 拒绝与忽略都不发事件、不改状态、不抛异常（命令通道不报错）。
4. `GameLanguage` 数值稳定（0 中文 / 1 英语 / 2 日语）：存档与配置只按数值存，禁止重排或复用。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止在查询路径建临时集合或拼字符串；表只读，禁止运行期改写。
* 禁止把文案堆进全局大表单例，只允许读本切片 `TbLocalization`。
* 禁止在本切片做 TMP 字体回退、语言持久化与系统语言探测（属适配层/存档职责）。

## 6. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

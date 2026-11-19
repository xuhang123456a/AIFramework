# Feature.Mail 切片边界契约

## 1. 职责定位

邮件信箱建立、7 天有效期判定、附件领取、删除与红点增量。
不含弹窗调度、背包存储实现、红点树实现与服务器补发逻辑。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`ClaimMailCommand(mailId)`、`DeleteMailCommand(mailId)`
* **只读查询（In）**：`MailListQueryRequest`（空结构体）→ `MailListSnapshot`
* **发出指令（Out）**：`GrantItemCommand(rewardItemId, rewardCount, 6301)`、`RedDotDeltaCommand(RedDotNodeIds.Mail, ±1 / +未读数)`
* **广播事件（Out）**：`MailClaimedEvent(mailId, rewardItemId, rewardCount)`
* **端口依赖（In）**：`ITimeProvider`（唯一时间源）、`IBinaryTableLoader`（加载本切片子表）

## 3. 独立子表（Config）

* 表名 `TbMail`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 mailId; int32 rewardItemId; int32 rewardCount; int32 sendDayOffset; }`，单行 16 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  `length != 4 + rowCount * 16`、`rowCount < 0`、`mailId` 重复、`rewardCount <= 0` → 抛 `InvalidDataException`。

## 4. 状态与规则

* 收件天序号 `SentDayIndex = 今天 - sendDayOffset`，构造时固化；`0 = 今天`，`1 = 昨天`。
* 有效期 7 天：`今天 - SentDayIndex >= 7` 即 `Expired`；只在查询时实时判定，不落状态机字段。
* 状态优先级：已领取 > 过期 > 未读；已领取邮件不因时间推移翻成 `Expired`。
* 领取顺序固定：`GrantItemCommand` → `MailClaimedEvent` → `RedDotDeltaCommand(Mail, -1)` → 落已领取标记。
* 领取静默拒绝（不抛异常、不发指令、不发事件）：`mailId` 不存在 / 已领取 / 已过期。
* 删除：`mailId` 不存在 → 静默拒绝；未领取 → 移除并 `RedDotDeltaCommand(Mail, -1)`，不发奖励。
  已领取邮件的删除是产品未定义项，Phase 1 按静默拒绝处理（不删、不发红点），见 §6 偏离说明。
* 红点：构造期不发总线；装配层在 `Register` 之后调 `SeedRedDot(bus)`，按"未领取条数"一次性 +N
  （含构造即已过期的行，N = 0 时不发），与删除分支的 -1 配平。
* 查询按 `mailId` 升序，与子表物理行序无关；`GetState` 对未知 `mailId` 抛 `ArgumentOutOfRangeException`。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包数量，写操作只能走 `GrantItemCommand`。
* 禁止把邮件行堆进全局大表单例，只允许读本切片 `TbMail`。

## 6. 偏离与未定义项

* 已领取邮件的 `DeleteMailCommand`：规则仅定义"存在且未领取"的删除路径，Phase 1 拒绝，待产品确认。
* `IMailListQuery` 定义了端口接口，但查询处理器的注册类型是具体请求体 `MailListQueryRequest`
  （`CqeBus` 按消息类型槽位派发，`IQueryHandler<TQuery, TResult>` 的 `TQuery` 必须是具体 struct）。

## 7. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~Features.Tests.Mail."
```

# Feature.Inventory 切片边界契约

## 1. 职责定位

背包账本的唯一写入点：受理发放/消耗指令、校验参数与余额、维护 `itemId → count` 账本，
并以只读快照对外提供数量。纯内存实现（Phase 1），不含持久化、离线补发、堆叠上限、品质与过期。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`GrantItemCommand`、`ConsumeItemCommand`（只用 `ItemId`/`Count`，原因标签原样透传不解析）
* **处理查询（In）**：`InventoryQueryRequest`（实现 `IInventoryQuery`）→ 返回 `InventorySnapshot`
* **广播事件（Out）**：`ItemGrantedEvent`、`ItemConsumedEvent`、`ItemChangeRejectedEvent`（原因取 `MetaReasonTags.NotEnoughItem` / `MetaReasonTags.InvalidRequest`）
* **端口依赖（In）**：仅 `AIFramework.Contracts.Cqe.ICqeBus`，由装配入口经 `Register` 注入
* **不发出**：不发 `GrantItemCommand` / `ConsumeItemCommand` / `RedDotDeltaCommand`，本切片是它们的终端消费者

## 3. 独立子表（Config）

无。Phase 1 不读任何配置表；道具 ID 由调用方传入或引用 `MetaItemIds`，本切片内禁止写魔法数字。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ、文件级 namespace、global using（`LangVersion=9.0`）。
* 禁止业务拒绝抛异常：参数非法/余额不足只发 `ItemChangeRejectedEvent`，只有总线重复注册才允许抛。
* 禁止在 `Handle` 内再发 `GrantItemCommand` / `ConsumeItemCommand`（自激循环），也不得直接改他切片状态。
* 禁止把 0 数量条目留在账本里；`Snapshot()` 必须按 `ItemId` 升序、稳定、不含 0 数量条目。
* 禁止暴露可写账本：`Snapshot()` 每次返回新数组，调用方修改不影响内部状态。
* `Seed` 仅供演示/测试注入初始账本，生产装配禁止调用；未 `Register` 时事件静默丢弃但不抛异常。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

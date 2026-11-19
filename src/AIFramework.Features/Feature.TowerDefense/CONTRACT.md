# Feature.TowerDefense 切片边界契约

## 1. 职责定位

Phase 1 塔防 Meta 切片：持有 8×8 棋盘状态，判定"这一格能不能建这种塔"，扣款成功后落子并广播事实。
**不含**战斗推演、伤害计算、寻路、波次编排与 UI 摆盘——战斗全部归 `Kernel.Realtime`（`BattleWorld`），
本切片只向适配层提供"塔在哪、是哪种塔"这段只读布局，由适配层翻译成世界坐标布场。

建造规则（逐条锁死）：
1. 坐标越界（`x ∉ [0,8)` 或 `y ∉ [0,8)`）、格子已占用、`towerId` 不在子表 → **静默拒绝**：不查金币、不发指令、不发事件。
2. 金币余额 = `bus.Query<InventoryQueryRequest, InventorySnapshot>(new InventoryQueryRequest(MetaItemIds.Gold))`；
   余额 < `cost` → **静默拒绝**。
3. 余额足够 → 顺序固定：`ConsumeItemCommand(Gold, cost, 6601)` → 写格子（`0` 之外即占用）→ `Publish(TowerBuiltEvent)`。
4. `cost = 0` 是合法配置（免费塔）：仍按上面三步走，`ConsumeItemCommand` 的 `Count` 为 0。
5. Phase 1 **不发任何红点指令**：建塔不改变大厅入口的可点状态，`RedDotDeltaCommand` 属于无意义噪音。
6. 无拆除 / 无升级 / 无法术：一局内棋盘只增不减，`BuildCount` 只统计成功建造。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`BuildTowerCommand`（x + y + towerId）
* **处理查询（In）**：`TowerLayoutQueryRequest`（空结构体）→ `TowerLayoutSnapshot`（宽/高/塔数/`TowerIdAt`）
* **只读查询（Out）**：`IInventoryQuery`，请求体 `InventoryQueryRequest(MetaItemIds.Gold)`
* **发出指令（Out）**：`ConsumeItemCommand`（扣金币，原因标签 6601）
* **广播事件（Out）**：`TowerBuiltEvent`（x / y / towerId / cost）
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）

## 3. 独立子表（Config）

* 表名 `TbTower`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 towerId; int32 cost; int32 damage; int32 range; }`，单行 16 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`，切片显式失败，禁止静默空表继续；
  `length != 4 + rowCount * 16`、同一 `towerId` 重复、`cost < 0`、`damage <= 0`、`range <= 0` → 抛 `InvalidDataException`。
* `rowCount = 0` 是合法配置（塔全部下架）：`Count = 0`，任何建造都走"未知塔"拒绝，不是加载失败。
* `Get(index)` 越界、`GetById(towerId)` 未命中 → 抛 `ArgumentOutOfRangeException`；切片内判定"未知塔"用 `TryFind`，不抛。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改金币数量，写操作只能走 `ConsumeItemCommand`。
* 禁止把塔表堆进全局大表单例，只允许读本切片 `TbTower`；禁止按格坐标反算世界坐标（引擎概念留在适配层）。
* 禁止在切片内做伤害结算、敌人 AI、波次推进与 UI 提示；被拒绝的建造不发任何事件。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

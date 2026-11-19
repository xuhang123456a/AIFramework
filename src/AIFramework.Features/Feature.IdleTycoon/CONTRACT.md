# Feature.IdleTycoon 切片边界契约

## 1. 职责定位

Phase 1 放置/经营玩法内核（T1-7）：按本切片子表维护建筑等级、升级扣金币，并按 `ITimeProvider` 结算离线收益。
不含背包存储实现、数值曲线、建筑解锁/建造放置、UI 面板与存档。

规则（逐条锁死）：
1. 构造后所有建筑等级为 0；`GetLevel` 传未知 ID 抛 `ArgumentOutOfRangeException`。
2. `UpgradeBuildingCommand`：未知建筑 → 静默拒绝；等级 ≥ `MaxLevel` → 静默拒绝；金币（`MetaItemIds.Gold`）< `UpgradeCost` → 静默拒绝。被拒绝时不发指令、不发事件、等级不变。
3. 合法升级顺序：`ConsumeItemCommand(Gold, UpgradeCost, 6401)` → 等级 +1 → 升级计数 +1。升级成功**不广播任何事件**，也不动红点。
4. `IncomePerSecond = Σ(level × BaseIncome)`，每次访问实时累加，不缓存快照。
5. `SettleOfflineIncome()`：`elapsed = ElapsedSeconds - 上次结算时刻`，先夹到 `[0, 86400]`（24 小时封顶，防改系统时间刷收益），产出 `floor(elapsed × IncomePerSecond)`；无论本次是否被封顶，结算后基线无条件对齐到"现在"。
6. 金额 > 0 → 先 `GrantItemCommand(Gold, amount, 6402)`，再对**每栋等级 > 0** 的建筑广播 `IdleIncomeCollectedEvent`；金额按"累计率差额法"分摊（`share_i = floor(elapsed × 累计率_i) - floor(elapsed × 累计率_i-1)`），保证各 `Amount` 之和严格等于本次总额。
7. 时钟只来自 `ITimeProvider`；测试用可控时间源快进，实现不得读墙钟。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`UpgradeBuildingCommand`（buildingId）
* **只读查询（Out）**：`InventoryQueryRequest(MetaItemIds.Gold)` → `InventorySnapshot`，只读金币数量
* **发出指令（Out）**：`ConsumeItemCommand`（升级扣款，原因标签 6401）、`GrantItemCommand`（离线收益发放，原因标签 6402）
* **广播事件（Out）**：`IdleIncomeCollectedEvent`（buildingId + 该建筑本次分摊金币）
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）、`ITimeProvider`（`ElapsedSeconds`）

## 3. 独立子表（Config）

* 表名 `TbBuilding`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 buildingId; int32 baseIncome; int32 upgradeCost; int32 maxLevel; }`，单行 16 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；长度不等于 `4 + rowCount × 16`、`buildingId` 重复、`baseIncome <= 0`、`upgradeCost < 0`、`maxLevel <= 0` → 抛 `InvalidDataException`。
* `rowCount = 0` 是合法配置（空建筑表）：`Count = 0`，任何升级都走"未知建筑"拒绝，不是加载失败。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包或金币数量，写操作只能走 `ConsumeItemCommand` / `GrantItemCommand`。
* 禁止把建筑堆进全局大表单例，只允许读本切片 `TbBuilding`；禁止在切片内做数值曲线、建筑解锁与 UI 提示。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

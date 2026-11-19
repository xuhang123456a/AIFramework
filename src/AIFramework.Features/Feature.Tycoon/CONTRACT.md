# Feature.Tycoon 切片边界契约

## 1. 职责定位

T2-11 模拟经营（Tycoon）经营循环：多资源链（建造 / 升级 / 员工）+ 显式 tick 结算，不进场、不做离线收益。
与 `Feature.IdleTycoon` 的差异：没有 `ITimeProvider`、没有离线累计；收入只由 `TycoonTickCommand` × 当前阵容产出。
不含背包存储实现、UI 面板、存档与时间驱动调度。

规则（逐条锁死）：
1. 构造后所有建筑均未建造；快照 `Buildings` 只含已建造建筑，顺序 = 子表行序。
2. 建造：未知 ID / 已建造 / 金币不足 → 静默拒绝；成功 `ConsumeItemCommand(Gold, BuildCost, 6811)` → 1 级 0 员工，`BuiltCount++`。
3. 收入：`IncomePerTick = (BaseIncome + StaffCount × (BaseIncome / 2)) × Level`（整数除法）；`TotalIncomePerTick` = 已建造建筑之和。
4. 升级：未建造 / `Level >= MaxLevel` / 金币不足 → 静默拒绝；成本 `BuildCost / 2 × Level`；扣款后 `Level++`、`UpgradeCount++`。
5. 雇员工：未建造 / `StaffCount >= 2 × Level` / 金币不足 → 静默拒绝；成本 `BuildCost / 4`；扣款后 `StaffCount++`、`HiredCount++`。
6. tick：`Ticks` 夹 1..5；每次 `Gold += TotalIncomePerTick` 并 `GrantItemCommand(Gold, total, 6812)`，`TickCount++`；收入为 0 时不发空发奖指令，但照样计数。
7. 金币缓存：`_gold` 首次需要金币时由 `InventoryQueryRequest(Gold)` 播种一次，之后只由本切片的扣款 / 发奖同步；`cost = 0` 不发零额扣款。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`BuildTycoonCommand`、`UpgradeTycoonCommand`、`HireStaffCommand`、`TycoonTickCommand`（分别只带 buildingId / ticks）
* **只读查询（In/Out）**：`InventoryQueryRequest(MetaItemIds.Gold)` → `InventorySnapshot`；`TycoonSnapshotQueryRequest` → `TycoonSnapshot`（本切片对外唯一读口）
* **发出指令（Out）**：`ConsumeItemCommand`（建造 / 升级 / 雇员工扣款，原因标签 6811）、`GrantItemCommand`（tick 收入，原因标签 6812）
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）
* **不广播任何事件**：状态变化只经快照查询读取，避免为 UI 制造第二套真相。

## 3. 独立子表 `TbTycoonBuilding`（Phase 1 夹具）

布局：小端 `[int32 rowCount]` + `rowCount × { int32 buildingId; int32 kind; int32 buildCost; int32 baseIncome; int32 maxLevel; }`，单行 20 字节。
校验：缺表（loader 返回 `null`）→ `InvalidOperationException`；头部 < 4 字节 / 长度 ≠ `4 + rowCount × 20` / 行数为负 / `buildingId` 重复 / `kind` 不在 0..2 / `buildCost < 0` / `baseIncome < 0` / `maxLevel < 1` → `InvalidDataException`。
`rowCount = 0` 是合法配置：`Count = 0`，任何建造都走"未知建筑"拒绝，tick 只推进计数。

夹具（3 行，kind 0=Stall、1=Shop、2=Restaurant）：

| buildingId | kind | buildCost | baseIncome | maxLevel |
| --- | --- | --- | --- | --- |
| 1 | Stall (0) | 100 | 5 | 5 |
| 2 | Shop (1) | 300 | 12 | 5 |
| 3 | Restaurant (2) | 800 | 30 | 3 |

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包或金币数量，写操作只能走 `ConsumeItemCommand` / `GrantItemCommand`。
* 禁止把建筑堆进全局大表单例，只允许读本切片 `TbTycoonBuilding`；禁止在本切片做数值曲线、解锁、UI 提示与离线收益。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -m
```

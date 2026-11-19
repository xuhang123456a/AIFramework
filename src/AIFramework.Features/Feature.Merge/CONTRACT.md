# Feature.Merge 切片边界契约

## 1. 职责定位

合成玩法（T2-1）：6×6 棋盘状态机 + 配置驱动的等级链。拖拽相邻同等级物品，两格清空、目标格升一级、按新等级查表发放金币。
不含卖物品、生成器冷却/掉落权重、棋盘存档与 UI 表现层。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`MergeCellsCommand(fromX, fromY, toX, toY)`、`SpawnItemCommand(x, y, level)`
* **处理查询（In）**：`MergeGridQueryRequest`（空结构体）→ `MergeGridSnapshot`（行优先等级快照，0 = 空）
* **发出指令（Out）**：`GrantItemCommand(MetaItemIds.Gold, goldAwarded, 6701)`，仅 `goldAwarded > 0` 时发
* **广播事件（Out）**：`MergePerformedEvent(newLevel, gridX, gridY, goldAwarded)`
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表 `TbMergeItem`）

## 3. 独立子表（Config）

* 表名 `TbMergeItem`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 level; int32 itemId; int32 sellGold; }`，单行 12 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；长度与声明行数不符、`level <= 0`、
  `itemId <= 0`、`level` 重复、`level` 不是从 1 连续到 `Count` → 抛 `InvalidDataException`。
* `Count` = 行数、`MaxLevel` = `Count`（等级连续）；`Get(index)` 越界、`GetByLevel(level)` 查无此行 → `ArgumentOutOfRangeException`。
* `rowCount = 0` 是退化配置而非加载失败：任何生成/合成都被静默拒绝。

## 4. 规则（逐条锁死）

1. 棋盘 6×6，构造时全空；每格存一个物品等级，0 = 空，`MaxLevel` = 满级。
2. 合法合成需同时满足：坐标在盘内、源格非空、目标格与源格等级相同、两格正交相邻（曼哈顿距离 = 1）、未满级。
3. 任一条件不满足 → 静默拒绝：不改棋盘、不计数、不发指令、不发事件（对角、隔格、同一格同样拒绝）。
4. 合法合成：源格清空、目标格 = `level + 1`、`MergeCount++`；`goldAwarded` = 新等级在 `TbMergeItem` 的 `sellGold`；
   `goldAwarded > 0` → 先 `GrantItemCommand` 再 `Publish(MergePerformedEvent)`（先结算后广播）。
5. `SpawnItemCommand`：越界、`level < 1`、`level > MaxLevel`、格子已占 → 静默拒绝；成功只落格子并 `SpawnCount++`，不发事件。
6. 查询返回防御性快照：之后棋盘怎么变都不影响已发出的快照；`LevelAt` 越界返回 -1，与空格 0 区分。

## 5. 偏离与未定义项

* 规则原文"目标格非空 → 拒绝"与"两格等级不同 → 拒绝"、"两格清空、目标格变成 level + 1"以及
  "1+1→2，再 2+2→3"的链条验收互相矛盾：目标格若必须为空，则两格等级必然不同，任何合成都无法发生。
  本实现按链式合成口径：目标格必须已放同等级物品；目标格为空或等级不同一律静默拒绝。
* 子表校验已保证等级连续 1..Count，故"合成后等级无对应行 → 0"的分支正常不可达，仅作兜底；
  "不发金币指令"的分支由 `sellGold = 0` 的用例覆盖。
* 卖物品、生成器掉落概率、棋盘持久化均未实现；`SellGold` 只作配置读取。
* `Contracts` 无缺口：本次未改动任何 `Contracts` 文件。

## 6. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~Features.Tests.Merge."
```

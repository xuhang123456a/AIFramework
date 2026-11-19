# Feature.Gacha 切片边界契约

## 1. 职责定位

抽卡/扭蛋：次数校验、金币费用、按整数权重抽卡、10 次省底改判、发货与事件广播。
不含背包/货币存储实现、概率公示 UI、限时卡池、保底跨池继承与服务器补单。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`DrawGachaCommand(count)`，`count` 只接受 1（单抽）/ 10（十连）
* **只读查询（Out）**：`IInventoryQuery` → `InventoryQueryRequest(MetaItemIds.Gold)`
* **发出指令（Out）**：`ConsumeItemCommand(Gold, 100/900, 6501)`、`GrantItemCommand(itemId, 1, 6502)`
* **广播事件（Out）**：`GachaDrawnEvent(itemId, isRare, pityTriggered)`，每抽中一项发一条
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）、`IRandomSource`（唯一随机源）

## 3. 独立子表（Config）

* 表名 `TbGachaPool`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 itemId; int32 weight; int32 isRare; }`，单行 12 字节，`isRare` 只能为 0/1。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  `length != 4 + rowCount * 12`、`rowCount < 0`、`itemId` 重复、`weight <= 0`、`isRare` 非 0/1、
  没有稀有项、权重和超出 `int` 上限 → 抛 `InvalidDataException`。
* `rowCount = 0` 不是"空池子合法"：没有稀有项时保底规则无法成立，按非法配置拒绝。
* `Count` = 行数；`TotalWeight` = 全部整数权重之和；`Get(index)` 越界抛 `ArgumentOutOfRangeException`。

## 4. 规则（逐条锁死）

1. `count` 非 1/10 → 静默拒绝：不查余额、不取随机、不扣费、不发指令、不发事件。
2. 费用：单抽 100 金币、十连 900 金币，货币 `MetaItemIds.Gold`；余额 < 费用 → 静默拒绝（余额恰好等于费用可抽）。
3. 顺序固定：`ConsumeItemCommand` → 逐抽 `GrantItemCommand` + `GachaDrawnEvent` → 最后落 `LastResult`。
4. 抽卡：每抽调用一次 `IRandomSource.NextInt(0, TotalWeight)`，按行序累加权重落行；同种子 + 同表 + 同保底状态必得同序列。
5. 保底：非稀有 → `PityCounter + 1`；累计到 10（第 10 次仍非稀有）→ 丢弃本次自然结果，
   再用 `IRandomSource.NextInt(0, 稀有行数)` 在稀有池中取一项，`pityTriggered = true`，计数归零；
   自然抽中稀有同样把计数归零。
6. 每抽中 → `GrantItemCommand(itemId, 1, 6502)` → `Publish(GachaDrawnEvent)`；十连按抽卡顺序交错发 10 组。
7. `LastResult` 在整次命令处理完后赋值；被拒绝的命令不改动 `LastResult` 与 `PityCounter`。
8. `GachaResult.PityBefore` = 本次命令开始前的计数；十连中任一项保底触发则 `PityTriggered = true`。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包/货币数量，写操作只能走 `ConsumeItemCommand` / `GrantItemCommand`。
* 禁止把抽卡行堆进全局大表单例，只允许读本切片 `TbGachaPool`。

## 6. 偏离与未定义项

* 保底改判发生在自然结果已消耗一次随机之后，因此触发保底的该抽共消耗 2 个随机数（权重取整 + 稀有池取整），回放须按此顺序。
* 保底计数是切片内单实例状态：不落盘、不跨卡池继承；Phase 1 无"保底进位/跨十连结余"规则。
* 概率公示文本不由本切片生成，UI 用 `Weight / TotalWeight` 自行换算（只读 `Get` / `TotalWeight`）。
* `Contracts` 无缺口：本次未改动任何 `Contracts` 文件。

## 7. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~Features.Tests.Gacha."
```

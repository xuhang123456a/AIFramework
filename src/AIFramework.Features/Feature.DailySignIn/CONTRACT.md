# Feature.DailySignIn 切片边界契约

## 1. 职责定位

管理 7 日每日签到的周期推进、当日领取防重、补签卡校验与奖励发放请求。
不含弹窗调度、红点、背包存储实现与服务器日切广播。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`ClaimDailySignInCommand`（空结构体）、`RetroactiveSignInCommand`（绝对天序号）
* **只读查询（Out）**：`IInventoryQuery`，请求体为 `InventoryQueryRequest(9001)`，只读补签卡数量
* **发出指令（Out）**：`GrantItemCommand`（发奖，原因标签 5001）、`ConsumeItemCommand`（扣补签卡 9001，原因标签 5002）
* **广播事件（Out）**：`DailySignInClaimedEvent`（绝对天序号 + 本次奖励 itemId/itemCount）
* **端口依赖（In）**：`ITimeProvider`（唯一时间源）、`IBinaryTableLoader`（加载本切片子表）
* **UI 视图契约**：驱动 `ISignInView`（`.g.cs` 自动生成接口，勿手改；Presenter 只读状态、只发命令）

## 3. 独立子表（Config）

* 表名 `TbSignInReward`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 dayIndex; int32 itemId; int32 itemCount; }`，单行 12 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`，切片显式失败；
  `length != 4 + rowCount * 12`、`rowCount != 7`、`dayIndex` 越出 1..7 或重复 → 抛 `InvalidDataException`。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包数量，写操作只能走 `GrantItemCommand` / `ConsumeItemCommand`。
* 禁止把签到奖励堆进全局大表单例，只允许读本切片 `TbSignInReward`。
* 禁止在 Presenter 内做奖励计算或改状态，状态只允许 `SignInHandler` 改写。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
```

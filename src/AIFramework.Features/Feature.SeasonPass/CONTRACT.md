# Feature.SeasonPass 切片边界契约

## 1. 职责定位

只做"赛季经验累计 → 等级推进 → 免费/付费双轨奖励领取 + 发奖指令 + 事件广播 + 剩余时间/过期判定"。
不碰 UI、红点、存档、IAP 收据校验、背包实现，也不决定经验从哪来（由调用方上报）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`AddSeasonXpCommand(amount)`、`ClaimSeasonRewardCommand(level, paidTrack)`、`UnlockPaidTrackCommand`。
* **只读查询（Out）**：`SeasonPassQueryRequest` → `SeasonPassSnapshot`（等级 / 经验 / 双轨领取状态 / 剩余秒数）。
* **发出指令（Out）**：`GrantItemCommand`（`AIFramework.Contracts.Meta`，领取发奖，原因标签 `6501`）。
* **广播事件（Out）**：`SeasonLevelUpEvent(level)`、`SeasonRewardClaimedEvent(level, paidTrack, itemId, count)`。
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）、`ITimeProvider`（剩余时间与过期判定）。

## 3. 独立子表（Config）

* 表名 `TbSeasonPass`，列顺序即字节顺序：
  `level:int32`、`requiredXp:int32`、`freeItemId:int32`、`freeCount:int32`、`paidItemId:int32`、`paidCount:int32`。
* 布局：小端 `[int32 rowCount]` + rowCount × 6 个 int32（单行 24 字节，全数值定长）。
* `requiredXp` 是**达到该等级所需的累计总经验**（不是升级增量）：1 级为 0，逐级严格递增。
* 校验：表缺失（loader 返回 `null`）→ 抛 `InvalidOperationException`；截断/尾部残留/level 不从 1 连续递增/
  requiredXp 不递增/freeItemId 或 freeCount 非正/paidItemId 或 paidCount 为负/付费列只置 0 一半 → 抛 `InvalidDataException`。
* 0 行是合法配置：查询返回退化快照（Level = 0、IsMaxLevel = true、Items 为空），命令静默无效。
* 付费列允许整对为 0：表示该等级无付费奖励（paidItemId 为 0 时 paidCount 必须为 0）。

## 4. 关键规则（测试已钉死）

* `amount <= 0`、空表 → 经验命令静默忽略；跨 N 级按等级升序逐级广播 `SeasonLevelUpEvent`。
* 满级后经验继续累加、不再升级：快照 `CurrentXp` 保留真实值，`NextLevelXp` 退化为等于 `CurrentXp`。
* 领取拒绝条件（全部静默：不发指令、不广播、不改状态）：等级越界、等级未到达、重复领取、
  付费轨未解锁、该级无付费奖励、赛季已过期。命令无返回值，拒绝原因由快照字段自解释。
* 成功领取顺序固定：先 `GrantItemCommand`，再广播 `SeasonRewardClaimedEvent`，最后落领取位。
* 付费轨解锁只认 `UnlockPaidTrackCommand`（由 IAP 适配层发出），重复调用幂等、不广播事件。
* 过期判定：注入 `ITimeProvider` + 构造参数 `seasonEndUnixSeconds`，`now >= end` 即过期，
  过期后 `RemainingSeconds = 0`、`ClaimableCount = 0`、所有领取被拒；经验累计不因过期停止（过期只冻结领取）。
* `ClaimableCount` = 已到达等级中未领的免费轨 + 已解锁付费轨中未领且有奖励的付费轨。

## 5. 绝对禁区

* 禁止引用任何其他 `AIFramework.Features.*` 命名空间（AIF001）；禁止 `Core`/`Kernels`/`Adapters`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包、直接碰红点树或存档；禁止把通行证表堆进全局 `Tables` 单例。
* 命令/查询/事件只允许定义在本切片目录内，禁止进 `Contracts`。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~SeasonPass"
dotnet run --project tools/tablegen -- --check
```

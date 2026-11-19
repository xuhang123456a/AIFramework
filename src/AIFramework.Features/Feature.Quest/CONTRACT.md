# Feature.Quest 切片边界契约

## 1. 职责定位

管理任务列表的进度推进（每局 +1、封顶）、完成领奖防重与红点增量请求。
不含任务弹窗调度、红点树维护、背包存储实现、战斗结果（胜负/关卡）过滤与任务配置生成。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`ClaimQuestRewardCommand(questId)`
* **订阅事件（In）**：`GameFinishedEvent`（一局结束事实，不区分胜负/关卡）
* **只读查询（Out）**：`IQuestListQuery`，请求体 `QuestListQueryRequest`（空结构体）
* **发出指令（Out）**：`GrantItemCommand(rewardItemId, rewardCount, 6201)`、`RedDotDeltaCommand(RedDotNodeIds.Quest, ±1)`
* **广播事件（Out）**：`QuestProgressedEvent`（进度真变化）、`QuestClaimedEvent`（发奖后）
* **端口依赖（In）**：`ICqeBus`（构造注入，发送侧）、`IBinaryTableLoader`（加载本切片子表）

## 3. 独立子表（Config）

* 表名 `TbQuest`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 questId; int32 targetCount; int32 rewardItemId; int32 rewardCount; }`，单行 16 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`，切片显式失败；
  `length != 4 + rowCount * 16`、`questId` 重复、`targetCount <= 0` → 抛 `InvalidDataException`。
* 解析后按 `questId` 升序排列：`Get(index)` 顺序、列表查询顺序、`GetState` 二分查找共用同一顺序。
* 状态初值：每条任务 `Progress = 0`、`Claimed = false`。

## 4. 行为规则

1. `GameFinishedEvent` → **所有未领取任务**进度 +1，封顶 `targetCount`；进度真变化时先 `Publish(QuestProgressedEvent)`、再 `Send(RedDotDeltaCommand(+1))`。
2. `ClaimQuestRewardCommand` 合法路径顺序固定：`GrantItemCommand` → `QuestClaimedEvent` → `RedDotDeltaCommand(-1)` → `Claimed = true`。
3. 未知 questId / 未完成（`progress < target`）/ 已领取 → **静默拒绝**：不发任何指令、不发事件、状态不变（命令通道不抛异常）。
4. `GetState(questId)` 未知 ID → `ArgumentOutOfRangeException`：那是调用方编程错误，不是业务拒绝。
5. 查询按 `questId` 升序返回全量任务（含未完成、已领取）；已领取任务不再推进进度。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包数量，写操作只能走 `GrantItemCommand`。
* 禁止把任务配置堆进全局大表单例，只允许读本切片 `TbQuest`。
* 禁止在切片外改写 `QuestState.Progress` / `QuestState.Claimed`（`internal set` 仅限本程序集）。

## 6. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

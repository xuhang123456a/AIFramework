# Feature.Guide 切片边界契约

## 1. 职责定位

表驱动的 FTUE（新手引导）步骤机：一步一条配置，靠外部事实触发"可操作"，靠显式上报推进。
不含浮层/高亮/遮罩的绘制、不含点击拦截与坐标解析、不含存档落盘（只产出可序列化快照）、
不含发奖与红点点亮（引导奖励由别的切片或适配层决定）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`GuideTriggerCommand(trigger, targetId)`（外部事实）、
  `GuideAdvanceCommand(stepId)`（完成当前步）、`GuideSkipCommand(stepId)`（跳过当前步）、
  `GuideSkipAllCommand()`（不再提示）
* **只读查询（Out）**：`IGuideQuery`，请求体 `GuideQueryRequest`（空结构体）→ `GuideSnapshot`
* **广播事件（Out）**：`GuideStepChangedEvent(stepId, title)`（触发命中或切换步骤）、
  `GuideCompletedEvent()`（走完全部步骤）
* **发出指令（Out）**：无。本切片不发任何命令（不发奖、不点红点、不碰背包）
* **端口依赖（In）**：`ICqeBus`（构造注入，只用于广播）、`IBinaryTableLoader`（加载本切片子表）

## 3. 独立子表（Config）

* 表名 `TbGuideStep`，列顺序即解析顺序：`stepId` / `title` / `hint` / `trigger` / `targetId` / `skippable`。
* 布局：小端 `[int32 rowCount]` + 每行 `{ int32 stepId; [int32 len]+UTF-8 title; [int32 len]+UTF-8 hint;
  int32 trigger; int32 targetId; int32 skippable }`。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  截断 / 尾部残留 / `stepId` 不从 1 连续 / `trigger` 不在 0..5 / `targetId < 0` /
  `skippable` 非 0/1 / `title` 为空 / 步数 > 32 → 抛 `InvalidDataException`。
* 解析后行按 `stepId` 升序且强制连续，故恒有 `stepId == 下标 + 1`（位图与 O(1) 定位依赖它）。
* `trigger` 语义：0=启动、1=进入大厅、2=打开商店、3=完成一场战斗、4=领取签到、5=达到等级。
  `targetId` 是该事实的具体 id（0=不限定）；`skippable=1` 表示该步允许跳过。

## 4. 行为规则

1. 构造后激活第 1 步、`Reached=false`；空表视为已完成（`IsCompleted=true`、`TotalCount=0`），不抛。
2. `GuideTriggerCommand` 命中当前步（`trigger` 相等，且 `targetId` 为 0 或相等）→ `Reached=true`
   并广播一次 `GuideStepChangedEvent`；重复命中不重复广播。**它不推进步骤**。
3. `GuideAdvanceCommand` 只有"等于当前步 **且** `Reached=true`"才推进：落完成位、`CompletedCount+1`，
   然后广播下一步的 `GuideStepChangedEvent`；最后一步完成 → `IsCompleted=true`、失活、广播 `GuideCompletedEvent`。
   未 `Reached` / 步号不符 / 引导未激活 → 静默忽略。
4. `GuideSkipCommand` 仅"等于当前步且 `skippable=1`"生效：落跳过位（不计入 `CompletedCount`）并推进；
   跳过最后一步同样广播 `GuideCompletedEvent`。其余静默忽略。
5. `GuideSkipAllCommand` → `IsSkippedAll=true`、`IsActive=false`，**不发任何事件**；
   已完成 / 空表 / 重复调用静默忽略。
6. 快照 `GuideSnapshot` 只在 `IsActive=true` 时填当前步字段，否则全 0 / 空串；
   `Steps` 每项含 `completed` / `skipped` 两个互斥标记。
7. 存档：`CaptureState()` → `GuideSaveState(currentStepId, completedMask, skippedMask, skippedAll)`；
   `TryRestoreState(in state)` 校验通过才整体覆盖（`Reached` 重置为 false、不广播事件），坏状态返回 `false` 且不改状态。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）；
  存档只出快照，落盘由适配层用 `SaveService` 负责。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接发 `GrantItemCommand` / `RedDotDeltaCommand` 等跨切片指令。
* 禁止把引导配置堆进全局大表单例，只允许读本切片 `TbGuideStep`。
* 禁止在切片外改写 `_completed` / `_skipped`（状态只能经本类的 CQE 处理器与 `TryRestoreState` 变更）。

## 6. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

# Feature.ActivitySchedule 切片边界契约

## 1. 职责定位

表驱动活动排期：只回答"此刻哪些活动是开启的、还有多久关、今天该不该做每日重置"。
不持有活动进度与领奖状态、不发奖、不点红点、不画 UI、不落存档、不管活动玩法本身
（进度与奖励归各活动切片，本切片只输出排期事实与重置信号）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`ActivityTickCommand`（让切片按当前时间重算开启集合，幂等）。
* **只读查询（Out）**：`ActivityQueryRequest` → `ActivityScheduleSnapshot`。
* **广播事件（Out）**：`ActivityOpenedEvent(activityId)`、`ActivityClosedEvent(activityId)`（只在集合变化时发）。
* **端口依赖（In）**：`ICqeBus`（构造注入，只用于广播）、`ITimeProvider`（窗口与跨日判定）、
  `IBinaryTableLoader`（加载本切片子表）。**不发任何跨切片指令。**

## 3. 独立子表（Config）

* 表名 `TbActivitySchedule`，列顺序即解析顺序：`activityId` / `name` / `startUnixSeconds` /
  `endUnixSeconds` / `dailyResetHourUtc` / `rotationGroup` / `priority`（6 个 int32 + 1 个 string）。
* 布局：小端 `[int32 rowCount]` + 每行 `{ int32 activityId; [int32 len]+UTF-8 name;
  int32 startUnixSeconds; int32 endUnixSeconds; int32 dailyResetHourUtc; int32 rotationGroup; int32 priority }`。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  截断 / 尾部残留 / `activityId <= 0` 或重复 / `name` 为空 / `endUnixSeconds <= startUnixSeconds` /
  `dailyResetHourUtc` 不在 0..23 / `rotationGroup < 0` → 抛 `InvalidDataException`。
* 0 行是合法配置：快照为空、无事件、不崩。行顺序即快照顺序与事件顺序，载入后不排序。

## 4. 关键规则（测试已钉死）

1. `isOpen` = `startUnixSeconds <= now < endUnixSeconds`（**左闭右开**）：结束那一秒已算关闭。
2. `secondsRemaining` = `endUnixSeconds - now`（按查询时刻折算，夹到 ≥ 0）；已关闭或未开始为 0。
3. 轮换：同 `rotationGroup` 内若多个活动同时落在窗口内，只保留 `priority` 最大者，
   相等取 `activityId` 小者；被挤掉的活动仍在快照里可见（`isOpen=false`）。不同组互不影响。
4. 每日重置：仅对已开启活动，当 UTC 日序号（`now / 86400` 向下取整）与该活动
   "上次报过重置的日序号"不同、且当日 UTC 小时数 `>= dailyResetHourUtc` → 本拍 `isDailyResetDue=true`。
   水位只在真正报出时为 true 的那拍推进：跨日首拍落在 reset 小时之前时**不**推进，
   否则同一天稍后过了 reset 小时那拍会漏判；水位初值为"从未报过"，进程跨午夜重启后首拍可补报。
   同一天只报一次；只报信号，不持有也不改写任何活动进度。
5. 事件：与上一拍逐位比对，新开 → `ActivityOpenedEvent`，关闭 → `ActivityClosedEvent`；
   `ActivityTickCommand` 幂等，无变化不发事件。事件顺序 = 配置表行顺序。
6. 顺序固定：先把开启集合与重置标记整体落定，再广播事件；订阅方回调里查询到的是最终态。
7. 快照的开启态只来自最近一次 tick（查询不重算，保证快照与事件同源）；
   `NowUnixSeconds` 与 `secondsRemaining` 按查询时刻的时间源折算。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）；
  禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止发 `GrantItemCommand` / `RedDotDeltaCommand` 等跨切片指令；禁止把本表堆进全局 `Tables`。
* 禁止在切片外改写 `_open` / `_resetDue` / `_resetDay`（状态只经 `ActivityTickCommand` 变更）。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~ActivitySchedule"
dotnet run --project tools/tablegen -- --check
```

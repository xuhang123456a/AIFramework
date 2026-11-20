# Feature.Experiment 切片边界契约

## 1. 职责定位

表驱动 A/B 实验：只回答"这个玩家在每个实验里落在 A 组还是 B 组、曝光了几条"。
不控制功能开关的真实生效（业务侧读到 `Variant` 后自己判断）、不发奖、不点红点、不画 UI、
不落存档、不做统计聚合与看板、不承接服务端下发分组（服务端实验走别的端口）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`ExperimentEvaluateCommand(playerId)`（按玩家确定性分桶并记录曝光，幂等）。
* **只读查询（Out）**：`ExperimentQueryRequest` → `ExperimentSnapshot`。
* **广播事件（Out）**：`ExperimentExposedEvent(experimentId, variant, playerId)`（每个 `(experimentId, playerId)` 只发一次）。
* **端口依赖（In）**：`ICqeBus`（构造注入，只用于广播）、`IBinaryTableLoader`（加载本切片子表）。
  **不发任何跨切片指令，也不直接依赖 `IAnalyticsService`**：曝光事实走总线，由适配层翻译成埋点。

## 3. 独立子表（Config）

* 表名 `TbExperiment`，列顺序即解析顺序：`experimentId` / `key` / `variantA` / `variantB` /
  `trafficPercent` / `enabled`（5 个 int32 + 1 个 string + 1 个 int32）。
* 布局：小端 `[int32 rowCount]` + 每行 `{ int32 experimentId; [int32 len]+UTF-8 key;
  int32 variantA; int32 variantB; int32 trafficPercent; int32 enabled }`（行定长 24 字节 + key 字节）。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；
  截断 / 尾部残留 / `experimentId <= 0` 或重复 / `key` 为空或重复 / `trafficPercent` 不在 0..100 /
  `enabled` 非 0/1 → 抛 `InvalidDataException`。
* 0 行是合法配置：快照为空、无事件、不崩。行顺序即快照顺序与曝光事件顺序，载入后不排序。

## 4. 关键规则（测试已钉死）

1. **确定性分桶**：`hash = FNV-1a32(UTF-8(key) + ':' + 十进制(playerId))`，`bucket = hash % 100`，
   `bucket < trafficPercent` → B 组（`variantB`），否则 A 组（`variantA`）。
   **禁止 `string.GetHashCode()`**（.NET Core 起每进程随机化，重启就换组）、禁止 `Random` 与时间参与。
   等价字符串是 `key + ":" + playerId`；`ExperimentBucketing` 公开同一纯函数供运营/工具侧试算。
2. **只有 `enabled=1` 参与分桶与曝光**；禁用行在快照里 `Enabled=false`、`IsB=false`、`Variant = variantA`。
3. **曝光去重**：同一 `(experimentId, playerId)` 只广播一次 `ExperimentExposedEvent` 并只计一次数；
   `ExperimentEvaluateCommand` 幂等——重复调用分配不变、不发事件、`ExposedCount` 不增长。
4. **未 evaluate 前查询**：`Assignments` 为空、`ExposedCount = 0`（查询不做隐式分桶，曝光只由命令驱动）。
5. **`ExposedCount`** = 本切片自构造以来广播过的曝光条数（按 pair 去重累加，单调不减）；
   单玩家会话下即"该玩家被曝光的实验数"。换玩家 evaluate 会为新玩家补发。
6. 顺序固定：先整体落定分配与计数，再按配置表行顺序广播曝光；订阅方回调里查询到的是最终态。
7. `IsB` 是分桶结论而非"`Variant == variantB`"：配置允许 A/B 同值（对照组），此时两者不等价。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）；
  禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止发 `GrantItemCommand` / `RedDotDeltaCommand` 等跨切片指令；禁止把本表堆进全局 `Tables`。
* 禁止在切片外改写 `_assignments` / `_exposedPairs`（分配与曝光只经 `ExperimentEvaluateCommand` 演进）。
* 禁止在别处复制一份分桶实现：跨端/服务端必须复用 `ExperimentBucketing` 的字节口径。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~Experiment"
dotnet run --project tools/tablegen -- --check
```

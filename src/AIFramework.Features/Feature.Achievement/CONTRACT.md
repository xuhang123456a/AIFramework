# Feature.Achievement 切片边界契约

## 1. 职责定位

只做"成就进度累计 → 跨 target 解锁判定 → 发奖指令 + 解锁事件"。
不碰 UI、红点、存档、服务端校验、背包数量实现，也不决定何时杀敌/通关/抽卡/签到/产出金币（由调用方上报）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`AchievementRecordCommand(int metric, int amount)`，累加指定指标。
* **只读查询（Out）**：`AchievementQueryRequest` → `AchievementSnapshot`（Total / UnlockedCount / Items 只读数组）。
* **发出指令（Out）**：`GrantItemCommand`（`AIFramework.Contracts.Meta`，解锁发奖，原因标签 `6401`）。
* **广播事件（Out）**：`AchievementUnlockedEvent(achievementId, name, rewardItemId, rewardCount)`。
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）。

## 3. 独立子表（Config）

* 表名 `TbAchievement`，列顺序即字节顺序：
  `achievementId:int32`、`name:string`、`metric:int32`、`target:int32`、`rewardItemId:int32`、`rewardCount:int32`。
* 布局：小端 `[int32 rowCount]` + rowCount × { 6 个字段，string 为 `[int32 UTF-8 字节长度]` + 字节 }，无固定行宽。
* 校验：表缺失（loader 返回 `null`）→ 抛 `InvalidOperationException`；截断/尾部残留/ID 重复/
  metric 不在 0..4/target <= 0/rewardCount < 0/name 为空 → 抛 `InvalidDataException`。
* 0 行是合法配置：查询返回空快照（Total = 0），不抛异常。

## 4. 关键规则（测试已钉死）

* `amount <= 0`、未知 metric 一律**静默忽略**（不抛、不广播、不改状态）。
* 指标语义：0 击杀数、1 通关次数、2 抽卡次数、3 签到天数、4 金币累计。
* 一次上报可跨多个阶梯，按 `achievementId` 升序依次解锁；同一成就只解锁一次。
* 每个成就解锁顺序固定：先 `GrantItemCommand`，再广播 `AchievementUnlockedEvent`。
* 内部累计值按真实增量饱和累加（不回绕）；**快照 `Current` 截断到 target**，累计本身不截断。

## 5. 绝对禁区

* 禁止引用任何其他 `AIFramework.Features.*` 命名空间（AIF001）；禁止 `Core`/`Kernels`/`Adapters`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包、直接碰红点树或存档；禁止把成就表堆进全局 `Tables` 单例。
* 命令/查询/事件只允许定义在本切片目录内，禁止进 `Contracts`。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~Achievement"
dotnet run --project tools/tablegen -- --check
```

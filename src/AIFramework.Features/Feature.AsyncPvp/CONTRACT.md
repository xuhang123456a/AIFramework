# Feature.AsyncPvp 切片边界契约（T2-14 异步 PVP）

## 1. 职责定位

Meta 切片：挑战离线对手的防守快照，用纯算术结算比分/胜负/段位/连胜，发奖并广播事实。
不驱动实时战斗、不做网络匹配、不存防守录像（Phase 1 用服务端快照的本地确定性夹具代替）。

结算规则（逐条锁死）：
1. 得分：`attackerScore = Min(9999, attackerPower * 10 + seed % 100)`；`defenderScore = Min(9999, defender.Power * 10 + defender.RandomSeed % 100)`（同一 seed，纯算术）。
2. 胜负：分高者胜；平分按 power 高者胜；分数与 power 全相同 → 进攻方胜（平局进攻方略优口径）。
3. 段位：胜 +20，连胜 ≥3 时 +30；负 -15，连败 ≥3 时 -20；rating 下限 0，`RatingDelta` 报实际变化（触底时可能 -10 或 0）。
4. `Streak`：胜 `max(1, Streak + 1)`，负 `min(-1, Streak - 1)`；正=连胜、负=连败、0=未打。
5. 每场胜利发 `GrantItemCommand(MetaItemIds.Gold, 20, 6801)`；连胜为 3 的倍数再发 `GrantItemCommand(MetaItemIds.RetroactiveCard, 1, 6802)`。
6. 成功结算后依次：发奖指令 → `Publish(AsyncDuelResolvedEvent)` → `DuelCount++` → 更新 `LastResult`。
7. 非法输入（`attackerPower < 0`、`> 1000`，`defender.Power < 0`）→ 静默拒绝：段位/连胜/场次/事件全部不动。

## 2. 跨模块契约依赖（唯一合法入口）

契约全部在 `src/AIFramework.Contracts/Meta/AsyncPvpContracts.cs`（本切片不重复定义消息/快照）：

* 处理指令（In）：`AsyncDuelCommand(DefenseSnapshot defender, int attackerPower)`（3 个只读属性）
* 处理查询（In）：`PvpProfileQueryRequest` → `PvpProfileSnapshot`；`PvpOpponentsQueryRequest` → `PvpOpponentList`
* 发出指令（Out）：`GrantItemCommand`（金币 6801 / 里程碑补签卡 6802）
* 广播事件（Out）：`AsyncDuelResolvedEvent(AsyncDuelResult Result)`
* 端口依赖（In）：`ICqeBus`（构造注入；`Register` 注册 1 命令 + 2 查询）

## 3. 对手池与快照隔离

* 固定 3 个对手，第 i 个用 `slot = DuelCount + i`：`playerId = 100 + slot % 3`、`power = 50 + slot * 7 % 50`、`randomSeed = 20261120 + slot * 13`、`layoutId = slot % 4`。
* 真实环境由服务端下发对手（按隐藏分匹配真人防守录像），本地生成仅为确定性夹具。
* 查询零副作用、每次返回新建快照；`PvpOpponentList` 构造时复制数组，改返回值不污染内部状态。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包/货币，发奖只能走 `GrantItemCommand`。
* 禁止在切片内做匹配、录像存储、实时战斗与 UI 提示；被拒绝的请求不发任何事件。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -m
```

# Feature.IdleRpg 切片边界契约

## 1. 职责定位

挂机 RPG（T2-12）：自动战斗 tick 结算、关卡推进、英雄等级/战力成长。
与 `Feature.IdleTycoon`（按时间累计经营产出）不同：本切片**不读时间**，只认 `IdleBattleTickCommand` 的确定性推进。
不含存档、离线收益结算、装备背包、数值曲线配置与 UI 面板。

规则（逐条锁死）：

1. 初始：`level=1, exp=0, power=10, goldPerSecond=1, highestStage=1, currentStage=1`，且已在打第 1 关（`inBattle=true`、`enemyHp`=表内满血、`battleTick=0`）；构造期不发任何指令/事件。
2. `power = 10 + (level-1)*6`、`goldPerSecond = level`，每次访问实时计算；`initialHeroLevel < 1` 按 1 处理。
3. 一个战斗 tick：`battleTick++` → 英雄先出手打 `power` 点；敌人血量归零即通关，本 tick 不再反击；敌人未死且 `battleTick % 3 == 0` 时反击，只累加 `DamageTaken`（不扣英雄血，无死亡/失败状态）。
4. 通关：`GrantItemCommand(Gold, goldReward, 6911)` → `StageClearCount++` → `exp += 10*stage` → `highestStage = max(highestStage, stage)` → 表里有下一关则自动进入（血量回满、tick 归零），否则停在最后一关并 `inBattle=false`、`enemyHp=0`、`battleTick=0`。
5. `IdleBattleTickCommand.ticks` 夹到 `1..10`（构造期归一，处理期再夹一次兜底 `default` 结构体）；逐 tick 执行，战斗已结束立即停止。
6. `AdvanceStageCommand`：仅当当前关已通关，或本次会话首关尚未执行过任何 tick（"首次进入"）时生效；目标关 `currentStage+1` 不在表里则静默拒绝。跳关不发奖、不给经验。
7. `LevelUpHeroCommand`：`exp >= level*100` 时 `exp -= level*100; level++`（消耗用升级前的等级，经验永不为负），一次命令最多升 1 级；不足则静默拒绝。
8. 所有拒绝都是静默的：状态不变、不发指令、不发事件；金币只走 `GrantItemCommand`，处理器不缓存余额。
9. 快照：`enemyHp` 当前剩余（无战斗为 0）/ `enemyMaxHp` 来自子表 / `inBattle` 当前是否在打 / `battleTick` 当前这场战斗的 tick 数。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`IdleBattleTickCommand(ticks)`、`AdvanceStageCommand`、`LevelUpHeroCommand`（三者与快照均在 `AIFramework.Contracts.Meta`，本切片不自建消息类型）
* **只读查询（In）**：`IdleRpgSnapshotQueryRequest` → `IdleRpgSnapshot`
* **发出指令（Out）**：`GrantItemCommand(MetaItemIds.Gold, goldReward, 6911)`，仅通关时发
* **端口依赖（In）**：`IBinaryTableLoader`（只加载本切片子表）
* **不依赖**：`ITimeProvider`、`IRandomSource`、背包查询（金币余额由外部切片读）

## 3. 独立子表（Config）

* 表名 `TbIdleStage`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 stageId; int32 enemyHp; int32 enemyAttack; int32 goldReward; int32 requiredPower; }`，单行 20 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`；长度不足 4 字节、`rowCount < 0`、长度 ≠ `4 + rowCount × 20`、`stageId` 非"从 1 起逐行连续"（重复或跳号）、`enemyHp <= 0`、`enemyAttack < 0`、`goldReward < 0`、`requiredPower < 0` → 抛 `InvalidDataException`。
* `rowCount = 0` 合法：`Count = 0`，处理器进入"无战斗"退化态（tick / advance / levelup 全静默无效）。
* `Get(index)` 越界抛 `ArgumentOutOfRangeException`；`TryFind(stageId)` 未命中返回 `false`。
* Phase 1 夹具（5 行，`(stageId, enemyHp, enemyAttack, goldReward, requiredPower)`）：`(1,30,2,20,10)`、`(2,60,4,40,16)`、`(3,120,6,80,28)`、`(4,240,9,160,46)`、`(5,480,12,320,70)`。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包或金币数量，写操作只能走 `GrantItemCommand`。
* 禁止把关卡堆进全局大表单例，只允许读本切片 `TbIdleStage`。

## 5. 偏离与未定义项

* 规则 3 的 `DamageTaken` 未出现在锁定的属性清单中，但"每 3 tick 反击计数"必须有观测面，故**新增**只读属性 `IdleRpgHandler.DamageTaken`；锁定成员一个都没改。
* `battleTick` 按"当前这场战斗"计价：进入新关归零，反击节奏因此以每关为单位重新起算（跨关不连续）。
* 规则 6 的"已通关"在正常推进时会被自动进关立即消费，只有最后一关会停留；"首次进入"窗口因此限定在**本次会话首关且一个 tick 都还没打**，避免"刚进关就能连跳"的推进链漏洞。
* `requiredPower` 只做子表校验与展示（`IdleStageDef.RequiredPower`），Phase 1 不参与战斗判定（本切片没有死亡机制可以挡）。
* 未定义：装备/词条等属性成长（只有"等级 → 战力"一条线性曲线）、离线战斗结算、关卡掉落表与扫荡。

## 6. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -m --filter "FullyQualifiedName~Features.Tests.IdleRpg."
```

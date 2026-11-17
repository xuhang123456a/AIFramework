# Feature.HeroC_Shield 切片边界契约

## 1. 职责定位

护盾反射技能的单帧交互阶段：持有 `UnitShieldActive` 标签的单位在自身判定圈内收集飞行物，
按标签过滤后逐条提交反射意图。不含飞行物归属改写、伤害结算、反射特效与动画事件。

## 2. 跨模块契约依赖（唯一合法入口）

* **实现接口（In）**：`ISkillInteractionPhase`，由强实时内核固定管线按帧驱动（本切片不引用内核程序集）
* **帧内只读（In）**：`PhaseContext`（`Owner` / `World` / `Projectiles` / `Spatial`），全部为只读视图
* **写入通道（Out）**：`IIntentWriter.Submit(in ReflectProjectileIntent)`，只提交意图不改世界
* **标签仲裁（In）**：`GameplayTags.UnitShieldActive`、`GameplayTags.ProjUnreflectable`、`GameplayTags.ProjReflected`
* **句柄协议（In/Out）**：`UnitHandle`、`ProjectileHandle`
* **UI 视图契约**：无

## 3. 独立子表（Config）

* 无。反射半径只由构造函数参数 `queryRadiusPadding`（默认 0.5f）决定，不读任何配置表。

## 4. 绝对禁区

* 禁止出现任何英雄专名或具体类型判断（`is HeroX`、类名特判、`switch` 类型分发），只认标签与句柄。
* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止帧内堆分配：只允许 `stackalloc` 定长缓冲与值类型，禁止 `List` / 数组 / 闭包 / 字符串拼接 / 装箱。
* 禁止帧内直接改飞行物归属、速度或位置，只能提交 `ReflectProjectileIntent`。
* 禁止引用 `UnityEngine` / 协程 / `DateTime.Now`（AIF002、AIF003）。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
```

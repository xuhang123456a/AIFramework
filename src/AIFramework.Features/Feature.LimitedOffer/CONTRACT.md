# Feature.LimitedOffer 切片边界契约

## 1. 职责定位

表驱动限时礼包：只回答"这个礼包此刻能不能买、实付多少、今天/总计还剩几次"。
不查余额、不扣货币、不发货记账、不画 UI、不落存档、不管礼包后的经济系统
（扣款与余额归属专属经济系统，本切片只发发货指令与成交事实）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`LimitedOfferPurchaseCommand(offerId)`（购买意图）。
* **只读查询（Out）**：`LimitedOfferQueryRequest` → `LimitedOfferSnapshot`。
* **发出指令（Out）**：`GrantItemCommand(itemId, grantCount, 6901)`（仅成交时发一次，见 §4.6）。
* **广播事件（Out）**：`LimitedOfferPurchasedEvent(offerId, itemId, grantCount, finalPrice)`（仅成交）。
* **端口依赖（In）**：`ICqeBus`（构造注入）、`ITimeProvider`（窗口与跨日）、`IBinaryTableLoader`（子表）。

## 3. 独立子表（Config）

* 表名 `TbLimitedOffer`，列顺序即解析顺序：`offerId` / `name` / `itemId` / `grantCount` /
  `priceItemId` / `originalPrice` / `discountPercent` / `startUnixSeconds` / `endUnixSeconds` /
  `dailyLimit` / `totalLimit`（10 个 int32 + 1 个 string）。
* 布局：小端 `[int32 rowCount]` + 每行 `{ int32 offerId; [int32 len]+UTF-8 name; int32 × 9 }`。
* 校验：表缺失 → 构造抛 `InvalidOperationException`；截断 / 尾部残留 / `offerId <= 0` 或重复 /
  `name` 为空 / `itemId`、`grantCount`、`priceItemId`、`originalPrice` 非正 /
  `discountPercent` 不在 1..100 / `endUnixSeconds <= startUnixSeconds` / 任一 limit 为负
  → 抛 `InvalidDataException`。
* 0 行是合法配置：快照为空、购买返回 `Unavailable`、不崩。行顺序即快照顺序，载入后不排序。

## 4. 关键规则（测试已钉死）

1. `isOpen` = `startUnixSeconds <= now < endUnixSeconds`（**左闭右开**）：结束那一秒已不可买。
2. `finalPrice` = `floor(originalPrice * discountPercent / 100)`，**至少 1**（杜绝 0 元购）；
   折扣是"实付百分比"：`100` = 原价，`10` = 一折。
3. 限购：`dailyRemaining = dailyLimit - 当日已购`（UTC 自然日，`now / 86400` 向下取整，跨日重置）；
   `totalRemaining = totalLimit - 累计已购`（进程内）。`dailyLimit = 0` = **不限当日**，
   此时 `dailyRemaining = int.MaxValue`（`LimitedOfferState.UnlimitedRemaining`）。
   `totalLimit = 0` = **一次都不能买**（`TotalLimit - 0 = 0` 天然命中 `LimitReached`），两侧语义刻意不对称。
4. 购买判定顺序：空表（`Unavailable`）→ 未知 id（`UnknownOffer`）→ 窗口（`NotOpen`）→
   每日限购（`LimitReached`）→ 总限购（`LimitReached`）；任一失败即返回，**零副作用**。
5. 成交顺序：先落定计数 → 再发 `GrantItemCommand` → 最后广播事件；
   `LimitedOfferPurchasedEvent` 每次成交只发一次，订阅方回调里查到的剩余次数已是成交后口径。
6. **扣款/发货边界**：本切片不判断余额、不发 `ConsumeItemCommand`。装配层在
   `Purchase()` 返回 `Success` 后按 `FinalPrice` 扣款，失败/取消时以服务端为准；
   客户端计数仅供防误触，真限购以存档/服务端权威计数为准（进程重启后归零）。
7. 查询只读：窗口、报价、剩余次数按查询时刻实时折算，绝不推进跨日水位、不改写限购计数。
8. 空表 → 空快照（`OpenCount = 0`）；`Purchase` → `Unavailable`；不抛、不广播。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）；
  禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止查背包/发 `ConsumeItemCommand`（越界到经济系统）；禁止把本表堆进全局 `Tables`。
* 禁止在切片外改写 `_dailyPurchased` / `_dailyPurchasedDay` / `_totalPurchased`。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~LimitedOffer"
dotnet run --project tools/tablegen -- --check
```

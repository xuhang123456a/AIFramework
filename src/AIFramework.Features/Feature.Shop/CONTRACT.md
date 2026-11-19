# Feature.Shop 切片边界契约

## 1. 职责定位

Phase 1 商店：按本切片子表判定商品是否存在，校验单价购买与货币余额，按"先扣后给"发起扣款/发货并广播购买事实。
不含货币与道具存储实现、折扣、限购计数、库存扣减、UI 弹窗与红点调度。

购买规则（逐条锁死）：
1. 数量 `count != 1` 或表中无该 `itemId` → 静默拒绝：不查背包、不发指令、不发事件。
2. 余额 = `bus.Query<InventoryQueryRequest, InventorySnapshot>(new InventoryQueryRequest(def.PriceItemId))`；余额 < `PriceCount` → 静默拒绝。
3. 余额足够 → 顺序固定：`ConsumeItemCommand(PriceItemId, PriceCount, 6101)` → `GrantItemCommand(ItemId, 1, 6102)` → `Publish(ShopItemPurchasedEvent)`。
4. 货币 ID 复用 `MetaItemIds`（金币 = `MetaItemIds.Gold`）；商品道具 ID 见 `ShopItemIds`。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`BuyShopItemCommand`（itemId + count，Phase 1 只支持 count = 1）
* **处理查询（In）**：`ShopCatalogQueryRequest`（空结构体）→ `ShopCatalogSnapshot`（全量商品，顺序 = 子表行序）
* **只读查询（Out）**：`IInventoryQuery`，请求体 `InventoryQueryRequest(PriceItemId)`，只读货币数量
* **发出指令（Out）**：`ConsumeItemCommand`（扣货币，原因标签 6101）、`GrantItemCommand`（发货，原因标签 6102）
* **广播事件（Out）**：`ShopItemPurchasedEvent`（itemId / count / priceItemId / priceCount）
* **端口依赖（In）**：`IBinaryTableLoader`（加载本切片子表）

## 3. 独立子表（Config）

* 表名 `TbShopItem`，布局：小端 `[int32 rowCount]` + `rowCount × { int32 itemId; int32 priceItemId; int32 priceCount; }`，单行 12 字节。
* 校验：表缺失（loader 返回 `null`）→ 构造抛 `InvalidOperationException`，切片显式失败，禁止静默空表继续；
  `length != 4 + rowCount * 12`、同一 `itemId` 重复 → 抛 `InvalidDataException`。
* `rowCount = 0` 是合法配置（空商店）：`Count = 0`，任何购买都走"未知商品"拒绝，不是加载失败。

## 4. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*` 命名空间（AIF001）。
* 禁止引用 `AIFramework.Core`、`AIFramework.Kernel.*`、`AIFramework.Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止 LINQ；禁止直接改背包/货币数量，写操作只能走 `ConsumeItemCommand` / `GrantItemCommand`。
* 禁止把商品堆进全局大表单例，只允许读本切片 `TbShopItem`。
* 禁止在切片内做折扣、限购、库存扣减与 UI 提示；被拒绝的请求不发任何事件。

## 5. 验收命令

```
dotnet build src/AIFramework.Features/AIFramework.Features.csproj -v m
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj -v m
```

# Feature.ReplayBrowser · 边界契约卡

## 一句话职责
回放列表页的**纯逻辑**：把存储端口里的录像元信息按 UI 语义排序、分页，并处理删除意图；
不解析帧数据、不渲染、不落盘。

## 允许依赖
- `AIFramework.Contracts`（`ICqeBus`、`IReplayArchivePort`、`ReplayBrowserEntry`）
- 禁止引用 `Core`、`Kernels`、`Features` 其他切片、任何 `Adapters`、任何引擎命名空间。

## 对外契约（都在切片命名空间内）
| 类型 | 说明 |
|---|---|
| `ReplayBrowserHandler(ICqeBus, IReplayArchivePort)` | 查询/删除入口；`Register(bus)` 挂 CQE |
| `ReplayListQuery(page, pageSize, order)` | 0 基页码；pageSize 上限 200，超出按 200 |
| `ReplayListPage` | 本页条目 + 总数 + PageCount/HasNext/HasPrevious + `ToReportLine()` |
| `ReplayListQueryRequest → ReplayListPage` | 总线查询（UI 可直连 handler，也可走总线） |
| `ReplayDeleteCommand(slotKey)` | 删除意图；空键直接忽略且不发事件 |
| `ReplayDeletedEvent(slotKey, success)` | 删除结果事实（成功/失败都发，便于 UI 提示） |
| `ReplaySortOrder` | NewestFirst / OldestFirst / LongestFirst |

## 行为约定（测试钉死）
1. 排序稳定：同时间按 `SlotKey` 升序；LongestFirst 同帧数回退到 NewestFirst。
2. 页码越界 → 夹到最后一页（UI 删完最后一条不会白屏）。
3. 空仓库 → 0 页、0 条、无下一页。
4. `Delete` 未知键 → 返回 false + 发 `success=false` 事件；空键不发事件。
5. 端口返回的数组可能为空/null：统一兜底为空数组。

## 明确不做
- 不做帧数据解码（`Feature`: 不引 Core `ReplayArchiveStore`）。
- 不做时长格式化/本地化文案（UI 层职责）。
- 不做自动清理策略（保留条数由存储端 `ReplayArchiveStore` 负责）。
- 不做播放：播放走 `ReplayArchiveStore.TryLoad` + 内核，属适配层编排。

## 已知缺口
- 无按关卡/时间范围筛选（策划未提，等真实需求再加，避免过度设计）。
- 列表数据是快照：删除后需 UI 重新查询（不推送增量）。

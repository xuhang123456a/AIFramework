# AIFramework 持续路线图（无限队列）

> **本文件是"不停推进"的执行队列**：T1 做完进 T2，T2 做完进 T3，T3 有新缺口就回填 T1/T2。
> 每轮结束更新状态与「轮次日志」，队列不允许为空——若某梯队清空，就地扩展新条目。
> 唯一的停止条件是用户叫停；环境硬阻塞（许可证/网络/磁盘）时转做纯逻辑离线任务，不空转。

## 状态图例

- `[ ]` 待做　`[~]` 进行中　`[x]` 已完成（含闸门证据）　`[!]` 阻塞（写明原因与替代动作）

## 硬性完成标准（缺一不算完成）

1. `dotnet build AIFramework.Pure.sln` → 0 warning / 0 error
2. `dotnet test AIFramework.Pure.sln` → 全绿
3. `dotnet run --project tools/viewgen -- <spec> --check` → 无漂移（有 UI 的面板）
4. `pwsh unity/tools/check-adapter-compile.ps1` → 通过（动了适配层时）
5. 新切片：`CONTRACT.md`（≤60 行）+ 无头测试（正常 + ≥2 反例）+ 场景/接线脚本
6. 每轮一次 commit，message 写清"本轮完成 / 下轮取哪项"

---

## T1 · 基础玩法（框架主干）

| # | 场景 | 品类 | 依赖内核/层 | 关键新能力 | 状态 |
|---|---|---|---|---|---|
| T1-1 | `Boot` | 启动/装配/加载 | Bootstrap | 装配入口、场景切换、存档加载、日志导出 | [ ] 存档服务已就绪（Core.Save） |
| T1-2 | `Lobby` | 大厅/主城 | Core + Features | 背包/商店/任务/邮件/红点/弹窗队列/签到/活动入口 | [~] 切片 + Presenter + Unity 装配已完成，缺场景 UI |
| T1-3 | `CardBattle` | 回合卡牌 | Kernel.Turn（通用化） | 抽牌/能量/出牌/回合状态机/结算 | [ ] |
| T1-4 | `Match3` | 三消 | Kernel.Turn 棋盘变体 | 网格状态/消除/连击/目标步数 | [ ] |
| T1-5 | `RealtimeArena` | MOBA/ARPG 向 | Kernel.Realtime | 技能时间轴/弹幕/位移/打断/固定步长/回放 | [ ] |
| T1-6 | `TowerDefense` | 塔防 | **双内核共存** | Meta 建造 + Realtime 战斗，验证多卡带隔离 | [ ] |
| T1-7 | `IdleTycoon` | 放置/经营 | Meta + ITimeProvider | 离线收益/升级曲线/时间快进断言 | [ ] |
| T1-8 | `Gacha` | 抽卡/扭蛋 | Meta + IRandomSource | 概率表/保底/种子复现/概率公示 | [ ] |

## T2 · 品类扩展（每项必须证明一个框架能力）

| # | 品类 | 依赖 | 证明的能力 | 状态 |
|---|---|---|---|---|
| T2-1 | 合成 Merge | Meta | 网格状态机 + 配置驱动 | [ ] |
| T2-2 | 肉鸽卡牌 | Turn | 随机地图 + 局内构筑 + 种子复现 | [ ] |
| T2-3 | 自走棋 | Turn | 阵容经济 + 自动战斗推演 | [ ] |
| T2-4 | 战棋 SRPG | Turn | 网格寻路 + 技能范围仲裁 | [ ] |
| T2-5 | 弹幕射击 | Realtime | **最硬 0 GC/高实体数** pressure test | [ ] |
| T2-6 | 跑酷 Runner | Realtime | 输入→位移确定性 | [ ] |
| T2-7 | 平台跳跃 | Realtime | 物理查询端口可替换（Mock vs PhysX） | [ ] |
| T2-8 | 棋盘/大富翁 | Turn | 回合事件链 + 随机驱动 | [ ] |
| T2-9 | 俄罗斯方块类 | Realtime/独立 | 固定步长 + 形态旋转状态机 | [ ] |
| T2-10 | 解谜（推箱子/数独） | 纯数据 | 零引擎依赖 + 纯逻辑可测 | [ ] |
| T2-11 | 模拟经营/农场 | Meta | 时间系统 + 离线结算 | [ ] |
| T2-12 | 挂机 RPG | Meta | 数值曲线 + 离线收益 | [ ] |
| T2-13 | 音游 | Realtime | 时间精度 + 判定窗口 | [ ] |
| T2-14 | 异步 PVP | Meta + Realtime | 快照/回放复用 | [ ] |

## T3 · 框架深化

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T3-1 | Luban 真表接入 | 替换 12 字节夹具，schema + 导出 + 加载器 | [ ] |
| T3-2 | `UIBindNode` 导出器 | Prefab/场景 → bindings.json，闭合 viewgen 前置 | [~] 生成器侧已完备（Number/--all），导出器待做 |
| T3-3 | 存档服务 + 版本迁移 | IStoragePort 之上的一层，含 CRC/版本 | [~] 包络+CRC 已实现（SaveService），迁移策略待补 |
| T3-4 | 场景框架 SceneDirector | 加载/卸载/内核会话生命周期 | [ ] |
| T3-5 | 通用 UI 框架 | 弹窗栈/Toast/Loading/红点绑定 | [ ] |
| T3-6 | 确定性回放 | 输入录制 + 同种子重放断言 | [ ] |
| T3-7 | 网络与服务器校验骨架 | 帧/状态同步端口 + 校验契约 | [ ] |
| T3-8 | HybridCLR 全链路 | 热更程序集切分 + AOT 元数据 + 真机 | [ ] |
| T3-9 | 本地化 | 文案表 + 语言切换 + TMP 回退 | [ ] |
| T3-10 | 音频/特效端口 | IAudioPort / IVfxPort + 适配实现 | [ ] |
| T3-11 | 性能与内存基线 | Profiler 采样 + 0 GC 回归门禁 | [ ] |
| T3-12 | 编辑器工具 | 关卡编辑器、技能编辑器、数值表校验 | [ ] |
| T3-13 | CI/CD | GitHub Actions：build/test/viewgen check/形状校验/出包 | [~] 工作流已落地（.github/workflows/ci.yml），出包待补 |
| T3-14 | 云存档 | IStoragePort 远端实现 + 冲突策略 | [ ] |

---

## 轮次日志（每轮追加，最新在上）

### R2 — 2026-11-20
- 目标：兑现切片提的 Contracts 缺口；三个面板 Presenter + 视图契约 + 绑定描述；Unity 大厅装配
- 完成：
  - 契约：`RedDotSetCommand`（重登重算）、`InventoryListQueryRequest/Snapshot`（背包整包）、`MetaReasonTags` 集中化（5001/5002/6001/6101/6102/6201/6301）
  - Core/Inventory 相应处理器与测试；`RedDotTree` 注册 2 命令 + 1 查询，`InventoryHandler` 2 命令 + 2 查询
  - Presenter：`ShopPresenter`/`QuestPresenter`/`MailPresenter` + 各自视图契约与 `*.bindings.json`；视图接口改由 viewgen 统一生成（4 个面板一致）
  - viewgen：新增 `Number` 绑定类型（金币用 int，避免视图层转字符串）、`--all` 全仓校验；CI 改用 `--all`
  - Unity：`AIFrameworkUnityHost` 接入真实背包/红点/商店/任务/邮件/存档；`PublishGameFinished` + 战利品入账；删除适配层占位背包
- 验证：321 例全绿（Core 109 / Features 129 / Adapters 38 / ViewGen 18 / Arch 27）、形状校验通过、`--all` 无漂移
- 下轮：UIBindNode 导出器（T3-2）+ Lobby 场景 + Boot/SceneDirector（T1-1）

### R1 — 2026-11-20
- 基线：`ce7e756`（Unity meta 清零）
- 目标：ROADMAP 机制落地；Meta 协议契约 v1；`SaveService`（CRC32 包络）；5 个并行作业（Inventory/Shop/Quest/Mail/RedDot+Popup）
- 环境限制：Unity 批处理被占用（用户编辑器开着同一工程），改用离线闸门；Pipeline 包已装进工程，等编辑器空闲即可接管
- 结果：进行中

---

## 扩展规则（队列永不见底）

T2/T3 清空时，按以下顺序补新条目：

1. **框架缺口**：把每个新场景暴露的"适配层被迫 `new` 具体类型 / 端口缺失 / 契约不够"转成 T3 条目。
2. **玩法组合**：两个已完成的玩法做混合（塔防+肉鸽、卡牌+自走棋、经营+放置……），成本低收益高。
3. **平台与商业化**：IAP、广告、排行榜、成就、Battle Pass、A/B、埋点——每个都能落成切片 + 适配端口。
4. **跨端能力**：多语言、多分辨率、手柄/触屏输入、WebGL/移动端包体优化。

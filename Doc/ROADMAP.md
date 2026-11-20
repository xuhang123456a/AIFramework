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
| T1-1 | `Boot` | 启动/装配/加载 | Bootstrap | 装配入口、场景切换、存档加载、日志导出 | [x] BootScene + BootController + UnitySceneDirector；PlayMode 冒烟通过 |
| T1-2 | `Lobby` | 大厅/主城 | Core + Features | 背包/商店/任务/邮件/红点/弹窗队列/签到/本地化/23 场景导航 | [x] LobbyScene（258KB）+ 全部 HUD/Presenter；PlayMode 验证 |
| T1-3 | `CardBattle` | 回合卡牌 | Kernel.Turn（通用化） | 抽牌/能量/出牌/回合状态机/结算 | [x] 内核 + 测试 + CardBattleScene |
| T1-4 | `Match3` | 三消 | Kernel.Turn 棋盘变体 | 网格状态/消除/连击/目标步数 | [x] 内核 + 测试 + Match3Scene |
| T1-5 | `RealtimeArena` | MOBA/ARPG 向 | Kernel.Realtime | 技能时间轴/弹幕/位移/打断/固定步长/回放 | [x] ArenaScene + RealtimeSceneBinder + 回放比对 |
| T1-6 | `TowerDefense` | 塔防 | **双内核共存** | Meta 建造 + Realtime 战斗，验证多卡带隔离 | [x] 切片 + TowerDefenseController + 场景 |
| T1-7 | `IdleTycoon` | 放置/经营 | Meta + ITimeProvider | 离线收益/升级曲线/时间快进断言 | [x] 切片 + IdlePresenter + IdleScene |
| T1-8 | `Gacha` | 抽卡/扭蛋 | Meta + IRandomSource | 概率表/保底/种子复现/概率公示 | [x] 切片 + GachaPresenter + GachaScene |
## T2 · 品类扩展（每项必须证明一个框架能力）

| # | 品类 | 依赖 | 证明的能力 | 状态 |
|---|---|---|---|---|
| T2-1 | 合成 Merge | Meta | 网格状态机 + 配置驱动 | [x] 切片 50 例 + MergeScene |
| T2-2 | 肉鸽卡牌 | Turn | 随机地图 + 局内构筑 + 种子复现 | [x] RoguelikeWorld 27 例 + RoguelikeScene |
| T2-3 | 自走棋 | Turn | 阵容经济 + 自动战斗推演 | [x] AutoChessWorld 31 例 + AutoChessScene |
| T2-4 | 战棋 SRPG | Turn | 网格寻路 + 技能范围仲裁 | [x] TacticsWorld 30 例 + TacticsScene |
| T2-5 | 弹幕射击 | Realtime | **最硬 0 GC/高实体数** pressure test | [x] BulletHellScene（≈530 弹幕/1200 帧）+ 出界回收 |
| T2-6 | 跑酷 Runner | Turn（实时感） | 输入→位移确定性 | [x] RunnerWorld 28 例 + RunnerScene |
| T2-7 | 平台跳跃 | Turn（自实现物理） | 重力/单向平台/收集品 + 子步防穿墙 | [x] PlatformerWorld 38 例 + PlatformerScene |
| T2-8 | 棋盘棋类 | Turn | 回合棋盘 + 搜索 AI | [x] 黑白棋 BoardGameWorld 29 例 + BoardGameScene（大富翁类未做，可另扩） |
| T2-9 | 俄罗斯方块类 | Turn | 形态旋转状态机 + 消行计分 | [x] TetrisWorld 37 例 + TetrisScene |
| T2-10 | 解谜（推箱子） | 纯数据 | 零引擎依赖 + 纯逻辑可测 | [x] PuzzleWorld 25 例 + PuzzleScene |
| T2-11 | 模拟经营 | Meta | 时间系统 + 建造/升级/员工 | [x] Tycoon 切片 38 例 + TycoonScene |
| T2-12 | 挂机 RPG | Meta | 数值曲线 + 自动战斗 + 关卡推进 | [x] IdleRpg 切片 40 例 + IdleRpgScene |
| T2-13 | 音游 | Turn（时间轴） | 时间精度 + 判定窗口 | [x] RhythmWorld 37 例 + RhythmScene |
| T2-14 | 异步 PVP | Meta + 回放 | 快照/段位/确定性结算 | [x] AsyncPvp 切片 30 例 + AsyncPvpScene |
## T3 · 框架深化

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T3-1 | 配置表导出器（Luban 替代） | JSON schema + 导出 + 12 表逐字节等价 + `--check` 门禁 | [x] `tools/tablegen` + `tables/*.json` + 29 例；CI 双目标交叉校验 |
| T3-2 | `UIBindNode` 导出器 | Prefab/场景 → bindings.json，闭合 viewgen 前置 | [x] 组件 + 导出器 + 文档（真机菜单待人眼验） |
| T3-3 | 存档服务 + 版本迁移 | IStoragePort 之上的一层，含 CRC/版本 | [x] v2 包络 + CRC32 + schemaVersion + 迁移链（见 T3-17） |
| T3-4 | 场景框架 SceneDirector | 加载/卸载/内核会话生命周期 | [x] UnitySceneDirector + SceneNavigator + BootController；PlayMode 覆盖 |
| T3-5 | 通用 UI 框架 | 弹窗栈/安全区/层级/Toast/Loading | [x] 弹窗栈 + SafeAreaFitter + CanvasSortOrder + Toast/Loading（见 T3-15） |
| T3-6 | 确定性回放 | 输入录制 + 同种子重放断言 | [x] Core 17 例 + ArenaScene 逐帧指纹回放（PlayMode） |
| T3-7 | 网络与服务器校验骨架 | 帧/状态同步端口 + 校验契约 | [x] NetworkContracts + MatchValidationLedger 13 例（HTTP 未接线） |
| T3-8 | HybridCLR 全链路 | 热更程序集切分 + AOT 元数据 + 真机 | [~] 包 v9.1.0 + 安装器自检 + 热更程序集 + `hotUpdateAssemblies` + link.xml/Il2CppDef 生成已通；**阻塞：本机 Editor 未装 IL2CPP 模块且无 Unity Hub**，`AIFW_IL2CPP=1` 出包报 `IL2CPP is not installed`（解除步骤见 Doc/07） |
| T3-9 | 本地化 | 文案表 + 语言切换 + 回退 | [x] 切片 33 例 + 2 套夹具表 + LobbyScene 切语言（真机） |
| T3-10 | 音频/特效端口 | IAudioPort / IVfxService + 适配实现 | [x] 音频 32 例 + 特效 19 例 + 双端实现 + 宿主接线 |
| T3-11 | 性能与内存基线 | Profiler 采样 + 0 GC 回归门禁 | [x] FrameProfiler/PerformanceHud + PlayMode 采样 + 回合内核 0 分配门禁 + Realtime Unity 内 600 帧 ≤1KB 门禁 |
| T3-12 | 编辑器工具 | 关卡/场景/数值表校验 | [x] UIBindExporter + OneClickTools（一键校验）+ GenerateAllScenes/SceneVerifier |
| T3-13 | CI/CD | build/test/viewgen/tablegen/形状校验/出包 | [x] 纯逻辑五道闸门进 CI；Unity 闸门走 `unity-gated.yml`（需 license，默认不跑）+ 本地脚本 |
| T3-14 | 云存档 | 远端实现 + 冲突策略 | [x] 端口 + CloudSaveMerger 34 例 + 双端实现（HTTP 未接线） |
| T3-15 | Toast/Loading 层 | 轻提示 + 转圈遮罩 | [x] ToastHud + LoadingOverlay + Lobby OverlayLayer + PlayMode 3 例 |
| T3-16 | VFX 端口 | IVfxService + Unity 池 + Console 记录 | [x] 契约 + 池实现 + 双端 19 例 + 宿主接线 |
| T3-17 | 存档版本迁移 | v1→v2 schema 迁移 + 迁移链测试 + 损坏回退 | [x] SaveMigrations + codec v2 + SaveService schemaVersion + 42 例 |

---

## T4 · 平台与商业化（T1~T3 清空后自行扩展的下一梯队）

> 触发条件：T3 只剩"环境阻塞"项（当前 T3-8 缺 IL2CPP 模块）。这些条目全部可在**纯逻辑 + 离线**条件下推进，不依赖 Unity 出包。

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T4-1 | IAP 端口 | `IIapService`（商品目录/购买/收据校验/恢复购买）+ Console 假商店 + 双端测试 | [x] 契约 + 台账 + 双端实现 + 35 例；宿主 `Iap` 属性 + Console `[Iap]` 自检 |
| T4-2 | 广告端口 | `IAdService`（激励视频/插屏/冷却）+ 假填充 + 完播奖励口径 | [x] 契约 + `AdShowScheduler`（冷却/每日配额/UTC 跨日）+ 双端实现 + 47 例 + Console `[Ad]` 自检 |
| T4-3 | 排行榜端口 | `ILeaderboardService`（提交/查询/自己排名）+ 本地台账 | [x] 契约 + `LeaderboardTable`（同分先到先排确定性）+ 双端实现 + 25 例 + Console `[Leaderboard]` 自检 |
| T4-4 | 成就系统 | 切片：成就定义表 + 解锁判定 + 通知事件 + 无头测试 | [x] `Feature.Achievement` + `TbAchievement`(430B,12 行) + 38 例；宿主/Console 桥接已接 |
| T4-5 | 活动/Battle Pass | 赛季配置 + 任务进度 + 奖励领取 + 时间源可快进 | [x] `Feature.SeasonPass` + `TbSeasonPass`(724B,30 级) + 双轨领取 + 58 例；宿主/Console 已接 |
| T4-6 | 埋点端口 | `IAnalyticsService`（零分配事件写法）+ Console 环形记录 | [x] 契约 + `AnalyticsEventBuffer`(零分配) + 双端实现 + 24 例 + Console `[Track]` 自检 |
| T4-7 | 输入抽象 | 手柄/触屏/键鼠统一 `IInputSource`，让 Runner/Platformer 可换输入 | [x] `IInputSource` + Scripted/Keyboard/Touch/Composite + 场景路由（Runner/Platformer 两子类）+ 25 例 + PlayMode 4 例 |
| T4-8 | 大富翁玩法 | Turn 内核新卡带（骰子 + 事件格 + 资产），补 T2-8 未做的分支 | [x] 内核 50 例 + Controller/HUD/Scene（第 24 场景，PlayMode 冒烟过） |
| T4-9 | 视觉小说 | 纯数据剧本状态机 + 选项分支 + 存档快照 | [x] `VisualNovelWorld` 51 例 + 条件选项/4 结局 + CaptureState；第 25 场景 PlayMode 过 |
| T4-10 | 多端包体体检 | 各平台模块/包体大小检查清单（待 IL2CPP 模块就绪后执行） | [ ] |

---

## T5 · 闭环与工具体检（T4 只剩"环境阻塞"项后自行扩展）

> T4 除 T4-10（依赖缺失的 IL2CPP 模块）外全部完成。本梯队全部可离线验证。

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T5-1 | 新手引导（FTUE） | 步骤机 + 条件触发 + 跳过 + 存档断点续引导 + 无头测试 | [x] `Feature.Guide` + `TbGuideStep`(10 步) + 条件/跳过/快照；宿主/Console 已接 |
| T5-2 | 活动轮换框架 | 表驱动排期（开始/结束/每日重置/轮换），时间源可快进 | [x] `Feature.ActivitySchedule` + `TbActivitySchedule`(8 活动/2 轮换组) + UTC 重置信号；Console `[Activity]` 已接 |
| T5-3 | 商店→IAP→发货→埋点闭环 | 购买结果接进 Shop/Inventory，重复购买/恢复购买不发双份，埋点记录 | [x] `StorePurchaseCoordinator` 34 例 + Console 流程 12 例；防双发/恢复不重发/台账两份实例的坑已锁 |
| T5-4 | 回放落盘与列表 | Arena 录像经 IStoragePort 落盘、列表查询、版本校验 | [x] `ReplayArchiveCodec/Store`（AIFR 头 + 帧指纹，容量 20 淘汰最旧，坏档跳过计数）55 例；Console `[Replay]` 已接 |
| T5-5 | 数值健康检查工具 | `tools/tablecheck`：价格>0、经验单调、奖励 id 可解析 | [x] 14 张表规则 + 20 例（每条规则有"写坏必红"的用例）+ verify-all 第 5 层 + CI |
| T5-6 | 本地化完整性工具 | 扫描 bindings/场景与文案表，报告缺失 key 与多余 key | [x] `tools/loccheck`：ui.* 引用扫描 + 缺 key 报错/未引用提示 + `loccheck:ignore` 豁免 + 12 例；verify-all 第 6 层 |
| T5-7 | Realtime 音效桥 | 战斗事件（命中/格挡/死亡）→ IAudioService，节流与去重 | [x] `BattleAudioCueSelector`(28 例，帧差推断 + 节流/优先级) + Unity 桥 + ArenaController 接线 + 宿主共享 Audio + PlayMode 3 例 |
| T5-8 | 旧档回归夹具 | 把 v1 存档字节做成 fixture，锁住迁移链不退化 | [x] 48 字节真实 v1 档冻结 + CRC 损坏/未来版本拒绝 + 迁移后解码 4 例 |
| T5-9 | 关卡编辑器（网格） | 编辑器里画格子 → 生成关卡数据 | [x] `GridLevelEditorWindow` + `PuzzleLevelJson` + `Resources/Levels/puzzle-demo.json`；**内核新增外部行构造** `PuzzleWorld(init, rows)`（14 例）+ PuzzleController 真注入 + 场景注入 |
| T5-10 | 手柄输入源 | `GamepadInputSource`（A/B/Start/摇杆）补齐 T4-7 未映射按键 | [x] 自做边沿的 `GamepadInputSource` + router `enableGamepad`，与键鼠/触屏合成 |
---

## T6 · 账号/社交/运营（T5 清空后的下一梯队）

> T5 只剩"环境阻塞"项后从这里取；全部可在纯逻辑 + 假实现下验证。

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T6-1 | 账号与登录端口 | `IAccountService`（游客/第三方绑定/登出/换号）+ 假实现 + 会话失效处理 | [x] `AccountSessionManager` + 双端假后端 + TTL 过期；Console `[Account]`、宿主 `Account` 已接 |
| T6-2 | 社交端口 | `ISocialService`（好友列表/赠送/领取体力）+ 双端假后端 + 每日限量跨日重置；Console `[Social]`、宿主 `Friends` 已接 |
| T6-3 | 推送端口 | `IPushService`（本地通知/远端 token 登记）+ 假实现 | [x] `PushScheduler`（每日提醒换绝对时刻、到期/顺延、token 校验）+ 双端假后端 70 例；Console `[Push]`、宿主 `Push/PushScheduler` 已接 |
| T6-4 | A/B 实验框架 | 分桶（确定性哈希）+ 实验表 + 曝光埋点 | [x] `Feature.Experiment` + `TbExperiment`(6 实验) + FNV 分桶 53 例；tablecheck 新规则 4 例；Console `[Experiment]` 已接 |
| T6-5 | 限时礼包/折扣 | 时间段 + 折扣 + 限购 | [x] `Feature.LimitedOffer` + `TbLimitedOffer`(6 礼包) 54 例；成交只发 `GrantItemCommand`、扣款交装配层；Console `[LimitedOffer]` 已接 |
| T6-6 | 回放分享/导出 | 录像导出可分享文本 + 导入 | [x] `ReplaySharing`（AIFR-SHARE 信封 + FNV 校验和，截断/篡改/超长全拒）15 例；Console `[ReplayShare]` 已接 |
| T6-7 | 引导高亮 UI | 大厅 HUD 订阅 Guide 事件做目标高亮 | [x] `GuideHighlightHud`（事件驱动渲染、Reached 点亮、完成收起）+ 场景接线；PlayMode 1 例；顺带修 SkipAll 不发完成事件的真缺陷 |
| T6-8 | 多语言热切换 | 运行时切语言刷新已打开面板 | [x] `ILocalizedPanel` + `LocalizationHotSwitcher`（同物体自动收集、单面板异常隔离）+ Lobby 接线；PlayMode 2 例 |
| T6-9 | 错误码与提示映射 | 统一错误码 → 本地化 key → Toast，替代散落字符串 | [x] `ErrorCatalog`(15 码) + `ErrorToastRouter` + `ToastRequestedEvent` + 双端 Toast 表现层；22 例 |
| T6-10 | 运营校验工具 | `tablecheck` 扩展：活动/礼包窗口与折扣检查 | [x] 新增 TbActivitySchedule / TbLimitedOffer / TbExperiment 规则 + 折后价为 0 / 日限>总限 / int32 时间溢出；32 例 |
---

## T7 · 生产化/服务端化（T6 清空后的下一梯队）

> 全部仍可在纯逻辑 + 假实现下验收；真实 SDK/服务端接入另立任务。

| # | 能力 | 说明 | 状态 |
|---|---|---|---|
| T7-1 | 商店掉单对账 | 收据台账 + 掉单检测 + 补发/退款状态机 | [ ] |
| T7-2 | 云存档 HTTP 端口 | `ICloudSaveTransport` + 冲突合并 + 重试退避 | [ ] |
| T7-3 | 排行榜服务端化 | 分页/同分排序/赛季重置的服务端契约与假实现 | [ ] |
| T7-4 | 热更资源校验 | 资源清单 hash + 缺失/损坏回退 | [ ] |
| T7-5 | 画质与帧率自适应 | 设备档位表 + 动态降级（不碰内核） | [ ] |
| T7-6 | 跨平台存档迁移 | 游客→正式账号合并、设备迁移夹具 | [ ] |
| T7-7 | 运营后台模拟器 | 活动/礼包/公告热配置校验 + 冲突检测 | [ ] |
| T7-8 | 本地化语音/字幕 | 语音 key 与字幕时间轴、缺语言回退 | [ ] |
| T7-9 | 性能基线回归 | PlayMode 帧耗时/分配阈值 + 回归报告 | [ ] |
| T7-10 | 错误上报与降级面板 | 崩溃/异常聚合 + 一键上报假实现 | [ ] |
---

## 轮次日志（每轮追加，最新在上）

### R25 — 2026-11-20
- **T4-7 输入抽象**：`IInputSource` + InputAction/位掩码边沿语义；Scripted（Core）/Keyboard/Touch/Composite；SceneInputRouter 抽象基类 + Runner/Platformer 两个场景专属子类（因 SceneVerifier 强制引用非空，不放松校验）
- **T4-9 视觉小说**：16 节点演示剧本 + 条件选项（flag 位掩码）+ 4 结局 + 存档快照；第 25 场景
- 数字：纯逻辑 **1553** 例、PlayMode **39**、25 场景

### R24 — 2026-11-20
- **T4-2 广告端口**：冷却/UTC 每日配额/完播口径；Console/Unity 双实现；47 例
- **T4-5 赛季通行证**：30 级双轨、经验跨级、付费轨解锁、过期冻结；58 例；表 13→14
- 数字：纯逻辑 1481 例、14 张表

### R23 — 2026-11-20
- **T4-1 IAP**（35 例）、**T4-4 成就切片**（38 例，表 12→13）、**T4-6 埋点**（24 例）、**T4-8 大富翁内核**（50 例）
- **T4-3 排行榜**（25 例，同分确定性裁决；修满榜插入丢条目 bug）与第 24 场景
- 数字：纯逻辑 1376 例、24 场景、PlayMode 34

### R20 — 2026-11-20
- 场景扩到 **20 个**（+BoardGameScene / TycoonScene / IdleRpgScene），全部离屏生成 + 装配校验通过
- 真机：EditMode 11/11、PlayMode 22/22（20 个场景冒烟 + 物理链路 + 弹窗队列）
- 修"桩比真机宽松"隐患：UnityEvent.AddListener 真机只收 UnityAction，桩此前收 Action →
  BoardGameHud 用 Action 变量直传，桩里通过、真 Unity 编译失败；桩已改严，以后会被形状校验拦住

### R19 — 2026-11-20
- 四个新玩法：黑白棋（29 例）、模拟经营（38 例）、挂机 RPG（40 例）、异步 PVP（30 例）
- 纯逻辑测试破千（1033 例）

### R18 — 2026-11-20
- 一键出 Windows 冒烟包：`unity/tools/build-player.ps1`（batchmode，实测 60 MB 出包成功）
- 补 `UnityEditor.Build.Reporting` 形状桩，让出包脚本本身也进离线门禁

### R17 — 2026-11-20
- 跑酷/平台跳跃场景（真实内核离线验证：平台三关 6.4s/7.7s/9.1s 全胜）
- 肉鸽卡牌内核（27 例，200 种子里 188 个可通关）

### R16 — 2026-11-20
- 音游/弹幕场景；音频端口 + 服务端校验端口（双端实现 + 32 例）
- 平台跳跃内核（38 例）；Runner Low 障碍清障高度 1.0→0.6（原值与跳跃顶点几乎等宽，真机不可躲）

### R15 — 2026-11-20
- 飞行物出界回收（弹幕玩法跑满 1200 帧会打满槽位，新弹幕静默失败）

### R14 — 2026-11-20
- 音游卡带进 CLI 巡礼（AutoPlay 全 Perfect）；修形状桩 Component 构造递归（StackOverflow）

### R13 — 2026-11-20
- 俄罗斯方块/自走棋场景（12 场景）；大厅导航补齐

### R12 — 2026-11-20
- 推箱子/俄罗斯方块/自走棋三套卡带 + 解谜场景（10 场景）

### R11 — 2026-11-20
- 本地化切片接进宿主与大厅 UI（切语言真机验证）

### R10 — 2026-11-20
- 完成：弹窗队列接进 Unity UI（宿主新增 `IPopupQueue` 端口）+ PlayMode 优先级出队验收
- 并行作业：本地化切片（T3-9）、推箱子（T2-10）、俄罗斯方块（T2-9）、自走棋（T2-3）

### R9 — 2026-11-20
- 战斗场景接入 `RealtimeSceneBinder`（碰撞体 + 每帧 Sync + `Physics.SyncTransforms`）
- 战棋内核（T2-4）：8×8、BFS 可达、敌方 AI、50 回合平局，30 例
- PlayMode 场景冒烟：9 个场景真机加载跑帧零错误；补 122 个文件 `#nullable enable`

### R8 — 2026-11-20
- `RealtimeSceneBinder`：把布场数据变成已登记碰撞体的场景对象（此前战斗场景"能跑但不打架"）
- PlayMode 测试层：护盾反射真机验证（ReflectedCount≥1）、飞行物 Transform 同步
- 大厅九场景导航补全（SceneNavigator 复用）；9 场景重新生成 + 装配校验通过

### R7 — 2026-11-20
- 目标：竞技场场景（T1-5）、放置/抽卡面板场景、确定性回放（T3-6）
- 已完成：全玩法汇入大厅冒烟（Merge/Idle/TowerDefense/Gacha 同总线协作）、回合内核零分配基线
- 并行作业：ArenaController/Hud/SceneBuilder、Idle+Gacha Presenter/HUD/Scene、Core.Replay 录制回放

### R6 — 2026-11-20
- 完成：`Feature.Merge`（6×6 合成状态机，50 例）、`Feature.TowerDefense`（Meta 建造切片）、塔防场景（双内核演示）、
  `UIBindNode`/`UIViewDescriptor`/导出器（T3-2）、**Unity EditMode 测试闭环**（10 例真机跑通）
- 新增工具：`unity/tools/run-unity-tests.ps1`（离屏跑 EditMode 测试）、manifest 加 `com.unity.test-framework` 1.6.0
- 场景：6 个场景全部离屏生成 + 装配校验通过，Build Settings 按 Boot→Lobby→CardBattle→Match3→TowerDefense 注册

### R5 — 2026-11-20
- 完成：`Feature.IdleTycoon`（离线收益/升级/24h 封顶）、`Feature.Gacha`（权重抽取 + 10 抽保底）、场景装配校验器、离屏场景生成流水线
- 场景导航：大厅 → 卡牌/三消、战斗场景 → 大厅（`SceneNavigator` + `UnitySceneDirector`），场景重新生成并通过装配校验

### R4 — 2026-11-20
- 目标：T1-3 卡牌回合 + T1-4 三消两套内核内容；T1-1 Boot 场景与场景切换
- 契约：`GameplayKernelType.CardBattle/Match3`；`CardViewData`/`ICardBattleReadView`/`ICardBattleInput`；`IMatch3ReadView`/`IMatch3Input`；`TbCard` 夹具（20 字节/行，84 字节 4 行）
- 并行作业全部落地：`CardBattleWorld`（抽牌/能量/护盾/敌人行动/30 回合上限）、`Match3World`（8×8/5 色/级联/重力/步数与目标分）、`UnitySceneDirector`+`BootController`+`BootSceneBuilder`、卡牌与三消场景 + HUD
- 适配层工厂接上两个新卡带（Console `DemoKernelFactory` / Unity `UnityKernelFactory`，卡牌子表走 `TbCard.bin`）
- 验证：384 例全绿（Core 172 / Features 129 / Adapters 38 / ViewGen 18 / Arch 27）；Console 冒烟四玩法全通
- 追加验证闭环：`unity/tools/generate-scenes.ps1` 离屏生成 5 个场景（真实 Unity，0 编译错误），
  并写回 EditorBuildSettings；修了两个"形状桩验不出、真 Unity 才炸"的问题：缺 `com.unity.ugui`、
  Image/Text 同 GameObject 冲突
- 下轮：T2 扩展（合成/战棋等）+ Unity EditMode/PlayMode 自动化 + 表现层补强

### R3 — 2026-11-20
- 目标：Unity 场景层落地（大厅场景可点）、离线编译校验扩展到编辑器脚本
- 完成：`SignInHud`/`ShopHud`/`QuestHud`/`MailHud` 四个视图皮肤 + `LobbyController`；`LobbySceneBuilder` 一键建场景（Canvas/四面板/按钮/引用注入）；形状桩扩到 UnityEngine.UI / UnityEditor，`check-adapter-compile.ps1` 现在同时编译 `unity/Assets/**/Editor/**`
- 下轮：Boot 场景 + SceneDirector（T1-1/T3-4）；卡牌回合内核通用化（T1-3）

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

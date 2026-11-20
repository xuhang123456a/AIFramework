# AIFramework

> **一句话**：引擎无关（纯 C#）的游戏框架 —— 一套 CQE 契约 + 两套玩法内核 + 十九个业务切片 + 二十五个 Unity 场景，
> 所有玩法共用同一个总线/背包/红点/存档/因果日志，新增玩法不改其它模块。
>
> 当前状态（2026-11-20）：**3203 个纯逻辑测试 + 11 个 Unity EditMode 测试 + 46 个 PlayMode 测试全绿**；
> 二十四个场景离屏生成并通过装配校验；`AIFramework.exe` 真出包 86.6 MB；平台端口（IAP/广告/排行榜/云存档/埋点/推送契约）全部有假实现与无头测试。

---

## 一、这个仓库解决什么问题

1. **锁死修改爆炸半径** —— 业务拆成互不引用的自治切片（`AIF001` 编译期拦截跨切片引用）。
2. **上下文自洽** —— 每个切片 = 几个文件 + 60 行 `CONTRACT.md`，AI 不用通读全仓就能安全施工。
3. **脱离引擎毫秒级验收** —— 核心四层零引擎引用，`dotnet test` 秒级跑完；Unity 侧另有形状校验与真机测试。

## 二、一分钟上手

```powershell
# 1) 六层验证（build → test → viewgen → tablegen --check → 形状校验），约 25 秒
pwsh tools/verify-all.ps1

# 2) 全量验证（追加真 Unity：场景生成 + 装配校验 + EditMode + PlayMode），约 6~8 分钟
pwsh tools/verify-all.ps1 -Unity

# 3) 命令行冒烟（签到/商店/任务/邮件/弹窗 + 九套卡带巡礼 + 音频/校验端口）
dotnet run --project src/Adapters/AIFramework.Adapters.Console/AIFramework.Adapters.Console.csproj

# 4) 打开 Unity 工程，打开 BootScene 点 Play（24 个场景可从大厅进入）
#    unity/ 是完整工程；脚本都在"临时副本工程"里跑 batchmode，不影响正在用的编辑器

# 5) 出 Windows 冒烟包（产物在副本工程 Build/Windows 下，不进版本库）
pwsh unity/tools/build-player.ps1
```

## 三、目录地图

```
src/
  AIFramework.Contracts/      契约：CQE、Tag、数学、Session、端口、回放、服务端口
  AIFramework.Core/           CQE 总线、因果环形日志、会话宿主、红点、存档、回放、校验台账
  AIFramework.Kernel.Turn/    回合卡带：卡牌 / 三消 / 战棋 / 推箱子 / 俄罗斯方块 /
                              自走棋 / 跑酷 / 音游 / 平台跳跃 / 肉鸽卡牌 / 黑白棋
  AIFramework.Kernel.Realtime/实时固定帧管线（6 阶段、零 GC、护盾反射、出界回收）
  AIFramework.Features/       业务切片：签到/背包/商店/任务/邮件/放置/抽卡/合成/塔防/
                              本地化/模拟经营/挂机RPG/异步PVP
  AIFramework.Analyzer/       编译守卫 AIF001~AIF004
  Adapters/Console/           无头适配（冒烟、卡带巡礼）
  Adapters/Unity/             引擎端口实现 + 场景绑定 + 视图 Binder
unity/                        Unity 工程（17 场景 + 4 个 Editor 流水线脚本）
tests/                        纯逻辑测试（1481 例）+ ArchTests 架构守卫 + Unity EditMode/PlayMode
tools/                        verify-all / new-slice / viewgen / tablegen / fixtures(legacy)
Doc/                          架构总纲 / 切片规范与验证流 / 玩法卡带索引 / HybridCLR 方案 / 路线图
```

## 四、核心设计决策（为什么这么定）

| 决策 | 理由 |
|---|---|
| 少数物理程序集 + 平行依赖 `Contracts` | 兼容 Unity + HybridCLR 的极简程序集粒度，同时用 analyzer 挡住越界 |
| 玩法只加"卡带内容"，不加内核类型 | 回合内核已承载 11 套规则；新增玩法只写内容 + 场景，`GameplayKernelType` 只增不改 |
| 零 GC 实时管线 | `Span<T>` 端口 + 预分配槽位 + 定长缓冲；1000 帧分配增量断言为 0 |
| 一切跨系统通信走 CQE | 命令 / 查询 / 事件；切片之间零引用，适配层只做装配 |
| 视图契约由生成器产出 | `*.bindings.json` → `I<Panel>View.g.cs` + `<Panel>ViewBinder.g.cs`，禁止手写 |
| 场景全部脚本生成 | 场景 YAML 不进人工编辑，`generate-scenes.ps1` 离屏生成 + `SceneVerifier` 反射校验注入 |
| 配置表也走生成器 | `tables/*.json` → `tools/tablegen` → `.bin`（21 张表与手写夹具逐字节一致，`--check` 拦漂移） |

## 五、文档索引

| 文档 | 内容 |
|---|---|
| [Doc/01 总体架构设计总纲](Doc/01_AIFramework总体架构设计总纲.md) | 分层、拓扑、红线 |
| [Doc/02 切片工程规范与验证流](Doc/02_AI切片工程规范与验证流.md) | 目录树、CONTRACT 模板、**五层验证流水线**与已知边界 |
| [Doc/06 玩法卡带索引](Doc/06_玩法卡带索引.md) | 13 套卡带 / 14 个切片 / 17 个场景的对照表与"再加一个玩法"清单 |
| [Doc/07 HybridCLR 接入方案](Doc/07_HybridCLR接入方案.md) | 热更边界、接入步骤、验证清单、风险 |
| [Doc/ROADMAP](Doc/ROADMAP.md) | 三层无限队列与每轮里程碑 |
| [Doc/08 当前状态与验证证据](Doc/08_当前状态与验证证据.md) | 最近一次全量验证的命令/输出/已知未验证项 |

## 六、已知边界（诚实记录）

- **Unity 观感未人眼验收**：字符网格用 `LegacyRuntime.ttf`（比例字体），列宽不严格对齐；中文/符号字形是否缺字需真机确认。
- **Unity 音频未真机听**：`IAudioService` 与 `AudioSource` 池只过了形状校验。
- **异步 PVP 的对手池由本地公式生成**（注释标明"真实环境由服务端下发"）；`IMatchValidator` 的 HTTP 未接线，Unity 侧远端一律返回 `Unavailable`。
- **HybridCLR 只完成方案文档**，未在工程内安装（原因见 Doc/07 第五节）。
- **云存档未接真实 HTTP**：`ICloudSaveService` 与合并策略已完成并有 46 例测试，但 Unity 侧是离线模拟（`persistentDataPath/cloudsaves` 文件镜像），远端接口未接线。
- **帧耗时基线是"整帧间隔"**，不是"本组件耗时"；`GC.GetAllocatedBytesForCurrentThread` 在 IL2CPP 上待真机确认。
- **跑酷/平台跳跃的自动演示**：跑酷默认 autoPlay 永不撞死（内核只在碰撞时结束），要看结算需关掉 autoPlay 撞一次。

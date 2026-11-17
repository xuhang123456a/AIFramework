---
tags: [ai工程, AIFramework, 游戏架构, 引擎解耦]
status: 🌳
created: 2026-11-17
reviewed: 2026-11-17
source: AIFramework 顶层架构设计讨论与商业基建拍板
next_action: 基于本总纲、切片规范与搭建提示词，初始化 AIFramework.Pure.sln 工程骨架
---

# AIFramework 总体架构设计总纲

> **核心结论（TL;DR）**：`AIFramework` 不追求"单一玩法大一统"的伪命题，而是采用 **"70% 跨品类通用商业主板 + 30% 按品类可插拔的双玩法卡带 + 15% 极薄引擎适配壳"** 的模块化分层架构。通过将 85% 核心代码锁死在零引擎引用的纯 C#（`netstandard2.1`）层，既实现了同语言生态（Unity C# / Godot C# / .NET 服务端）的近乎零改动迁移，又通过 **"少数物理程序集 + 自定义 Roslyn 编译守卫 + Luban 独立子表 + UI 绑定代码生成"**，在适配 Unity + HybridCLR 商业热更的同时，将 AI Agent 的每次修改牢牢锁死在单一自治切片内。

---

## 📑 目录

- [一、 项目定位：为什么不做"大一统玩法引擎"](#一-项目定位为什么不做大一统玩法引擎)
  - [1.1 包容所有游戏类型的工程悖论](#11-包容所有游戏类型的工程悖论)
  - [1.2 务实的商业级解法：主板 + 可插拔卡带](#12-务实的商业级解法主板--可插拔卡带)
- [二、 总体分层架构与依赖拓扑](#二-总体分层架构与依赖拓扑)
  - [2.1 七大程序集拓扑与单向依赖关系](#21-七大程序集拓扑与单向依赖关系)
  - [2.2 各核心模块职责边界](#22-各核心模块职责边界)
- [三、 主板与双玩法内核的可插拔机制](#三-主板与双玩法内核的可插拔机制)
  - [3.1 为什么同一项目经常需要共存多套内核](#31-为什么同一项目经常需要共存多套内核)
  - [3.2 极窄的进出门契约（Session 沙盒模型）](#32-极窄的进出门契约session-沙盒模型)
  - [3.3 一条禁止触碰的抽象红线](#33-一条禁止触碰的抽象红线)
- [四、 引擎解耦四条铁律与迁移边界](#四-引擎解耦四条铁律与迁移边界)
  - [4.1 四条防沾染铁律](#41-四条防沾染铁律)
  - [4.2 UI 系统的跨引擎切分（Presenter 与 View 分离）](#42-ui-系统的跨引擎切分presenter-与-view-分离)
  - [4.3 实际移植成本与跨语言边界](#43-实际移植成本与跨语言边界)
- [五、 实战验收：架构防线与跨引擎基线检查标准](#五-实战验收架构防线与跨引擎基线检查标准)
- [六、 商业化基建拍板方案（热更、导表与 UI 生成）](#六-商业化基建拍板方案热更导表与-ui-生成)
  - [6.1 热更新 DLL 粒度与 Roslyn 编译守卫（方案 B）](#61-热更新-dll-粒度与-roslyn-编译守卫方案-b)
  - [6.2 Luban 纯 C# 二进制导表与切片独立子表](#62-luban-纯-c-二进制导表与切片独立子表)
  - [6.3 UI 自动绑定生成工具链](#63-ui-自动绑定生成工具链)
- [七、 核心术语对照表](#七-核心术语对照表)
- [八、 核心工具与参考资料](#八-核心工具与参考资料)

---

## 一、 项目定位：为什么不做"大一统玩法引擎"

### 1.1 包容所有游戏类型的工程悖论

在设计商业框架时，最容易踩入的陷阱是试图用同一套玩法层代码同时支撑三消、卡牌回合、SLG 与 MOBA/吃鸡。不同游戏品类的底层运行假设天然冲突：

* **宏观与回合品类**（卡牌、放置、消除、模拟经营）：核心诉求是异步状态流转、配置表驱动与弹窗队列。业务节奏由用户操作或服务器回包驱动，对单帧微秒级时序不敏感，最适合 **垂直切片（Vertical Slice）+ CQE 总线**。
* **强实时高频品类**（MOBA、动作、弹幕、射击）：核心诉求是固定步长逻辑帧、零 GC 连续内存复用、空间碰撞查询与帧内严格确定性仲裁。如果套用异步事件总线，会瞬间引发时序错乱与内存抖动。

强行大一统只会导致两种结果：要么底座堆满数十层泛化接口，让 AI 光理解胶水代码就耗尽上下文窗口；要么只留下毫无约束力的 `Init/Update/EventBus` 空壳，放任 AI 写出网状互调代码。

### 1.2 务实的商业级解法：主板 + 可插拔卡带

`AIFramework` 选择将"跨项目 100% 一致的商业基础设施"与"随游戏类型变化的局内战斗循环"彻底拆开：

* **通用主板（Meta-Game Kernel）**：覆盖所有商业游戏约 60%~70% 的代码量——大厅、背包、商店、任务、活动、邮件、红点、新手引导、弹窗排队、资源/配置加载与因果日志。
* **可插拔玩法卡带（Gameplay Kernels）**：按游戏类型按需引入、甚至在同一项目中同时挂载多套彼此隔离的玩法引擎（回合核 `Kernel.Turn` 与强实时核 `Kernel.Realtime`）。
* **极薄引擎适配壳（Engine Adapter）**：占代码量约 10%~15%，隔离引擎生命周期、渲染、物理与本地存储，保证主体工程不绑定任何特定游戏引擎。

---

## 二、 总体分层架构与依赖拓扑

### 2.1 七大程序集拓扑与单向依赖关系

全框架在物理目录与程序集上分为**纯逻辑侧（Pure C#）**与**引擎适配侧（Adapter）**。`Core`、`Kernel.Turn`、`Kernel.Realtime`、`Features` 与 `Adapters` 全部**平行单向依赖顶层 `Contracts`**，彼此之间互不引用，最后由适配壳内的 `EngineBootstrapper`（`ConsoleAppBootstrap` / `UnityBootstrapper`，不单独建程序集）装配注入；为兼容 Unity + HybridCLR 热更新，业务切片统一收口进单一 `AIFramework.Features` 程序集，内部命名空间隔离与禁引 `Core/Kernels` 由 `AIFramework.Analyzer` 在编译期强制拦截：

```text
+-----------------------------------------------------------------------+
|  纯逻辑侧（Pure C# / netstandard2.1，占 85% 代码，编译期零引擎引用）      |
|                                                                       |
|  [AIFramework.Contracts]                                              |
|   ├── ICqeBus / ICausalLog / CQE 基类 / 全局事件与指令协议              |
|   ├── IGameplaySession / GameplayInitData / GameplayResult            |
|   ├── GameplayTag / UnitHandle / ProjectileHandle / ISpatialQuery     |
|   └── IBinaryTableLoader / UI 视图接口 / 自有纯值类型数学库（Vec2/Vec3） |
|             ▲                    ▲                    ▲               |
|    ┌────────┘                    │                    └────────┐      |
|    │                             │                             │      |
| [AIFramework.Core]  [AIFramework.Kernel.*]       [AIFramework.Features]|
|  CQE 总线实现与      Turn（回合状态流）            单一热更聚合程序集：   |
|  定长环形因果日志    Realtime（固定帧管线+仲裁）   Shop/DailySignIn/Hero |
+----▲─────────────────────────────▲─────────────────────────────▲------+
     │                             │                             │
     └──────────────────────┐      │      ┌──────────────────────┘
                            │      │      │  (各自仅依赖 Contracts 端口)
              [AIFramework.Adapters.<Engine>]（装配职责内嵌，不单独建程序集）
               ├── EngineBootstrapper（驱动核心 Step + 唯一允许装配注入层）
               ├── UnitySpatialQuery （NonAlloc 写入 Span<UnitHandle>）
               ├── UnityAssetLoader  （实现 IAssetLoader / IBinaryTableLoader）
               └── 自动生成 ViewBinder（纯展示皮肤：透传点击、播动画特效）

  [编译守卫] AIFramework.Analyzer (netstandard2.0 Roslyn DiagnosticAnalyzer)
             ──> 编译期挂接 Contracts / Core / Kernels / Features 拦截越界
```

### 2.2 各核心模块职责边界

| 模块名称 | 所在侧 | 允许引用的下游 | 绝对禁止的行为 |
|---|---|---|---|
| `AIFramework.Contracts` | 纯逻辑侧 | 无任何内部依赖（仅基础 .NET 库） | 禁止包含任何具体业务逻辑实现、禁止引用任何引擎类型 |
| `AIFramework.Core` | 纯逻辑侧 | 仅 `Contracts` | 禁止知晓具体游戏有哪些商店、活动或玩法内核 |
| `AIFramework.Kernel.*` | 纯逻辑侧 | 仅 `Contracts` | 两个内核之间禁止互引，禁止直接读取全局玩家存档 |
| `AIFramework.Features` | 纯逻辑侧 | **仅 `Contracts`** | **禁止引用 `Core` / `Kernel.*`（`AIF004`），禁止 `Feature.<A>` 互引 `Feature.<B>`（`AIF001`）** |
| `AIFramework.Adapters.*` | 引擎适配侧 | `Contracts` + 对应引擎 SDK | 严禁在适配器与 `ViewBinder` 中编写任何业务条件分支 `if-else` |
| `Adapters.<Engine>` 内的 `EngineBootstrapper` | 装配启动职责（非独立程序集） | 全部模块（仅在初始化阶段） | 禁止在装配脚本里编写游戏业务规则；`Features` 永远看不到内核与总线实现 |
| `AIFramework.Analyzer` | 编译守卫 | Roslyn API (`netstandard2.0`) | 仅在编译期执行静态语法与语义校验，不打入运行包体 |

---

## 三、 主板与双玩法内核的可插拔机制

### 3.1 为什么同一项目经常需要共存多套内核

商业混合玩法游戏（例如主玩法为卡牌回合制、限时活动内置实时割草小游戏，或 SLG 大地图内嵌即时小队副本）要求同一个客户端同时具备多种玩法节奏。如果把 `Kernel.Turn` 和 `Kernel.Realtime` 做成编译宏互斥开关，就无法应对这种商业混合形态。

因此，`AIFramework` 的"可插拔"设计为**独立的沙盒插件（Cartridge）**：
* 纯卡牌项目只需把 `Kernel.Turn` 放进工程，完全不引入 `Kernel.Realtime`，编译零负担；
* 混合玩法项目可同时挂载两个内核包，主城点击不同入口时按需拉起对应内核的沙盒实例，**运行期彼此彻底隔离，互不干扰内存池与调度步长**。

### 3.2 极窄的进出门契约（Session 沙盒模型）

外围主板与任意玩法卡带之间，只通过 `Contracts` 中定义的 `IGameplaySession` 接口和两份纯数据结构体交互：

1. **进门（`GameplayInitData`）**：玩家开启战斗或小游戏时，主板将战前所需数据（出战单位属性快照、关卡 ID、随机种子、局内可用道具）打包为不可变初始化快照传入指定内核，创建独立沙盒实例。**局内运行期间，玩法内核严禁跨出沙盒读取背包、商店或玩家全局存档。**
2. **运行期（完全黑盒自治）**：
   * `Kernel.Turn` 接收局内玩家操作指令，经纯数据指令队列推演状态机，吐出只读表现指令流（如播受击动作、飘字）。
   * `Kernel.Realtime` 通过固定 6 阶段管线（输入采集 → Buff 步进 → 技能时间轴 → 空间碰撞与伤害仲裁 → 移动与控制权仲裁 → 清理与快照输出）推进 `BattleWorld.Step()`，利用编译期只读视图与预分配池化 `Intent` 缓冲做到零 GC 帧内隔离。
3. **出门（`GameplayResult`）**：对局结束时，玩法内核仅向外返回纯数据结算报告（胜负结果、剩余状态、触发的事件标签列表、请求发放的战利品清单及可选复现输入录像），随后整个沙盒内存原地销毁回收。主板拿到 `GameplayResult` 后，再通过 CQE 总线派发 `GrantItemCommand` 修改背包、推动任务进度。

> 会话的创建统一走 `Contracts` 中的 `IGameplayKernelFactory`（输入 `GameplayKernelType` 与进门前数据快照，返回 `IGameplaySession`），实现方只能是适配壳内的装配入口；`Core` 的 `GameplaySessionHost` 只持有该接口，绝不直接实例化具体内核——这是 `Core` 只依赖 `Contracts` 的必然结果。

### 3.3 一条禁止触碰的抽象红线

**绝对禁止在 `Kernel.Turn` 与 `Kernel.Realtime` 之间强行抽取共同的 `Unit` / `Entity` 基类。** 回合制棋子与强实时弹幕飞行物的内存模型、坐标体系、生命周期完全不同，强行合流只会在底层制造出臃肿的抽象漏斗。两套内核唯一允许共享的，只有进出门时那两个纯数据 DTO 结构体。

---

## 四、 引擎解耦四条铁律与迁移边界

### 4.1 四条防沾染铁律

要保证 `AIFramework` 既能让 AI 在无引擎环境下毫秒级验证，又能轻松迁移到同语言的其他引擎，必须从第一天起执行以下四条硬约束（详见 [AGENTS.md](../AGENTS.md)）：

1. **编译期物理零引擎引用**：纯逻辑侧程序集目标框架锁定 `netstandard2.1`（Unity `.asmdef` 开启 `noEngineReferences: true`），禁止出现 `UnityEngine`、`Debug.Log`、`Time.deltaTime`、`PlayerPrefs`、`DateTime.Now`。
2. **自有数学与纯数据配置**：逻辑层统一使用 `Contracts` 自带值类型数学库（`Vec2`、`Vec3`、`Quat`），仅在适配器边缘转换；策划配表与技能 `FrameData` 时间轴全部由 Luban 导出为纯 C# 只读数据对象，切断逻辑对 `ScriptableObject` 与动画帧事件的依赖。
3. **禁用引擎特有控制流**：禁用 `IEnumerator` 协程与 `MonoBehaviour` 回调（`Awake/Start/Update`）；异步流程使用标准 `async/await` 或逻辑帧计时器。
4. **独立 `.sln` 与统一编译基线**：纯逻辑侧维护为独立标准 `.NET` 解决方案（`AIFramework.Pure.sln`），`Directory.Build.props` 统一锁定 `LangVersion=9.0`（保证源码无缝兼容 Unity 2022/6 与 HybridCLR 编译）、`Nullable=enable` 与 `TreatWarningsAsErrors=true`（豁免非代码缺陷警告 `NU1901;NU1902;NU1903;NU1904;CS8032`）；`netstandard2.1` 需配合 `build/IsExternalInit.cs` 垫片（由 `Directory.Build.targets` 链接）才能使用 C# 9 的 `record` / `init`（垫片缺失时退回 `readonly struct`）；Console 宿主与测试项目统一采用当前 SDK 原生 `net10.0`。日常让 AI 开发切片与跑单元测试时直接在命令行运行 `dotnet test`，不启动游戏编辑器。**每天脱离引擎编译测试，是防止代码悄悄沾染引擎 API 的最硬保障。**

### 4.2 UI 系统的跨引擎切分（Presenter 与 View 分离）

为确保换引擎时只重写皮肤而不重写 UI 交互逻辑，同时保留"单个 UI 可安全让 AI 推倒重构"的优势，每个 UI 切片拆成职责严格切开的两半：

* **Presenter（逻辑大脑，留在纯 C# 切片内）**：负责列表排序、按钮可点校验、红点状态计算、发送业务 `Command`、组装纯数据展示视图模型（ViewModel）。完全支持脱离引擎运行自动化单测。
* **ViewBinder（控件皮肤，留在引擎 Adapter 侧）**：由编辑器绑定工具自动生成，仅把引擎控件点击事件透传给 Presenter，并将 Presenter 输出的文字/图标 Key/显隐状态赋给 UGUI、FairyGUI 或 Godot `Control`。

### 4.3 实际移植成本与跨语言边界

必须客观区分"同语言换引擎"与"跨语言换引擎"两类迁移场景的真实工作量：

| 迁移场景 | 可直接复用的资产比例 | 迁移时的实际工作内容 |
|---|---|---|
| **同语言跨引擎**（Unity C# ↔ Godot C# ↔ 纯 .NET 服务端校验） | **85% ~ 90% 代码零修改直接复用** | 仅重写 `Adapters.<NewEngine>`（约千行左右的端口实现：时间心跳、资源加载、空间查询、本地存储）以及新引擎下的 UI `View` 绑定皮肤。 |
| **跨语言跨引擎**（Unity C# → Unreal C++ / Cocos TS） | **0% 源码直接编译复用；100% 架构与契约可迁移** | 代码需按目标语言重新翻译，但因为无协程、`MonoBehaviour` 等引擎黑魔法缠绕，AI 可直接以现有纯 C# 切片 + `FrameData` 配置 + 单元测试集作为唯一真源，逐模块翻译并做等价行为验收。 |

---

## 五、 实战验收：架构防线与跨引擎基线检查标准

在框架骨架搭建完成或每次重大底座改动后，必须按以下可检验步骤执行基线验收：

1. **纯 `.NET` 独立编译与测试验收**
   * **操作步骤**：在不安装或完全关闭 Unity/Godot 编辑器的环境中，在框架根目录执行 `dotnet build AIFramework.Pure.sln` 与 `dotnet test`。
   * **验收标准**：纯逻辑侧全部工程（`Contracts`、`Core`、`Kernels`、`Features`、`Tests`）编译 0 Error，全部单元测试快速通过。
2. **Roslyn 编译守卫与架构测试验收**
   * **操作步骤**：运行 `AIFramework.ArchTests` 与 `AIFramework.Analyzer` 负向诊断用例（故意构造跨切片 `using`、引入 `UnityEngine` 或 `DateTime.Now`）。
   * **验收标准**：负向用例必须触发对应的编译器 Error 诊断码；正常代码库中 `Contracts`、`Core`、`Kernels` 与 `Features` 对引擎 API 引用数为 0，任意两个 `Feature.*` 命名空间交叉引用数为 0。
3. **跨引擎最小适配验证（Smoke Test）**
   * **操作步骤**：编写控制台极简 `ConsoleAdapter`（实现 `ITimeSource`、`ILogger`、`IBinaryTableLoader` 与 `MockSpatialQuery`），接入 `ConsoleAppBootstrap` 启动完整业务流程（登录 → 商店购买 → 拉起 `Realtime` 战斗沙盒推进 60 帧 → 返回 `GameplayResult` 发奖）。
   * **验收标准**：在纯黑框命令行下完整跑通"进局前 → 局内结算 → 局外背包入账"全链路且断言数据一致，证明框架真正独立于任何游戏渲染引擎运行。

---

## 六、 商业化基建拍板方案（热更、导表与 UI 生成）

针对 Unity + **HybridCLR**（基于原生 IL2CPP 运行时补充热更 C# 代码的商业热更方案）为主力环境的商业交付需求，以下三项基建方案已正式确定并纳入框架标准：

### 6.1 热更新 DLL 粒度与 Roslyn 编译守卫（方案 B）

不在工程里拆分几十个小切片 `.csproj`/`.asmdef`（避免热更 DLL 碎片化、补充元数据开销膨胀以及拖慢 Unity 域重载），而是采用 **"少数物理程序集 + Roslyn Analyzer 硬隔离 + ArchTests 兜底"**：
* **物理程序集收敛为少数大包**：全工程仅保留 `AIFramework.Contracts`、`AIFramework.Core`、`AIFramework.Kernel.Turn`、`AIFramework.Kernel.Realtime`、`AIFramework.Features` 与 `Adapters` 等少量程序集，Pure `.sln` 与 Unity 热更程序集一一对应，零同步脚本、零裁剪调试冲突。
* **编译期红线门禁（`AIFramework.Analyzer`）**：自研基于 `netstandard2.0` 的 **Roslyn DiagnosticAnalyzer**，直接挂入各 `.csproj` 与 Unity 工程中，在 IDE、命令行和 Unity 内部三端实时拦截以下行为并直接报为编译 Error：
  1. `AIF001`：`AIFramework.Features.<SliceA>` 命名空间下引用或 `using` 任何 `AIFramework.Features.<SliceB>`（除非属于 `Interactions` 隔离目录）；
  2. `AIF002`：纯逻辑程序集中出现 `UnityEngine`、`UnityEditor`、`Godot` 等引擎命名空间；
  3. `AIF003`：所有纯逻辑程序集（`Contracts`、`Core`、`Kernels`、`Features`）内直接调用 `DateTime.Now`、`DateTime.UtcNow` 或无参 `new Random()`；
  4. `AIF004`：`AIFramework.Features` 直接引用 `AIFramework.Core`、`AIFramework.Kernel.Turn`、`AIFramework.Kernel.Realtime` 或 `Adapters.*`（所有跨层能力必须经由 `Contracts` 接口注入）。
* **热路径因果日志零分配约束**：无论是宏观 `Core` 的 `ICausalLog` 还是强实时核内的帧追踪，热路径禁止拼接字符串或调用 `GetType()` 产生堆分配，统一使用**数值 Tag / 消息类型 ID + 固定结构体 + 定长环形缓冲（Ring Buffer）**，仅在日志导出或测试断言阶段按数值 ID 查表映射为字符串。

### 6.2 Luban 纯 C# 二进制导表与切片独立子表

选用 **Luban** 作为配置导出工具，导出纯 C# 只读数据代码（`cs-bin`）与二进制数据文件：
1. **公共表 vs 切片独立子表分离**：`TbItem`、`TbGlobalParam`、`TbGameplayTag` 等全游戏通用基础表归入公共只读配置域；活动与玩法专属表（如 `TbSignInReward`）通过 Luban 分组导出为切片独立子表，由对应切片接口挂载，严禁将所有活动子表堆砌在一个全局静态 `Tables` 单例中。
2. **加载端口抽象**：`Contracts` 定义纯 C# 接口 `IBinaryTableLoader`（输入表名返回 `byte[]?`；表不存在返回 `null` 由切片显式判定失败，格式/长度校验不符显式抛异常，禁止静默按空表继续；具体 Phase 1 Mock 二进制布局见 `Doc/03` §5.4）。真机接入 YooAsset/Addressables 读取热更资源，命令行 `dotnet test` 直接注入本地文件加载器读取真实策划二进制表。

### 6.3 UI 自动绑定生成工具链

为了确保 AI 永远不触碰上千行的 `.prefab` YAML 序列化文本，建立一条 **"显式标记 → 双向代码生成"** 的编辑器管线：
1. **语义化标记组件（`UIBindNode`）**：美术/程序在 Prefab 关键节点挂上轻量化标记组件并填写语义名（如 `ClaimButton`、`RewardList`），不依赖脆弱的节点层级路径，调整 UI 层级不丢引用。
2. **自动生成左右两半代码**：
   * **左半边（纯 C#，`I<Panel>View.g.cs`）**：生成至纯逻辑侧，仅含纯 C# 事件与渲染方法（如 `event Action OnClaimClicked; void RenderItems(IReadOnlyList<SignInSlotData> items);`），这是 AI 编写 `Presenter` 时唯一阅读的视图契约。
   * **右半边（Adapter 侧，`<Panel>ViewBinder.g.cs`）**：生成至 `Adapters.Unity/Views/`，继承 `MonoBehaviour` 并自动序列化绑定节点组件，实现 `I<Panel>View` 及通用列表池刷新，零运行期路径查找开销。

---

## 七、 核心术语对照表

| 标准术语 | 英文 / 代码对应 | 禁混淆别名 | 简明释义 |
|---|---|---|---|
| **纯逻辑侧** | `Pure C# Side` | 业务层/脚本层 | 脱离任何特定游戏引擎 API 的标准 `.NET` 代码层（目标 `netstandard2.1`），占约 85% 代码量，可在命令行直接编译测试。 |
| **引擎适配侧** | `Engine Adapter` | 引擎工具类 | 专门负责实现纯逻辑端口接口、对接具体引擎物理/渲染/资源/UI 控件的薄胶水层，换引擎时只重写此层。 |
| **主板** | `AIFramework.Core` | 全局大管家 | 跨品类通用的宏观业务底座，负责 CQE 总线调度、日志与生命周期宿主管理。 |
| **玩法卡带** | `Gameplay Kernel` | 战斗管理器 | 可按需引入甚至同项目共存的独立玩法引擎沙盒（如 `Kernel.Turn` 回合核与 `Kernel.Realtime` 强实时核）。 |
| **进出门契约** | `GameplayInitData` / `GameplayResult` | 共享全局上下文 | 主板与玩法沙盒之间唯一允许的数据通道，进入时传只读快照，退出时吐出纯数据结算单并销毁沙盒。 |
| **UI 逻辑大脑** | `Presenter` / `ViewModel` | UI 控制器脚本 | 留在纯 C# 切片中的界面交互逻辑层，负责数据格式化、按钮校验与发送指令，完全脱离控件运行。 |
| **Roslyn 编译守卫** | `AIFramework.Analyzer` | 人工 Code Review 约束 | 嵌入编译管道的静态分析插件（`netstandard2.0`），在三端实时将跨切片命名空间引用与引擎 API 泄漏报为编译 Error。 |

---

## 八、 核心工具与参考资料

1. **端口与适配器架构**：Alistair Cockburn, [Hexagonal Architecture](https://alistair.cockburn.us/hexagonal-architecture/)（框架纯逻辑核与引擎外设隔离的理论原型）。
2. **垂直切片架构**：Jimmy Bogard, [Vertical Slice Architecture](https://www.jimmybogard.com/vertical-slice-architecture/)（商业业务切片按特性垂直聚合的设计参考）。
3. **HybridCLR 商业热更新方案**：[HybridCLR 官方文档](https://hybridclr.doc.code-philosophy.com/)（Unity IL2CPP 热更程序集拆分与补充元数据依据）。
4. **Luban 配置解决方案**：[Luban 官方文档](https://luban.doc.code-philosophy.com/)（零引擎依赖纯 C# 二进制导表与分组导出依据）。
5. **Roslyn 编译期分析器**：[Tutorial: Write your first analyzer and code fix - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix)（实现 `AIFramework.Analyzer` 编译拦截）。
6. **强实时技能与标签系统工业参考**：[Gameplay Ability System (GAS) - Unreal Engine Documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/gameplay-ability-system-for-unreal-engine)（`Kernel.Realtime` 中标签与修改器仲裁的设计参照）。
7. **Unity 官方程序集定义**：[Assembly definitions - Unity Manual](https://docs.unity3d.com/Manual/assembly-definitions.html)（`noEngineReferences` 与少程序集热更边界配置依据）。
8. **.NET 独立构建与测试工具链**：[.NET CLI - dotnet test](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test)（支撑纯逻辑侧脱离引擎秒级回归的核心工具）。

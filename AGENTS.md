# AIFramework 项目工程宪法（AGENTS.md）

> 本文档为 `AIFramework` 的最高级别工程约束文件。所有参与设计、生成、重构或评审 `AIFramework` 代码的 AI Agent 与工程师，必须在动手前完整读取并严格遵守本规范；当局部习惯与本文件冲突时，以本文件为准。

---

## 一、 项目定位与核心目标

`AIFramework` 是一套**引擎无关（Pure C#）、面向 AI Agent 协作设计的商业级游戏开发框架**（主力交付环境为 Unity + HybridCLR，同时保留向其他 C# 引擎零业务改写迁移的能力）。

核心设计目标只有三项：
1. **锁死单次修改的爆炸半径**：将系统拆分为相互不可见的自治切片，确保重构任意单一 UI、活动或战斗技能时，其余模块零感知。
2. **上下文高度自洽**：每个业务/玩法切片的逻辑、状态与局部测试集中在极少文件内，让 Agent 无需通读全局代码即可安全施工。
3. **脱离引擎实现毫秒级自动验收**：核心代码编译期零引擎依赖，所有逻辑变更必须能在纯 `.NET` 命令行环境下直接编译并通过无头（Headless）单元测试。

---

## 二、 总体分层与依赖拓扑（不可违背）

为兼顾 Unity + HybridCLR 热更新极简程序集粒度与严格的 AI 防越界隔离，工程采用 **"少物理程序集 + Roslyn 编译守卫 + 架构测试兜底"** 方案。核心运行模块全部**平行单向依赖顶层 `Contracts`**，彼此互不可见，最后由适配壳内的唯一装配入口（`ConsoleAppBootstrap` / `UnityBootstrapper`）统一注入：

```text
                           [AIFramework.Contracts]  (netstandard2.1)
                       （CQE/Tag/自有数学/Session/ISpatialQuery/UI接口）
                                      ▲
         ┌────────────────────────────┼────────────────────────────┬────────────────────────┐
         │                            │                            │                        │
[AIFramework.Core]     [AIFramework.Kernel.Turn]   [AIFramework.Kernel.Realtime]   [AIFramework.Features]
（CQE总线/因果环形日志）  （回合/状态流玩法卡带）     （强实时固定帧管线卡带）      （业务/UI/技能切片聚合包）
         ▲                            ▲                            ▲                        ▲
         └────────────────────────────┴────────────────────────────┴────────────────────────┘
                                                    │
                     [AIFramework.Adapters.<Engine>]（内含唯一装配入口 <Engine>Bootstrap）
                        （引擎端口实现 / Console 无头宿主 / 装配注入职责，不单独成程序集）

[编译守卫] AIFramework.Analyzer（netstandard2.0 Roslyn DiagnosticAnalyzer）
           ──> 编译期强制挂接 Contracts / Core / Kernels / Features
```

### 强制拓扑规则
1. **严禁平级互引（`AIF001`）**：在 `AIFramework.Features` 内部，任何 `AIFramework.Features.<SliceA>` 命名空间下的代码，绝对禁止引用或 `using` 任何 `AIFramework.Features.<SliceB>`；越界行为必须由 `AIFramework.Analyzer` 直接报为编译错误。
2. **严禁核心逆向依赖与越级引用（`AIF004`）**：`AIFramework.Features` **只能引用 `AIFramework.Contracts`**，绝对禁止直接引用 `AIFramework.Core`、`AIFramework.Kernel.Turn`、`AIFramework.Kernel.Realtime` 或任何 `Adapters.*`；所有总线、日志、阶段接口与会话服务均通过 `Contracts` 中的接口由装配入口注入。
3. **严禁跨核引用**：`Kernel.Turn` 与 `Kernel.Realtime` 互相不可见，两者唯一共享的只有 `Contracts` 中的进出门契约（`IGameplaySession`、`GameplayInitData`、`GameplayResult`）。严禁为两套内核强行抽象统一的 `Entity` 基类。
4. **双边特例隔离**：极少数无法抽象的跨切片专属互动，严禁偷偷写进双方主体代码，必须显式收口至独立的 `AIFramework.Features.Interactions` 目录与命名空间白名单管理。

---

## 三、 引擎解耦与工程规范铁律

1. **编译期零引擎引用（`AIF002`）**：`Contracts`、`Core`、`Kernels` 与 `Features` 目标框架锁定 `netstandard2.1`（在 Unity 中 `.asmdef` 必须开启 `noEngineReferences: true`）。**严禁出现 `UnityEngine`、`UnityEditor`、`Godot`、`Debug.Log`、`Time.deltaTime`、`PlayerPrefs`。**
2. **确定性基础类型与时间随机源（`AIF003`）**：逻辑层统一使用 `AIFramework.Contracts` 提供的自有值类型（`Vec2`、`Vec3`、`Quat`）；时间与随机源统一走 `ITimeProvider` 与 `IRandomSource` 接口，**严禁在任何纯逻辑程序集（`Contracts`、`Core`、`Kernels`、`Features`）内直接调用 `DateTime.Now`、`DateTime.UtcNow` 或无参 `new System.Random()`**。
3. **C# 语言版本与严格编译基线**：纯逻辑库统一在 `Directory.Build.props` 中配置 `<LangVersion>9.0</LangVersion>`（确保源码可直接跨入 Unity 2022/6 与 HybridCLR 编译）、`<Nullable>enable</Nullable>` 与 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`；宿主与测试统一采用当前 SDK 原生目标框架（`net10.0`）。`TreatWarningsAsErrors` 的豁免范围固定为 `NU1901;NU1902;NU1903;NU1904;CS8032`，不得以关闭 `TreatWarningsAsErrors` 的方式绕过；同时 `netstandard2.1` 未内置 `IsExternalInit`，使用 `record` / `init` 前必须确认 `build/IsExternalInit.cs` 垫片已由 `Directory.Build.targets` 链接进工程。
4. **禁用引擎黑魔法控制流**：业务逻辑中**严禁使用协程（`IEnumerator` / `StartCoroutine`）**与 `MonoBehaviour` 生命周期回调；配置表使用 Luban 导出纯 C# 二进制结构并通过 `IBinaryTableLoader` 端口加载（活动专属表作为切片独立子表，禁止堆入全局大 `Tables` 单例）。
5. **UI 逻辑大脑与工具生成皮肤分离**：每个 UI 切片的 `Presenter` 留在 `AIFramework.Features` 接受无头单测；Unity 侧通过 `UIBindNode` 标记生成 `I<Panel>View.g.cs` 与 `Adapters.Unity` 侧的 `<Panel>ViewBinder.g.cs`。**AI 严禁直接读取或修改 `.prefab` YAML，且严禁在 `ViewBinder` 中编写任何业务条件分支。**

---

## 四、 宏观业务与强实时战斗边界红线

### 4.1 宏观外围系统（Meta-Game）
* 背包、商店、任务、活动、邮件、红点、引导等模块必须按特性纵向切片，跨系统通信一律收口为 `Contracts` 中的 **CQE 契约**（`Query` 只读查询、`Command` 显式意图、`Event` 事实广播）。
* 因果日志 `ICausalLog` 在热路径禁止拼接字符串或调用 `GetType().Name` 产生堆分配，必须使用**数值 Tag / 消息 ID + 固定值结构体 + 定长环形缓冲（Ring Buffer）**，字符串映射仅在导出或断言阶段按需转换。

### 4.2 强实时玩法系统（`Kernel.Realtime`）
* **禁止帧内直接篡改状态**：技能与 Buff 切片只接收编译期只读视图（`readonly struct` / `in`），只允许向预分配缓冲提交修改意图（`Intent` / `Modifier`），由固定 6 阶段管线（`BattleWorld.Step()`）中的统一仲裁器结算。
* **零 GC 内存与零分配端口契约**：`ISpatialQuery` 等空间查询端口必须采用 `Span<THandle>` 零分配签名（如 `int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)`），禁止返回堆数组或 `List<T>`；帧内预热后连续 `Step()` 1000 帧托管堆增量分配必须严格为 `0` 字节。
* **切断物理与动画的反向绑架**：出伤与打断判定必须由纯数据 `FrameData` 时间轴驱动，**严禁把核心判定逻辑挂在美术动画帧事件上**。
* **Tag 与协议降维**：禁止在技能代码里按具体英雄类名写特判（如 `if (target is HeroB)`）；所有跨英雄打断、反弹、免疫统一通过底座值句柄协议（如 `ProjectileHandle` / `IProjectileReadView`）与 `GameplayTag` 仲裁。

---

## 五、 AI Agent 标准施工流（必须执行）

当 Agent 接到开发或重构任务时，必须严格按以下 4 步执行，不得跳步：

1. **定位边界（Read-Only）**：先读取 `AIFramework.Contracts` 相关契约及目标切片根目录下的 `CONTRACT.md`（限 60 行以内的边界说明书），禁止为了改单一切片而顺手修改其他切片或底座核心。
2. **契约先行，防越界实现**：若必须新增跨模块消息，先与开发者确认 `Contracts` 变更，再在目标切片目录内实现代码；若发现需要跨切片直接调用或引用 `Core`/`Kernels`，立刻停止并改用 `Contracts` 接口与 CQE 协议。
3. **强制补齐无头单元测试**：任何新增或重构的业务/玩法逻辑，必须在 `tests/` 下编写不依赖引擎画面的纯内存单元测试（覆盖正常路径与至少 2 个边界反例）。
4. **命令行验证交付**：修改完成后必须执行纯 `.NET` 编译与 `dotnet test`（包含 `AIFramework.Analyzer` 规则 `AIF001`~`AIF004` 负向编译检验与 `AIFramework.ArchTests` 架构守卫），确认编译 **0 Error 且代码层 0 Warning**（`NU1901`~`NU1904`、`CS8032`、xUnit 分析器告警等依赖与工具链提示不计入，但必须在交付报告中列出实际出现的豁免警告 ID 与条数）且全部用例通过后，方可报告完成。

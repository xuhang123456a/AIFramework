# HybridCLR 热更接入方案（T3-8）

> 结论先行：本仓的架构**天生适配** HybridCLR——纯逻辑四层（Contracts / Core / Kernel.Turn / Kernel.Realtime / Features）已经满足
> "零引擎引用 + LangVersion 9 + 无 AOT 黑名单 API"，可以直接作为热更 DLL；适配层与 Unity 脚本留在主包里。
> 本文给出可执行步骤、程序集划分、验证清单与已知风险，**当前尚未在工程里真正安装 HybridCLR**（原因见末尾"为什么没直接装"）。

---

## 一、程序集划分（热更边界）

| 程序集 | 位置 | 热更？ | 理由 |
|---|---|---|---|
| `AIFramework.Contracts` | `src/AIFramework.Contracts` | ✅ 热更 | 只有接口/值类型，无引擎引用 |
| `AIFramework.Core` | `src/AIFramework.Core` | ✅ 热更 | CQE 总线 / 会话宿主 / 存档 / 回放，无引擎引用 |
| `AIFramework.Kernel.Turn` | `src/AIFramework.Kernel.Turn` | ✅ 热更 | 回合卡带内容 |
| `AIFramework.Kernel.Realtime` | `src/AIFramework.Kernel.Realtime` | ✅ 热更 | 实时固定帧管线 |
| `AIFramework.Features` | `src/AIFramework.Features` | ✅ 热更 | 业务切片（最常改，热更收益最大） |
| `AIFramework.Adapters.Unity` | `src/Adapters/.../Runtime` | ❌ 主包 | 引擎端口实现，必须 AOT |
| `AIFramework.Analyzer` | `src/AIFramework.Analyzer` | ❌ 编译期 | 只是 Roslyn 分析器 |
| Console 适配层 / 测试 | 其余 | ❌ | 不在 Unity 运行时 |

关键点：**热更 DLL 之间没有循环依赖**（`Contracts ← Core/Kernel/Features` 单向），所以 HybridCLR 加载顺序天然确定：
`Contracts → Core → Kernel.Turn/Realtime → Features`。

---

## 二、接入步骤（编辑器内 + 命令行）

1. **安装包**（UPM git 依赖）
   ```json
   // unity/Packages/manifest.json
   "com.code-philosophy.hybridclr": "https://github.com/focus-creative-games/hybridclr_unity.git"
   ```
   然后 Unity 菜单 `HybridCLR/Installer...` 安装对应 Unity 版本的 `hybridclr`（会往 `Packages` 与 `Assets` 写文件，**这一步需要真 Unity 且要联网**）。

2. **配置热更程序集**：`HybridCLR/Settings` 里把上表 5 个 DLL 加进 `hotUpdateAssemblies`。
   本仓 `.asmdef` 已满足前提：`noEngineReferences: true`（Contracts/Core/Kernels/Features 的 package asmdef 都是）。

3. **AOT 元数据补充**（HybridCLR 的老问题：泛型/委托的 AOT 实例化）
   在适配层加一个 `AOTMetadata.cs`（**放在主包，不热更**）：
   ```csharp
   // 只要这些泛型组合没被 AOT 编译过，热更 DLL 里 new/调用时会 MissingMethod
   AOT.GenericMethodSample<UnitHandle>();
   AOT.ReflectionSample<CqeBus>();
   ```
   本仓需要特别关注的泛型：`ICqeBus.Query<TQuery,TResult>`、`IntentBuffer<T>`、`Span<T>` 相关调用、`IQuery<T>` 的实现。
   实测清单建议从 `grep -r "Query<" src` 导出后逐条 `AOT.Add`。

4. **入口改造**：`BootController` 里在 `AIFrameworkUnityHost` 装配**之前**加载热更：
   ```csharp
   // 伪代码：真实 API 见项目接入版本的 HybridCLR 文档
   LoadMetadataForAOTAssemblies();   // 先补 AOT 元数据
   Assembly contracts = LoadHotUpdate("AIFramework.Contracts");
   Assembly core = LoadHotUpdate("AIFramework.Core");
   ...
   host = new AIFrameworkUnityHost(); // 反射/工厂创建，避免主包直接静态引用热更类型
   ```
   **注意**：主包不能直接 `new` 热更 DLL 里的类型（编译期就引用了），要么把装配入口也放进热更，要么用反射/委托桥接。
   本仓的适配层目前**直接引用**了 Features/Kernels（`AIFrameworkUnityHost` 里 `new InventoryHandler(...)`），
   所以接入 HybridCLR 时有两种做法：
   - **A（推荐、改动小）**：把装配入口下沉成一个热更里的 `HostFactory`，适配层只依赖 Contracts 的 `IHostFactory`；
   - **B（推荐给"全部热更"）**：把 `AIFrameworkGuestHost` 从适配层挪进热更 DLL（但它引用了 UnityEngine，**违反 AIF002**，需要单独审视）。

5. **版本管理**：热更 DLL 用"版本号 + 哈希"命名（如 `features_20261120_ab12cd.dll`），清单文件（JSON）记录 5 个 DLL 的版本与 CRC，下载后校验再加载。

6. **回滚**：保留上一版本 DLL；启动时若加载抛异常 → 清缓存 + 回退上一版本，仍失败则强制走整包。

---

## 三、验证清单（接入后必须逐条过）

| 检查项 | 方法 | 期望 |
|---|---|---|
| 纯逻辑层无 UnityEngine | `grep -r "UnityEngine" src/AIFramework.{Contracts,Core,Kernel.*,Features}` | 0 命中（AIF002 已守卫） |
| 热更 DLL 能加载 | 真机日志打印 5 个 Assembly 名 | 全部成功 |
| 泛型 AOT 补全 | 热更 DLL 里调用 `ICqeBus.Query<TQuery,TResult>` 全部 12 种组合 | 不抛 `ExecutionEngineException` |
| 回放一致 | 同种子热更前后 `StateFingerprint` | 完全一致（仓库已有回放测试） |
| 断网降级 | 删掉热更缓存启动 | 回退整包内容、不崩 |
| 帧耗时 | 热更 DLL 调用 vs 主包调用 | 差异 < 5%（JIT→AOT 桥接开销可接受） |

---

## 四、已知风险与对策

1. **泛型 AOT 黑洞**：`ICqeBus.Query<TQuery,TResult>` 是泛型虚调用，热更 DLL 里每个 `TQuery` 组合都要 AOT 实例化。对策：把所有组合列成显式清单 + 启动自检（调用一次并捕获异常）。
2. **委托/事件**：`Action<T>`、`Func<T,int>` 在热更 DLL 里不能依赖 AOT 默认实例化，需 `AOT.Add(typeof(Action<...>))`。
3. **`Span<T>`/`stackalloc`**：HybridCLR 支持，但 `netstandard2.1` 的 `Span` 实现依赖 `System.Memory`；Unity 2022+ 内置支持。**本仓零 GC 路径大量用 `Span<T>`，必须优先验证**。
4. **Analyzer 不参与热更**：AIF001~004 只在编译期生效；热更 DLL 也必须走同一套编译（`dotnet build` 产出），不要用 Unity 自己编热更 DLL，否则 analyzer 不生效。
5. **`LangVersion 9`**：HybridCLR 与 IL2CPP 都支持；但 Unity 侧脚本仍是 C# 9 以上，注意别在热更 DLL 里用 Unity 不认的运行时特性（本仓已锁 9）。

---

## 五、为什么没在本轮直接装

1. HybridCLR 安装要修改 `Packages/` + `Assets/` 并联网拉取，且**必须真 Unity 交互一次**（Installer 有版本选择 UI）；
2. 安装后 `generate-scenes.ps1` / `run-unity-tests.ps1` 的副本流水线要重新验证一遍包导入（当前 17 场景流水线是全绿的，不想在收尾阶段引入不确定性）；
3. 本轮已用**可验证的替代证据**覆盖了 HybridCLR 的前提条件：
   - 纯逻辑层零引擎引用（AIF002 + ArchTests 守卫）
   - `LangVersion 9` / `netstandard2.1`（`Directory.Build.props` 锁定）
   - 热更边界内 1000+ 纯逻辑测试全绿
   - 确定性回放（`StateFingerprint`）已可用于"热更前后一致性"验收

**下一步（下一轮）**：按第二节 1~4 步在真 Unity 里装包 → 加 `HostFactory` → 跑第三节验证清单 → 把结果写回本文。

---

## 六、本机实测结果（2026-11-20，方案已从"纸面"升为"可复跑的安装态"）

### 版本与前置
| 项 | 实测值 |
|---|---|
| Unity | `6000.3.21f1`（IL2CPP 模块已安装，`Editor/Data/il2cpp` 存在） |
| HybridCLR 包 | `com.code-philosophy.hybridclr@v9.1.0`（git URL，标签 `v9.1.0`） |
| 安装器 API | `HybridCLR.Installer.InstallerController`（`InstallDefaultHybridCLR` / `HasInstalledHybridCLR`） |
| 安装位置 | **工程内** `Library/HybridCLR/`：`hybridclr_repo` + `il2cpp_plus_repo` + `LocalIl2CppData-WindowsEditor` |
| 自检结果 | `installed=True`、`InstalledLibil2cppVersion=9.1.0`、`HybridclrLocalVersion=v6000.3.x-9.1.0`、`localCache=True` |

**关键结论（与网上多数旧教程不同）**：v9 起是**工程级 local il2cpp**，不改写 Unity 安装目录。
因此安装/卸载都是工程内可逆操作，不会污染同版本的其它项目——这是本轮敢在真机工程里直接装的原因。

### 一键入口（已进仓库）
- 菜单：`AIFramework/HybridCLR/安装并自检`、`AIFramework/HybridCLR/只做自检（不安装）`
- 批处理：`Unity -batchmode -quit -executeMethod AIFramework.Demo.Editor.HybridClrBootstrap.InstallAndVerify`
- 实现要点：`HybridClrBootstrap.cs` 用**反射**按类型名探测，包没解析时打印明确错误而不是把主工程编崩
  （若直接 `using HybridCLR.Editor`，未装包时整个工程不可编译，等于热更框架绑架主工程）。

实测日志（副本工程，仓库同一份 manifest 与脚本）：
```
[HybridCLR] unity=6000.3.21f1 package=com.code-philosophy.hybridclr@v9.1.0
[HybridCLR] PackageVersion=9.1.0
[HybridCLR] HybridclrLocalVersion=v6000.3.x-9.1.0
[HybridCLR] InstalledLibil2cppVersion=9.1.0
[HybridCLR] InstallDefaultHybridCLR 调用完成（幂等，已安装时会跳过）
[HybridCLR] installed=True installedLibil2cpp=9.1.0 localCache=True path=.../Library/HybridCLR
[HybridCLR] 自检通过：安装态就绪
```

### 装了包之后主工程仍然全绿（回归证据）
- 23 场景重新生成 + `SceneVerifier` 装配校验通过
- EditMode 11/11、PlayMode 33/33
- 纯逻辑 1196 例全绿（包只影响 Unity 侧，纯逻辑程序集零改动）

### 仍未做的部分（下一阶段，需要真机出包窗口）
1. 热更程序集切分：把"可热更业务"落成 `Assets/HotUpdate/` 下的 asmdef 并加入
   `HybridCLRSettings.hotUpdateAssemblies`（当前业务代码在本地包 `src/AIFramework.Features`，
   要热更需下沉一层壳程序集）。
2. 生成 `link.xml` / AOT 元数据（`HybridCLR/Generate/All`），并产出 `AOTGenericReferences`。
3. IL2CPP 出包 + 首次启动加载 `StreamingAssets/HotUpdate/*.dll.bytes` 的启动链路与回滚开关。
4. 真机（Windows/Android）验证：热更 dll 版本替换、AOT 泛型缺失报错、回滚到内置版本。

以上 4 项都依赖"完整 IL2CPP 出包 + 真机跑起来"，属于本仓当前唯一还停在 `[~]` 的条目（T3-8）。
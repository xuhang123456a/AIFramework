# AIFramework · Unity 接入骨架（M2 第一步）

本目录是**引擎适配侧的 Unity 宿主工程**。纯逻辑代码不在这里复制一份——它作为 UPM 本地包被引用：

```
src/AIFramework.Contracts        -> com.aiframework.contracts
src/AIFramework.Core             -> com.aiframework.core
src/AIFramework.Kernel.Turn      -> com.aiframework.kernel.turn
src/AIFramework.Kernel.Realtime  -> com.aiframework.kernel.realtime
src/AIFramework.Features         -> com.aiframework.features
src/Adapters/AIFramework.Adapters.Unity -> com.aiframework.adapters.unity
```

每个包根目录有 `package.json` + `*.asmdef`；纯逻辑包的 asmdef 都写了 `noEngineReferences: true`，所以在 Unity 里也拿不到 `UnityEngine`（与 `AIF002` 同一条红线，双重拦截）。构建产物统一落在仓库根 `artifacts/`，不会污染 Unity 的资源导入。

## 一、首次打开

1. Unity Hub → **Add / Open project** → 选择本目录（`unity/`）。
   - `ProjectSettings/ProjectVersion.txt` 固定为 `6000.3.21f1`；本机版本不同时 Hub 会提示切换或升级，**接受即可**（2022.3 LTS 同样支持 5 个纯逻辑包）。
   - **`manifest.json` 里的 `file:` 相对路径以 `unity/Packages/` 为基准**（不是工程根！），所以写的是 `file:../../src/...`。写成 `file:../src/...` 会解析到 `unity/src/` 并报 `Project has invalid dependencies: ... cannot be found`。
   - Unity 会自动补齐 `ProjectSettings/` 其余文件，并把 `.meta` 生成到各包目录——**把这些 .meta 一起提交**，否则每次打开都会重新分配 GUID。
2. 等待包解析。Console 里若出现 `com.aiframework.*` 的编译错误，先确认根目录 `artifacts/` 存在（Unity 不导入仓库根，不影响编译；这里只是提示你别把 `bin/obj` 生成回 `src/` 下）。
3. 菜单 **AIFramework → 创建演示场景** → 保存 → Play。

## 二、Play 起来应该看到什么

- Console 打印签到 7 格状态（`D1=Claimable, D2..D7=Locked`）。
- 场景里两个 Capsule（护盾单位 / 来袭单位）与一颗飞行物；`AIFrameworkBehaviour` 每帧推进 `BattleWorld.Step`。
- 反射发生后打印 `ReflectedCount=1`，主角存活则结算 `IsVictory=true`。

## 三、把编译守卫接进 Unity（可选但推荐）

自研 Roslyn 规则 `AIF001~AIF004` 也可以在 Unity 编辑器里实时生效：

```powershell
pwsh unity/tools/setup-analyzer.ps1
```

脚本会 `dotnet build` 分析器并复制到 `unity/Assets/Plugins/Analyzers/`；`Assets/Editor/AnalyzerLabelInstaller.cs` 会自动给它打上 `RoslynAnalyzer` 标签（不需要手点）。

## 三之二、没有 Unity 也能校验适配层（推荐进 CI）

```powershell
pwsh unity/tools/check-adapter-compile.ps1
```

它把 `src/Adapters/AIFramework.Adapters.Unity/Runtime/**` 连同 `unity/tools/shims/` 里的 UnityEngine 形状桩，按 `netstandard2.1 + TreatWarningsAsErrors` 编译一遍。能拦住语法/类型/接口实现缺成员/API 形状写错；**拦不住运行时行为**（桩里的 `Physics`、`Debug`、`Time` 全是空实现）。新增 Unity API 时要同步补桩。

## 四、已知限制（按优先级）

| 限制 | 现状 | 后续 |
|---|---|---|
| HybridCLR 热更 | **未接入** | 按包内 asmdef 拆热更程序集、补齐 AOT 元数据清单、真机验证 |
| 资源加载 | `UnityAssetLoader` 直读 `StreamingAssets` | Android 上 `StreamingAssets` 不是普通文件，接 YooAsset/Addressables 时整体替换该类，接口不变 |
| UI 绑定代码生成 | **未接入** | `UIBindNode` 标记 + `I<Panel>View.g.cs` / `<Panel>ViewBinder.g.cs` 双向生成器 |
| 导表 | 只有 Phase 1 的 12 字节夹具（`Assets/StreamingAssets/AIFrameworkDemo/TbSignInReward.bin`） | 接 Luban `cs-bin`，`IBinaryTableLoader` 换实现 |
| 场景/预制体 | 只有菜单生成的演示场景 | 正式场景由项目侧搭建 |

## 五、排障

| 现象 | 原因 | 处理 |
|---|---|---|
| `Project has invalid dependencies: com.aiframework.*: The file [...\unity\src\...\package.json] cannot be found` | `manifest.json` 的 `file:` 相对路径基准是 `unity/Packages/`，不是工程根 | 确保是 `file:../../src/...`；改完点 Retry，或删掉 `Packages/packages-lock.json` 后重开工程 |
| 包解析成功但 `AIFramework.*` 报编译错 | `src/<Assembly>/` 下出现了 `bin/`、`obj/`，Unity 把编译产物当资源导入 | 删掉它们；根 `Directory.Build.props` 已把产物重定向到 `artifacts/`，不要改回去 |
| Unity 里不报 `AIF001~AIF004` | 分析器 dll 没打 `RoslynAnalyzer` 标签 | 跑 `pwsh unity/tools/setup-analyzer.ps1`，`AnalyzerLabelInstaller` 会自动打标签 |

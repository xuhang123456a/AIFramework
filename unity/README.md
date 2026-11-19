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

## 二之二、UI 绑定（生成物已就绪，尚未接进演示场景）

签到面板的视图契约已经由 `tools/viewgen` 从 [SignIn.bindings.json](../src/AIFramework.Features/Feature.DailySignIn/SignIn.bindings.json) 生成：

- 左半边（纯逻辑）：[ISignInView.g.cs](../src/AIFramework.Features/Feature.DailySignIn/ISignInView.g.cs)
- 右半边（引擎皮肤）：[SignInViewBinder.g.cs](../src/Adapters/AIFramework.Adapters.Unity/Runtime/Views/SignInViewBinder.g.cs)

在场景里用它：

1. 建 Canvas，再建一个空 GameObject，挂 `SignInViewBinder`。
2. Inspector 里把列表容器拖到 `slotsContent`、列表项预制体拖到 `slotsItemPrefab`。
3. 按钮的 `onClick` 连到绑定组件上的 `RaiseClaimClicked`（带序号的补签按钮连 `RaiseRetroactiveClicked`）。
4. 列表项要显示什么，由项目侧写一个 `partial class SignInViewBinder` 实现 `OnBindSlotsItem(GameObject item, SignInSlotViewData data)` —— 生成器只管容器与显隐，不管皮肤细节。

演示场景目前用的是无头 `NullSignInView`（只往 Console 打格子状态），因为 Phase 1 还没做「从 `UIBindNode` 导出 bindings.json」的编辑器工具。

## 二之三、大厅（Meta 切片）在 Unity 里怎么用

`AIFrameworkUnityHost` 已经装好整条大厅链路，场景脚本只需要取用：

| 成员 | 用途 |
|---|---|
| `host.Inventory` | 真实背包账本（`GetCount` / `Snapshot` / `Seed`） |
| `host.RedDots` | 红点树（切片发 `RedDotDeltaCommand` / `RedDotSetCommand`，UI 查 `RedDotQueryRequest`） |
| `host.Shop` / `ShopAvailable` | 商店（子表 `TbShopItem.bin` 缺失时自动降级，不牵连其它切片） |
| `host.Quests` / `QuestAvailable` | 任务（`TbQuest.bin`） |
| `host.Mails` / `MailAvailable` | 邮件（`TbMail.bin`） |
| `host.LobbySave` | 大厅存档服务（`SaveService`，CRC 包络） |
| `host.PublishGameFinished(kernel, levelId, isVictory)` | 把一局结果广播给任务/活动切片（`AIFrameworkBehaviour` 结算时已自动调用并结算战利品） |

四个面板（签到 / 商店 / 任务 / 邮件）的视图契约与绑定皮肤都由 `tools/viewgen` 生成：`I<Panel>View.g.cs`（纯逻辑侧）+ `<Panel>ViewBinder.g.cs`（`Runtime/Views/`）。改 UI 先改 `*.bindings.json` 再重新生成，Presenter 与测试不动。

## 二之四、大厅场景（一键生成）

菜单 **AIFramework → 创建大厅场景**，生成 `Assets/AIFrameworkDemo/Scenes/LobbyScene.unity`：Canvas + 签到/商店/任务/邮件四个面板 + `LobbyController`。

- 场景由编辑器脚本建（`unity/Assets/AIFrameworkDemo/Editor/LobbySceneBuilder.cs`），AI 不碰 `.unity` YAML；
- 运行时 `LobbyController` 自己 `new AIFrameworkUnityHost()` 并装配四个 Presenter，控件引用在生成时已喂好；
- 按 Play 后：点"领取今天"走签到切片，点商品买走商店切片（先扣后给），点"打一场"跑一轮 Realtime 内核并把 `GameFinishedEvent` 喂给任务切片；
- 子表缺失时对应面板自动降级（Console 会打 `[AIFramework] xxx 装配失败`），不影响其它面板。

## 二之五、场景一览（全部由编辑器菜单生成）

| 菜单项 | 场景 | 玩法 | 状态 |
|---|---|---|---|
| `AIFramework/创建演示场景` | `DemoScene` | Realtime 护盾反射最小演示（两个 Capsule + 飞行物） | 已有 |
| `AIFramework/创建大厅场景` | `LobbyScene` | Meta 主板：签到/商店/任务/邮件 + 打一场 | 已有 |
| `AIFramework/创建启动场景` | `BootScene` | 启动 → 自动跳 `LobbyScene` | 新增 |
| `AIFramework/创建卡牌战斗场景` | `CardBattleScene` | Kernel.Turn · CardBattle（抽牌/能量/回合） | 新增 |
| `AIFramework/创建三消场景` | `Match3Scene` | Kernel.Turn · Match3（交换/消除/级联） | 新增 |
| `AIFramework/把场景加入 Build Settings` | — | 按 Boot → Lobby → CardBattle → Match3 顺序写入 Build Settings | 新增 |

**场景切换纪律**：Boot 场景**不创建宿主**；每个玩法场景自己 `new AIFrameworkUnityHost()` 并在 `OnDestroy` `Dispose()`（`LoadScene` 是同步的，旧场景对象先销毁，宿主时序自然正确）。

**场景间导航**：大厅有「进入卡牌战斗场景」「进入三消场景」两个按钮；战斗场景有「返回大厅」。跳转统一走 `SceneNavigator` + `UnitySceneDirector`（同场景不重载、场景缺失只打错误日志不崩）。切场景前大厅会先存一次背包。

### 场景可以离屏批量生成（不需要人点菜单）

```powershell
pwsh unity/tools/generate-scenes.ps1
```

它把仓库复制到临时目录（保留 `Library` 做增量导入）→ 在副本里跑 `Unity -batchmode -executeMethod AIFramework.Demo.Editor.GenerateAllScenes.Generate` → 把 `*.unity`、`.meta`、`EditorBuildSettings.asset` 拷回仓库。用户当前开着编辑器也能跑（副本不受工程独占锁影响）。

### Unity 侧自动化测试（真机引擎里跑）

```powershell
pwsh unity/tools/run-unity-tests.ps1            # EditMode，10 例
pwsh unity/tools/run-unity-tests.ps1 -Filter HostSmokeTests
```

覆盖纯 .NET 单测覆盖不到的部分：`StreamingAssets` 表加载、Unity 时间/随机/存档端口、Session 端口行为、
Build Settings 场景顺序。跑在与 `generate-scenes.ps1` 相同的副本工程里，不影响你正在用的编辑器。

> 依赖 `com.unity.ugui`（`UnityEngine.UI` 在独立包里，不在 `com.unity.modules.*`）——缺了它 HUD 在真 Unity 里编不过。已写进 `Packages/manifest.json`。

## 三、把编译守卫接进 Unity（可选，默认不装）

纯逻辑侧的 `AIF001~AIF004` 已经由 `dotnet build` 与 `check-adapter-compile.ps1` 守住；Unity 侧守卫只是"编辑器里实时提示"的便利功能，**默认不安装**。

需要时装：

```powershell
pwsh unity/tools/setup-analyzer.ps1          # 安装（会同时写 .meta：打 RoslynAnalyzer 标签 + 关闭引用校验）
pwsh unity/tools/setup-analyzer.ps1 -Remove  # 移除
```

为什么要写 meta：Unity 会把 `Assets/` 下的 dll 当普通插件做**引用校验**，分析器引用的 `Microsoft.CodeAnalysis` 在工程里不存在，于是报 `Unable to resolve reference 'Microsoft.CodeAnalysis'`。脚本把 `validateReferences` 置 0 并打上 `RoslynAnalyzer` 标签，Unity 才会把它当分析器而不是插件。仍失败就跑 `-Remove` 退回纯 `dotnet build` 形态。

## 三之二、没有 Unity 也能校验适配层（推荐进 CI）

```powershell
pwsh unity/tools/check-adapter-compile.ps1
```

它把 `src/Adapters/AIFramework.Adapters.Unity/Runtime/**` **以及 `unity/Assets/**/Editor/**` 的编辑器脚本**，连同 `unity/tools/shims/` 里的 UnityEngine / UnityEngine.UI / UnityEditor 形状桩，按 `netstandard2.1 + TreatWarningsAsErrors` 编译一遍。能拦住语法/类型/接口实现缺成员/API 形状写错；**拦不住运行时行为**（桩里的 `Physics`、`Debug`、`AssetDatabase` 全是空实现）。新增 Unity API 时要同步补桩。

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
| Unity 里不报 `AIF001~AIF004` | 分析器 dll 没装（默认不装） | 跑 `pwsh unity/tools/setup-analyzer.ps1`；报 Roslyn 解析失败就 `-Remove` 退回，红线仍由 `dotnet build` 守 |
| Console 报 `Unable to resolve reference 'Microsoft.CodeAnalysis'` | 分析器 dll 被当普通插件做引用校验 | 同上：`setup-analyzer.ps1 -Remove` 立即解除；重装用脚本（会写 `validateReferences: 0` 的 meta） |
| 报 `CS0104: 'ILogger' 是不明确的引用` | `UnityEngine.ILogger` 与 `Contracts.Ports.ILogger` 撞名 | 已在 `UnityLogger.cs` 用 `using ILogger = ...` 修好；新增文件同时 using 这两个命名空间时同样要写别名（`check-adapter-compile.ps1` 的形状桩已含 `UnityEngine.ILogger`，会提前拦到） |
| Package Manager 报 `Error searching for packages` | 访问 Unity 官方 registry 失败（网络/代理），与本工程无关 | 本地 `file:` 包不经 registry，可忽略；需要官方包时再修代理 |

# tools/viewgen · UI 绑定代码生成器

把「UI 控件语义描述」翻译成左右两半代码，是 `Doc/01` §6.3「AI 永远不碰 `.prefab` YAML」的落地工具。

```
*.bindings.json  ──viewgen──┬──> I<Panel>View.g.cs          （纯逻辑侧：接口契约，Features 切片内）
                            └──> <Panel>ViewBinder.g.cs     （引擎侧：MonoBehaviour 皮肤，Adapter 包 Runtime/Views/）
```

## 一、为什么要有它

- **AI 只读接口**：Presenter 面对的永远是生成的 `I<Panel>View`，不碰 `prefab`、不写引擎代码。
- **皮肤可重写**：换 UI 方案（UGUI → UI Toolkit → 换引擎）只重生成右侧 Binder，Presenter 与测试一行不动。
- **生成物可校验**：`.g.cs` 文件头写明来源，`--check` 与 `dotnet test` 都会拦住手改。

## 二、描述文件格式

```json
{
  "panel": "SignIn",
  "viewNamespace": "AIFramework.Features.DailySignIn",
  "summary": "签到面板视图接口：只负责展示与把交互抛给 Presenter。",
  "bindings": [
    { "kind": "Event",        "name": "ClaimClicked",       "summary": "点击当日领取按钮。" },
    { "kind": "EventWithInt", "name": "RetroactiveClicked", "parameter": "dayIndex" },
    { "kind": "List",         "name": "Slots",              "itemType": "SignInSlotViewData" }
  ]
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `panel` | 是 | 面板名，决定 `I<Panel>View` / `<Panel>ViewBinder` |
| `viewNamespace` | 是 | 接口所在命名空间（纯逻辑侧切片命名空间） |
| `summary` | 否 | 接口的 XML 注释 |
| `bindings[].kind` | 是 | `Event` / `EventWithInt` / `List` / `Text` / `Visible`（大小写不敏感） |
| `bindings[].name` | 是 | 语义名，唯一；决定成员名（`On<Name>` / `Render<Name>`） |
| `bindings[].itemType` | `List` 必填 | 列表元素类型 |
| `bindings[].parameter` | 否 | `EventWithInt` 的参数名，默认 `dayIndex` |
| `bindings[].summary` | 否 | 该成员的 XML 注释 |

## 三、生成的成员

| kind | 接口（左半边） | Binder（右半边） |
|---|---|---|
| `Event` | `event Action? On<Name>;` | `public void Raise<Name>()`（供 UnityEvent 连接） |
| `EventWithInt` | `event Action<int>? On<Name>;` | `public void Raise<Name>(int param)` |
| `List` | `void Render<Name>(IReadOnlyList<T> items);` | 池化 `Instantiate` + 显隐；项目侧实现 `partial void OnBind<Name>Item(GameObject, T)` |
| `Text` | `void Render<Name>(string text);` | 转发 `partial void OnRender<Name>Text(string)` |
| Number | `void Render<Name>(int value);` | 转发 `partial void OnRender<Name>Number(int)` |
| `Visible` | `void Render<Name>(bool visible);` | 转发 `partial void OnRender<Name>Visible(bool)` |

`partial void` 是刻意设计：**没实现时编译器会把调用消掉**，所以先跑通逻辑、后补皮肤，不会因为缺皮肤编译不过。

## 四、用法

```powershell
# 生成（默认输出：描述同目录的 I<Panel>View.g.cs + Adapter 包 Runtime/Views/<Panel>ViewBinder.g.cs）
dotnet run --project tools/viewgen -- src/AIFramework.Features/Feature.DailySignIn/SignIn.bindings.json

# 只校验磁盘上的生成物是否与描述一致（CI / 提交前；过期返回码 4）
dotnet run --project tools/viewgen -- src/AIFramework.Features/Feature.DailySignIn/SignIn.bindings.json --check

# 扫描全仓 *.bindings.json 逐份校验（CI 用这个）
dotnet run --project tools/viewgen -- --all

# 覆盖输出位置
dotnet run --project tools/viewgen -- <json> --interface-out <path> --binder-out <path> --binder-namespace <ns>
```

`dotnet test tests/AIFramework.ViewGen.Tests` 里有一条**金标准用例**：直接比对仓库里已提交的两份 `.g.cs` 与描述文件，手改生成物会当场变红。

## 五、边界（Phase 1）

- Unity 侧还没有「从场景/预制体收集 `UIBindNode` 并导出 `bindings.json`」的编辑器工具，**描述文件目前手写**（一个面板一份，很小）。
- Binder 不引用 UGUI 类型：按钮通过 `RaiseXxx()` 交给 UnityEvent 连线，列表项内容交给 `partial` 钩子——这样生成的代码不绑定具体 UI 框架。
- 列表池只增不减（`Ensure<Name>Items`），符合「UI 面板生命周期内不抖动分配」的口径。

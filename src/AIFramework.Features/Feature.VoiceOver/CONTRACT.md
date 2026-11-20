# Feature.VoiceOver 切片边界契约

## 1. 职责定位

表驱动语音/字幕：只回答"这一行此刻该用哪条语种配音、字幕在不在窗口内、播完了没"。
不加载音频、不解析文案、不画 UI、不落存档、不管口型与字幕样式
（`audioKey` 只是"应播"事实，资源与播放通道归适配层；`textKey` 归本地化切片解析）。

## 2. 跨模块契约依赖（唯一合法入口）

* **处理指令（In）**：`VoiceLinePlayCommand(lineId)`（播放意图）。
* **只读查询（Out）**：`VoiceLineQueryRequest` → `VoiceOverSnapshot`。
* **广播事件（Out）**：`VoiceLineChangedEvent(lineId, speaker, textKey, startMs)` / `VoiceLineCompletedEvent(lineId)`。
* **端口依赖（In）**：`ICqeBus`（构造注入）、`IBinaryTableLoader`（子表）。
* **时间来源**：**本切片不读任何时钟**，进度只由调用方 `Tick(deltaMs)` 推进（AIF003）。

## 3. 独立子表（Config）

* 表名 `TbVoiceLine`，列顺序即解析顺序：`lineId` / `speaker` / `textKey` / `audioKey` /
  `language` / `startMs` / `endMs`（3 个 string + 4 个 int32）。
  主键是**复合键** `(lineId, language)`：同一台词按语种各占一行（`tools/tablecheck` 已按复合键校验）。
* 布局：小端 `[int32 rowCount]` + 每行
  `{ int32 lineId; [int32 len]+UTF-8 speaker; …textKey; …audioKey; int32 language; int32 startMs; int32 endMs }`。
* 校验：表缺失 → 构造抛 `InvalidOperationException`；截断 / 尾部残留 / `lineId <= 0` /
  `speaker` 空 / `textKey` 空 / `language` 不在 0..2 / `startMs < 0` / `endMs <= startMs` /
  同一 `lineId`+`language` 重复 → 抛 `InvalidDataException`。`audioKey` 允许为空（只有字幕）。
* 0 行是合法配置：快照为空、`Play` 返回 false、不崩。行顺序即配置顺序，`Lines` 顺序 = lineId 首次出现顺序。

## 4. 关键规则（测试已钉死）

1. 语种三级选行：当前语种行 → 默认语种（0=简中）行 → 该 lineId 的任意语种行（仅元数据，
   `hasSubtitle`/`hasAudio` 强制 false，UI 显示"该语音缺失"）；三级全无 = 该 lineId 不存在。
   `VoiceLineState.Language` 回报**实际选中行**的语种，UI 可据此判断是否发生回退。
2. 字幕时间轴：`startMs`/`endMs` 相对该行播放起点，区间左闭右开 `[startMs, endMs)`；
   `hasSubtitle` 只在"该行正在播且 `elapsed ∈ 区间`"时为 true，其它行恒 false。
3. `hasAudio` = 选中行 `audioKey` 非空；落到"任意语种"层时恒 false（禁止串语种配音）。
4. `Play`：置 `CurrentLineId`、进度归 0、`IsPlaying=true`，广播一次 `Changed`；
   未知 lineId（含空表）静默忽略——不改状态、不发事件、返回 false。
5. `Tick(deltaMs)`：未播放 / 非正 delta 一律忽略；`elapsed >= endMs` 时夹到 `endMs`、
   `IsPlaying=false` 并广播**一次** `Completed`（同一行生命周期只发一次）。
6. 播放中的行沿用**播放时选中**的行（语种/时间轴/audioKey）：切语种只影响下次 `Play` 与非当前行。
   `SetLanguage` 对非法值、同值返回 false，不发事件。
7. 快照只读：不推进进度、不改状态；`CurrentLineId = 0`（`NoLine`）表示还没播过任何台词。

## 5. 绝对禁区

* 禁止引用或 `using` 任何其他 `AIFramework.Features.*`（AIF001）；禁止 `Core`/`Kernel.*`/`Adapters.*`（AIF004）。
* 禁止 `UnityEngine` / 协程 / `DateTime.Now` / `DateTime.UtcNow` / 无参 `new Random()`（AIF002、AIF003）。
* 禁止调用 `IAudioService` 或加载音频资源（切片只记录 audioKey）；禁止查本地化表解析文案。
* 禁止 LINQ；禁止把本表堆进全局 `Tables`。

## 6. 验收命令

```
dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~VoiceOver"
dotnet run --project tools/tablegen -- --check
```

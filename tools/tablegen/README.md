# tools/tablegen · 配置表导出器（Luban 替代）

把 `tables/*.json`（表定义 + 数据）导出成各切片解析器正在吃的二进制，并提供 `--check` 漂移门禁。
定位与 `tools/viewgen` 同构：**JSON 是唯一源，`.bin` 是生成物，禁止手改 `.bin`**。

```
tables/<Table>.json  ──tablegen──>  unity/Assets/StreamingAssets/AIFrameworkDemo/<Table>.bin
                                 └> src/Adapters/AIFramework.Adapters.Console/DemoTables/<Table>.bin
```

## 一、为什么要有它

- **消灭手写二进制**：`tools/fixtures` 里一行行 `writer.Write(...)` 改成可读 JSON，改数值不再碰 C#。
- **两宿主同源**：Console 无头宿主与 Unity 读同一份 `.bin`，杜绝"冒烟过、Unity 里另一套数值"。
- **漂移可拦截**：`--check` 逐字节比对，改了 JSON 忘导出会被 CI 与 `dotnet test` 同时拦下。
- **迁移期可证伪**：`--verify-against <dir>` 直接与既有 fixtures 产物比对，本次迁移 12 张表逐字节一致。

## 二、JSON 格式

```json
{
  "table": "TbShopItem",
  "columns": [
    { "name": "itemId", "type": "int32" },
    { "name": "priceItemId", "type": "int32" },
    { "name": "priceCount", "type": "int32" }
  ],
  "rows": [
    { "itemId": 2001, "priceItemId": 1, "priceCount": 100 }
  ]
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `table` | 是 | 表名，必须等于文件名（`TbShopItem.json` ↔ `"TbShopItem"`），并决定输出 `<Table>.bin` |
| `columns[]` | 是 | 列定义，**顺序即字节顺序**；至少一列，列名唯一 |
| `columns[].name` | 是 | 列名（= 行对象的字段名） |
| `columns[].type` | 是 | `int32` / `float32` / `bool` / `string` / `int32[]` / `string[]` / `<types 里声明的结构体>[]` |
| `rows[]` | 否 | 行数据；每行必须**不多不少**包含全部列，缺失或多出都报错 |
| `types` | 否 | 结构体声明（见下），只在本文件内可见 |

### 字节布局（小端，全仓库统一）

| 表头 | 说明 |
|---|---|
| `[int32 rowCount]` | 行数（`0` 合法，表示空表；不是"表缺失"） |

| 列类型 | 单行编码 | 定长 |
|---|---|---|
| `int32` | 4 字节小端 | 是 |
| `float32` | 4 字节 IEEE754 小端（`BitConverter.SingleToInt32Bits`） | 是 |
| `bool` | 1 字节（`0` / `1`） | 是 |
| `string` | `[int32 UTF-8 字节长度]` + 字节（空串写 `0`，仍是 4 字节前缀） | 否 |
| `int32[]` | `[int32 元素数]` + 元素×int32 | 否 |
| `string[]` | `[int32 元素数]` + 元素×(长度前缀 + 字节) | 否 |
| `<Struct>[]` | `[int32 元素数]` + 元素×(字段按声明顺序逐个编码) | 否 |

> 别用 `BinaryWriter.Write(string)`：那是 7-bit 变长长度前缀，和本仓布局不兼容。导出器手写长度前缀与小端字节。

## 三、嵌套结构：`TbLocalization` 为什么用 `types`

`LocalizationTables` 的布局是 **key + langCount + 每个语言条目 (lang, textLen, text) 交错**：

```
[int32 keyCount] + keyCount × { [int32 keyLen]key + [int32 langCount]
                              + langCount × { [int32 lang][int32 textLen]text } }
```

若按「并列数组」写成 `languages:int32[]` + `texts:string[]`，字节会变成
`[count][langs...][count][texts...]`，与既有 `.bin` 不逐字节一致。因此 `TbLocalization.json`
用结构体数组表达交错关系（`int32[]` / `string[]` 两种并列类型仍然完整实现并被单测覆盖）：

```json
{
  "table": "TbLocalization",
  "types": {
    "LocalizationEntry": [
      { "name": "lang", "type": "int32" },
      { "name": "text", "type": "string" }
    ]
  },
  "columns": [
    { "name": "key", "type": "string" },
    { "name": "entries", "type": "LocalizationEntry[]" }
  ],
  "rows": [
    { "key": "ui.play", "entries": [ { "lang": 0, "text": "开始游戏" }, { "lang": 1, "text": "Play" } ] }
  ]
}
```

结构体字段只允许标量与并列数组，不允许再嵌套结构体（避免导出器递归出无穷形状）。

## 四、用法

```powershell
# 导出到两个默认目标目录（Console DemoTables + Unity StreamingAssets）
dotnet run --project tools/tablegen

# 导出到指定目录（可重复 --out）
dotnet run --project tools/tablegen -- --out artifacts/tables-out

# 漂移门禁：重新编码并与目标目录逐字节比对，不一致返回 1（CI 用这个）
dotnet run --project tools/tablegen -- --check

# 迁移期：额外与既有 fixtures 产物比对（可重复）
dotnet run --project tools/tablegen -- --verify-against unity/Assets/StreamingAssets/AIFrameworkDemo

# 指定表定义目录（默认 <仓库根>/tables）
dotnet run --project tools/tablegen -- --tables D:\tmp\tables --out D:\tmp\out --check
```

| 选项 | 说明 |
|---|---|
| `--tables <dir>` | JSON 源目录，默认 `<仓库根>/tables` |
| `--out <dir>` | 输出目录，可重复；省略时写两个默认目标 |
| `--check` | 只校验不写盘：重新编码 → 与目标目录逐字节比对 |
| `--verify-against <dir>` | 额外与既有产物目录比对，可重复 |
| `-h` / `--help` | 用法 |

退出码：`0` 成功 / `1` 漂移 / `2` 用法或环境错误 / `3` 表定义不合法（未知列类型、行字段缺失、值类型不匹配等）。

`--check` 除了比对已有 `.bin`，还会抓"目标目录多出 `tables/` 里没有的 `.bin`"（删表忘删产物同样是漂移）。

## 五、与 `tools/fixtures` 的关系

`tools/fixtures` 是 Phase 1 的手写二进制生成器，**暂时保留**（等正式把 fixtures 标 legacy 再删）。
两套产物必须逐字节一致，这是本次迁移的硬指标，验证命令：

```powershell
dotnet run --project tools/fixtures/Fixtures.csproj -- --out artifacts/fixtures-ref
dotnet run --project tools/tablegen -- --out artifacts/tables-ref
Get-FileHash artifacts/fixtures-ref/*.bin, artifacts/tables-ref/*.bin   # 12 张表 SHA256 两两相同
```

## 六、加一张新表

1. 在 `tables/` 新建 `<Table>.json`（`table` 必须等于文件名）；
2. `dotnet run --project tools/tablegen` 导出；
3. 切片侧按 `*.bin` 布局写解析器（`AddSource` 到 `Directory.Build.props` 之外的宿主工程，别让 Unity 导入 `tools/`）；
4. CI 的 `--check` 门禁会自动覆盖新表——**忘了导出就红**。

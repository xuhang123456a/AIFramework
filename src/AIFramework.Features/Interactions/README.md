# Interactions —— 双边专属互动收口区

## 1. 本目录的定位

只收口**无法抽象为通用 CQE / GameplayTag 的两两专属互动**（例：`ShieldC` 的护盾与 `BoomerangD` 的回旋镖之间存在只有这两者才成立的判定顺序）。

凡能用 `Contracts` 既有 `Command` / `Event` / `Query` / `GameplayTag` 表达的协作，一律回到各自切片实现，禁止搬进本目录。

## 2. 文件命名规则

```
Interaction_<A>_<B>.cs
```

* `<A>` / `<B>` 必须是两个切片的短名（`Feature.<Name>` 去掉 `Feature.` 前缀），按字典序书写，单词之间用 `_` 连接。
* 一个文件只允许承载一对互动；禁止出现 `Interaction_All.cs` 之类的聚合文件。

## 3. 命名空间与 Analyzer 白名单

* 命名空间固定为 `AIFramework.Features.Interactions`，文件放本目录根部，不再建子目录。
* 本命名空间是 `AIFramework.Analyzer` 中 `AIF001`（切片禁止互引）的**显式白名单**：任何切片都可以引用本命名空间且不报错；反过来本目录可以引用任意切片。
* 白名单只对 `AIF001` 生效，`AIF002` / `AIF003` / `AIF004` 在本目录同样强制：禁止引引擎、禁止 `DateTime.Now`、禁止引 `Core` / `Kernel` / `Adapters`。

## 4. 现状

**目前为空。** 没有任何一对互动达到"必须显式收口"的程度；新增文件前请先在对应切片的 `CONTRACT.md` 里写明为什么通用协议不够用。

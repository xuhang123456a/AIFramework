<#
.SYNOPSIS
    把 AIFramework.Analyzer 装进 Unity 工程（或移除），让 AIF001~AIF004 在 Unity 编辑器里也生效。

.DESCRIPTION
    默认不装：纯逻辑侧的红线已经由 `dotnet build` 与 `unity/tools/check-adapter-compile.ps1` 守住，
    Unity 侧守卫只是"编辑器里实时提示"的便利功能。

    安装时做三件事：
    1) dotnet build src/AIFramework.Analyzer（Release）
    2) 复制 AIFramework.Analyzer.dll 到 unity/Assets/Plugins/Analyzers/
    3) 同时写一份 .meta：打 RoslynAnalyzer 标签，并关掉 Unity 的引用校验
       （validateReferences: 0）。原因：分析器引用 Microsoft.CodeAnalysis，而 Unity 会把
       Assets 下的 dll 当普通插件做引用校验，不关就会报
       "Unable to resolve reference 'Microsoft.CodeAnalysis'"。

.PARAMETER Remove
    移除已安装的分析器（dll + meta），常用于"Unity Console 持续报 Roslyn 解析失败"时先解除阻塞。

.EXAMPLE
    pwsh unity/tools/setup-analyzer.ps1
    pwsh unity/tools/setup-analyzer.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$targetDir = Join-Path $repoRoot 'unity/Assets/Plugins/Analyzers'
$targetDll = Join-Path $targetDir 'AIFramework.Analyzer.dll'
$targetMeta = $targetDll + '.meta'

# 固定 GUID：重复安装不会换 GUID，Unity 也不会每次重新导入。
$metaGuid = 'a1f0a1f0a1f0a1f0a1f0a1f0a1f0a1f0'

if ($Remove) {
    foreach ($path in @($targetDll, $targetMeta)) {
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Force
            Write-Host "已删除：$path"
        }
    }

    Write-Host 'Unity 侧分析器已移除；AIF001~AIF004 仍由 dotnet build 与 check-adapter-compile.ps1 守住。'
    return
}

$project = Join-Path $repoRoot 'src/AIFramework.Analyzer/AIFramework.Analyzer.csproj'
if (-not (Test-Path $project)) {
    throw "找不到分析器工程：$project"
}

Write-Host "构建分析器（$Configuration）…"
dotnet build $project -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build 失败，退出码 $LASTEXITCODE"
}

$built = Get-ChildItem -Path (Join-Path $repoRoot "artifacts/bin/AIFramework.Analyzer/$Configuration") -Recurse -Filter 'AIFramework.Analyzer.dll' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $built) {
    throw "构建完成但没找到 AIFramework.Analyzer.dll（预期在 artifacts/bin/AIFramework.Analyzer/$Configuration 下）"
}

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Copy-Item -LiteralPath $built.FullName -Destination $targetDll -Force

$meta = @"
fileFormatVersion: 2
guid: $metaGuid
labels:
- RoslynAnalyzer
PluginImporter:
  externalObjects: {}
  serializedVersion: 3
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
    Any:
      enabled: 1
      settings: {}
    Editor:
      enabled: 0
      settings:
        DefaultValueInitialized: true
    WindowsStoreApps:
      enabled: 0
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"@

Set-Content -LiteralPath $targetMeta -Value $meta -Encoding utf8NoBOM

Write-Host "已安装：$targetDll（含 RoslynAnalyzer 标签、已关闭引用校验）"
Write-Host '回到 Unity 等待重新编译；Console 里不应再出现 Microsoft.CodeAnalysis 解析失败。'
Write-Host '若仍失败，直接用 -Remove 退回"只靠 dotnet build 守红线"的形态。'

<#
.SYNOPSIS
    构建 AIFramework.Analyzer 并复制到 Unity 工程，使 AIF001~AIF004 在 Unity 编辑器里同样生效。

.DESCRIPTION
    1) dotnet build src/AIFramework.Analyzer（Release）
    2) 复制 AIFramework.Analyzer.dll 到 unity/Assets/Plugins/Analyzers/
    3) Unity 打开时由 Assets/Editor/AnalyzerLabelInstaller.cs 自动补 RoslynAnalyzer 标签

.EXAMPLE
    pwsh unity/tools/setup-analyzer.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'src/AIFramework.Analyzer/AIFramework.Analyzer.csproj'
$targetDir = Join-Path $repoRoot 'unity/Assets/Plugins/Analyzers'

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

$target = Join-Path $targetDir 'AIFramework.Analyzer.dll'
Copy-Item -LiteralPath $built.FullName -Destination $target -Force

Write-Host "已复制：$target"
Write-Host '打开 Unity 后会由 AnalyzerLabelInstaller 自动打 RoslynAnalyzer 标签；若没生效，选中该 dll 在 Inspector 底部手动勾选标签。'

<#
.SYNOPSIS
    在没有"独占工程"的 Unity Editor 时，离线生成全部演示场景并写回仓库。

.DESCRIPTION
    背景：Unity 同一工程同时只能被一个进程打开。如果用户正开着编辑器（很常见），
    `-batchmode -projectPath unity` 会直接 abort；而场景必须由 Unity 生成（不许手写 .unity YAML）。

    做法：把仓库复制到临时目录（不含 Library/Logs/Temp），在**副本**里跑 batchmode
    执行 `AIFramework.Demo.Editor.GenerateAllScenes.Generate`，再把生成的
    `*.unity` + `.meta` 与 `ProjectSettings/EditorBuildSettings.asset` 拷回仓库。
    副本目录会复用（保留 Library），第二次起只需增量导入，通常 1~2 分钟。

.NOTES
    - 需要已激活的 Unity 编辑器（本脚本不处理许可证）。
    - 必须在仓库根目录执行（能读到 AIFramework.Pure.sln）。
    - 生成的场景 Guid 由 Unity 分配；仓库里已有 .meta 的会被覆盖为副本里的（保持一致来源）。

.EXAMPLE
    pwsh unity/tools/generate-scenes.ps1
    pwsh unity/tools/generate-scenes.ps1 -UnityPath 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe'
#>
[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe',
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'aifw-scene-gen'),
    [switch]$ForceRestore
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $repoRoot 'AIFramework.Pure.sln'))) {
    throw "请在仓库根目录执行：找不到 AIFramework.Pure.sln（解析到的仓库根：$repoRoot）"
}

if (-not (Test-Path $UnityPath)) {
    throw "找不到 Unity 可执行文件：$UnityPath（用 -UnityPath 指定）"
}

$workProject = Join-Path $WorkDir 'unity'
$logPath = Join-Path $WorkDir 'generate-scenes.log'

function Sync-Sources {
    $pairs = @(
        @{ Src = 'src'; Dst = Join-Path $WorkDir 'src'; Exclude = @('obj', 'bin', 'artifacts') },
        @{ Src = 'unity/Assets'; Dst = Join-Path $WorkDir 'unity/Assets'; Exclude = @('Library') },
        @{ Src = 'unity/Packages'; Dst = Join-Path $WorkDir 'unity/Packages'; Exclude = @() },
        @{ Src = 'unity/ProjectSettings'; Dst = Join-Path $WorkDir 'unity/ProjectSettings'; Exclude = @() }
    )

    foreach ($pair in $pairs) {
        $srcPath = Join-Path $repoRoot $pair.Src
        $args = @($srcPath, $pair.Dst, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
        if ($pair.Exclude.Count -gt 0) {
            $args += '/XD'
            $args += $pair.Exclude
        }

        & robocopy @args | Out-Null
        if ($LASTEXITCODE -ge 8) {
            throw "复制失败（robocopy 退出码 $LASTEXITCODE）：$($pair.Src)"
        }
    }

    $global:LASTEXITCODE = 0
}

if (-not (Test-Path $workProject)) {
    Write-Host "首次运行：创建临时工程副本 $WorkDir（首次导入约 5~10 分钟）"
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

    $exclude = @('Library', 'Logs', 'Temp', 'UserSettings', '.git', '.github', 'obj', 'bin', 'artifacts')
    & robocopy $repoRoot $WorkDir /MIR /XD @exclude /XJ /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "复制仓库失败（robocopy 退出码 $LASTEXITCODE）"
    }

    $global:LASTEXITCODE = 0
} elseif ($ForceRestore) {
    Write-Host '强制重建副本中…'
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
    & pwsh -NoProfile -File $PSCommandPath -UnityPath $UnityPath -WorkDir $WorkDir
    return
}

Sync-Sources

# 清理上次异常退出残留的工程锁：Unity 崩溃时不会删 Temp/UnityLockfile，
# 下一次 batchmode 会误报"另一个实例已打开该工程"。
$staleLock = Join-Path $workProject 'Temp/UnityLockfile'
if (Test-Path $staleLock) {
    Remove-Item -LiteralPath $staleLock -Force -ErrorAction SilentlyContinue
}
Write-Host '在副本工程里离屏生成场景…'

# Unity.exe 是"启动器"，会立刻返回、真正的编辑器进程在后台跑并把日志异步写出来。
# 所以这里不能靠进程退出码判断，只能轮询日志里的完成标记。
if (Test-Path $logPath) {
    Remove-Item -LiteralPath $logPath -Force
}

& $UnityPath -batchmode -quit -projectPath $workProject `
    -executeMethod AIFramework.Demo.Editor.GenerateAllScenes.Generate -logFile $logPath

$deadline = (Get-Date).AddMinutes(20)
$completed = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3

    if (-not (Test-Path $logPath)) {
        continue
    }

    $content = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) {
        continue
    }

    if ($content -match '全部场景生成完成' -or $content -match '场景装配校验失败' -or $content -match 'Exiting without the bug reporter') {
        $completed = $true
        break
    }
}

if (-not $completed) {
    throw "等待 Unity 生成超时（20 分钟）。日志：$logPath"
}

Start-Sleep -Seconds 2

if (-not (Test-Path $logPath)) {
    throw "Unity 没有写出日志，说明它没真正启动（检查许可证 / 是否已有实例占用了副本目录）：$logPath"
}

$verifyFailures = Select-String -Path $logPath -Pattern '场景装配校验失败|校验失败（'
if ($verifyFailures) {
    $verifyFailures | Select-Object -First 20 | ForEach-Object { Write-Host $_.Line }
    throw "场景装配校验失败，已中止写回（看上面的失败项，或 GeneratedScenes 日志：$logPath）"
}

$compileErrors = Select-String -Path $logPath -Pattern 'error CS'
if ($compileErrors) {
    $compileErrors | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }
    throw '副本工程存在编译错误，场景未生成。'
}

$generated = Get-ChildItem (Join-Path $workProject 'Assets/AIFrameworkDemo/Scenes') -Filter '*.unity' -ErrorAction SilentlyContinue
if (-not $generated) {
    throw '没有生成任何场景，检查 GenerateAllScenes 是否被正确执行（看日志）。'
}

Write-Host "生成完成，共 $($generated.Count) 个场景，写回仓库…"
$sceneTarget = Join-Path $repoRoot 'unity/Assets/AIFrameworkDemo/Scenes'
New-Item -ItemType Directory -Path $sceneTarget -Force | Out-Null
Copy-Item -Path (Join-Path $workProject 'Assets/AIFrameworkDemo/Scenes/*.unity') -Destination $sceneTarget -Force
Copy-Item -Path (Join-Path $workProject 'Assets/AIFrameworkDemo/Scenes/*.unity.meta') -Destination $sceneTarget -Force

$buildSettingsSrc = Join-Path $workProject 'ProjectSettings/EditorBuildSettings.asset'
if (Test-Path $buildSettingsSrc) {
    Copy-Item -LiteralPath $buildSettingsSrc -Destination (Join-Path $repoRoot 'unity/ProjectSettings/EditorBuildSettings.asset') -Force
    Write-Host '已写回 EditorBuildSettings.asset（Boot → Lobby → CardBattle → Match3）'
}

foreach ($scene in Get-ChildItem $sceneTarget -Filter '*.unity') {
    Write-Host ("  " + $scene.Name + "  " + $scene.Length + " 字节")
}

Write-Host '完成。真机验收：Unity 打开工程 → 打开 BootScene → Play。'

<#
.SYNOPSIS
    在"临时副本工程"里一键出 Windows 冒烟包（不需要关掉正在使用的 Unity 编辑器）。

.DESCRIPTION
    与 generate-scenes.ps1 / run-unity-tests.ps1 同源：
      1. robocopy /MIR 把仓库同步进副本工程（保留 Library 做增量导入）
      2. Unity batchmode -executeMethod AIFramework.Demo.Editor.BuildPlayer.BuildWindows
      3. 轮询日志判断成功/失败，把产物路径与体积打出来

    产物在副本工程的 Build/Windows/ 下，不写回仓库（构建产物不该进版本库）。

.NOTES
    需要已安装 Windows Build Support 模块。

.EXAMPLE
    pwsh unity/tools/build-player.ps1
#>
[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe',
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'aifw-scene-gen')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $repoRoot 'AIFramework.Pure.sln'))) {
    throw '请在仓库根目录执行：找不到 AIFramework.Pure.sln'
}

if (-not (Test-Path $UnityPath)) {
    throw "找不到 Unity 可执行文件：$UnityPath（用 -UnityPath 指定）"
}

$workProject = Join-Path $WorkDir 'unity'
if (-not (Test-Path $workProject)) {
    throw "副本工程不存在：$WorkDir。请先跑一次 pwsh unity/tools/generate-scenes.ps1 建立副本。"
}

function Sync-Sources {
    $pairs = @(
        @{ Src = 'src'; Dst = Join-Path $WorkDir 'src' },
        @{ Src = 'unity/Assets'; Dst = Join-Path $WorkDir 'unity/Assets' },
        @{ Src = 'unity/Packages'; Dst = Join-Path $WorkDir 'unity/Packages' },
        @{ Src = 'unity/ProjectSettings'; Dst = Join-Path $WorkDir 'unity/ProjectSettings' }
    )

    foreach ($pair in $pairs) {
        & robocopy (Join-Path $repoRoot $pair.Src) $pair.Dst /MIR /NFL /NDL /NJH /NJS /NP /XD obj bin artifacts | Out-Null
        if ($LASTEXITCODE -ge 8) {
            throw "复制失败（robocopy 退出码 $LASTEXITCODE）：$($pair.Src)"
        }
    }

    $global:LASTEXITCODE = 0
}

Sync-Sources

$staleLock = Join-Path $workProject 'Temp/UnityLockfile'
if (Test-Path $staleLock) {
    Remove-Item -LiteralPath $staleLock -Force -ErrorAction SilentlyContinue
}

$logPath = Join-Path $WorkDir 'build-player.log'
if (Test-Path $logPath) {
    Remove-Item -LiteralPath $logPath -Force
}

Write-Host '在副本工程里出 Windows 冒烟包（首次导入可能要几分钟）…'

& $UnityPath -batchmode -nographics -quit `
    -projectPath $workProject `
    -executeMethod AIFramework.Demo.Editor.BuildPlayer.BuildWindows `
    -logFile $logPath

$deadline = (Get-Date).AddMinutes(30)
$outputExe = Join-Path $workProject 'Build/Windows/AIFramework.exe'

while ((Get-Date) -lt $deadline -and -not (Test-Path $outputExe)) {
    if (Test-Path $logPath) {
        $hit = Select-String -Path $logPath -Pattern '\[Build\] 出包失败|BuildPlayer 失败|Aborting batchmode' -SimpleMatch:$false -ErrorAction SilentlyContinue
        if ($hit) {
            break
        }
    }

    Start-Sleep -Seconds 5
}

if (-not (Test-Path $outputExe)) {
    if (Test-Path $logPath) {
        Select-String -Path $logPath -Pattern 'error|Exception|\[Build\]' | Select-Object -Last 20 | ForEach-Object { Write-Host $_.Line }
    }

    throw "出包失败或超时。日志：$logPath"
}

$exe = Get-Item $outputExe
$exeData = Get-ChildItem (Split-Path $exe.FullName -Parent) -Recurse -File | Measure-Object -Property Length -Sum

Write-Host ("出包成功：{0}（可执行 {1:N1} MB，整包 {2:N1} MB）" -f $exe.FullName, ($exe.Length / 1MB), ($exeData.Sum / 1MB))
Write-Host "产物在副本工程里，未写回仓库（构建产物不进版本库）。"

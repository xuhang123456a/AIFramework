<#
.SYNOPSIS
    在没有"独占工程"的 Unity Editor 时，离屏跑 Unity EditMode 测试。

.DESCRIPTION
    与 generate-scenes.ps1 同源：把仓库复制到临时目录（保留 Library 增量导入），
    在副本里执行 `Unity -batchmode -runTests -testPlatform EditMode`，把结果 XML 解析成
    通过/失败数并打印失败用例名。

    为什么要这么绕：Unity 同一工程同时只能被一个进程打开；用户开着编辑器时直接对
    `unity/` 跑 batchmode 会被 abort。副本方案让"真 Unity 测试"也能进日常流程。

.NOTES
    需要已激活的 Unity 编辑器与副本工程里已安装 com.unity.test-framework（manifest 已声明）。

.EXAMPLE
    pwsh unity/tools/run-unity-tests.ps1
    pwsh unity/tools/run-unity-tests.ps1 -TestPlatform PlayMode
#>
[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe',
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'aifw-scene-gen'),
    [ValidateSet('EditMode', 'PlayMode')]
    [string]$TestPlatform = 'EditMode',
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $repoRoot 'AIFramework.Pure.sln'))) {
    throw "请在仓库根目录执行：找不到 AIFramework.Pure.sln"
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
        @{ Src = 'src'; Dst = Join-Path $WorkDir 'src'; Exclude = @('obj', 'bin', 'artifacts') },
        @{ Src = 'unity/Assets'; Dst = Join-Path $WorkDir 'unity/Assets'; Exclude = @() },
        @{ Src = 'unity/Packages'; Dst = Join-Path $WorkDir 'unity/Packages'; Exclude = @() },
        @{ Src = 'unity/ProjectSettings'; Dst = Join-Path $WorkDir 'unity/ProjectSettings'; Exclude = @() }
    )

    foreach ($pair in $pairs) {
        $args = @((Join-Path $repoRoot $pair.Src), $pair.Dst, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
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

Sync-Sources

$resultsPath = Join-Path $WorkDir ("test-results-$TestPlatform.xml")
$logPath = Join-Path $WorkDir ("tests-$TestPlatform.log")
foreach ($path in @($resultsPath, $logPath)) {
    if (Test-Path $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$unityArgs = @(
    '-batchmode', '-nographics',
    '-projectPath', $workProject,
    '-runTests', '-testPlatform', $TestPlatform,
    '-testResults', $resultsPath,
    '-logFile', $logPath
)

if ($Filter) {
    $unityArgs += @('-testFilter', $Filter)
}

# 清理上次异常退出残留的工程锁：Unity 崩溃时不会删 Temp/UnityLockfile，
# 下一次 batchmode 会误报"另一个实例已打开该工程"。
$staleLock = Join-Path $workProject 'Temp/UnityLockfile'
if (Test-Path $staleLock) {
    Remove-Item -LiteralPath $staleLock -Force -ErrorAction SilentlyContinue
}
Write-Host "在副本工程里跑 $TestPlatform 测试…"
& $UnityPath @unityArgs

$deadline = (Get-Date).AddMinutes(20)
while ((Get-Date) -lt $deadline -and -not (Test-Path $resultsPath)) {
    Start-Sleep -Seconds 3
}

if (-not (Test-Path $resultsPath)) {
    if (Test-Path $logPath) {
        Select-String -Path $logPath -Pattern 'error CS|Exception|Aborting' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
    }

    throw "等待测试结果超时。日志：$logPath"
}

[xml]$results = Get-Content -LiteralPath $resultsPath -Raw
$run = $results.'test-run'
if ($null -eq $run) {
    throw "测试结果文件格式异常：$resultsPath"
}

$total = $run.total
$passed = $run.passed
$failed = $run.failed
$skipped = $run.skipped

Write-Host ("测试结果：总计 $total，通过 $passed，失败 $failed，跳过 $skipped（$TestPlatform）")

if ([int]$failed -gt 0) {
    Write-Host '—— 失败用例 ——'
    $failedCases = $results.SelectNodes("//test-case[@result='Failed']")
    foreach ($case in $failedCases) {
        Write-Host ("  ✗ " + $case.name)
        $message = $case.SelectSingleNode('failure/message')
        if ($message -and $message.InnerText) {
            Write-Host ("      " + ($message.InnerText.Trim() -split "`n")[0])
        }
    }

    throw "Unity $TestPlatform 测试失败：$failed 个用例不通过（结果：$resultsPath）"
}

Write-Host "Unity $TestPlatform 测试全部通过。"

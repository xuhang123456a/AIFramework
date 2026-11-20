<#
.SYNOPSIS
    一条命令跑完 AIFramework 的验证流水线（五层闸门）。

.DESCRIPTION
    按"越靠后越贵、越接近真机"的顺序执行，任一闸门失败立即停止并打印失败层：

      1. 语言层        dotnet build AIFramework.Pure.sln   （0 警告 0 错误）
      2. 纯逻辑测试    dotnet test  AIFramework.Pure.sln   （全部用例）
      3. 生成物一致性  dotnet run --project tools/viewgen -- --all
      4. 配置表一致性  dotnet run --project tools/tablegen -- --check
      5. 适配层形状    pwsh unity/tools/check-adapter-compile.ps1
      6. 真 Unity      场景生成 + 装配校验 / EditMode / PlayMode（需 -Unity 开关）

    第 5 层跑在临时副本工程里（robocopy /MIR + 保留 Library），不影响正在使用的编辑器。
    用户开着 Unity 编辑器时也能跑，但**单机同一时间只应有一个 Unity batchmode**。

.NOTES
    - 默认不跑第 5 层（秒级反馈）；发布前用 `-Unity` 跑全量。
    - 依赖已安装的 Unity 6000.3.21f1（可用 -UnityPath 覆盖）。

.EXAMPLE
    pwsh tools/verify-all.ps1
    pwsh tools/verify-all.ps1 -Unity
    pwsh tools/verify-all.ps1 -Unity -PlayModeOnly:$false
#>
[CmdletBinding()]
param(
    [switch]$Unity,
    [switch]$SkipUnityTests,
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$results = New-Object System.Collections.Generic.List[string]
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Gate {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host ''
    Write-Host ("=== {0} ===" -f $Name) -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw ("闸门失败：{0}（退出码 {1}）" -f $Name, $LASTEXITCODE)
    }

    $results.Add("OK   " + $Name)
}

Invoke-Gate -Name '1/9 语言层：dotnet build' -Action {
    dotnet build AIFramework.Pure.sln -v q --nologo
}

Invoke-Gate -Name '2/9 纯逻辑测试：dotnet test' -Action {
    dotnet test AIFramework.Pure.sln -v q --nologo
}

Invoke-Gate -Name '3/9 生成物一致性：viewgen --all' -Action {
    dotnet run --project tools/viewgen/ViewGen.csproj -- --all
}

Invoke-Gate -Name '4/9 配置表一致性：tablegen --check' -Action {
    dotnet run --project tools/tablegen/TableGen.csproj -- --check
}

Invoke-Gate -Name '5/9 数值体检：tablecheck' -Action {
    dotnet run --project tools/tablecheck/TableCheck.csproj -- --tables tables
}

Invoke-Gate -Name '6/9 本地化完整性：loccheck' -Action {
    dotnet run --project tools/loccheck/LocCheck.csproj -- --tables tables
}

Invoke-Gate -Name '7/9 运营排期：opcheck' -Action {
    dotnet run --project tools/opcheck/OpCheck.csproj -- --tables tables
}

Invoke-Gate -Name '8/9 适配层形状：check-adapter-compile' -Action {
    pwsh -NoProfile -File unity/tools/check-adapter-compile.ps1
}

if ($Unity) {
    if (-not (Test-Path $UnityPath)) {
        throw ("找不到 Unity 可执行文件：{0}（用 -UnityPath 指定）" -f $UnityPath)
    }

    Invoke-Gate -Name '9/9a 真 Unity：场景生成 + 装配校验' -Action {
        pwsh -NoProfile -File unity/tools/generate-scenes.ps1 -UnityPath $UnityPath
    }

    if (-not $SkipUnityTests) {
        Invoke-Gate -Name '9/9b 真 Unity：EditMode 测试' -Action {
            pwsh -NoProfile -File unity/tools/run-unity-tests.ps1 -UnityPath $UnityPath -TestPlatform EditMode
        }

        Invoke-Gate -Name '9/9c 真 Unity：PlayMode 测试' -Action {
            pwsh -NoProfile -File unity/tools/run-unity-tests.ps1 -UnityPath $UnityPath -TestPlatform PlayMode
        }
    }
}

$stopwatch.Stop()

Write-Host ''
Write-Host '================ 验证结果 ================' -ForegroundColor Green
foreach ($line in $results) {
    Write-Host ("  " + $line) -ForegroundColor Green
}

Write-Host ("  总耗时：{0:N1} 秒" -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host '全部闸门通过。' -ForegroundColor Green

<#
.SYNOPSIS
    生成一个标准 AIFramework 业务切片骨架（元玩法 / 技能两种模板）。

.DESCRIPTION
    在 src/AIFramework.Features/Feature.<Name>/ 下生成带正确命名空间的模板文件与 60 行内的 CONTRACT.md，
    并在 tests/AIFramework.Features.Tests/Feature.<Name>/ 下生成对应的无头单测模板。
    禁止手工新建切片目录，一律走本脚本，避免命名空间拼错导致 Analyzer 漏检。

.PARAMETER Name
    切片名（Domain），只允许字母、数字与下划线，例如 DailySignIn、HeroC_Shield。

.PARAMETER Type
    Meta = 元玩法切片（State + Handler + Presenter 占位）；Skill = 强实时技能切片（Intent 驱动的阶段实现）。

.PARAMETER Force
    目标目录已存在时覆盖生成（默认拒绝并报错）。

.EXAMPLE
    pwsh tools/new-slice.ps1 -Name DailySignIn -Type Meta

.EXAMPLE
    pwsh tools/new-slice.ps1 -Name HeroD_Spear -Type Skill
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Meta', 'Skill')]
    [string]$Type,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sliceDir = Join-Path $repoRoot "src/AIFramework.Features/Feature.$Name"
$testDir = Join-Path $repoRoot "tests/AIFramework.Features.Tests/Feature.$Name"

if ((Test-Path $sliceDir) -and -not $Force) {
    throw "切片目录已存在：$sliceDir（如需覆盖请加 -Force）"
}

function Write-Utf8 {
    param([string]$Path, [string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -LiteralPath $Path -Value $Content -Encoding utf8NoBOM
    Write-Host "  + $Path"
}

$ns = "AIFramework.Features.$Name"

$contract = @"
# Feature.$Name 切片边界契约

## 1. 职责定位
（一句话写清本切片负责什么、不负责什么。）

## 2. 跨模块契约依赖（唯一合法入口）
* **订阅事件（In）**：（如 ``DayChangedEvent``，无则写"无"）
* **处理指令（In）**：（如 ``ClaimXxxCommand``）
* **只读查询（Out）**：（如 ``IInventoryQuery``）
* **独立子表（Config）**：（如 ``TbXxxReward``，无则写"无"）
* **发出指令（Out）**：（如 ``GrantItemCommand``、``ConsumeItemCommand``）
* **广播事件（Out）**：（如 ``XxxClaimedEvent``）
* **UI 视图契约**：（如 ``IXxxView``，无则写"无"）

## 3. 绝对禁区
* 禁止引用或 ``using`` 任何其他 ``AIFramework.Features.*`` 命名空间（``Interactions`` 除外）。
* 禁止引用 ``AIFramework.Core``、``AIFramework.Kernel.*``、``AIFramework.Adapters.*``。
* 禁止引用 ``UnityEngine``、协程或 ``DateTime.Now``（时间必须通过 ``ITimeProvider`` 获取）。
* 禁止直接扣减道具或读全局 ``Tables`` 单例，必须走 ``Command`` 与切片子表接口。

## 4. 验收命令
``dotnet test tests/AIFramework.Features.Tests/AIFramework.Features.Tests.csproj --filter "FullyQualifiedName~$Name"``
"@

if ($Type -eq 'Meta') {
    $state = @"
namespace $ns
{
    /// <summary>本切片专属纯数据状态：只有字段，没有行为，可序列化。</summary>
    public sealed class ${Name}State
    {
        /// <summary>示例字段，按业务替换。</summary>
        public int Count { get; internal set; }
    }
}
"@

    $handler = @"
using AIFramework.Contracts.Cqe;

namespace $ns
{
    /// <summary>本切片核心业务规则：只收发 CQE，不碰存储与 UI 控件。</summary>
    public sealed class ${Name}Handler
    {
        private readonly ICqeBus _bus;

        public ${Name}Handler(ICqeBus bus)
        {
            _bus = bus;
        }

        public ${Name}State Snapshot { get; } = new ${Name}State();
    }
}
"@

    Write-Utf8 -Path (Join-Path $sliceDir 'CONTRACT.md') -Content $contract
    Write-Utf8 -Path (Join-Path $sliceDir "${Name}State.cs") -Content $state
    Write-Utf8 -Path (Join-Path $sliceDir "${Name}Handler.cs") -Content $handler
}
else {
    $steps = @"
using AIFramework.Contracts.Gameplay;

namespace $ns
{
    /// <summary>
    /// 强实时技能切片：只认句柄、标签与意图，禁止出现任何具体英雄类型判断。
    /// </summary>
    public sealed class ${Name}Steps : ISkillInteractionPhase
    {
        public void Execute(in PhaseContext context, IIntentWriter writer)
        {
            // 1. 用 context.Spatial.QueryProjectilesCircle(..., Span<ProjectileHandle>) 零分配取周边飞行物
            // 2. 读 context.Projectiles.TryGetProjectile 拿只读视图
            // 3. 按 GameplayTag 仲裁，向 writer.Submit(new ReflectProjectileIntent(...)) 提交意图
        }
    }
}
"@

    Write-Utf8 -Path (Join-Path $sliceDir 'CONTRACT.md') -Content $contract
    Write-Utf8 -Path (Join-Path $sliceDir "${Name}Steps.cs") -Content $steps
}

$test = @"
using Xunit;

namespace AIFramework.Features.Tests.$Name
{
    public sealed class ${Name}Tests
    {
        [Fact]
        public void Placeholder_ShouldBeReplaced()
        {
            Assert.True(true);
        }
    }
}
"@

Write-Utf8 -Path (Join-Path $testDir "${Name}Tests.cs") -Content $test

Write-Host ''
Write-Host "切片已生成：$Type"
Write-Host "  实现：$sliceDir"
Write-Host "  测试：$testDir"
Write-Host '下一步：填写 CONTRACT.md 契约 → 只在上述两个目录内写代码 → 跑 CONTRACT.md 里的验收命令。'

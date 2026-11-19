<#
.SYNOPSIS
    在没有 Unity 的机器上，对 Unity 适配层做"形状级"编译校验。

.DESCRIPTION
    本机不装 Unity 也能拦住 80% 的适配层回归：语法错、用法错、接口实现缺成员、调用了不存在的 API 形状。
    做法是：把 src/Adapters/AIFramework.Adapters.Unity/Runtime 下的源码，连同 unity/tools/shims 里的
    UnityEngine 形状桩，编译成一个 netstandard2.1 程序集。

    注意它不是"Unity 能跑"的证明：
    - 桩里的 Physics / Debug / Time 全是空实现，物理命中与运行时行为仍需真机验证；
    - 桩只覆盖适配层实际用到的 API 形状，新增 Unity API 时要同步补桩。

.EXAMPLE
    pwsh unity/tools/check-adapter-compile.ps1
#>
[CmdletBinding()]
param(
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$adapterRuntime = Join-Path $repoRoot 'src/Adapters/AIFramework.Adapters.Unity/Runtime'
$shimDir = Join-Path $PSScriptRoot 'shims'

if (-not (Test-Path $adapterRuntime)) {
    throw "找不到适配层源码目录：$adapterRuntime"
}

if (-not (Test-Path $shimDir)) {
    throw "找不到形状桩目录：$shimDir"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('aifw-unity-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $projectPath = Join-Path $tempRoot 'adapter-shape-check.csproj'
    $runtimeGlob = ($adapterRuntime -replace '\\', '/') + '/**/*.cs'
    $editorRoot = Join-Path $repoRoot 'unity/Assets'
    $editorRootGlob = ($editorRoot -replace '\\', '/') + '/**/Editor/**/*.cs'
    $shimGlob = ($shimDir -replace '\\', '/') + '/*.cs'
    $srcRoot = (Join-Path $repoRoot 'src') -replace '\\', '/'

    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>disable</ImplicitUsings>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$shimGlob" />
    <Compile Include="$runtimeGlob" />
    <Compile Include="$editorRootGlob" Exclude="$repoRoot/unity/Assets/Editor/AnalyzerLabelInstaller.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$srcRoot/AIFramework.Contracts/AIFramework.Contracts.csproj" />
    <ProjectReference Include="$srcRoot/AIFramework.Core/AIFramework.Core.csproj" />
    <ProjectReference Include="$srcRoot/AIFramework.Kernel.Turn/AIFramework.Kernel.Turn.csproj" />
    <ProjectReference Include="$srcRoot/AIFramework.Kernel.Realtime/AIFramework.Kernel.Realtime.csproj" />
    <ProjectReference Include="$srcRoot/AIFramework.Features/AIFramework.Features.csproj" />
  </ItemGroup>
</Project>
"@

    Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8NoBOM

    Write-Host '校验 Unity 适配层能否编译（形状级）…'
    dotnet build $projectPath -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "适配层形状级编译失败，退出码 $LASTEXITCODE"
    }

    Write-Host '通过：适配层语法/类型/接口实现与 UnityEngine API 形状一致（运行时行为仍需真机验证）。'
}
finally {
    if ($KeepTemp) {
        Write-Host "临时工程保留在：$tempRoot"
    }
    else {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}



#Requires -Version 5.1
<#
.SYNOPSIS
    Restore + build the whole Apage solution (Apage.Core + Apage.Portable + Apage.Core.Tests).

.DESCRIPTION
    1. Locates MSBuild via vswhere (VS 2022).
    2. dotnet restore for Apage.Core + Apage.Core.Tests (SDK-style; a clean
       checkout needs both project.assets.json before the solution build).
    3. MSBuild /t:Restore for Apage.Portable (classic csproj + PackageReference).
    4. MSBuild full build of Apage.sln.
    Prints a per-step summary and exits non-zero if any step failed.

.EXAMPLE
    build\build.ps1                      # Release build (default)
    build\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$script:failed = $false
$summary = New-Object System.Collections.Generic.List[object]

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Command
    )
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Command
    $code = $LASTEXITCODE
    $sw.Stop()
    $ok = ($code -eq 0)
    if (-not $ok) { $script:failed = $true }
    $summary.Add([pscustomobject]@{
        Step     = $Name
        Result   = $(if ($ok) { 'OK' } else { "FAILED (exit $code)" })
        Duration = $sw.Elapsed.ToString('mm\:ss')
    })
}

try {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "vswhere not found at '$vswhere'." }
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($msbuild) -or -not (Test-Path $msbuild)) {
        throw "MSBuild not found via vswhere. Is Visual Studio 2022 (with MSBuild) installed?"
    }

    # VS 2022 Community without the ".NET SDK" workload cannot resolve
    # <Project Sdk="Microsoft.NET.Sdk"> (Apage.Core). In that case point MSBuild
    # at a standalone dotnet SDK's Sdks folder and disable the workload resolver.
    # Prefer a .NET 8 SDK: it matches VS2022's MSBuild (a 10.x SDK triggers
    # NETSDK1216 compiler-toolset mismatch under VS MSBuild).
    $vsSdks = Join-Path (Split-Path (Split-Path $msbuild -Parent) -Parent) 'Sdks\Microsoft.NET.Sdk\Sdk'
    if (-not (Test-Path $vsSdks)) {
        $sdkLines = dotnet --list-sdks
        $sdk8 = $sdkLines | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
        $line = if ($sdk8) { $sdk8 } else { $sdkLines | Select-Object -Last 1 }
        if ($line -match '^(\S+) \[(.+)\]$') {
            # Bracket path already ends with "...dotnet\sdk" — append version\Sdks only.
            $env:MSBuildSDKsPath = Join-Path $Matches[2] "$($Matches[1])\Sdks"
            $env:MSBuildEnableWorkloadResolver = 'false'
            Write-Host "SdksPath: $env:MSBuildSDKsPath (VS has no bundled .NET SDK, workload resolver off)"
        }
    }

    Write-Host "Root    : $root"
    Write-Host "MSBuild : $msbuild"
    Write-Host "Config  : $Configuration"

    Invoke-Step 'dotnet restore Apage.Core' {
        dotnet restore Apage.Core/Apage.Core.csproj --nologo
    }

    # 测试工程也在 Apage.sln 内：干净环境（如 CI）缺少 project.assets.json
    # 会让整解构建报 NETSDK1004，必须显式 restore。
    Invoke-Step 'dotnet restore Apage.Core.Tests' {
        dotnet restore Apage.Core.Tests/Apage.Core.Tests.csproj --nologo
    }

    Invoke-Step 'msbuild /t:Restore Apage.Portable' {
        & $msbuild 'Apage.Portable\Apage.Portable.csproj' /t:Restore /p:Configuration=$Configuration /v:m /nologo
    }

    Invoke-Step "msbuild Apage.sln ($Configuration)" {
        & $msbuild Apage.sln /p:Configuration=$Configuration /v:m /nologo /m
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "===== Build summary =====" -ForegroundColor Cyan
$summary | Format-Table -AutoSize | Out-String | Write-Host

if ($script:failed) {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "BUILD SUCCEEDED" -ForegroundColor Green
exit 0

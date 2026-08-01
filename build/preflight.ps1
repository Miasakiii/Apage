#Requires -Version 5.1
<#
.SYNOPSIS
    Read-only environment preflight: reports which Apage build routes this
    machine supports, without changing anything.

.DESCRIPTION
    Probes three components (no install, no restore, no build):
      1. .NET SDK      - dotnet CLI present and global.json (8.0.x) resolvable.
      2. MSBuild       - VS 2022 MSBuild located via vswhere; also whether VS
                         bundles the .NET SDK component (else build.ps1 falls
                         back to MSBuildSDKsPath automatically).
      3. .NET FX 4.8   - targeting pack (reference assemblies) present.
    Then prints the supported build subset:
      - Core/Tests route : dotnet build Apage.Core + dotnet test Apage.Core.Tests
      - Portable route   : build\build.ps1 (full solution) + build\smoke.ps1
    Exit codes: 0 = full environment; 2 = partial (only some routes); 1 = no
    build route available.

.EXAMPLE
    build\preflight.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Ok,
        [Parameter(Mandatory)][string]$Detail
    )
    $checks.Add([pscustomobject]@{
        Component = $Name
        Status    = $(if ($Ok) { 'OK' } else { 'MISSING' })
        Detail    = $Detail
    })
    return $Ok
}

Write-Host "==> Apage build environment preflight (read-only)" -ForegroundColor Cyan
Write-Host "Root: $root"

# --- 1. .NET SDK (global.json pins 8.0.423, rollForward latestFeature) -------
$dotnetOk = $false
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    $null = Add-Check '.NET SDK' $false 'dotnet CLI not on PATH'
}
else {
    # Run from repo root so resolution honors global.json; non-zero exit means
    # no installed SDK satisfies the pin. Relax EAP locally: under 'Stop',
    # PS 5.1 turns native stderr redirected via 2>&1 into a terminating error.
    Push-Location $root
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $resolved = (& dotnet --version 2>&1) -join ' '; $code = $LASTEXITCODE
        if ($code -ne 0) {
            $installed = ((& dotnet --list-sdks 2>&1) | ForEach-Object { ($_ -split ' ')[0] }) -join ', '
        }
    }
    finally { $ErrorActionPreference = $prevEap; Pop-Location }
    if ($code -eq 0) {
        $dotnetOk = Add-Check '.NET SDK' $true "resolves to $resolved (satisfies global.json)"
    }
    else {
        if ([string]::IsNullOrWhiteSpace($installed)) { $installed = 'none' }
        $null = Add-Check '.NET SDK' $false "no SDK satisfies global.json (installed: $installed)"
    }
}

# --- 2. MSBuild (VS 2022 via vswhere) ----------------------------------------
$msbuildOk = $false
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    $null = Add-Check 'MSBuild' $false 'vswhere not found (Visual Studio 2022 not installed?)'
}
else {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($msbuild) -or -not (Test-Path $msbuild)) {
        $null = Add-Check 'MSBuild' $false 'vswhere found no MSBuild (VS 2022 MSBuild component missing)'
    }
    else {
        # Same probe as build.ps1: does VS bundle the .NET SDK component?
        # If not, build.ps1 needs a standalone .NET 8 SDK for MSBuildSDKsPath.
        $vsSdks = Join-Path (Split-Path (Split-Path $msbuild -Parent) -Parent) 'Sdks\Microsoft.NET.Sdk\Sdk'
        if (Test-Path $vsSdks) {
            $msbuildOk = Add-Check 'MSBuild' $true "$msbuild (VS bundles .NET SDK)"
        }
        elseif ($dotnetOk) {
            $msbuildOk = Add-Check 'MSBuild' $true "$msbuild (no bundled .NET SDK; build.ps1 uses MSBuildSDKsPath fallback)"
        }
        else {
            $null = Add-Check 'MSBuild' $false "$msbuild found, but neither VS-bundled nor standalone .NET SDK available to resolve SDK-style projects"
        }
    }
}

# --- 3. .NET Framework 4.8 targeting pack ------------------------------------
$fx48Dir = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
$fx48Ok = if (Test-Path (Join-Path $fx48Dir 'mscorlib.dll')) {
    Add-Check '.NET FX 4.8 targeting pack' $true $fx48Dir
}
else {
    Add-Check '.NET FX 4.8 targeting pack' $false "not found at $fx48Dir (install via VS Installer or the 4.8 Developer Pack)"
}

# --- Report -------------------------------------------------------------------
Write-Host ""
Write-Host "===== Component checks =====" -ForegroundColor Cyan
$checks | Format-Table -AutoSize -Wrap | Out-String | Write-Host

$coreRoute     = $dotnetOk
$portableRoute = $msbuildOk -and $fx48Ok

Write-Host "===== Supported build subset on this machine =====" -ForegroundColor Cyan
if ($coreRoute) {
    Write-Host "[YES] Core + Tests (dotnet CLI):" -ForegroundColor Green
    Write-Host "        dotnet build Apage.Core/Apage.Core.csproj -c Release"
    Write-Host "        dotnet test Apage.Core.Tests/Apage.Core.Tests.csproj"
}
else {
    Write-Host "[NO ] Core + Tests (dotnet CLI) - requires a .NET 8 SDK satisfying global.json" -ForegroundColor Yellow
}
if ($portableRoute) {
    Write-Host "[YES] Portable / full solution (VS 2022 MSBuild):" -ForegroundColor Green
    Write-Host "        build\build.ps1        (then: build\smoke.ps1)"
}
else {
    Write-Host "[NO ] Portable / full solution - requires VS 2022 MSBuild + .NET FX 4.8 targeting pack" -ForegroundColor Yellow
}

Write-Host ""
if ($coreRoute -and $portableRoute) {
    Write-Host "PREFLIGHT: FULL - all build routes available." -ForegroundColor Green
    exit 0
}
if ($coreRoute -or $portableRoute) {
    Write-Host "PREFLIGHT: PARTIAL - only the routes marked [YES] above are usable here." -ForegroundColor Yellow
    exit 2
}
Write-Host "PREFLIGHT: NONE - no Apage build route available on this machine." -ForegroundColor Red
exit 1

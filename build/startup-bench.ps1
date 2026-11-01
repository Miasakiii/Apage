#Requires -Version 5.1
<#
.SYNOPSIS
    Measure real Apage.Portable startup phases in an isolated portable directory.

.DESCRIPTION
    Runs the real --smoke path repeatedly and records process-start to
    ContentRendered / TabsReady / WebView2-ready / PrivacyAssets-ready.
    With -ExePath, ONLY that EXE is copied into the isolated app directory, so
    the packaged single-file artifact is measured—not the multi-file bin tree.
    The warm median ContentRendered is a hard gate by default (<1000 ms).
    Never reads or deletes the user's real data/ or %TEMP%\Apage cache.

.EXAMPLE
    build\startup-bench.ps1 -Iterations 5

.EXAMPLE
    build\startup-bench.ps1 -ExePath artifacts\portable\Apage.Portable.exe `
      -Iterations 3 -OutputDirectory artifacts\release-startup
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(2, 20)]
    [int]$Iterations = 5,
    [int]$TimeoutSec = 120,
    [string]$ExePath,
    [string]$OutputDirectory,
    [ValidateRange(1.0, 10000.0)]
    [double]$WarmMedianLimitMs = 1000,
    [switch]$SkipGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$singleFileMode = -not [string]::IsNullOrWhiteSpace($ExePath)

if ($singleFileMode) {
    $sourceExe = [System.IO.Path]::GetFullPath($ExePath)
    $sourceDir = Split-Path -Parent $sourceExe
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "Apage.Portable.exe not found: $sourceExe"
    }
    $releaseFiles = @(Get-Item -LiteralPath $sourceExe)
}
else {
    $sourceDir = Join-Path $root "Apage.Portable\bin\$Configuration"
    $sourceExe = Join-Path $sourceDir 'Apage.Portable.exe'
    if (-not (Test-Path $sourceExe)) {
        throw "Apage.Portable.exe not found: $sourceExe. Run build\build.ps1 first."
    }
    $releaseFiles = @(Get-ChildItem -LiteralPath $sourceDir -Recurse -File |
        Where-Object {
            $_.Extension -ne '.pdb' -and
            $_.FullName -notmatch '\\(data|cache|wallpapers)\\' -and
            $_.FullName -notmatch '\\(win-x86|win-arm64)\\'
        })
}
$releaseBytes = ($releaseFiles | Measure-Object Length -Sum).Sum

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $artifactRoot = Join-Path $artifactsRoot ("StartupBench\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
else {
    $artifactRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
    $prefix = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $artifactRoot.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputDirectory must stay under artifacts/: $artifactRoot"
    }
    if ([System.IO.Directory]::Exists($artifactRoot)) {
        [System.IO.Directory]::Delete($artifactRoot, $true)
    }
}

$appDir = Join-Path $artifactRoot 'app'
$runsDir = Join-Path $artifactRoot 'runs'
$dataDir = Join-Path $appDir 'data'
New-Item -ItemType Directory -Path $appDir, $runsDir, $dataDir -Force | Out-Null

if ($singleFileMode) {
    Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $appDir 'Apage.Portable.exe')
}
else {
    Get-ChildItem -LiteralPath $sourceDir -Force |
        Where-Object { $_.Name -notin @('data', 'cache', 'wallpapers') -and $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $appDir -Recurse -Force
}

# 只在本基准副本内启用便携缓存：避免读取/删除用户 %TEMP%\Apage 缓存，
# 同时让首次运行使用全新 profile，后续轮次复用同一 profile 形成热启动对照。
@{ PortableCache = $true; SearchEngine = 'bing' } |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $dataDir 'settings.json') -Encoding UTF8

$exe = Join-Path $appDir 'Apage.Portable.exe'
$rows = New-Object System.Collections.Generic.List[object]

function Get-MarkMs {
    param($Metrics, [DateTimeOffset]$Started, [string]$Name)
    $property = $Metrics.marks.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [math]::Round(([DateTimeOffset]::Parse($property.Value) - $Started).TotalMilliseconds, 1)
}

for ($i = 1; $i -le $Iterations; $i++) {
    $runDir = Join-Path $runsDir "run-$i"
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    $metricsPath = Join-Path $runDir 'startup.json'
    Write-Host "==> Startup run $i/$Iterations" -ForegroundColor Cyan

    $proc = Start-Process -FilePath $exe -ArgumentList @(
        '--smoke',
        '--startup-metrics', "`"$metricsPath`""
    ) -PassThru
    $started = [DateTimeOffset]$proc.StartTime

    if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
        taskkill /PID $proc.Id /T /F *> $null
        throw "Startup run $i timed out after $TimeoutSec seconds."
    }
    if (-not (Test-Path -LiteralPath $metricsPath)) {
        throw "Startup run $i exited $($proc.ExitCode) without metrics file: $metricsPath"
    }

    $metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
    if ($metrics.outcome -ne 'pass') {
        throw "Startup run $i outcome=$($metrics.outcome), exit=$($proc.ExitCode)."
    }
    if ($proc.ExitCode -ne 0) {
        throw "Startup run $i failed with exit $($proc.ExitCode)."
    }
    if ($metrics.processBitness -ne 'x64') {
        throw "Startup run $i ran as $($metrics.processBitness), expected x64."
    }

    $row = [pscustomobject]@{
        Run = $i
        Kind = if ($i -eq 1) { 'first' } else { 'warm' }
        ProcessBitness = $metrics.processBitness
        OnStartupMs = Get-MarkMs $metrics $started 'OnStartup'
        SettingsMs = Get-MarkMs $metrics $started 'SettingsAndStatsReady'
        NtpAssetsMs = Get-MarkMs $metrics $started 'NtpAssetsReady'
        WindowConstructionMs = Get-MarkMs $metrics $started 'WindowConstructionStarting'
        MainWindowXamlMs = Get-MarkMs $metrics $started 'MainWindowXamlLoaded'
        MainWindowWiredMs = Get-MarkMs $metrics $started 'MainWindowWired'
        WindowConstructedMs = Get-MarkMs $metrics $started 'WindowConstructed'
        WindowShowMs = Get-MarkMs $metrics $started 'WindowShowStarting'
        WindowShownMs = Get-MarkMs $metrics $started 'WindowShown'
        ContentRenderedMs = Get-MarkMs $metrics $started 'ContentRendered'
        TabsReadyMs = Get-MarkMs $metrics $started 'TabsReady'
        PrivacyAssetsMs = Get-MarkMs $metrics $started 'PrivacyAssetsReady'
        WebViewReadyMs = Get-MarkMs $metrics $started 'CoreReady'
        ExitMs = [math]::Round(([DateTimeOffset]$proc.ExitTime - $started).TotalMilliseconds, 1)
        ExitCode = $proc.ExitCode
    }
    $rows.Add($row)
}

$first = $rows[0]
$warmRows = @($rows | Select-Object -Skip 1)
$warmWindows = @($warmRows | ForEach-Object { [double]$_.ContentRenderedMs } | Sort-Object)
$mid = [int][math]::Floor($warmWindows.Count / 2)
$warmMedian = if ($warmWindows.Count % 2 -eq 1) {
    $warmWindows[$mid]
}
else {
    ($warmWindows[$mid - 1] + $warmWindows[$mid]) / 2
}
$gatePassed = $warmMedian -lt $WarmMedianLimitMs

$metricsAllPath = Join-Path $artifactRoot 'metrics.json'
[pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    Mode = if ($singleFileMode) { 'single-file' } else { 'multi-file' }
    SourceExe = $sourceExe
    Configuration = $Configuration
    ExeBytes = (Get-Item -LiteralPath $exe).Length
    ReleaseBytes = $releaseBytes
    ReleaseFiles = $releaseFiles.Count
    Iterations = $Iterations
    WarmMedianLimitMs = $WarmMedianLimitMs
    FirstContentRenderedMs = $first.ContentRenderedMs
    WarmMedianContentRenderedMs = $warmMedian
    GatePassed = $gatePassed
    GateSkipped = [bool]$SkipGate
    Runs = $rows
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metricsAllPath -Encoding UTF8

$rows | Format-Table Run, Kind, OnStartupMs, SettingsMs, NtpAssetsMs, WindowShownMs, ContentRenderedMs, TabsReadyMs, PrivacyAssetsMs, WebViewReadyMs, ExitMs -AutoSize |
    Out-String -Width 240 | Write-Host
$rows | Format-Table Run, WindowConstructionMs, MainWindowXamlMs, MainWindowWiredMs, WindowConstructedMs, WindowShownMs -AutoSize |
    Out-String -Width 240 | Write-Host

Write-Host "Mode: $(if ($singleFileMode) { 'single-file' } else { 'multi-file' }); release output: $($releaseFiles.Count) files / $releaseBytes bytes"
Write-Host ("First ContentRendered: {0} ms; warm median: {1} ms (gate < {2} ms)" -f `
    $first.ContentRenderedMs, $warmMedian, $WarmMedianLimitMs) -ForegroundColor Cyan
Write-Host "Metrics: $metricsAllPath"

if ($SkipGate) {
    Write-Host 'STARTUP GATE SKIPPED (metrics recorded).' -ForegroundColor Yellow
    exit 0
}
if (-not $gatePassed) {
    Write-Host "STARTUP GATE FAILED: warm median ContentRendered >= $WarmMedianLimitMs ms" -ForegroundColor Red
    exit 2
}

Write-Host "STARTUP GATE PASSED: warm median ContentRendered < $WarmMedianLimitMs ms." -ForegroundColor Green
exit 0

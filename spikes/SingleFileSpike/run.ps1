#Requires -Version 5.1
<#
.SYNOPSIS
    Build and measure the net48 WPF + WebView2 + SQLite packaging spike.
.PARAMETER Mode
    Baseline copies the normal dependency folder; SingleFile stages only the EXE.
.EXAMPLE
    spikes\SingleFileSpike\run.ps1 -Mode Baseline -Iterations 3
    spikes\SingleFileSpike\run.ps1 -Mode SingleFile -Iterations 5
#>
[CmdletBinding()]
param(
    [ValidateSet('Baseline', 'SingleFile')]
    [string]$Mode = 'SingleFile',
    [ValidateRange(1, 20)]
    [int]$Iterations = 5,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$root = Split-Path (Split-Path $projectDir -Parent) -Parent
$project = Join-Path $projectDir 'SingleFileSpike.csproj'
$useCostura = if ($Mode -eq 'SingleFile') { 'true' } else { 'false' }
$artifactRoot = Join-Path $root ("artifacts\SingleFileSpike\$Mode\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$dist = Join-Path $artifactRoot 'dist'
$runs = Join-Path $artifactRoot 'runs'

New-Item -ItemType Directory -Path $dist, $runs -Force | Out-Null

Write-Host "==> Restore ($Mode, UseCostura=$useCostura)" -ForegroundColor Cyan
& dotnet restore $project -p:UseCostura=$useCostura -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host "==> Build ($Mode)" -ForegroundColor Cyan
& dotnet build $project -c $Configuration -p:UseCostura=$useCostura -p:Platform=x64 --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

$output = Join-Path $projectDir "bin\x64\$Configuration\net48"
$builtExe = Join-Path $output 'SingleFileSpike.exe'
if (-not (Test-Path $builtExe)) { throw "Built EXE not found: $builtExe" }

if ($Mode -eq 'SingleFile') {
    Copy-Item -LiteralPath $builtExe -Destination $dist
}
else {
    Copy-Item -Path (Join-Path $output '*') -Destination $dist -Recurse
}

$exe = Join-Path $dist 'SingleFileSpike.exe'
$exeBytes = (Get-Item -LiteralPath $exe).Length
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
# 符号与无关架构不计入分发口径；保留文件本体以便调试，不做破坏性清理。
$distributionFiles = @(Get-ChildItem -LiteralPath $dist -Recurse -File |
    Where-Object { $_.Extension -ne '.pdb' -and $_.FullName -notmatch '\\(win-x86|win-arm64)\\' })
$distributionBytes = ($distributionFiles | Measure-Object -Property Length -Sum).Sum

if ($Mode -eq 'SingleFile' -and
    ($distributionFiles.Count -ne 1 -or $distributionFiles[0].FullName -ne $exe)) {
    throw "SingleFile distribution must contain exactly SingleFileSpike.exe; found $($distributionFiles.Count) file(s)."
}

$metrics = New-Object System.Collections.Generic.List[object]
for ($i = 1; $i -le $Iterations; $i++) {
    $runDir = Join-Path $runs "run-$i"
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    Write-Host "==> Run $i/$Iterations" -ForegroundColor Cyan
    $proc = Start-Process -FilePath $exe -ArgumentList @('--results', "`"$runDir`"") -PassThru
    $started = [DateTimeOffset]$proc.StartTime
    if (-not $proc.WaitForExit(90000)) {
        taskkill /PID $proc.Id /T /F *> $null
        throw "Spike run $i timed out."
    }
    if ($proc.ExitCode -ne 0) {
        $errorFile = Join-Path $runDir 'error.txt'
        $detail = if (Test-Path $errorFile) { Get-Content -LiteralPath $errorFile -Raw } else { '(no error file)' }
        throw "Spike run $i failed with exit $($proc.ExitCode): $detail"
    }

    $result = Get-Content -LiteralPath (Join-Path $runDir 'result.json') -Raw | ConvertFrom-Json
    if ($result.sqliteValue -ne 42) {
        throw "Spike run $i reported sqliteValue=$($result.sqliteValue), expected 42."
    }
    if ($result.processBitness -ne 'x64') {
        throw "Spike run $i reported processBitness=$($result.processBitness), expected x64."
    }
    if ($result.sqliteDatabaseBytes -le 0) {
        throw "Spike run $i reported an empty SQLite database."
    }
    if (-not $result.corePrivacyDefaults) {
        throw "Spike run $i did not load Apage.Core privacy defaults."
    }
    if ([string]::IsNullOrWhiteSpace($result.webViewVersion)) {
        throw "Spike run $i did not report a WebView2 runtime version."
    }
    if ($result.navigationTitle -ne 'ApageSingleFileSpike') {
        throw "Spike run $i rendered title '$($result.navigationTitle)', expected 'ApageSingleFileSpike'."
    }
    $windowMs = ([DateTimeOffset]::Parse($result.windowRenderedUtc) - $started).TotalMilliseconds
    $sqliteMs = ([DateTimeOffset]::Parse($result.sqliteReadyUtc) - $started).TotalMilliseconds
    $webViewMs = ([DateTimeOffset]::Parse($result.webViewReadyUtc) - $started).TotalMilliseconds
    $completedMs = ([DateTimeOffset]::Parse($result.completedUtc) - $started).TotalMilliseconds
    $exitMs = ([DateTimeOffset]$proc.ExitTime - $started).TotalMilliseconds
    $metrics.Add([pscustomobject]@{
        Mode = $Mode
        Iteration = $i
        WindowMs = [math]::Round($windowMs, 1)
        SQLiteMs = [math]::Round($sqliteMs, 1)
        WebViewMs = [math]::Round($webViewMs, 1)
        CompletedMs = [math]::Round($completedMs, 1)
        ExitMs = [math]::Round($exitMs, 1)
        ExeBytes = $exeBytes
        DistributionBytes = $distributionBytes
        DistributionFiles = $distributionFiles.Count
        SQLiteValue = $result.sqliteValue
        ProcessBitness = $result.processBitness
        WebViewVersion = $result.webViewVersion
        ExitCode = $proc.ExitCode
    })
}

$distributionAfterRuns = @(Get-ChildItem -LiteralPath $dist -Recurse -File)
if ($Mode -eq 'SingleFile' -and
    ($distributionAfterRuns.Count -ne 1 -or $distributionAfterRuns[0].FullName -ne $exe)) {
    $names = ($distributionAfterRuns | ForEach-Object { $_.Name }) -join ', '
    throw "SingleFile distribution changed after runs; expected only SingleFileSpike.exe, found: $names"
}

$summary = [pscustomobject]@{
    Mode = $Mode
    Configuration = $Configuration
    ExeBytes = $exeBytes
    ExeSHA256 = $exeHash
    DistributionBytes = $distributionBytes
    DistributionFiles = $distributionFiles.Count
    Runs = $metrics
}
$metricsPath = Join-Path $artifactRoot 'metrics.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metricsPath -Encoding UTF8

Write-Host ""
$metrics | Format-Table Mode, Iteration, WindowMs, SQLiteMs, WebViewMs, CompletedMs, ExitMs, ExitCode -AutoSize | Out-String | Write-Host
Write-Host "Distribution: $($distributionFiles.Count) file(s), $distributionBytes bytes" -ForegroundColor Cyan
Write-Host "EXE SHA256: $exeHash"
Write-Host "Metrics: $metricsPath"

if ($exeBytes -ge 5000000) { Write-Host 'SIZE GATE FAILED: EXE >= 5,000,000 bytes' -ForegroundColor Red; exit 2 }
if ($exeBytes -gt 4250000) { Write-Host 'SIZE BUDGET WARNING: EXE exceeds 4,250,000-byte development target' -ForegroundColor Yellow }
if ($Mode -eq 'SingleFile' -and $distributionFiles.Count -eq 1) {
    Write-Host 'FUNCTIONAL GATE PASSED: one EXE, Core loaded, SQLite round-trip, WebView2 navigation.' -ForegroundColor Green
}
exit 0

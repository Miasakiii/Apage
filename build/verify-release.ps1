#Requires -Version 5.1
<#
.SYNOPSIS
    Post-signing release acceptance for the single-file Apage.Portable.exe.

.DESCRIPTION
    Consolidates docs\release\signing.md section 4 into one repeatable gate.
    Signing changes the EXE bytes and SHA-256, so every acceptance signal must
    come from the SAME signed artifact. Never rebuild or replace the EXE after
    signing.

    Checks:
      1. Authenticode Valid      (build\verify-signature.ps1 -RequireValid)
      2. one EXE / <5,000,000 B  (hard release ceiling)
      3. five-signal smoke       (build\smoke.ps1, includes zero-outbound probe)
      4. startup gate <1000 ms   (build\startup-bench.ps1, opt-in -RunStartupBench)
      5. exFAT media evidence    (artifacts\fault-matrix\results.json)

    Checks 4 and 5 are external-dependency gates. The owner has deferred the
    startup gate (artifacts\release-waiver-startup.json, threshold unchanged),
    and this host has no exFAT volume. Both are reported as blockers, NEVER as
    passes. Use -RequireReleaseReady for the final release decision; CI uses
    the default evidence-collection mode.

    Exit codes: 0 = evidence collected (release may still be blocked),
                2 = at least one check failed, or -RequireReleaseReady with
                    pending blockers.

.EXAMPLE
    build\verify-release.ps1 -ExePath artifacts\signed\Apage.Portable.exe

.EXAMPLE
    build\verify-release.ps1 -ExePath artifacts\signed\Apage.Portable.exe `
      -RunStartupBench -StartupIterations 5 -RequireReleaseReady
#>
[CmdletBinding()]
param(
    [string]$ExePath = 'artifacts\signed\Apage.Portable.exe',
    [string]$ReportPath = 'artifacts\signed\release-verification.json',
    [switch]$RunStartupBench,
    [ValidateRange(2, 20)]
    [int]$StartupIterations = 5,
    [ValidateRange(1.0, 10000.0)]
    [double]$WarmMedianLimitMs = 1000,
    [string]$FaultMatrixPath = 'artifacts\fault-matrix\results.json',
    [switch]$SkipSmoke,
    [switch]$RequireReleaseReady
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))

function Resolve-LocalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

$exe = Resolve-LocalPath $ExePath
$report = Resolve-LocalPath $ReportPath
$faultMatrix = Resolve-LocalPath $FaultMatrixPath
$artifactsPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $report.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ReportPath must stay under artifacts/: $report"
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Signed EXE not found: $exe. Run the SignPath workflow first."
}

$signedDir = Split-Path -Parent $exe
$signatureJson = Join-Path $signedDir 'signature.json'
$reportDir = Split-Path -Parent $report
if (-not [System.IO.Directory]::Exists($reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}

$checks = New-Object System.Collections.Generic.List[object]
$blockers = New-Object System.Collections.Generic.List[string]

function Add-Check {
    param([string]$Name, [string]$Status, [string]$Detail)
    $checks.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail })
}

$exeFiles = @(Get-ChildItem -LiteralPath $signedDir -File -Filter '*.exe')
$bytes = (Get-Item -LiteralPath $exe).Length
$postSignHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash

Write-Host "==> Post-signing acceptance: $exe" -ForegroundColor Cyan

# 1. Authenticode -----------------------------------------------------------
$LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'verify-signature.ps1') -ExePath $exe -RequireValid -JsonPath $signatureJson
$sigExit = $LASTEXITCODE
if ($sigExit -eq 0) {
    Add-Check 'authenticode' 'pass' 'Valid (verify-signature.ps1 -RequireValid)'
}
else {
    Add-Check 'authenticode' 'fail' "verify-signature.ps1 exit $sigExit"
    $blockers.Add('Authenticode signature is not Valid.')
}

# 2. Single file and hard size ceiling -------------------------------------
$sizeOk = ($exeFiles.Count -eq 1) -and ($bytes -lt 5000000)
if ($sizeOk) {
    Add-Check 'single-file-size' 'pass' "$($exeFiles.Count) EXE / $bytes bytes (< 5,000,000)"
}
else {
    Add-Check 'single-file-size' 'fail' "$($exeFiles.Count) EXE / $bytes bytes (expected 1 EXE < 5,000,000)"
    $blockers.Add('Single-file or 5MB hard ceiling violated.')
}
$devTargetExceeded = $bytes -gt 4250000
if ($devTargetExceeded) {
    Write-Host "NOTE: $bytes bytes exceeds the 4,250,000-byte development target; review before release." -ForegroundColor Yellow
}

# 3. Five-signal smoke ------------------------------------------------------
if ($SkipSmoke) {
    Add-Check 'smoke' 'skipped' 'skipped by operator'
    $blockers.Add('Five-signal smoke was skipped.')
}
else {
    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'smoke.ps1') -ExePath $exe -TimeoutSec 120
    $smokeExit = $LASTEXITCODE
    if ($smokeExit -eq 0) {
        Add-Check 'smoke' 'pass' 'five signals + zero-outbound probe'
    }
    else {
        Add-Check 'smoke' 'fail' "smoke.ps1 exit $smokeExit"
        $blockers.Add('Five-signal smoke failed on the signed artifact.')
    }
}

# 4. Startup gate on the signed artifact -----------------------------------
if (-not $RunStartupBench) {
    Add-Check 'startup' 'skipped' 'deferred-by-owner; 1000 ms threshold unchanged (release-waiver-startup.json)'
    $blockers.Add('Startup gate not re-verified on the signed artifact (owner waiver).')
}
else {
    $benchOut = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\signed-startup'))
    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'startup-bench.ps1') -ExePath $exe `
        -Iterations $StartupIterations -OutputDirectory $benchOut `
        -WarmMedianLimitMs $WarmMedianLimitMs
    $benchExit = $LASTEXITCODE
    $metricsPath = Join-Path $benchOut 'metrics.json'
    $gate = $false
    $median = $null
    if (Test-Path -LiteralPath $metricsPath) {
        $m = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
        $gate = [bool]$m.GatePassed
        $median = $m.WarmMedianContentRenderedMs
    }
    if ($benchExit -eq 0 -and $gate) {
        Add-Check 'startup' 'pass' "warm median $median ms < $WarmMedianLimitMs ms"
    }
    else {
        Add-Check 'startup' 'fail' "exit $benchExit, gatePassed=$gate, warmMedian=$median"
        $blockers.Add('Startup gate failed on the signed artifact.')
    }
}

# 5. exFAT physical-media evidence -----------------------------------------
$exfatOk = $false
$exfatDetail = 'no evidence file'
if (Test-Path -LiteralPath $faultMatrix) {
    $fm = Get-Content -LiteralPath $faultMatrix -Raw | ConvertFrom-Json
    $row = @($fm.results | Where-Object { $_.Scenario -eq 'exfat-media' })
    if ($row.Count -gt 0) {
        $exfatOk = ($row[0].Passed -eq $true)
        $skippedProp = $row[0].PSObject.Properties['Skipped']
        $reasonProp = $row[0].PSObject.Properties['Reason']
        if ($null -ne $skippedProp -and $skippedProp.Value -eq $true) {
            $reason = if ($null -ne $reasonProp) { $reasonProp.Value } else { 'no reason recorded' }
            $exfatDetail = "skipped: $reason"
        }
        else {
            $exfatDetail = "passed=$($row[0].Passed)"
        }
    }
    else {
        $exfatDetail = 'exfat-media scenario missing from results.json'
    }
}
if ($exfatOk) {
    Add-Check 'exfat-media' 'pass' $exfatDetail
}
else {
    Add-Check 'exfat-media' 'pending' "$exfatDetail; run artifacts\fault-matrix-run.ps1 with an exFAT volume"
    $blockers.Add('exFAT portable-media matrix has no passing evidence.')
}

$checkFailures = @($checks | Where-Object { $_.status -eq 'fail' }).Count
$releaseReady = ($blockers.Count -eq 0)

[ordered]@{
    generatedAt                 = (Get-Date).ToString('o')
    exePath                     = $exe
    sha256PostSign              = $postSignHash
    bytes                       = $bytes
    exeFileCount                = $exeFiles.Count
    hardLimitBytes              = 5000000
    developmentTargetBytes      = 4250000
    developmentTargetExceeded   = $devTargetExceeded
    startupWaiver               = 'artifacts\release-waiver-startup.json'
    checks                      = $checks.ToArray()
    blockers                    = $blockers.ToArray()
    releaseReady                = $releaseReady
    requireReleaseReady         = [bool]$RequireReleaseReady
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8

Write-Host ''
Write-Host '===== Post-signing acceptance summary =====' -ForegroundColor Cyan
foreach ($c in $checks) {
    $color = switch ($c.status) {
        'pass' { 'Green' }
        'fail' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ('{0,-18} {1,-8} {2}' -f $c.name, $c.status, $c.detail) -ForegroundColor $color
}
Write-Host "SHA-256 (post-sign): $postSignHash"
Write-Host "Report: $report"
if ($releaseReady) {
    Write-Host 'RELEASE READY: all acceptance evidence present.' -ForegroundColor Green
}
else {
    Write-Host "RELEASE NOT READY: $($blockers.Count) pending blocker(s)." -ForegroundColor Yellow
    foreach ($b in $blockers) {
        Write-Host "  - $b" -ForegroundColor Yellow
    }
}

if ($checkFailures -gt 0) { exit 2 }
if ($RequireReleaseReady -and -not $releaseReady) { exit 2 }
exit 0

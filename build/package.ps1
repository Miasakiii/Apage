#Requires -Version 5.1
<#
.SYNOPSIS
    Build the production single-file portable artifact and enforce release gates.

.DESCRIPTION
    1. Builds the solution (unless -SkipBuild).
    2. Checks project/data licenses and PSL/rule snapshot hashes.
    3. Copies ONLY Apage.Portable.exe to artifacts\portable (Costura must embed all
       managed assemblies; WebView2Loader/e_sqlite3 are embedded via NativeBootstrap).
    4. Asserts exactly one required file and enforces the 4.25MB development target
       plus the 5MB hard ceiling.
    5. Runs the real five-signal --smoke path from an isolated copy.
    6. Runs the packaged EXE through the startup benchmark and writes its metrics.
    7. Produces a separate legal bundle and a JSON release manifest with version,
       git revision, SHA-256, startup and signature status.

.EXAMPLE
    build\package.ps1
    build\package.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$SkipSmoke,
    [switch]$SkipLicenseCheck,
    [switch]$SkipStartupBench,
    [ValidateRange(2, 20)]
    [int]$StartupIterations = 3,
    [ValidateRange(1.0, 10000.0)]
    [double]$StartupLimitMs = 1000,
    [switch]$AllowDevTargetOverage,
    [switch]$RequireCleanWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourceExe = Join-Path $root "Apage.Portable\bin\$Configuration\Apage.Portable.exe"
$distDir = Join-Path $root 'artifacts\portable'
$distExe = Join-Path $distDir 'Apage.Portable.exe'
$smokeDir = Join-Path $root 'artifacts\portable-smoke'
$startupDir = Join-Path $root 'artifacts\release-startup'
$legalDir = Join-Path $root 'artifacts\legal'
$hashPath = Join-Path $root 'artifacts\portable.sha256'
$manifestPath = Join-Path $root 'artifacts\portable.metrics.json'
$hardLimit = 5000000
$devLimit = 4250000

if (-not $SkipBuild) {
    Write-Host "==> Building $Configuration" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

if (-not $SkipLicenseCheck) {
    Write-Host "==> License and privacy-asset check" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'check-licenses.ps1')
    if ($LASTEXITCODE -ne 0) { throw "License check failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Built exe not found: $sourceExe"
}

# artifacts/ is an ignored, reproducible build-output area. Only these exact
# directories are reset; user data and source files are never touched.
if ([System.IO.Directory]::Exists($distDir)) {
    [System.IO.Directory]::Delete($distDir, $true)
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $distExe

$files = @(Get-ChildItem -LiteralPath $distDir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Apage.Portable.exe') {
    $names = ($files | ForEach-Object { $_.Name }) -join ', '
    throw "Distribution must contain exactly Apage.Portable.exe; found $($files.Count): $names"
}

$bytes = (Get-Item -LiteralPath $distExe).Length
$hash = (Get-FileHash -LiteralPath $distExe -Algorithm SHA256).Hash
"$hash  Apage.Portable.exe" | Set-Content -LiteralPath $hashPath -Encoding ascii

if ($bytes -gt $hardLimit) {
    throw "Size gate failed: $bytes bytes > $hardLimit hard ceiling."
}
if ($bytes -gt $devLimit -and -not $AllowDevTargetOverage) {
    throw "Development size gate failed: $bytes bytes > $devLimit target. Use -AllowDevTargetOverage only for an explicit, documented exception."
}
if ($bytes -gt $devLimit) {
    Write-Warning "Size $bytes exceeds the $devLimit-byte development target."
}

if (-not $SkipSmoke) {
    if ([System.IO.Directory]::Exists($smokeDir)) {
        [System.IO.Directory]::Delete($smokeDir, $true)
    }
    New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
    $smokeExe = Join-Path $smokeDir 'Apage.Portable.exe'
    Copy-Item -LiteralPath $distExe -Destination $smokeExe
    try {
        & (Join-Path $PSScriptRoot 'smoke.ps1') -ExePath $smokeExe -TimeoutSec 120
        if ($LASTEXITCODE -ne 0) { throw "Single-file smoke failed (exit $LASTEXITCODE)." }
    }
    finally {
        if ([System.IO.Directory]::Exists($smokeDir)) {
            [System.IO.Directory]::Delete($smokeDir, $true)
        }
    }
}

# 随包隐私资产必须先在源文件层自洽，再由 Core 在运行时二次校验。
$privacyDir = Join-Path $root 'Apage.Core\Resources\Privacy'
$assetManifestPath = Join-Path $privacyDir 'privacy-assets.json'
$pslPath = Join-Path $privacyDir 'public_suffix_list.dat'
$rulesPath = Join-Path $privacyDir 'builtin-rules.txt'
foreach ($required in @($assetManifestPath, $pslPath, $rulesPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Privacy asset missing: $required (run tools/SnapshotBuilder first)."
    }
}
$privacyManifest = Get-Content -LiteralPath $assetManifestPath -Raw | ConvertFrom-Json
$pslHash = (Get-FileHash -LiteralPath $pslPath -Algorithm SHA256).Hash
$rulesHash = (Get-FileHash -LiteralPath $rulesPath -Algorithm SHA256).Hash
if ($pslHash -ne $privacyManifest.psl.sha256) {
    throw "PSL hash mismatch between file and privacy-assets.json."
}
if ($rulesHash -ne $privacyManifest.rules.sha256) {
    throw "Rule snapshot hash mismatch between file and privacy-assets.json."
}

# 法律文本既可随 EXE 内嵌，也单独产出可上传的 legal bundle（分发目录仍只有一个 EXE）。
if ([System.IO.Directory]::Exists($legalDir)) {
    [System.IO.Directory]::Delete($legalDir, $true)
}
New-Item -ItemType Directory -Path $legalDir -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File |
    Copy-Item -Destination $legalDir -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $legalDir 'LICENSE-MIT.txt') -Force
$legalFiles = @(Get-ChildItem -LiteralPath $legalDir -File)
$legalManifest = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    projectLicense = 'MIT'
    components = @(
        [ordered]@{
            name = 'Public Suffix List'
            version = $privacyManifest.psl.version
            license = $privacyManifest.psl.license
            source = $privacyManifest.psl.sourceUrl
            sha256 = $pslHash
            file = 'LICENSE-MPL-2.0.txt'
        },
        [ordered]@{
            name = 'EasyList + EasyPrivacy filtered snapshot'
            license = $privacyManifest.rules.license
            attribution = $privacyManifest.rules.attribution
            sha256 = $rulesHash
            licenseFile = 'LICENSE-CC-BY-SA-3.0.txt'
            sources = @($privacyManifest.rules.sources)
        }
    )
    files = @($legalFiles | ForEach-Object {
        [ordered]@{
            name = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
$legalManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $legalDir 'components.json') -Encoding utf8

$startup = $null
if (-not $SkipStartupBench) {
    Write-Host "==> Startup benchmark on packaged single EXE" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'startup-bench.ps1') `
        -ExePath $distExe `
        -Iterations $StartupIterations `
        -OutputDirectory $startupDir `
        -WarmMedianLimitMs $StartupLimitMs
    $benchExit = $LASTEXITCODE
    $startupMetricsPath = Join-Path $startupDir 'metrics.json'
    if (-not (Test-Path -LiteralPath $startupMetricsPath)) {
        throw "Startup benchmark did not produce metrics (exit $benchExit)."
    }
    $startupMetrics = Get-Content -LiteralPath $startupMetricsPath -Raw | ConvertFrom-Json
    $startup = [ordered]@{
        metricsPath = $startupMetricsPath
        mode = $startupMetrics.Mode
        iterations = $startupMetrics.Iterations
        firstContentRenderedMs = $startupMetrics.FirstContentRenderedMs
        warmMedianContentRenderedMs = $startupMetrics.WarmMedianContentRenderedMs
        limitMs = $startupMetrics.WarmMedianLimitMs
        gatePassed = $startupMetrics.GatePassed
    }
    if ($benchExit -ne 0) {
        throw "Startup gate failed (exit $benchExit); metrics: $startupMetricsPath"
    }
}

$versionInfo = (Get-Item -LiteralPath $distExe).VersionInfo
$fileVersion = $versionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($fileVersion) -or $fileVersion -eq '0.0.0.0') {
    throw "Product version metadata is missing from the EXE."
}
$productVersion = $versionInfo.ProductVersion

$gitCommit = $null
$worktreeDirty = $null
try {
    $gitCommit = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
    if ($null -ne $gitCommit) { $gitCommit = $gitCommit.Trim() }
    $worktreeDirty = (@(git -C $root status --porcelain 2>$null).Count -gt 0)
}
catch {
    $gitCommit = $null
    $worktreeDirty = $null
}
if ($worktreeDirty -eq $true) {
    if ($RequireCleanWorktree) {
        throw 'Worktree is dirty; release packaging requires a clean commit.'
    }
    Write-Warning 'Worktree is dirty; the manifest will record worktreeDirty=true.'
}

$signatureStatus = 'Unknown'
try {
    $signatureStatus = (Get-AuthenticodeSignature -FilePath $distExe).Status.ToString()
}
catch {
    $signatureStatus = 'Unavailable'
}

$manifest = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    configuration = $Configuration
    fileCount = 1
    bytes = $bytes
    sha256 = $hash
    hardLimitBytes = $hardLimit
    developmentTargetBytes = $devLimit
    version = $fileVersion
    productVersion = $productVersion
    gitCommit = $gitCommit
    worktreeDirty = $worktreeDirty
    signatureStatus = $signatureStatus
    signed = ($signatureStatus -eq 'Valid')
    smokePassed = (-not $SkipSmoke)
    licenseCheckPassed = (-not $SkipLicenseCheck)
    legalBundle = $legalDir
    startup = $startup
    privacyAssets = [ordered]@{
        pslVersion = $privacyManifest.psl.version
        pslSha256 = $pslHash
        pslBytes = (Get-Item -LiteralPath $pslPath).Length
        pslLicense = $privacyManifest.psl.license
        rulesAccepted = $privacyManifest.rules.acceptedRules
        rulesSkipped = $privacyManifest.rules.skippedRules
        rulesSha256 = $rulesHash
        rulesBytes = (Get-Item -LiteralPath $rulesPath).Length
        rulesLicense = $privacyManifest.rules.license
        sources = @($privacyManifest.rules.sources | ForEach-Object {
            [ordered]@{ name = $_.name; url = $_.url; sha256 = $_.sha256; license = $_.license }
        })
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Write-Host ""
Write-Host "===== Portable package =====" -ForegroundColor Cyan
Write-Host "EXE      : $distExe"
Write-Host "Files    : 1"
Write-Host "Bytes    : $bytes"
Write-Host "SHA-256  : $hash"
Write-Host "Version  : $fileVersion ($productVersion)"
Write-Host "Signature: $signatureStatus"
Write-Host "PSL      : $($privacyManifest.psl.version) ($($privacyManifest.psl.license))"
Write-Host "Rules    : $($privacyManifest.rules.acceptedRules) accepted / $($privacyManifest.rules.skippedRules) skipped"
if ($null -ne $startup) {
    Write-Host "Startup  : first $($startup.firstContentRenderedMs) ms; warm median $($startup.warmMedianContentRenderedMs) ms (limit $($startup.limitMs) ms)"
}
Write-Host "Legal    : $legalDir"
Write-Host "Manifest : $manifestPath"
Write-Host "PACKAGE PASSED" -ForegroundColor Green

#Requires -Version 5.1
<#
.SYNOPSIS
    Mechanized red-line checks for the AGENTS.md high-risk conventions.

.DESCRIPTION
    1. Apage.Portable.csproj must keep its wildcard Compile/Page globs:
       exactly one <Compile> glob (**\*.cs), exactly one <Page> glob
       (**\*.xaml excluding App.xaml) and App.xaml as the single
       ApplicationDefinition. Per-file expansion fails the check.
    2. Apage.Core / Apage.Portable sources must stay free of telemetry or
       outbound-reporting calls (zero telemetry / zero background requests).
    3. Apage.Core must stay netstandard2.0 (single TFM, no Windows/.NET 8
       coupling) and its PackageReferences must stay inside the reviewed
       allowlist below.
    4. Portable runtime directories (data/ cache/ wallpapers/) must never be
       tracked by git, and .gitignore must keep ignoring them.
    5. The privacy-default baseline (AppSettings) must keep its locking unit
       test assertions in Apage.Core.Tests.
    Exits non-zero on any violation. Runs as a step inside the existing CI
    gate (.github/workflows/ci.yml); locally: build\check-redlines.ps1

.EXAMPLE
    build\check-redlines.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# Check 1: Apage.Portable.csproj keeps its wildcard Compile/Page structure
# ---------------------------------------------------------------------------
$csprojRel = 'Apage.Portable\Apage.Portable.csproj'
$csprojPath = Join-Path $root $csprojRel
[xml]$csproj = Get-Content -LiteralPath $csprojPath -Raw

$compile = @($csproj.GetElementsByTagName('Compile'))
if ($compile.Count -ne 1) {
    $failures.Add("$csprojRel : expected exactly 1 <Compile> item, found $($compile.Count) - wildcard glob must not be expanded into per-file entries")
}
else {
    if ($compile[0].GetAttribute('Include') -ne '**\*.cs') {
        $failures.Add("$csprojRel : <Compile Include> must stay the wildcard '**\*.cs', found '$($compile[0].GetAttribute('Include'))'")
    }
    $exclude = $compile[0].GetAttribute('Exclude')
    if ($exclude -notlike '*obj\**' -or $exclude -notlike '*bin\**') {
        $failures.Add("$csprojRel : <Compile> Exclude must keep 'obj\**' and 'bin\**', found '$exclude'")
    }
}

$page = @($csproj.GetElementsByTagName('Page'))
if ($page.Count -ne 1) {
    $failures.Add("$csprojRel : expected exactly 1 <Page> item, found $($page.Count) - wildcard glob must not be expanded into per-file entries")
}
else {
    if ($page[0].GetAttribute('Include') -ne '**\*.xaml') {
        $failures.Add("$csprojRel : <Page Include> must stay the wildcard '**\*.xaml', found '$($page[0].GetAttribute('Include'))'")
    }
    if ($page[0].GetAttribute('Exclude') -notlike '*App.xaml*') {
        $failures.Add("$csprojRel : <Page> Exclude must keep 'App.xaml' (it is the ApplicationDefinition)")
    }
}

$appDef = @($csproj.GetElementsByTagName('ApplicationDefinition'))
if ($appDef.Count -ne 1 -or $appDef[0].GetAttribute('Include') -ne 'App.xaml') {
    $failures.Add("$csprojRel : App.xaml must stay the single <ApplicationDefinition>")
}

# ---------------------------------------------------------------------------
# Check 2: zero telemetry / outbound-reporting calls in Core + Portable
# ---------------------------------------------------------------------------
# Raw HTTP client types plus well-known telemetry/crash-reporting SDK names.
# The browser itself talks through WebView2; app code has no business making
# its own outbound requests (AGENTS.md: zero telemetry / zero background requests).
$forbiddenPatterns = @(
    '\bHttpClient\b'
    '\bWebClient\b'
    '\bHttpWebRequest\b'
    '\bWebRequest\b'
    'System\.Net\.Http'
    '\bTelemetryClient\b'
    '\bTrackEvent\b'
    '\bTrackException\b'
    'ApplicationInsights'
    '\bAppCenter\b'
    '\bSentry\b'
    '\bCrashlytics\b'
    '\bFirebase\b'
)

$sourceDirs = @('Apage.Core', 'Apage.Portable')
$sourceFiles = foreach ($dir in $sourceDirs) {
    Get-ChildItem -Path (Join-Path $root $dir) -Recurse -File -Include *.cs, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
}

$hits = $sourceFiles | Select-String -Pattern $forbiddenPatterns
foreach ($hit in $hits) {
    $rel = $hit.Path.Substring($root.Length + 1)
    $failures.Add("telemetry/network : ${rel}:$($hit.LineNumber): $($hit.Line.Trim())")
}

# ---------------------------------------------------------------------------
# Check 3: Apage.Core stays netstandard2.0 with a reviewed package boundary
# ---------------------------------------------------------------------------
# AGENTS.md red line #5: the shared layer must not pull in Windows-only or
# .NET 8-only APIs, or future layering (Apage.Desktop etc.) breaks.
$coreCsprojRel = 'Apage.Core\Apage.Core.csproj'
$coreCsprojPath = Join-Path $root $coreCsprojRel
[xml]$coreCsproj = Get-Content -LiteralPath $coreCsprojPath -Raw

$coreTfm = @($coreCsproj.GetElementsByTagName('TargetFramework'))
if ($coreTfm.Count -ne 1 -or $coreTfm[0].InnerText.Trim() -ne 'netstandard2.0') {
    $found = if ($coreTfm.Count -ge 1) { ($coreTfm | ForEach-Object { $_.InnerText.Trim() }) -join ', ' } else { '(none)' }
    $failures.Add("$coreCsprojRel : <TargetFramework> must stay exactly 'netstandard2.0', found '$found'")
}
if (@($coreCsproj.GetElementsByTagName('TargetFrameworks')).Count -gt 0) {
    $failures.Add("$coreCsprojRel : multi-targeting via <TargetFrameworks> is not allowed - the shared layer stays single-TFM netstandard2.0")
}
if (@($coreCsproj.GetElementsByTagName('FrameworkReference')).Count -gt 0) {
    $failures.Add("$coreCsprojRel : <FrameworkReference> (e.g. Microsoft.WindowsDesktop.App) is not allowed in the shared layer")
}
foreach ($winProp in 'UseWPF', 'UseWindowsForms') {
    if (@($coreCsproj.GetElementsByTagName($winProp)).Count -gt 0) {
        $failures.Add("$coreCsprojRel : <$winProp> couples the shared layer to Windows desktop frameworks - not allowed")
    }
}

# Reviewed allowlist: every package here has been verified netstandard2.0-
# compatible with no Windows/.NET 8-only API surface. Adding a Core dependency
# means reviewing it against that bar first, then extending this list.
$allowedCorePackages = @(
    'CommunityToolkit.Mvvm'
    'System.Text.Json'
)
foreach ($pkg in @($coreCsproj.GetElementsByTagName('PackageReference'))) {
    $pkgName = $pkg.GetAttribute('Include')
    if ($allowedCorePackages -notcontains $pkgName) {
        $failures.Add("$coreCsprojRel : PackageReference '$pkgName' is outside the reviewed shared-layer allowlist - verify it is netstandard2.0-safe (no Windows/.NET 8-only APIs), then add it to `$allowedCorePackages in build\check-redlines.ps1")
    }
}

# ---------------------------------------------------------------------------
# Check 4: portable runtime directories are never tracked by git
# ---------------------------------------------------------------------------
# AGENTS.md red line #3: data/ cache/ wallpapers/ are runtime-only (portable
# layout) and must never be committed.
$portableDirPattern = '(^|/)(data|cache|wallpapers)/'

$gitignoreRel = '.gitignore'
$gitignoreLines = (Get-Content -LiteralPath (Join-Path $root $gitignoreRel)) | ForEach-Object { $_.Trim() }
foreach ($dir in 'data/', 'cache/', 'wallpapers/') {
    if ($gitignoreLines -notcontains $dir) {
        $failures.Add("$gitignoreRel : missing ignore entry '$dir' - portable runtime directories must stay ignored")
    }
}

# Guarded git probe: PS 5.1 + ErrorActionPreference=Stop turns redirected
# native stderr into a terminating error, and git may be absent entirely.
$insideGit = $false
try {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    git -C $root rev-parse --is-inside-work-tree 2>$null | Out-Null
    $insideGit = ($LASTEXITCODE -eq 0)
}
catch { $insideGit = $false }
finally { $ErrorActionPreference = $prevEap }

if ($insideGit) {
    $trackedHits = @(git -C $root ls-files) | Where-Object { $_ -match $portableDirPattern }
    foreach ($tracked in $trackedHits) {
        $failures.Add("portable-runtime-dir : '$tracked' is tracked by git - data/ cache/ wallpapers/ contents must never be committed (git rm --cached '$tracked')")
    }
}
else {
    Write-Host "WARN: git unavailable or not a work tree - skipping tracked-file scan for data/ cache/ wallpapers/ (gitignore entries still checked)." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Check 5: privacy-default baseline keeps its locking unit test assertions
# ---------------------------------------------------------------------------
# AGENTS.md red line #2: the privacy defaults themselves are locked by
# dotnet test, but only while the assertions exist. This check fails if
# someone removes/weakens them.
$privacyTestsRel = 'Apage.Core.Tests\AppSettingsTests.cs'
$testFiles = Get-ChildItem -Path (Join-Path $root 'Apage.Core.Tests') -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$testText = ($testFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

foreach ($prop in 'BlockThirdPartyCookies', 'SendDoNotTrack', 'SendGPC', 'WebRTCIPProtection') {
    if ($testText -notmatch "Assert\.True\(\s*\w+\.$prop\b") {
        $failures.Add("$privacyTestsRel : missing 'Assert.True(...$prop)' - privacy default-ON baseline must stay locked by a unit test assertion")
    }
}
foreach ($prop in 'OnlineSearchSuggestions', 'ClearOnExit') {
    if ($testText -notmatch "Assert\.False\(\s*\w+\.$prop\b") {
        $failures.Add("$privacyTestsRel : missing 'Assert.False(...$prop)' - privacy default-OFF baseline must stay locked by a unit test assertion")
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "===== Red-line check =====" -ForegroundColor Cyan
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "FAIL: $f" -ForegroundColor Red }
    Write-Host ""
    Write-Host "RED-LINE CHECK FAILED ($($failures.Count) violation(s))" -ForegroundColor Red
    exit 1
}
Write-Host "OK: all 5 red-line conventions hold (csproj wildcards, zero telemetry, Core netstandard2.0 + package boundary, portable runtime dirs untracked, privacy-default test assertions present)." -ForegroundColor Green
exit 0

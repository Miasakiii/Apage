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
Write-Host "OK: csproj wildcard structure intact; no telemetry/outbound-reporting calls found." -ForegroundColor Green
exit 0

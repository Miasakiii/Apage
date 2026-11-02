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
#
# A3 精确豁免：手动规则更新是唯一被批准的用户触发式出站请求，
# 只允许写在 RuleUpdateClient.cs 这一个文件里（按钮触发、HTTPS、限时限大小）。
# 豁免是文件级而非模式级：其他任何文件出现网络类型仍然失败；
# 遥测类模式对全部文件生效，不接受任何豁免。
$networkPatterns = @(
    '\bHttpClient\b'
    '\bWebClient\b'
    '\bHttpWebRequest\b'
    '\bWebRequest\b'
    'System\.Net\.Http'
)
$networkExemptFiles = @(
    'Apage.Portable\Services\RuleUpdateClient.cs'
)

$telemetryPatterns = @(
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

foreach ($hit in ($sourceFiles | Select-String -Pattern $telemetryPatterns)) {
    $rel = $hit.Path.Substring($root.Length + 1)
    $failures.Add("telemetry : ${rel}:$($hit.LineNumber): $($hit.Line.Trim())")
}

foreach ($hit in ($sourceFiles | Select-String -Pattern $networkPatterns)) {
    $rel = $hit.Path.Substring($root.Length + 1)
    if ($networkExemptFiles -contains $rel) { continue }
    $failures.Add("network($rel) : line $($hit.LineNumber): network types are only allowed in $($networkExemptFiles -join ', '); found: $($hit.Line.Trim())")
}

# 豁免文件必须存在：避免豁免名单指向已删除/改名的文件后静默失效。
foreach ($exempt in $networkExemptFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $exempt))) {
        $failures.Add("network-exempt : '$exempt' is listed as the only allowed network file but does not exist")
    }
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
    'System.Text.Json'
    'Microsoft.Data.Sqlite.Core'
    'SQLitePCLRaw.provider.e_sqlite3'
    'SQLitePCLRaw.lib.e_sqlite3'
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
# 只匹配仓库根目录下的运行时目录。必须使用大小写敏感匹配：
# Apage.Core/Data 是源码，不能被当成便携运行时的 data/。
$portableDirPattern = '^(data|cache|wallpapers)/'

# 内建回归用例：同时锁住“根目录运行时目录要命中”和“源码目录不能被误伤”。
# 这些路径不依赖当前工作区状态，避免脚本逻辑再次被无关提交悄悄改坏。
$portablePathCases = @(
    [pscustomobject]@{ Path = 'data/apage.db'; Expected = $true; Name = 'root data' }
    [pscustomobject]@{ Path = 'cache/WebView2/EBWebView'; Expected = $true; Name = 'root cache' }
    [pscustomobject]@{ Path = 'wallpapers/bg.jpg'; Expected = $true; Name = 'root wallpapers' }
    [pscustomobject]@{ Path = 'Apage.Core/Data/BookmarkRepository.cs'; Expected = $false; Name = 'Core Data source' }
    [pscustomobject]@{ Path = 'docs/data/example.md'; Expected = $false; Name = 'nested data' }
    [pscustomobject]@{ Path = 'Data/runtime.db'; Expected = $false; Name = 'case-sensitive root' }
)
foreach ($case in $portablePathCases) {
    $matched = [bool]($case.Path -cmatch $portableDirPattern)
    if ($matched -ne $case.Expected) {
        $failures.Add("portable-dir-selftest : '$($case.Path)' ($($case.Name)) expected match=$($case.Expected), got $matched")
    }
}

$gitignoreRel = '.gitignore'
$gitignoreLines = (Get-Content -LiteralPath (Join-Path $root $gitignoreRel)) | ForEach-Object { $_.Trim() }
foreach ($dir in '/data/', '/cache/', '/wallpapers/') {
    if ($gitignoreLines -notcontains $dir) {
        $failures.Add("$gitignoreRel : missing rooted ignore entry '$dir' - portable runtime directories must stay ignored; an unanchored entry would also hide Apage.Core/Data")
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
    # git ls-files 输出仓库相对路径且始终使用正斜杠；-cmatch 保持大小写敏感，
    # 只接受 data/、cache/、wallpapers/ 三个根目录，不匹配 Apage.Core/Data/**。
    $trackedHits = @(git -C $root ls-files) | Where-Object { $_ -cmatch $portableDirPattern }
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

foreach ($prop in 'SendDoNotTrack', 'SendGPC', 'WebRTCIPProtection') {
    if ($testText -notmatch "Assert\.True\(\s*\w+\.$prop\b") {
        $failures.Add("$privacyTestsRel : missing 'Assert.True(...$prop)' - privacy default-ON baseline must stay locked by a unit test assertion")
    }
}
# WebView2 无可靠第三方 Cookie 阻断入口；旧字段必须保持关闭，防止重新形成假防护。
foreach ($prop in 'OnlineSearchSuggestions', 'ClearOnExit', 'BlockThirdPartyCookies') {
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

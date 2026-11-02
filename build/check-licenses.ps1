#Requires -Version 5.1
<#
.SYNOPSIS
    Verify project and bundled-data licenses before packaging.
.DESCRIPTION
    Fails if any required license/notice file is missing, empty or lacks the
    expected license marker; verifies PSL/rule hashes against privacy-assets.json;
    and ensures the Core project embeds the legal texts in the single-file build.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

# ---- 图标资源微门禁（R11）：Icons.xaml 是 StreamGeometry 文本资产，超限通常意味着
# 误把位图/内联数据塞进了图标字典。当前约 9KB，门禁 32KB 留足扩充余量；
# 新增图标走 tools\IconTools\svg-to-path.ps1 转换并登记上游许可归属。
$iconsPath = Join-Path $root 'Apage.Portable\Themes\Icons.xaml'
if (-not (Test-Path -LiteralPath $iconsPath)) {
    $failures.Add('missing icon dictionary: Apage.Portable\Themes\Icons.xaml')
}
else {
    $iconsBytes = (Get-Item -LiteralPath $iconsPath).Length
    if ($iconsBytes -gt 32768) {
        $failures.Add("Icons.xaml too large: $iconsBytes bytes > 32768 budget (R11 icon micro-gate)")
    }
}

function Require-Text {
    param(
        [string]$RelativePath,
        [string[]]$Markers
    )
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        $failures.Add("missing license file: $RelativePath")
        return
    }
    $text = Get-Content -LiteralPath $path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) {
        $failures.Add("empty license file: $RelativePath")
        return
    }
    foreach ($marker in $Markers) {
        if (-not $text.Contains($marker)) {
            $failures.Add("$RelativePath missing marker: $marker")
        }
    }
}

Require-Text 'LICENSE' @('MIT License', 'Apage contributors')
Require-Text 'licenses\LICENSE-MIT.txt' @('MIT License')
Require-Text 'licenses\LICENSE-MPL-2.0.txt' @('Mozilla Public License Version 2.0')
Require-Text 'licenses\LICENSE-CC-BY-SA-3.0.txt' @('Attribution-ShareAlike 3.0 Unported')
Require-Text 'licenses\THIRD-PARTY-NOTICES.txt' @(
    'The EasyList authors',
    'MPL-2.0',
    'CC BY-SA 3.0'
)

$privacyDir = Join-Path $root 'Apage.Core\Resources\Privacy'
$manifestPath = Join-Path $privacyDir 'privacy-assets.json'
$pslPath = Join-Path $privacyDir 'public_suffix_list.dat'
$rulesPath = Join-Path $privacyDir 'builtin-rules.txt'
foreach ($path in @($manifestPath, $pslPath, $rulesPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        $failures.Add("missing privacy asset: $path")
    }
}

$manifest = $null
if ($failures.Count -eq 0) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.psl.license -ne 'MPL-2.0') {
        $failures.Add("psl license must be MPL-2.0, found '$($manifest.psl.license)'")
    }
    if ($manifest.rules.license -ne 'CC BY-SA 3.0') {
        $failures.Add("rules license must be CC BY-SA 3.0, found '$($manifest.rules.license)'")
    }
    if ([string]::IsNullOrWhiteSpace($manifest.rules.attribution)) {
        $failures.Add('rules attribution is missing')
    }
    if (@($manifest.rules.sources).Count -eq 0) {
        $failures.Add('rule snapshot has no sources')
    }
    foreach ($source in @($manifest.rules.sources)) {
        if ([string]::IsNullOrWhiteSpace($source.url) -or
            [string]::IsNullOrWhiteSpace($source.sha256) -or
            [string]::IsNullOrWhiteSpace($source.license)) {
            $failures.Add("source metadata incomplete: $($source.name)")
        }
    }

    $pslHash = (Get-FileHash -LiteralPath $pslPath -Algorithm SHA256).Hash
    $rulesHash = (Get-FileHash -LiteralPath $rulesPath -Algorithm SHA256).Hash
    if ($pslHash -ne $manifest.psl.sha256) {
        $failures.Add('PSL hash does not match privacy-assets.json')
    }
    if ($rulesHash -ne $manifest.rules.sha256) {
        $failures.Add('rule snapshot hash does not match privacy-assets.json')
    }
}

# Legal texts must be embedded, otherwise a single-EXE distribution would not
# contain the notices it is required to carry.
$coreCsproj = Get-Content -LiteralPath (Join-Path $root 'Apage.Core\Apage.Core.csproj') -Raw
foreach ($logical in @(
    'Apage.Core.Resources.Privacy.THIRD-PARTY-NOTICES.txt',
    'Apage.Core.Resources.Privacy.LICENSE-MIT.txt',
    'Apage.Core.Resources.Privacy.LICENSE-MPL-2.0.txt',
    'Apage.Core.Resources.Privacy.LICENSE-CC-BY-SA-3.0.txt'
)) {
    if (-not $coreCsproj.Contains($logical)) {
        $failures.Add("Core csproj does not embed $logical")
    }
}

Write-Host ''
Write-Host '===== License check =====' -ForegroundColor Cyan
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "FAIL: $failure" -ForegroundColor Red
    }
    Write-Host "LICENSE CHECK FAILED ($($failures.Count) violation(s))" -ForegroundColor Red
    exit 1
}

Write-Host "Project: MIT"
Write-Host "PSL    : $($manifest.psl.license) / $($manifest.psl.version)"
Write-Host "Rules  : $($manifest.rules.license) / $(@($manifest.rules.sources).Count) sources / $($manifest.rules.acceptedRules) rules"
Write-Host "Notices: embedded in single-file build"
Write-Host 'LICENSE CHECK PASSED' -ForegroundColor Green
exit 0

#Requires -Version 5.1
<#
.SYNOPSIS
    Developer-only: fetch upstream privacy sources and regenerate the embedded snapshot.

.DESCRIPTION
    This script is never called by Apage at runtime. It performs explicit network
    requests only when a developer runs it. The app itself continues to make zero
    background requests for PSL or rule updates.

    Sources:
      - Public Suffix List (MPL-2.0)
      - EasyList / EasyPrivacy (CC BY-SA 3.0; upstream also GPL-3.0-or-later)
#>
[CmdletBinding()]
param(
    [string]$SourceDir = 'artifacts/snapshot-source',
    [string]$OutputDir = 'Apage.Core/Resources/Privacy'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sourcePath = Join-Path $root $SourceDir
$outputPath = Join-Path $root $OutputDir
New-Item -ItemType Directory -Path $sourcePath -Force | Out-Null

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$downloads = @{
    'public_suffix_list.dat' = 'https://publicsuffix.org/list/public_suffix_list.dat'
    'easylist.txt' = 'https://easylist.to/easylist/easylist.txt'
    'easyprivacy.txt' = 'https://easylist.to/easylist/easyprivacy.txt'
    'LICENSE-MPL-2.0.txt' = 'https://www.mozilla.org/media/MPL/2.0/index.txt'
    'LICENSE-CC-BY-SA-3.0.txt' = 'https://creativecommons.org/licenses/by-sa/3.0/legalcode.txt'
}

foreach ($entry in $downloads.GetEnumerator()) {
    $destination = Join-Path $sourcePath $entry.Key
    Write-Host "==> GET $($entry.Key)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $entry.Value -OutFile $destination -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'Apage-SnapshotBuilder/1.0' }
}

Copy-Item -LiteralPath (Join-Path $sourcePath 'LICENSE-MPL-2.0.txt') `
    -Destination (Join-Path $root 'licenses\LICENSE-MPL-2.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $sourcePath 'LICENSE-CC-BY-SA-3.0.txt') `
    -Destination (Join-Path $root 'licenses\LICENSE-CC-BY-SA-3.0.txt') -Force

dotnet run --project (Join-Path $root 'tools\SnapshotBuilder\SnapshotBuilder.csproj') `
    -c Release -- $sourcePath $outputPath
if ($LASTEXITCODE -ne 0) { throw "SnapshotBuilder failed (exit $LASTEXITCODE)." }

Write-Host "Snapshot regenerated. Review privacy-assets.json and licenses/THIRD-PARTY-NOTICES.txt." -ForegroundColor Green

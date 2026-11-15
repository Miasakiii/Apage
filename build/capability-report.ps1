#Requires -Version 5.1
<#
.SYNOPSIS
    Aggregate existing artifacts/ evidence into one freshness-tagged capability report.

.DESCRIPTION
    READ-ONLY. Does not launch Apage, does not copy artifacts, does not re-run any gate.

    This script does NOT replace the gates. build/check-*.ps1, package.ps1,
    verify-release.ps1 and startup-bench.ps1 stay the single source of pass/fail
    truth. This script answers a different question:

        For every number the README claims, which evidence file backs it right now,
        which binary was that evidence measured on, and which numbers have no
        mechanical evidence at all?

    Freshness is deliberately three-valued: current / stale / unknown. "unknown" is
    not "current" -- most evidence files predate any commit stamp, so treating them
    as fresh would repeat exactly the drift this report exists to expose.

    Artifact identity is compared too: an evidence file that describes a different
    EXE than the one on disk is reported as a mismatch even when its numbers look fine.

    Output:
        artifacts/capability-report.json   machine-readable
        artifacts/capability-report.md     human-readable

.EXAMPLE
    build\package.ps1
    build\capability-report.ps1

.EXAMPLE
    build\capability-report.ps1 -OutputDirectory artifacts\capability
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifactsFull = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$outFull = [System.IO.Path]::GetFullPath(
    $(if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }))

if (-not $outFull.StartsWith($artifactsFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay under artifacts/: $outFull"
}
if (-not [System.IO.Directory]::Exists($outFull)) {
    New-Item -ItemType Directory -Path $outFull -Force | Out-Null
}

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

function Get-Field {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $Default }
    if ($null -eq $prop.Value) { return $Default }
    return $prop.Value
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

# TRX is the one evidence format that is not JSON. Keep the parser tiny and
# return an object shaped like the JSON evidence so the loop stays uniform.
function Read-TrxCounters {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.Load($Path)
        # TRX carries a default XML namespace, so plain paths never match.
        $node = $doc.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $node) { return $null }

        $values = @{}
        foreach ($attribute in $node.Attributes) {
            $values[$attribute.Name] = $attribute.Value
        }
        $toInt = {
            param($name)
            if ($values.ContainsKey($name)) { return [int]$values[$name] }
            return $null
        }

        $generatedAt = $null
        $durationMs = $null
        $times = $doc.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
        if ($null -ne $times) {
            try {
                $start = [System.DateTimeOffset]::Parse([string]$times.start)
                $finish = [System.DateTimeOffset]::Parse([string]$times.finish)
                $durationMs = [math]::Round(($finish - $start).TotalMilliseconds, 0)
                $generatedAt = $finish.ToString('o')
            }
            catch { }
        }

        return [pscustomobject]@{
            generatedAt = $generatedAt
            total       = (& $toInt 'total')
            passed      = (& $toInt 'passed')
            failed      = (& $toInt 'failed')
            notExecuted = (& $toInt 'notExecuted')
            durationMs  = $durationMs
        }
    }
    catch {
        return $null
    }
}

function Invoke-Git {
    param([string[]]$Arguments)
    try {
        $out = & git -C $root @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return (($out | Out-String).Trim())
    }
    catch {
        return $null
    }
}

function Get-AgeDays {
    param($GeneratedAt)
    if ($null -eq $GeneratedAt) { return $null }
    try {
        $when = [System.DateTimeOffset]::Parse([string]$GeneratedAt)
        return [math]::Round(((Get-Date) - $when.LocalDateTime).TotalDays, 1)
    }
    catch {
        return $null
    }
}

function Format-Bool {
    param($Value)
    if ($null -eq $Value) { return '—' }
    if ($Value -eq $true) { return 'true' }
    if ($Value -eq $false) { return 'false' }
    return [string]$Value
}

function Format-RelativePath {
    param([string]$FullPath)
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    $rel = $FullPath.Replace($prefix, '')
    return $rel.Replace('\', '/')
}

function Get-Freshness {
    param($EvidenceCommit, $HeadCommit)

    if ([string]::IsNullOrWhiteSpace($EvidenceCommit)) {
        return [pscustomobject]@{ state = 'unknown'; commitsBehind = $null }
    }
    if ([string]::IsNullOrWhiteSpace($HeadCommit)) {
        return [pscustomobject]@{ state = 'unknown'; commitsBehind = $null }
    }
    if ($EvidenceCommit.Trim().ToLowerInvariant() -eq $HeadCommit.Trim().ToLowerInvariant()) {
        return [pscustomobject]@{ state = 'current'; commitsBehind = 0 }
    }
    $behind = Invoke-Git @('rev-list', '--count', "$EvidenceCommit..HEAD")
    $behindCount = if ([string]::IsNullOrWhiteSpace($behind)) { $null } else { [int]$behind }
    return [pscustomobject]@{ state = 'stale'; commitsBehind = $behindCount }
}

# ---------------------------------------------------------------------------
# repo state + the artifact currently on disk
# ---------------------------------------------------------------------------

$headCommit = Invoke-Git @('rev-parse', 'HEAD')
$headShort = Invoke-Git @('rev-parse', '--short', 'HEAD')
$headBranch = Invoke-Git @('branch', '--show-current')
$dirtyRaw = Invoke-Git @('status', '--porcelain')

$head = [ordered]@{
    commit = $headCommit
    short  = $headShort
    branch = $headBranch
    dirty  = (-not [string]::IsNullOrWhiteSpace($dirtyRaw))
}

$portableExe = Join-Path $artifactsFull 'portable\Apage.Portable.exe'
$exeBytes = $null
$exeSha = $null
if (Test-Path -LiteralPath $portableExe) {
    $exeBytes = (Get-Item -LiteralPath $portableExe).Length
    $exeSha = (Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash
}

# Signing changes bytes on purpose, so there are two legitimate baselines:
# the unsigned portable candidate and the signed copy. Evidence measured on
# either one is consistent; evidence matching neither is the real mismatch.
$signedExe = Join-Path $artifactsFull 'signed\Apage.Portable.exe'
$signedExeBytes = $null
$signedExeSha = $null
if (Test-Path -LiteralPath $signedExe) {
    $signedExeBytes = (Get-Item -LiteralPath $signedExe).Length
    $signedExeSha = (Get-FileHash -LiteralPath $signedExe -Algorithm SHA256).Hash
}

# ---------------------------------------------------------------------------
# evidence sources
#
# Each spec extracts (a) the facts it publishes and (b) the identity of the
# binary those facts were measured on. Identity is what lets the report say
# "this number is fine, but it is about a different build".
# ---------------------------------------------------------------------------

$specs = @(
    [pscustomobject]@{
        id          = 'package-metrics'
        path        = Join-Path $artifactsFull 'portable.metrics.json'
        label       = 'Single-file package metrics'
        commitField = 'gitCommit'
        facts = {
            param($d)
            [ordered]@{
                bytes              = Get-Field $d 'bytes'
                hardLimitBytes     = Get-Field $d 'hardLimitBytes'
                version            = Get-Field $d 'version'
                sha256             = Get-Field $d 'sha256'
                signatureStatus    = Get-Field $d 'signatureStatus'
                smokePassed        = Get-Field $d 'smokePassed'
                licenseCheckPassed = Get-Field $d 'licenseCheckPassed'
                worktreeDirty      = Get-Field $d 'worktreeDirty'
            }
        }
        identity = {
            param($d)
            [ordered]@{ bytes = Get-Field $d 'bytes'; sha256 = Get-Field $d 'sha256' }
        }
    }
    [pscustomobject]@{
        id          = 'startup-metrics'
        path        = Join-Path $artifactsFull 'release-startup\metrics.json'
        label       = 'Startup benchmark'
        commitField = $null
        facts = {
            param($d)
            [ordered]@{
                mode                        = Get-Field $d 'Mode'
                iterations                  = Get-Field $d 'Iterations'
                exeBytes                    = Get-Field $d 'ExeBytes'
                firstContentRenderedMs      = Get-Field $d 'FirstContentRenderedMs'
                warmMedianContentRenderedMs = Get-Field $d 'WarmMedianContentRenderedMs'
                warmMedianLimitMs           = Get-Field $d 'WarmMedianLimitMs'
                gatePassed                  = Get-Field $d 'GatePassed'
                gateSkipped                 = Get-Field $d 'GateSkipped'
            }
        }
        identity = {
            param($d)
            [ordered]@{ bytes = Get-Field $d 'ExeBytes'; sha256 = $null }
        }
    }
    [pscustomobject]@{
        id          = 'startup-waiver'
        path        = Join-Path $artifactsFull 'release-waiver-startup.json'
        label       = 'Startup gate waiver'
        commitField = 'gitCommit'
        facts = {
            param($d)
            $decision = Get-Field $d 'decision'
            $last = Get-Field $d 'lastMeasuredResult'
            [ordered]@{
                status                   = Get-Field $decision 'status'
                ownerStatement           = Get-Field $decision 'ownerStatement'
                carriedForward           = Get-Field $decision 'carriedForward'
                lastMeasuredWarmMedianMs = Get-Field $last 'warmMedianContentRenderedMs'
                lastMeasuredLimitMs      = Get-Field $last 'limitMs'
                lastMeasuredGatePassed   = Get-Field $last 'gatePassed'
                lastMeasuredCommit       = Get-Field $last 'measuredCommit'
                lastMeasuredSha256       = Get-Field $last 'measuredArtifactSha256'
            }
        }
        identity = {
            param($d)
            $artifact = Get-Field $d 'artifact'
            [ordered]@{ bytes = Get-Field $artifact 'bytes'; sha256 = Get-Field $artifact 'sha256' }
        }
    }
    [pscustomobject]@{
        id          = 'fault-matrix'
        path        = Join-Path $artifactsFull 'fault-matrix\results.json'
        label       = 'Fault matrix'
        commitField = $null
        facts = {
            param($d)
            $rows = @(Get-Field $d 'results' @())
            $passed = @($rows | Where-Object { (Get-Field $_ 'Passed') -eq $true }).Count
            $skipped = @($rows | Where-Object { (Get-Field $_ 'Skipped') -eq $true }).Count
            $failed = @($rows | Where-Object {
                (Get-Field $_ 'Skipped') -ne $true -and (Get-Field $_ 'Passed') -ne $true
            }).Count
            [ordered]@{
                scenarios       = $rows.Count
                passed          = $passed
                skipped         = $skipped
                failedOrUnknown = $failed
                skippedNames    = @($rows | Where-Object { (Get-Field $_ 'Skipped') -eq $true } |
                    ForEach-Object { Get-Field $_ 'Scenario' })
            }
        }
        identity = {
            param($d)
            [ordered]@{ bytes = Get-Field $d 'exeBytes'; sha256 = Get-Field $d 'exeSha256' }
        }
    }
    [pscustomobject]@{
        id          = 'legal-bundle'
        path        = Join-Path $artifactsFull 'legal\components.json'
        label       = 'Legal bundle'
        commitField = $null
        facts = {
            param($d)
            $components = @(Get-Field $d 'components' @())
            [ordered]@{
                projectLicense = Get-Field $d 'projectLicense'
                components     = $components.Count
                names          = @($components | ForEach-Object { Get-Field $_ 'name' })
            }
        }
        identity = { param($d) [ordered]@{ bytes = $null; sha256 = $null } }
    }
    [pscustomobject]@{
        id          = 'userscript-spike'
        path        = Join-Path $artifactsFull 'user-script-injection-results.json'
        label       = 'User-script injection spike'
        commitField = $null
        facts = {
            param($d)
            $assertions = @(Get-Field $d 'assertions' @())
            [ordered]@{
                passed           = Get-Field $d 'passed'
                runtimeVersion   = Get-Field $d 'runtimeVersion'
                assertions       = $assertions.Count
                failedAssertions = @($assertions | Where-Object { (Get-Field $_ 'Pass') -ne $true }).Count
            }
        }
        identity = { param($d) [ordered]@{ bytes = $null; sha256 = $null } }
    }
    [pscustomobject]@{
        id          = 'core-tests'
        path        = Join-Path $artifactsFull 'test-results\core-tests.trx'
        label       = 'Core unit tests (TRX)'
        format      = 'trx'
        commitField = $null
        facts = {
            param($d)
            [ordered]@{
                total       = Get-Field $d 'total'
                passed      = Get-Field $d 'passed'
                failed      = Get-Field $d 'failed'
                notExecuted = Get-Field $d 'notExecuted'
                durationMs  = Get-Field $d 'durationMs'
            }
        }
        identity = { param($d) [ordered]@{ bytes = $null; sha256 = $null } }
    }
    [pscustomobject]@{
        id          = 'release-verification'
        path        = Join-Path $artifactsFull 'signed\release-verification.json'
        label       = 'Post-signing acceptance'
        commitField = $null
        facts = {
            param($d)
            [ordered]@{
                releaseReady   = Get-Field $d 'releaseReady'
                blockerCount   = @(Get-Field $d 'blockers' @()).Count
                blockerText    = @(Get-Field $d 'blockers' @())
                sha256PostSign = Get-Field $d 'sha256PostSign'
            }
        }
        identity = {
            param($d)
            [ordered]@{ bytes = Get-Field $d 'bytes'; sha256 = Get-Field $d 'sha256PostSign' }
        }
    }
)

$evidence = New-Object System.Collections.ArrayList

foreach ($spec in $specs) {
    $specFormat = 'json'
    $formatProp = $spec.PSObject.Properties['format']
    if ($null -ne $formatProp) { $specFormat = $formatProp.Value }

    $data = if ($specFormat -eq 'trx') { Read-TrxCounters $spec.path } else { Read-JsonFile $spec.path }
    $entry = [ordered]@{
        id            = $spec.id
        label         = $spec.label
        path          = (Format-RelativePath $spec.path)
        present       = ($null -ne $data)
        generatedAt   = Get-Field $data 'generatedAt' (Get-Field $data 'GeneratedAt')
        commit        = if ($spec.commitField) { Get-Field $data $spec.commitField } else { $null }
        freshness     = 'unknown'
        commitsBehind = $null
        ageDays       = $null
        artifact      = $null
        artifactMatch = $null
        facts         = [ordered]@{}
    }

    if ($null -ne $data) {
        $freshness = Get-Freshness -EvidenceCommit $entry.commit -HeadCommit $headCommit
        $entry.freshness = $freshness.state
        $entry.commitsBehind = $freshness.commitsBehind
        $entry.ageDays = Get-AgeDays $entry.generatedAt
        $entry.facts = & $spec.facts $data

        $ident = & $spec.identity $data
        $entry.artifact = $ident

        # sha256 wins when both sides have one; bytes is the fallback signal.
        if ($null -ne $ident) {
            $matchTarget = $null
            if (-not [string]::IsNullOrWhiteSpace($ident['sha256'])) {
                $candidateSha = $ident['sha256'].Trim().ToUpperInvariant()
                if (-not [string]::IsNullOrWhiteSpace($exeSha) -and $candidateSha -eq $exeSha.ToUpperInvariant()) {
                    $matchTarget = 'portable'
                }
                elseif (-not [string]::IsNullOrWhiteSpace($signedExeSha) -and $candidateSha -eq $signedExeSha.ToUpperInvariant()) {
                    $matchTarget = 'signed'
                }
            }
            elseif ($null -ne $ident['bytes']) {
                if ($null -ne $exeBytes -and [int64]$ident['bytes'] -eq [int64]$exeBytes) {
                    $matchTarget = 'portable'
                }
                elseif ($null -ne $signedExeBytes -and [int64]$ident['bytes'] -eq [int64]$signedExeBytes) {
                    $matchTarget = 'signed'
                }
            }
            $entry.artifactMatch = $matchTarget
        }
    }

    [void]$evidence.Add([pscustomobject]$entry)
}

# ---------------------------------------------------------------------------
# live observations (read-only, no process started)
# ---------------------------------------------------------------------------

$observations = [ordered]@{
    portableExePresent     = ($null -ne $exeBytes)
    portableExeBytes       = $exeBytes
    portableExeSha256      = $exeSha
    signedExePresent       = ($null -ne $signedExeBytes)
    signedExeBytes         = $signedExeBytes
    signedExeSha256        = $signedExeSha
    signedArtifactsPresent = (Test-Path -LiteralPath (Join-Path $artifactsFull 'signed'))
}

# ---------------------------------------------------------------------------
# claims with no mechanical evidence
#
# Keep this list honest and short. Every entry names what would produce real
# evidence. Add an entry whenever README quotes a number no artifact can back.
# ---------------------------------------------------------------------------

$claimCandidates = @(
    [pscustomobject]@{
        claim             = 'exFAT portable-media matrix'
        reason            = 'The scenario is skipped. Creating an exFAT volume needs administrator rights (Format-Volume / New-VHD); this host has no removable media and no elevated session.'
        evidenceToProduce = 'artifacts/fault-matrix-run.ps1 -ExfatDrive <letter>: (on an elevated session or a real exFAT USB volume)'
        satisfiedBy       = $null
    }
    [pscustomobject]@{
        claim             = 'Post-signing acceptance (signed single binary)'
        reason            = 'artifacts/signed/release-verification.json is absent; nothing has been signed yet.'
        evidenceToProduce = 'build/verify-signature.ps1 + build/verify-release.ps1 on the signed artifact'
        satisfiedBy       = 'release-verification'
    }
)

# A claim leaves the list as soon as its backing evidence file exists. Nothing
# here judges whether that evidence is *good* -- that is what the Facts and
# Contradictions sections are for.
$uncovered = @($claimCandidates | Where-Object {
        $needs = $_.satisfiedBy
        if ([string]::IsNullOrWhiteSpace($needs)) { return $true }
        $hit = @($evidence | Where-Object { $_.id -eq $needs -and $_.present })
        return ($hit.Count -eq 0)
    })

# ---------------------------------------------------------------------------
# contradictions between evidence files
#
# A clean-looking number can still contradict another file. Report the pair
# instead of picking a winner: resolving it is an owner decision, not a
# script decision. The script never edits the waiver.
# ---------------------------------------------------------------------------

$conflicts = New-Object System.Collections.ArrayList
$waiverEntry = $evidence | Where-Object { $_.id -eq 'startup-waiver' -and $_.present } | Select-Object -First 1
$benchEntry = $evidence | Where-Object { $_.id -eq 'startup-metrics' -and $_.present } | Select-Object -First 1

if ($null -ne $waiverEntry -and $null -ne $benchEntry) {
    if ($waiverEntry.facts['status'] -eq 'deferred-by-owner' -and $benchEntry.facts['gatePassed'] -eq $true) {
        [void]$conflicts.Add([pscustomobject]@{
            kind                  = 'startup-gate'
            detail                = 'Waiver records the startup gate as deferred, while the newest benchmark passed it on a non-idle host. Passing on this host does not satisfy the waiver conditions.'
            waiverStatus          = $waiverEntry.facts['status']
            benchmarkWarmMedianMs = $benchEntry.facts['warmMedianContentRenderedMs']
            benchmarkLimitMs      = $benchEntry.facts['warmMedianLimitMs']
            suggestedAction       = 'Keep the waiver until five first/warm starts are recorded on a quiet reference machine, with CPU, power plan, Windows and WebView2 versions noted (conditionsToClose in the waiver file). This script will not edit the waiver.'
        })
    }
}

# ---------------------------------------------------------------------------
# assemble + emit
# ---------------------------------------------------------------------------

$staleCount = @($evidence | Where-Object { $_.freshness -eq 'stale' }).Count
$missingCount = @($evidence | Where-Object { -not $_.present }).Count
$mismatchCount = @($evidence | Where-Object {
        $_.present -and $null -ne $_.artifact -and
        (($null -ne $_.artifact['bytes']) -or (-not [string]::IsNullOrWhiteSpace($_.artifact['sha256']))) -and
        $null -eq $_.artifactMatch
    }).Count

$report = [ordered]@{
    generatedAt     = (Get-Date).ToString('o')
    generator       = 'build/capability-report.ps1'
    purpose         = 'Map capability claims to the evidence files that back them. Not a gate.'
    head            = $head
    summary         = [ordered]@{
        evidenceSources      = $evidence.Count
        stale                = $staleCount
        missing              = $missingCount
        artifactMismatches   = $mismatchCount
        uncoveredClaims      = $uncovered.Count
        conflicts            = $conflicts.Count
    }
    evidence        = $evidence.ToArray()
    observations    = $observations
    uncoveredClaims = $uncovered
    conflicts       = $conflicts.ToArray()
    gateAuthority   = @(
        'build/check-redlines.ps1'
        'build/check-licenses.ps1'
        'build/smoke.ps1'
        'build/package.ps1'
        'build/verify-release.ps1'
    )
}

$jsonPath = Join-Path $outFull 'capability-report.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

# ---- markdown ----

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Apage capability report')
$lines.Add('')
$lines.Add("Generated: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))  |  HEAD: ``$($head.short)`` on ``$($head.branch)``  |  dirty: ``$(Format-Bool $head.dirty)``")
$lines.Add('')
$lines.Add('> This report maps claims to evidence. It is **not** a gate. Pass/fail authority stays with')
$lines.Add('> `check-redlines.ps1`, `check-licenses.ps1`, `smoke.ps1`, `package.ps1` and `verify-release.ps1`.')
$lines.Add('')

$lines.Add('## Evidence')
$lines.Add('')
$lines.Add('| Source | Present | Generated | Commit | Freshness | Artifact |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($e in $evidence) {
    $presentText = if ($e.present) { 'yes' } else { '**missing**' }
    $commitText = if ([string]::IsNullOrWhiteSpace($e.commit)) {
        '—'
    }
    else {
        '`' + $e.commit.Substring(0, [Math]::Min(7, $e.commit.Length)) + '`'
    }
    $freshText = switch ($e.freshness) {
        'current' { 'current' }
        'stale' {
            if ($null -ne $e.commitsBehind) { "**stale** ($($e.commitsBehind) behind)" } else { '**stale**' }
        }
        default { 'unknown' }
    }
    $hasIdentity = ($null -ne $e.artifact) -and
        (($null -ne $e.artifact['bytes']) -or (-not [string]::IsNullOrWhiteSpace($e.artifact['sha256'])))
    $matchText = if (-not $e.present -or -not $hasIdentity) { '—' } elseif ($null -eq $e.artifactMatch) { '**none**' } elseif ($e.artifactMatch -eq 'portable') { 'portable' } else { 'signed' }
    $genText = if ([string]::IsNullOrWhiteSpace($e.generatedAt)) { '—' } else { $e.generatedAt }
    $lines.Add("| $($e.label) | $presentText | $genText | $commitText | $freshText | $matchText |")
}
$lines.Add('')

$lines.Add('## Facts')
$lines.Add('')
foreach ($e in ($evidence | Where-Object { $_.present })) {
    $lines.Add("### $($e.label)")
    $lines.Add('')
    $lines.Add('| Key | Value |')
    $lines.Add('|---|---|')
    foreach ($key in $e.facts.Keys) {
        $value = $e.facts[$key]
        if ($value -is [System.Array]) {
            $value = if ($value.Count -eq 0) { '[]' } else { ($value -join ', ') }
        }
        $lines.Add("| $key | $value |")
    }
    $lines.Add('')
}

$lines.Add('## Artifact identity')
$lines.Add('')
$lines.Add("Unsigned: ``artifacts/portable/Apage.Portable.exe`` -> $(if ($null -ne $exeBytes) { "$exeBytes bytes / $exeSha" } else { 'absent' })")
$lines.Add("Signed:   ``artifacts/signed/Apage.Portable.exe`` -> $(if ($null -ne $signedExeBytes) { "$signedExeBytes bytes / $signedExeSha" } else { 'absent' })")
$lines.Add('')
$lines.Add('| Source | Bytes | SHA-256 | Matches |')
$lines.Add('|---|---|---|---|')
foreach ($e in ($evidence | Where-Object { $_.present -and $null -ne $_.artifact -and ($null -ne $_.artifact.bytes -or -not [string]::IsNullOrWhiteSpace($_.artifact.sha256)) })) {
    $b = if ($null -eq $e.artifact.bytes) { '—' } else { $e.artifact.bytes }
    $s = if ([string]::IsNullOrWhiteSpace($e.artifact.sha256)) { '—' } else { $e.artifact.sha256 }
    $m = if ($null -eq $e.artifactMatch) { '**none**' } elseif ($e.artifactMatch -eq 'portable') { 'portable' } else { 'signed' }
    $lines.Add("| $($e.label) | $b | $s | $m |")
}
$lines.Add('')

$lines.Add('## Live observations')
$lines.Add('')
foreach ($key in $observations.Keys) {
    $lines.Add("- ``$key``: $($observations[$key])")
}
$lines.Add('')

$lines.Add('## Claims without mechanical evidence')
$lines.Add('')
foreach ($u in $uncovered) {
    $lines.Add("- **$($u.claim)**")
    $lines.Add("  - Why: $($u.reason)")
    $lines.Add("  - Would be produced by: ``$($u.evidenceToProduce)``")
}
$lines.Add('')

$lines.Add('## Contradictions between evidence files')
$lines.Add('')
if ($conflicts.Count -eq 0) {
    $lines.Add('None detected.')
}
else {
    foreach ($c in $conflicts) {
        $lines.Add("- **$($c.kind)**: $($c.detail)")
        foreach ($key in $c.PSObject.Properties.Name) {
            if ($key -in @('kind', 'detail', 'suggestedAction')) { continue }
            $lines.Add("  - ``$key``: $($c.$key)")
        }
        $lines.Add("  - Owner action: $($c.suggestedAction)")
    }
}
$lines.Add('')

$mdPath = Join-Path $outFull 'capability-report.md'
$lines.Add('---')
$lines.Add('')
$lines.Add('Machine-readable twin: `capability-report.json`')
Set-Content -LiteralPath $mdPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

Write-Host ''
Write-Host '===== Capability report =====' -ForegroundColor Cyan
Write-Host "HEAD             : $($head.short) ($($head.branch)) dirty=$(Format-Bool $head.dirty)"
Write-Host "Evidence sources : $($evidence.Count) (stale=$staleCount, missing=$missingCount, artifact-mismatch=$mismatchCount)"
Write-Host "Uncovered claims : $($uncovered.Count)"
Write-Host "Contradictions   : $($conflicts.Count)"
Write-Host ''
foreach ($e in $evidence) {
    $state = if (-not $e.present) { 'MISSING' } elseif ($e.freshness -eq 'stale') { 'STALE  ' } elseif ($e.freshness -eq 'current') { 'current' } else { 'unknown' }
    $extra = if (($null -eq $e.artifactMatch) -and ($null -ne $e.artifact) -and
        (($null -ne $e.artifact['bytes']) -or (-not [string]::IsNullOrWhiteSpace($e.artifact['sha256'])))) {
        '  <- matches neither artifact'
    }
    else { '' }
    Write-Host ("  [{0}] {1}{2}" -f $state, $e.label, $extra)
}
Write-Host ''
Write-Host "Report: $mdPath"
Write-Host "JSON  : $jsonPath"

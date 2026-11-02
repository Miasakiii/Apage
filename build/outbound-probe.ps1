#Requires -Version 5.1
<#
.SYNOPSIS
    Outbound probe for Apage.Portable: asserts that a --smoke run makes zero
    non-whitelisted outbound TCP connections from Apage's WebView2 processes.

.DESCRIPTION
    Product red line (AGENTS.md): zero telemetry, zero background requests.
    The only two approved host-side outbound paths are RuleUpdateClient.cs
    (manual rule update, button-triggered) and FaviconFetcher.cs (one-shot
    engine favicon fetch, whitelist hosts, settings can disable). In --smoke
    mode BOTH paths are silent by construction: the app forces
    FetchEngineIcons=false (App.xaml.cs) and rule update is manual-only.
    Therefore a smoke run must show zero remote TCP connections from
    Apage-owned msedgewebview2.exe processes.

    The gate:
      * samples Get-NetTCPConnection for msedgewebview2.exe processes whose
        command line names Apage.Portable.exe (host filter - other WebView2
        apps on the machine are ignored), every 400ms until the app exits;
      * loopback / private / link-local endpoints are ignored (a local proxy
        terminates there - see LIMITATIONS);
      * any other remote endpoint is a violation unless its IP is listed in
        $AllowedRemoteIps below.

    DISCLOSURE (must not be silently dropped): Microsoft documents that
    WebView2 collects performance/reliability diagnostic data "regardless of
    Windows diagnostic data settings"; the host app can control only
    SmartScreen and custom crash reporting. Such Runtime endpoints are NOT
    under Apage's control. If a reference machine or CI runner observes them,
    add the endpoint IP to $AllowedRemoteIps with the Runtime version and
    date as an EXPLICIT, REVIEWED action - never by widening the filter or
    by switching to -WarnOnly to make the gate green.

    LIMITATIONS (honest scope of this gate):
      * it observes local TCP connections only; traffic sent through a local
        system proxy terminates on loopback and is invisible here;
      * UDP/DNS is not sampled;
      * reverse DNS (PTR) is best-effort for the report only.

    Exit codes: 0 clean; 1 setup failure (exe missing / launch failure);
    5 outbound violation; 6 probe timeout (app did not exit).

.EXAMPLE
    build\outbound-probe.ps1                       # Release (default), strict
    build\outbound-probe.ps1 -WarnOnly             # report but do not fail
    build\outbound-probe.ps1 -ExePath <path> -EvidencePath <file>
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ExePath,
    [int]$TimeoutSec = 90,
    [int]$SampleIntervalMs = 400,
    [string]$EvidencePath,
    [switch]$WarnOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = if ([string]::IsNullOrWhiteSpace($ExePath)) {
    Join-Path $root "Apage.Portable\bin\$Configuration\Apage.Portable.exe"
} else {
    [System.IO.Path]::GetFullPath($ExePath)
}

# ---------------------------------------------------------------------------
# Explicit allowlist for WebView2 Runtime's own diagnostic endpoints.
# Empty = this gate has never needed one (observed clean on Runtime
# 156.0.4314.58, 2026-11-02). Adding an entry requires the Runtime version,
# the date, and a one-line reason; it is a reviewed disclosure action.
# ---------------------------------------------------------------------------
$AllowedRemoteIps = @(
)

# Reference for the report: Apage's own two approved outbound paths.
#   RuleUpdateClient.cs  -> easylist.to (manual rule update)
#   FaviconFetcher.cs    -> SearchEngineHosts whitelist (one-shot engine icons)
$ApageOutboundHosts = @(
    'easylist.to'
    'www.baidu.com', 'baidu.com'
    'www.bing.com', 'cn.bing.com', 'bing.com'
    'www.google.com', 'google.com'
    'duckduckgo.com'
)

if (-not (Test-Path $exe)) {
    Write-Host "OUTBOUND PROBE FAILED: exe not found: $exe" -ForegroundColor Red
    Write-Host "Build first: build\build.ps1 -Configuration $Configuration"
    exit 1
}

function Test-PrivateOrLoopback([string]$ip) {
    if ($ip -eq '127.0.0.1' -or $ip -eq '::1') { return $true }
    if ($ip -like '127.*') { return $true }
    if ($ip -like '10.*' -or $ip -like '192.168.*') { return $true }
    if ($ip -like '169.254.*' -or $ip -like 'fe80:*') { return $true }
    if ($ip -like 'fc*' -or $ip -like 'fd*') { return $true }   # IPv6 ULA
    if ($ip -like '172.*') {
        $second = 0
        if ([int]::TryParse($ip.Split('.')[1], [ref]$second) -and $second -ge 16 -and $second -le 31) { return $true }
    }
    return $false
}

function Get-PtrHost([string]$ip) {
    try {
        $entry = [System.Net.Dns]::GetHostEntry($ip)
        if ($entry -and $entry.HostName) { return $entry.HostName }
    } catch { }
    return $null
}

Write-Host "==> Outbound probe: $exe --smoke (timeout ${TimeoutSec}s, sample ${SampleIntervalMs}ms)" -ForegroundColor Cyan
$observations = New-Object System.Collections.Generic.List[string]
$proc = Start-Process -FilePath $exe -ArgumentList @('--smoke') -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$appExited = $false

try {
    while ($true) {
        if ($proc.HasExited) { $appExited = $true; break }
        if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { break }

        $webview = Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like '*Apage.Portable.exe*' }
        $pids = @($webview | ForEach-Object { $_.ProcessId })
        if ($pids.Count -gt 0) {
            $conns = Get-NetTCPConnection -OwningProcess $pids -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.State -in @('Established', 'SynSent') -and
                    -not (Test-PrivateOrLoopback $_.RemoteAddress)
                }
            foreach ($c in @($conns)) {
                $observations.Add("$($c.RemoteAddress):$($c.RemotePort) state=$($_.State) pid=$($c.OwningProcess) t=$([math]::Round($sw.Elapsed.TotalSeconds,1))s")
            }
        }
        Start-Sleep -Milliseconds $SampleIntervalMs
    }
} finally {
    if (-not $appExited) {
        taskkill /PID $proc.Id /T /F *> $null
        Start-Sleep -Seconds 1
        taskkill /T /F /IM Apage.Portable.exe *> $null
    }
}

if (-not $appExited) {
    Write-Host "OUTBOUND PROBE FAILED: app did not exit within ${TimeoutSec}s (killed)." -ForegroundColor Red
    exit 6
}

$unique = @($observations | Sort-Object -Unique)
$violations = @($unique | Where-Object {
    $ip = ($_ -split ':')[0]
    $AllowedRemoteIps -notcontains $ip
})

$report = New-Object System.Collections.Generic.List[string]
$report.Add("outbound-probe $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') exe=$exe")
$report.Add("unique remote endpoints: $($unique.Count)")
$report.Add("Apage own outbound paths (must be silent in smoke): $($ApageOutboundHosts -join ', ')")
foreach ($v in $violations) {
    $ip = ($v -split ':')[0]
    $ptr = Get-PtrHost $ip
    $report.Add("VIOLATION $v ptr=$ptr")
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $report | Out-File -FilePath $EvidencePath -Encoding utf8
    Write-Host "Evidence: $EvidencePath"
}

if ($violations.Count -gt 0) {
    foreach ($v in $violations) { Write-Host "  VIOLATION $v" -ForegroundColor Red }
    if ($WarnOnly) {
        Write-Host "OUTBOUND PROBE WARNING: $($violations.Count) non-whitelisted remote endpoint(s) observed (-WarnOnly, not failing)." -ForegroundColor Yellow
        Write-Host "Reminder: WebView2 Runtime diagnostic endpoints are outside Apage's control; allowlisting them is an explicit reviewed action (see script header)." -ForegroundColor Yellow
        exit 0
    }
    Write-Host "OUTBOUND PROBE FAILED: $($violations.Count) non-whitelisted remote endpoint(s) from Apage's WebView2 processes." -ForegroundColor Red
    Write-Host "If these are WebView2 Runtime diagnostics (outside Apage's control), add them to `$AllowedRemoteIps with Runtime version + date as an explicit reviewed action - do NOT widen the filter or hide behind -WarnOnly." -ForegroundColor Red
    exit 5
}

Write-Host "OUTBOUND PROBE PASSED: zero non-whitelisted outbound connections from Apage's WebView2 processes (smoke mode)." -ForegroundColor Green
exit 0

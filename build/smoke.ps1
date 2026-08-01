#Requires -Version 5.1
<#
.SYNOPSIS
    Release-line smoke check for Apage.Portable: launches the built exe in
    --smoke mode and reports PASS/FAIL from its exit code.

.DESCRIPTION
    The app itself (SmokeCheck.cs) verifies three signals over the real
    startup path, then exits:
      1. app starts        (OnStartup completes)
      2. main window shows (ContentRendered fired)
      3. WebView2 inits    (current tab's CoreWebView2 ready)
    Exit codes: 0 pass; 2 in-app signal timeout; 3 single-instance conflict;
    4 WebView2 Runtime missing; anything else = startup crash.
    An outer timeout kills the process tree if it hangs before the in-app
    watchdog can even start. Run after a successful build (build\build.ps1);
    CI runs it right after the portable build step.

.EXAMPLE
    build\smoke.ps1                      # Release (default)
    build\smoke.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$TimeoutSec = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "Apage.Portable\bin\$Configuration\Apage.Portable.exe"

if (-not (Test-Path $exe)) {
    Write-Host "SMOKE FAILED: exe not found: $exe" -ForegroundColor Red
    Write-Host "Build first: build\build.ps1 -Configuration $Configuration"
    exit 1
}

Write-Host "==> Smoke: $exe --smoke (outer timeout ${TimeoutSec}s)" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exe -ArgumentList '--smoke' -PassThru

if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    # In-app watchdog never fired (hang before/inside OnStartup) - kill tree & fail.
    # taskkill /T also reaps msedgewebview2 children the host may have spawned.
    taskkill /PID $proc.Id /T /F *> $null
    Write-Host "SMOKE FAILED: process hung > ${TimeoutSec}s (outer timeout), killed." -ForegroundColor Red
    exit 1
}
$sw.Stop()

$code = $proc.ExitCode
$meaning = switch ($code) {
    0 { 'app started + main window rendered + WebView2 core ready' }
    2 { 'in-app watchdog timeout: window did not render or WebView2 core never became ready' }
    3 { 'single-instance conflict: another Apage using the same data directory is running' }
    4 { 'WebView2 Runtime not installed on this machine' }
    default { 'startup crash (exit 0x{0:X8})' -f $code }
}

if ($code -eq 0) {
    Write-Host ("SMOKE PASSED in {0:mm\:ss}: {1}" -f $sw.Elapsed, $meaning) -ForegroundColor Green
    exit 0
}
Write-Host "SMOKE FAILED (exit $code): $meaning" -ForegroundColor Red
exit 1

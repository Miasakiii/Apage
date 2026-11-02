#Requires -Version 5.1
<#
.SYNOPSIS
    Verify SHA-256 and Authenticode state for a release EXE.

.DESCRIPTION
    Used before signing (record NotSigned) and after SignPath signing with
    -RequireValid. Optionally compares the file hash to an expected value and
    writes a machine-readable verification report for the release record.

.EXAMPLE
    build\verify-signature.ps1 -ExePath artifacts\portable\Apage.Portable.exe

.EXAMPLE
    build\verify-signature.ps1 -ExePath artifacts\signed\Apage.Portable.exe `
      -ExpectedSha256 <hash> -RequireValid -JsonPath artifacts\signed\signature.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [string]$ExpectedSha256,
    [switch]$RequireValid,
    [string]$JsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$exe = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe)) {
    throw "EXE not found: $exe"
}

$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$hashMatches = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    $hashMatches = ($hash -eq $ExpectedSha256)
}

$signature = Get-AuthenticodeSignature -FilePath $exe
$status = $signature.Status.ToString()
$signer = $null
if ($null -ne $signature.SignerCertificate) {
    $signer = $signature.SignerCertificate.Subject
}
$timestampSigner = $null
if ($null -ne $signature.TimeStamperCertificate) {
    $timestampSigner = $signature.TimeStamperCertificate.Subject
}

$result = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    path = $exe
    bytes = (Get-Item -LiteralPath $exe).Length
    sha256 = $hash
    expectedSha256 = $ExpectedSha256
    expectedSha256Matches = $hashMatches
    signatureStatus = $status
    signatureMessage = $signature.StatusMessage
    signerSubject = $signer
    timestampSignerSubject = $timestampSigner
    valid = ($status -eq 'Valid')
}
if (-not [string]::IsNullOrWhiteSpace($JsonPath)) {
    $jsonFull = [System.IO.Path]::GetFullPath($JsonPath)
    $jsonDir = Split-Path -Parent $jsonFull
    if (-not [string]::IsNullOrWhiteSpace($jsonDir)) {
        New-Item -ItemType Directory -Path $jsonDir -Force | Out-Null
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonFull -Encoding utf8
}

Write-Host ''
Write-Host '===== Signature verification =====' -ForegroundColor Cyan
Write-Host "EXE      : $exe"
Write-Host "Bytes    : $($result.bytes)"
Write-Host "SHA-256  : $hash"
Write-Host "Signature: $status"
if (-not [string]::IsNullOrWhiteSpace($signer)) {
    Write-Host "Signer   : $signer"
}

if ($hashMatches -eq $false) {
    Write-Host 'SIGNATURE CHECK FAILED: SHA-256 does not match expected.' -ForegroundColor Red
    exit 1
}
if ($RequireValid -and $status -ne 'Valid') {
    Write-Host 'SIGNATURE CHECK FAILED: a valid Authenticode signature is required.' -ForegroundColor Red
    exit 1
}
if ($status -eq 'Valid') {
    Write-Host 'SIGNATURE CHECK PASSED: Authenticode Valid.' -ForegroundColor Green
}
else {
    Write-Host "Signature state recorded: $status (use -RequireValid after signing)." -ForegroundColor Yellow
}
exit 0

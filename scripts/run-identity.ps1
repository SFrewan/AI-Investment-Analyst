#requires -Version 5.1
<#
    SEC IDENTITY COMPLETION PASS.

    Switches the EDGAR connector on for the duration of this run only - the connector reads its
    own section while the container is being BUILT, earlier than a test factory's in-memory
    settings can reach, so appsettings' deliberate Enabled:false is what AddInfrastructure would
    otherwise see. Switched off again at the end.

    No EODHD variable is set and no billable call is possible from here.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host '  STOPPING. AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact.'
    exit 2
}

$env:Providers__SecEdgar__Enabled = 'true'
$env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
$env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
$env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

Write-Host ''
Write-Host '  contact address configured (value not printed).'
Write-Host '  EDGAR connector switched on for this run.'

$code = 0

try {
    $env:AIINV_UNIVERSE_IDENTITY = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~UniverseIdentityTests' `
        -LogName 'identity-stage.log' `
        -Label 'SEC identity completion, up to 367 free requests' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_UNIVERSE_IDENTITY -ErrorAction SilentlyContinue
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'identity-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== Identity outcome'

$report = Join-Path $root 'artifacts\verify\universe-identity.md'
if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'SEC calls used|Attempted|Succeeded|Refused|failed|Not requested|Already held|EODHD calls|confirmed|conflict|sec-only|sec-earlier|provisional|unmatched|SEC authority|PASS|FAIL|deferred|carry no ticker' } |
        Select-Object -First 40 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}

exit $code

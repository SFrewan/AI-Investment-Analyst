#requires -Version 5.1
<#
    FIVE-MEMBER RECOVERY - FINAL RECONCILIATION. READ ONLY.

    THIS SCRIPT DOES NOT WRITE TO ai_investment.

    No replay of any payload. No replay of any of the five members. No [] payload is replayed or
    even offered to the replay service - the tool does not resolve the replay service at all.
    No EODHD call, no SEC call, no acquisition, no authorisation created or consumed.
    Gate 6, coverage-gate6.json, SplitAdjustment, the sealed universe, src\ and tests\ are untouched.

    The only file written is the tool's own JSON record under artifacts\verify\.

    THE CONNECTION STRING IS NEVER PRINTED.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\recovery-reconcile\RecoveryReconcile.csproj'
$log = Join-Path $root 'artifacts\verify\reconcile-build.log'

$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\verify')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   FIVE-MEMBER RECOVERY - FINAL RECONCILIATION  (READ ONLY)'
Write-Host '   No replay. No provider. No acquisition. No database write.'
Write-Host '   Gate 6 is NOT modified, NOT re-evaluated and NOT resealed.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host '  STOPPING. AIINV_TEST_POSTGRES is not set.'
    exit 3
}

Write-Host '  Database configured (value not printed).'
Write-Host ''

if (-not (Test-Path -LiteralPath $tool)) {
    Write-Host ('  STOPPING. The tool project was not found: ' + $tool)
    exit 4
}

Write-Host '--- toolchain'
Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host '  target framework : net8.0'
Write-Host ''

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host '--- building the tool (Release, net8.0)'

& dotnet build $tool -c Release --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host '  FAIL  the tool did not build. NOTHING WAS RECONCILED.'
    Write-Host ''

    foreach ($line in (Get-Content -Path $log -Tail 60)) {
        if ($line -match 'error|Build FAILED') { Write-Host ('  ' + $line.Trim()) }
    }

    Write-Host ''
    Write-Host ('  Full build log: ' + $log)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

$exe = Join-Path $root 'tools\recovery-reconcile\bin\Release\net8.0\recovery-reconcile.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ('  STOPPING. The built executable was not found: ' + $exe)
    exit 7
}

& $exe $root
$code = $LASTEXITCODE

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '  READ ONLY. Nothing was replayed, acquired, authorised or written.'

exit $code

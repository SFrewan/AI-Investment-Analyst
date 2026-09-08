#requires -Version 5.1
<#
    PRODUCTION PROVENANCE MIGRATION - PREFLIGHT. READ ONLY.

    Describes ai_investment. Applies nothing.

    It does NOT run dotnet ef, does not construct a DbContext and does not touch the migration
    scripts. The tool it launches is built on raw Npgsql and issues SELECT statements only, so there
    is no migrator anywhere in its object graph to be invoked by accident.

    The existing guard in run-provenance-migrate.ps1 is untouched.

    NO EODHD. NO replay. NO authorisation. NO Gate 6 change.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\migration-preflight\MigrationPreflight.csproj'
$log = Join-Path $root 'artifacts\verify\migration-preflight-build.log'

$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\verify')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PRODUCTION PROVENANCE MIGRATION - PREFLIGHT  (READ ONLY)'
Write-Host '   Describes ai_investment. Applies nothing.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and [string]::IsNullOrWhiteSpace($env:AIINV_DESIGNTIME_DB)) {
    Write-Host '  STOPPING. No connection string is configured.'
    exit 3
}

Write-Host '  Connection strings are configured (values never printed).'
Write-Host ''
Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host ''
Write-Host '--- building the preflight tool (Release, net8.0)'

& dotnet build $tool -c Release --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host '  FAIL  the tool did not build. NO PREFLIGHT WAS RUN.'
    foreach ($line in (Get-Content -Path $log)) {
        if ($line -match 'error [A-Z]+[0-9]+') { Write-Host ('  ' + $line.Trim()) }
    }
    Write-Host ('  Full log: ' + $log)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

$exe = Join-Path $root 'tools\migration-preflight\bin\Release\net8.0\migration-preflight.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ('  STOPPING. The built executable was not found: ' + $exe)
    exit 7
}

& $exe $root
$code = $LASTEXITCODE

Write-Host ''
Write-Host '  READ ONLY. No migration was applied and no guard was changed.'

exit $code

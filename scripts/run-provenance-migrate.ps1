#requires -Version 5.1
<#
    PER-RUN REQUEST PROVENANCE - APPLY THE MIGRATION, THEN VERIFY.

    Applies 20260908141540_ProviderExchangeProvenance to the CONFIGURED DEVELOPMENT database only.

    IT REFUSES TO TOUCH ai_investment. That database holds the evidence this whole arc has been
    reconciling, and changing its schema is a separate decision that nobody has taken. If the
    resolved connection names it, this script stops without running anything.

    The migration is additive: one CreateTable, five CreateIndex, and a Down that drops only that
    table. No existing table is altered and no data is backfilled - the evidence it records can only
    be captured at request time, so there is nothing to backfill.

    NO provider call. NO acquisition. NO replay.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'AI-Investment-Analyst.sln'
$verify = Join-Path $root 'artifacts\verify'

$null = New-Item -ItemType Directory -Force -Path $verify

$applyLog = Join-Path $verify 'provenance-migrate-apply.log'
$listLog = Join-Path $verify 'provenance-migrate-list.log'
$focusedLog = Join-Path $verify 'provenance-migrate-focused.log'
$fullLog = Join-Path $verify 'provenance-migrate-release.log'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PER-RUN REQUEST PROVENANCE - MIGRATION + VERIFICATION'
Write-Host '   Additive only. Development database only. No provider call.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

# AIINV_TEST_POSTGRES FIRST, deliberately - the opposite order to scripts\add-migration.cmd.
#
# That script prefers AIINV_DESIGNTIME_DB, and on this machine AIINV_DESIGNTIME_DB names
# ai_investment: the database holding 1,077,380 observations and the whole recovery arc's evidence.
# Scaffolding a migration against it is harmless (nothing connects). APPLYING one is a decision
# about the evidence database, and this task was scoped to a development database. So the order is
# reversed here and the guard below stands behind it.
$cs = $env:AIINV_TEST_POSTGRES
if ([string]::IsNullOrWhiteSpace($cs)) { $cs = $env:AIINV_DESIGNTIME_DB }

if ([string]::IsNullOrWhiteSpace($cs)) {
    Write-Host '  STOPPING. No connection string is configured.'
    exit 3
}

# --- the guard that matters ---------------------------------------------------
# Parsed with a regex rather than DbConnectionStringBuilder: the generic builder's indexer throws
# for a key it has not seen, and a guard that throws before it can refuse is not a guard.
$target = ''
if ($cs -match '(?i)(^|;)\s*database\s*=\s*([^;]+)') { $target = $Matches[2].Trim() }

if ([string]::IsNullOrWhiteSpace($target)) {
    Write-Host '  STOPPING. The connection string does not name a database, so the guard cannot check it.'
    exit 5
}

Write-Host ('  target database : ' + $target)

if ($target -eq 'ai_investment') {
    Write-Host ''
    Write-Host '  STOPPING. The configured connection names ai_investment - the evidence database.'
    Write-Host '  Changing its schema is a separate decision and this task did not take it.'
    exit 4
}

Write-Host '  (ai_investment is NOT the target - refused by design if it were.)'
Write-Host ''

$env:Database__ConnectionString = $cs
$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

function Show([string]$path, [string]$pattern) {
    if (Test-Path -LiteralPath $path) {
        foreach ($line in (Get-Content -Path $path)) {
            if ($line -match $pattern) { Write-Host ('    ' + $line.Trim()) }
        }
    }
}

& dotnet tool restore 2>&1 | Out-Null

# ---------------------------------------------------------------- 1. apply
Write-Host '--- 1/4  applying the migration'

& dotnet ef database update `
    --project src\AI.Investment.Infrastructure `
    --startup-project src\AI.Investment.Api `
    --context AppDbContext `
    --configuration Release 2>&1 | Tee-Object -FilePath $applyLog | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host '  FAIL  the migration did not apply.'
    Show $applyLog 'error|Error|fail'
    Write-Host ('  Full log: ' + $applyLog)
    exit 6
}

Write-Host '  PASS  applied.'
Write-Host ''

# ---------------------------------------------------------------- 2. verify schema
Write-Host '--- 2/4  verifying the migration is applied and nothing is pending'

& dotnet ef migrations list `
    --project src\AI.Investment.Infrastructure `
    --startup-project src\AI.Investment.Api `
    --context AppDbContext `
    --configuration Release 2>&1 | Tee-Object -FilePath $listLog | Out-Null

$pending = @(Get-Content -Path $listLog | Where-Object { $_ -match '\(Pending\)' })

foreach ($line in (Get-Content -Path $listLog)) {
    if ($line -match 'ProviderExchangeProvenance|Pending') { Write-Host ('    ' + $line.Trim()) }
}

if ($pending.Count -ne 0) {
    Write-Host ''
    Write-Host ('  FAIL  ' + $pending.Count + ' migration(s) still pending after update.')
    exit 7
}

Write-Host '  PASS  no pending migrations.'
Write-Host ''

# ---------------------------------------------------------------- 3. focused tests again
Write-Host '--- 3/4  focused provenance tests, after the schema change'

& dotnet test $sln -c Release --nologo --filter 'FullyQualifiedName~ProviderExchangeProvenance' 2>&1 |
    Tee-Object -FilePath $focusedLog | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host '  FAIL  focused tests did not pass after the migration.'
    Show $focusedLog '^(Passed!|Failed!)|\[FAIL\]'
    exit 8
}

Show $focusedLog '^(Passed!|Failed!)'
Write-Host '  PASS  focused tests.'
Write-Host ''

# ---------------------------------------------------------------- 4. full Release suite
Write-Host '--- 4/4  full Release suite'

& dotnet test $sln -c Release --nologo 2>&1 | Tee-Object -FilePath $fullLog | Out-Null
$full = $LASTEXITCODE

Write-Host ''
Show $fullLog '^(Passed!|Failed!)'
Write-Host ''

Remove-Item Env:\Database__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

if ($full -ne 0) {
    Write-Host '  FAIL  the full Release suite did not pass.'
    Write-Host ('  Full log: ' + $fullLog)
    exit 9
}

Write-Host '  PASS  full Release suite.'
Write-Host ''
Write-Host '  ai_investment was NOT touched. No provider was contacted. Nothing was replayed.'

exit 0

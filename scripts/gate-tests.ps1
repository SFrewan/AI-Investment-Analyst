#requires -Version 5.1
<#
    THE THREE GATES - deterministic tests only.

    No provider call, no cycle, no cooldown wait. The gated operations (the subscription probe and
    the ledger repair) are separate scripts; nothing here sets their environment variables.
#>

[CmdletBinding()]
param([string]$Filter = 'FullyQualifiedName~IngestionLedgerConsistencyTests|FullyQualifiedName~CorporateActionsPathTests|FullyQualifiedName~SharedOwnedInstanceTests',
      [string]$LogName = 'gates-tests.log',
      [string]$Label = 'gate tests: ledger consistency + corporate actions')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out $LogName

# The backfill must stay off whatever the shell inherited - it is the expensive one, and nothing
# in this script should ever start it. The probe and the repair gates are set deliberately by their
# own wrappers, so they are left alone here rather than cleared out from under them.
Remove-Item Env:\AIINV_BACKFILL -ErrorAction SilentlyContinue

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) { . $localSettings }
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host '  WARNING: AIINV_TEST_POSTGRES is not set. Database-backed proofs will SKIP.'
}
else { Write-Host '  Integration database configured (value not printed).' }

Write-Host ''
Write-Host ('--- ' + $Label)
Write-Host ''

$code = 0
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

try {
    & dotnet test (Join-Path $root 'AI-Investment-Analyst.sln') `
        -c Release --no-build --nologo --filter $Filter 2>&1 |
        Tee-Object -FilePath $log | Out-Null

    $code = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previous }

$tail = @()
if (Test-Path $log) { $tail = @(Get-Content -Path $log -Tail 120) }

foreach ($t in $tail) {
    if ($t -match 'Passed!|Failed!|Passed:|Failed:|Skipped:|error |Assert\.|\[FAIL\]|Message:|Skipped ') {
        Write-Host ('  ' + $t.Trim())
    }
}

if ($code -eq 0) { Write-Host ('  PASS  ' + $Label) }
else { Write-Host ('  FAIL  ' + $Label + '  (exit ' + [string]$code + ')') }

Write-Host ''
Write-Host ('Full log: ' + $log)
exit $code

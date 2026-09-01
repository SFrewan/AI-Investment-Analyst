#requires -Version 5.1
<#
    BLOCK 2B - REGRESSION GATE AFTER THE BACKFILL.

    Build and test only. The backfill itself is gated on AIINV_BACKFILL, which this script does
    NOT set, so HistoryBackfillTests reports as skipped and no provider call is made.

    No cycle, no cooldown wait, no EODHD request.
#>

[CmdletBinding()]
param([switch]$BuildOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'block2b.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z') }

function Invoke-Dotnet([string]$label, [string[]]$dotnetArgs, [string]$logName) {
    $file = Join-Path $out $logName
    Say ''
    Say ('--- ' + $label + '   [' + (Stamp) + ']')
    Say ('  dotnet ' + ($dotnetArgs -join ' '))

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 0
    try {
        & dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $file | Out-Null
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }

    $tail = @()
    if (Test-Path $file) { $tail = @(Get-Content -Path $file -Tail 60) }

    $allSkipped = $false
    foreach ($t in $tail) {
        if ($t -match 'Skipped!\s+- Failed:\s+0, Passed:\s+0, Skipped:\s+[1-9]') { $allSkipped = $true }
    }

    if ($allSkipped -and $code -eq 0) { Say '  NOT PROVED  every test in this phase was SKIPPED.'; $code = 3 }
    elseif ($code -eq 0) { Say ('  PASS  ' + $label) }
    else { Say ('  FAIL  ' + $label + '  (exit ' + [string]$code + ')') }

    foreach ($t in $tail) {
        if ($t -match 'error |warning |Failed!|Passed!|Passed:|Failed:|Skipped:|Build succeeded|Build FAILED') {
            Say ('        ' + $t.Trim())
        }
    }

    Say ('        full output: ' + $file)
    Save-Log
    return [pscustomobject]@{ Label = $label; Code = $code; Log = $file }
}

Say '==============================================================='
Say ' BLOCK 2B - REGRESSION GATE AFTER THE BACKFILL'
Say (' started : ' + (Stamp))
Say ' AIINV_BACKFILL is deliberately NOT set: the backfill test skips'
Say ' no EODHD request, no cycle, no cooldown wait'
Say '==============================================================='

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) { . $localSettings }
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Say '  WARNING: AIINV_TEST_POSTGRES is not set. Database-backed proofs will SKIP.'
}
else { Say '  Integration database configured (value not printed).' }

$results = New-Object 'System.Collections.Generic.List[object]'

# Guarantee the gate is shut for this whole script, whatever the shell inherited.
Remove-Item Env:\AIINV_BACKFILL -ErrorAction SilentlyContinue

$build = Invoke-Dotnet 'Release build (warnings are errors)' @(
    'build'; (Join-Path $root 'AI-Investment-Analyst.sln'); '-c'; 'Release'; '--nologo'
) 'block2b-build.log'
$null = $results.Add($build)

if ($build.Code -ne 0) {
    Say ''
    Say ' STOPPING: the build failed.'
    Save-Log
    exit 1
}

if (-not $BuildOnly) {
    $focused = Invoke-Dotnet 'focused: the paths Block 2B touched' @(
        'test'; (Join-Path $root 'AI-Investment-Analyst.sln'); '-c'; 'Release'; '--no-build'; '--nologo'
        '--filter'
        'FullyQualifiedName~MoneyCanonicalisationTests|FullyQualifiedName~SplitAdjustmentTests|FullyQualifiedName~PriceSeriesPersistenceTests|FullyQualifiedName~LedgerExposurePersistenceTests|FullyQualifiedName~EodhdSplitsFeedTests|FullyQualifiedName~CompositionTests|FullyQualifiedName~PortfolioReadModelTests'
    ) 'block2b-focused.log'
    $null = $results.Add($focused)

    $full = Invoke-Dotnet 'full Release suite' @(
        'test'; (Join-Path $root 'AI-Investment-Analyst.sln'); '-c'; 'Release'; '--no-build'; '--nologo'
    ) 'block2b-full.log'
    $null = $results.Add($full)
}

Say ''
Say '==============================================================='
Say ' VERDICT'
foreach ($r in $results) {
    $mark = '  PASS  '
    if ($r.Code -ne 0) { $mark = '  FAIL  ' }
    Say ($mark + $r.Label)
}
Say ('  finished: ' + (Stamp))
Say '==============================================================='
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
$failed = @($results | Where-Object { $_.Code -ne 0 })
if ($failed.Count -gt 0) { exit 1 }
exit 0

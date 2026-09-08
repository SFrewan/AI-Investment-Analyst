#requires -Version 5.1
<#
    PART A ONLY - PROVENANCE ASSERTION CORRECTION, VERIFICATION.

    THIS SCRIPT DOES NOT WRITE TO ai_investment. It does not replay anything. It does not touch
    CCF observations, Gate 6, SplitAdjustment, the declarations or the acquisition authorisation.

    What it does:
      1. builds tools\replay-recover (Release, net8.0)   - the assertion fix must compile
      2. runs the focused provenance / replay / normalization tests
      3. runs the full Release suite

    EVBG is NOT recovered here. That is a separate script, and it only runs if this one passes.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\replay-recover\ReplayRecover.csproj'
$sln = Join-Path $root 'AI-Investment-Analyst.sln'
$verify = Join-Path $root 'artifacts\verify'

$null = New-Item -ItemType Directory -Force -Path $verify

$toolLog = Join-Path $verify 'provenance-fix-tool-build.log'
$focusedLog = Join-Path $verify 'provenance-fix-focused-tests.log'
$fullLog = Join-Path $verify 'provenance-fix-release-tests.log'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PART A - PROVENANCE ASSERTION CORRECTION'
Write-Host '   Build + focused tests + full Release suite.'
Write-Host '   NO replay. NO recovery. NO write to ai_investment.'
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
Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host ''

# Connectors pinned off for the whole session. Nothing here can reach a provider.
$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

function Show-Tail([string]$path, [string]$pattern) {
    if (Test-Path -LiteralPath $path) {
        foreach ($line in (Get-Content -Path $path -Tail 80)) {
            if ($line -match $pattern) { Write-Host ('    ' + $line.Trim()) }
        }
    }
}

# ---------------------------------------------------------------- 1. the tool must compile
Write-Host '--- 1/3  building tools\replay-recover (Release, net8.0)'

& dotnet build $tool -c Release --nologo 2>&1 | Tee-Object -FilePath $toolLog | Out-Null
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host '  FAIL  the corrected tool did not build. NOTHING ELSE WAS RUN.'
    Write-Host ''
    Show-Tail $toolLog 'error|Build FAILED'
    Write-Host ''
    Write-Host ('  Full log: ' + $toolLog)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

# ---------------------------------------------------------------- 2. focused tests
Write-Host '--- 2/3  focused provenance / replay / normalization tests'

$filter = 'FullyQualifiedName~ArchivedPayloadReplay|FullyQualifiedName~PartialNormalization|FullyQualifiedName~EodhdDailyPriceNormalizer'

& dotnet test $sln -c Release --nologo --filter $filter 2>&1 | Tee-Object -FilePath $focusedLog | Out-Null
$focusedCode = $LASTEXITCODE

Write-Host ''
Show-Tail $focusedLog 'Passed!|Failed!|error|Passed:|Failed:|Skipped:|Total:'
Write-Host ''

if ($focusedCode -ne 0) {
    Write-Host '  FAIL  the focused tests did not pass. THE FULL SUITE WAS NOT RUN.'
    Write-Host ('  Full log: ' + $focusedLog)
    exit 7
}

Write-Host '  PASS  focused tests.'
Write-Host ''

# ---------------------------------------------------------------- 3. full Release suite
Write-Host '--- 3/3  full Release suite'

& dotnet test $sln -c Release --nologo 2>&1 | Tee-Object -FilePath $fullLog | Out-Null
$fullCode = $LASTEXITCODE

Write-Host ''
Show-Tail $fullLog 'Passed!|Failed!|Passed:|Failed:|Skipped:|Total:'
Write-Host ''

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

if ($fullCode -ne 0) {
    Write-Host '  FAIL  the full Release suite did not pass.'
    Write-Host ('  Full log: ' + $fullLog)
    exit 8
}

Write-Host '  PASS  full Release suite.'
Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PART A COMPLETE. NOTHING WAS RECOVERED AND NOTHING WAS WRITTEN'
Write-Host '   TO ai_investment. EVBG HAS NOT BEEN TOUCHED.'
Write-Host '  ================================================================'

exit 0

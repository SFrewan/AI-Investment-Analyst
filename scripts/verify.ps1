<#
    Runs the repository's build and test gates and writes machine-readable results.

    Exists because the agent driving this repository has no .NET SDK of its own: it can write
    files here and read files back, but it cannot run a command. Launching this once from a
    terminal gives it the build and test results as text it can read, diagnose and act on,
    instead of a person having to copy console output back and forth.

    Everything it writes lands in artifacts/verify, which .gitignore already excludes.

        powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
        powershell -ExecutionPolicy Bypass -File scripts\verify.ps1 -SkipTests
        powershell -ExecutionPolicy Bypass -File scripts\verify.ps1 -TestFilter "FullyQualifiedName~Analytics"
#>
[CmdletBinding()]
param(
    [string] $TestFilter = '',
    [switch] $SkipTests,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -Path $repo

$outDir = Join-Path $repo 'artifacts\verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$buildLog   = Join-Path $outDir 'build.log'
$testLog    = Join-Path $outDir 'test.log'
$summaryLog = Join-Path $outDir 'summary.txt'
$doneMarker = Join-Path $outDir 'DONE.txt'

# The marker is what the agent polls for, so it must not survive from a previous run.
Remove-Item -Path $doneMarker -ErrorAction SilentlyContinue
Remove-Item -Path $summaryLog -ErrorAction SilentlyContinue
Remove-Item -Path $testLog    -ErrorAction SilentlyContinue

# ---- test database credential ------------------------------------------------------------
# NO CREDENTIAL IS STORED IN THIS FILE. It used to hold a live PostgreSQL password, in a tracked
# file, on a repository with a remote - which is exactly the failure docs/SECURITY.md exists to
# prevent, and rotating the password would not have removed it from history.
#
# Resolution order:
#   1. AIINV_TEST_POSTGRES already set in the environment (CI supplies it as a secret).
#   2. scripts/verify.local.ps1 - git-ignored, machine-local, created from
#      scripts/verify.local.example.ps1. It sets $env:AIINV_TEST_POSTGRES.
#
# With neither, the integration tests SKIP and say so. They are not silently counted as passed:
# the fixture reports the reason and the summary shows the skip count.
#
# The fixture additionally refuses any database whose name does not end in '_tests', so a
# mistyped value cannot be pointed at the development database.
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host "[verify] AIINV_TEST_POSTGRES is not set and scripts\verify.local.ps1 is absent."
    Write-Host "[verify] Integration tests that need a database will SKIP. See scripts\verify.local.example.ps1."
}

$started = Get-Date

Write-Host "[verify] building ($Configuration)..."
& dotnet build AI-Investment-Analyst.sln -c $Configuration --nologo 2>&1 |
    Out-File -FilePath $buildLog -Encoding utf8
$buildExit = $LASTEXITCODE

$testExit = -1

if ($buildExit -eq 0 -and -not $SkipTests) {
    Write-Host "[verify] testing ($Configuration)..."

    $testArgs = @('test', 'AI-Investment-Analyst.sln', '-c', $Configuration, '--no-build', '--nologo')
    if ($TestFilter -ne '') { $testArgs += @('--filter', $TestFilter) }

    & dotnet @testArgs 2>&1 | Out-File -FilePath $testLog -Encoding utf8
    $testExit = $LASTEXITCODE
}
elseif ($SkipTests) {
    Write-Host "[verify] tests skipped by request."
}
else {
    Write-Host "[verify] build failed; tests not run."
}

# ---- summary -----------------------------------------------------------------------------
# The full logs are kept, but the agent reads this file first: a whole test log is mostly the
# API suite's deliberate 'database unreachable' stack traces, and reading it wholesale would
# cost far more than it says.

function Get-Matches {
    param([string] $Path, [string] $Pattern, [int] $First)

    if (-not (Test-Path -Path $Path)) { return @() }

    return Select-String -Path $Path -Pattern $Pattern |
        Select-Object -First $First |
        ForEach-Object { $_.Line.TrimEnd() }
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("started=$($started.ToString('o'))")
$summary.Add("finished=$((Get-Date).ToString('o'))")
$summary.Add("configuration=$Configuration")
$summary.Add("filter=$TestFilter")
$summary.Add("build_exit=$buildExit")
$summary.Add("test_exit=$testExit")

$summary.Add('')
$summary.Add('--- build diagnostics ---')
foreach ($line in (Get-Matches -Path $buildLog -Pattern ': (error|warning) [A-Za-z]+[0-9]+' -First 120)) {
    $summary.Add($line)
}

$summary.Add('')
$summary.Add('--- per project ---')
# Two shapes are matched deliberately. VSTest prints one 'Passed!/Failed! - Failed: n, Passed: n,
# ...' line per assembly; the newer Microsoft.Testing.Platform runner prints 'X test succeeded'.
# An earlier version of this script matched only the second, so a fully green run reported no
# totals at all - which reads exactly like a run that never happened.
$perProject = Get-Matches -Path $testLog -Pattern '^\s*(Passed!|Failed!|.*test (succeeded|failed))' -First 40
foreach ($line in $perProject) {
    $summary.Add($line)
}

$summary.Add('')
$summary.Add('--- totals ---')
foreach ($line in (Get-Matches -Path $testLog -Pattern '^\s*Test summary:' -First 5)) {
    $summary.Add($line)
}

# Aggregate across assemblies, so the whole-suite number does not have to be added up by hand.
$failed = 0; $passed = 0; $skipped = 0; $total = 0; $counted = 0
foreach ($line in $perProject) {
    $m = [regex]::Match(
        $line,
        'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')

    if ($m.Success) {
        $failed  += [int]$m.Groups[1].Value
        $passed  += [int]$m.Groups[2].Value
        $skipped += [int]$m.Groups[3].Value
        $total   += [int]$m.Groups[4].Value
        $counted += 1
    }
}

if ($counted -gt 0) {
    $summary.Add("aggregate over $counted assemblies: total=$total passed=$passed failed=$failed skipped=$skipped")
}
else {
    $summary.Add('aggregate: no per-assembly result lines were recognised in the test log')
}

$summary.Add('')
$summary.Add('--- failed tests ---')
foreach ($line in (Get-Matches -Path $testLog -Pattern '\[FAIL\]' -First 100)) {
    $summary.Add($line)
}

$summary.Add('')
$summary.Add('--- failure detail ---')
foreach ($line in (Get-Matches -Path $testLog -Pattern 'Error Message:|Assert\.[A-Za-z]+\(\) Failure|Expected:|Actual:' -First 150)) {
    $summary.Add($line)
}

$summary | Set-Content -Path $summaryLog -Encoding utf8

"build_exit=$buildExit test_exit=$testExit" | Set-Content -Path $doneMarker -Encoding utf8

Write-Host "[verify] done. build_exit=$buildExit test_exit=$testExit"
Write-Host "[verify] wrote $summaryLog"

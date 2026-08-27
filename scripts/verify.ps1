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

# The integration fixture refuses any database whose name does not end in '_tests', so this
# cannot be pointed at the development database by accident.
$env:AIINV_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Database=ai_investment_tests;Username=postgres;Password=000160'

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
foreach ($line in (Get-Matches -Path $testLog -Pattern 'test (succeeded|failed)' -First 40)) {
    $summary.Add($line)
}

$summary.Add('')
$summary.Add('--- totals ---')
foreach ($line in (Get-Matches -Path $testLog -Pattern '^\s*Test summary:' -First 5)) {
    $summary.Add($line)
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

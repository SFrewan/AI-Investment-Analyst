#requires -Version 5.1
<#
    PER-RUN REQUEST PROVENANCE - BUILD + FOCUSED TESTS + ARCHITECTURE TESTS.

    NO migration. NO database schema change. NO provider call: every exchange in these tests comes
    from FakeDataProvider, which returns objects constructed in the test file.

    The integration tests DO talk to the configured test database (ai_investment_tests), exactly as
    they always have. Nothing here touches ai_investment.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'AI-Investment-Analyst.sln'
$verify = Join-Path $root 'artifacts\verify'

$null = New-Item -ItemType Directory -Force -Path $verify

$buildLog = Join-Path $verify 'provenance-tests-build.log'
$focusedLog = Join-Path $verify 'provenance-tests-focused.log'
$archLog = Join-Path $verify 'provenance-tests-architecture.log'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PER-RUN REQUEST PROVENANCE - BUILD + FOCUSED + ARCHITECTURE'
Write-Host '   No migration. No schema change. No provider call.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host ''

function Show([string]$path, [string]$pattern) {
    if (Test-Path -LiteralPath $path) {
        foreach ($line in (Get-Content -Path $path)) {
            if ($line -match $pattern) { Write-Host ('    ' + $line.Trim()) }
        }
    }
}

# ---------------------------------------------------------------- 1. build
Write-Host '--- 1/3  building (Release)'

& dotnet build $sln -c Release --nologo 2>&1 | Tee-Object -FilePath $buildLog | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host '  FAIL  the solution did not build. NO TESTS WERE RUN.'
    Write-Host ''
    Show $buildLog 'error [A-Z]+[0-9]+'
    Write-Host ''
    Write-Host ('  Full log: ' + $buildLog)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

# ---------------------------------------------------------------- 2. focused provenance tests
Write-Host '--- 2/3  focused provenance tests'

& dotnet test $sln -c Release --nologo --no-build --filter 'FullyQualifiedName~ProviderExchangeProvenance' 2>&1 |
    Tee-Object -FilePath $focusedLog | Out-Null
$focused = $LASTEXITCODE

Write-Host ''
Show $focusedLog '^(Passed!|Failed!)|error [A-Z]+[0-9]+|\[FAIL\]'
Write-Host ''

if ($focused -ne 0) {
    Write-Host '  FAIL  the focused provenance tests did not pass.'
    Write-Host ('  Full log: ' + $focusedLog)
    exit 7
}

Write-Host '  PASS  focused provenance tests.'
Write-Host ''

# ---------------------------------------------------------------- 3. architecture tests
Write-Host '--- 3/3  architecture tests (layering, data-plane rules)'

& dotnet test $sln -c Release --nologo --no-build --filter 'FullyQualifiedName~Architecture' 2>&1 |
    Tee-Object -FilePath $archLog | Out-Null
$arch = $LASTEXITCODE

Write-Host ''
Show $archLog '^(Passed!|Failed!)|\[FAIL\]'
Write-Host ''

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

if ($arch -ne 0) {
    Write-Host '  FAIL  the architecture tests did not pass.'
    Write-Host ('  Full log: ' + $archLog)
    exit 8
}

Write-Host '  PASS  architecture tests.'
Write-Host ''
Write-Host '  No migration was created or applied. No provider was contacted.'

exit 0

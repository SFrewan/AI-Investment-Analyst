#requires -Version 5.1
<#
    HARDENED SCAFFOLDING + PILOT READINESS - VERIFICATION RUN.

    Four steps, in order, all of them read-only with respect to the platform's evidence:

      1. The scaffolding guard's own tests            (no database, no EF tooling)
      2. The two scaffolding launchers, dry-inspected (no execution)
      3. The focused provenance tests                 (fake transport only)
      4. The composition test                         (no connection, no provider)

    WHAT THIS SCRIPT DOES NOT DO, deliberately and by construction:

      * It does not apply, roll back or scaffold a migration. It never invokes the EF tool.
      * It does not contact EODHD or SEC EDGAR. Step 3's tests use the in-repo fake provider, and
        step 4 composes with both connectors switched off.
      * It does not create or consume an acquisition authorization.
      * It does not write to ai_investment. Nothing here opens a connection to it.

    Exit 0 when every step passes; the failing step's number otherwise.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$verify = Join-Path $root 'artifacts\verify'

# Filtered runs are aimed at the ONE project that holds the matching tests, not at the solution.
# `dotnet test <solution> --filter` exits 1 when any project in it matches no test - the filter did
# its job, every test passed, and the run still reports failure. Naming the project keeps the exit
# code meaning what it says.
$applicationTests = Join-Path $root 'tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj'
$integrationTests = Join-Path $root 'tests\AI.Investment.Integration.Tests\AI.Investment.Integration.Tests.csproj'

$null = New-Item -ItemType Directory -Force -Path $verify

$guardLog = Join-Path $verify 'pilot-readiness-guard.log'
$focusedLog = Join-Path $verify 'pilot-readiness-focused.log'
$compositionLog = Join-Path $verify 'pilot-readiness-composition.log'
$summaryPath = Join-Path $verify 'pilot-readiness.json'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   SCAFFOLDING HARDENING + PILOT READINESS'
Write-Host '   No migration. No provider call. No authorization. Read-only.'
Write-Host '  ================================================================'
Write-Host ''

$steps = New-Object System.Collections.ArrayList

function Record {
    param([string] $Name, [bool] $Passed, [string] $Detail)

    $null = $steps.Add([PSCustomObject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    Write-Host ('  ' + $(if ($Passed) { 'PASS' } else { 'FAIL' }) + '  ' + $Name)
    if ($Detail) { Write-Host ('        ' + $Detail) }
}

function Show {
    param([string] $Path, [string] $Pattern)

    if (Test-Path -LiteralPath $Path) {
        foreach ($line in (Get-Content -Path $Path)) {
            if ($line -match $Pattern) { Write-Host ('        ' + $line.Trim()) }
        }
    }
}

# Native commands write progress and warnings to stderr. With $ErrorActionPreference = 'Stop' and a
# 2>&1 redirect, PowerShell turns the first such line into a terminating error - so the script died
# at the first `dotnet test` that had anything to say, BEFORE the log it was writing had been
# flushed, and reported its own exception instead of the test failure. Exit codes are what this
# script judges by, so stderr is demoted for the duration of a native call and restored after.
function Invoke-Native {
    param([scriptblock] $Command, [string] $LogPath)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        & $Command 2>&1 | Tee-Object -FilePath $LogPath | Out-Null
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# ---------------------------------------------------------------- 1. the guard's own tests
Write-Host '--- 1/4  scaffolding guard tests'

# A child process, not `& script.ps1`, so its exit code arrives here unambiguously rather than
# depending on how `exit` inside a called script interacts with the caller's scope.
$guardScript = Join-Path $PSScriptRoot 'test-scaffold-guard.ps1'
$guardExit = Invoke-Native { powershell -NoProfile -ExecutionPolicy Bypass -File $guardScript -RepoRoot $root } $guardLog

Show $guardLog '^\s*(PASS|FAIL)\s|checks,'
Record 'scaffolding guard tests' ($guardExit -eq 0) ("exit=$guardExit")

if ($guardExit -ne 0) { Write-Host ('  Log: ' + $guardLog); exit 1 }

Write-Host ''

# ---------------------------------------------------------------- 2. launchers, inspected only
Write-Host '--- 2/4  scaffolding launchers, resolved WITHOUT running the EF tool'

. (Join-Path $PSScriptRoot 'Resolve-ScaffoldTarget.ps1')

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

# This is the same call the launchers make, on this machine, with this machine's variables - and
# then it stops. Whatever it decides, no migration is scaffolded here.
$default = Resolve-ScaffoldTarget -Requested ''
Write-Host $default.Report

$designtime = Resolve-ScaffoldTarget -Requested 'designtime'
Write-Host $designtime.Report

$defaultIsTest = $default.Allowed -and $default.Source -eq 'AIINV_TEST_POSTGRES'
# Either it refused, or that variable does not name the evidence database on this machine. What
# must never happen is the evidence database being ALLOWED without an opt-in.
$evidenceRefused = (-not $designtime.Allowed) -or ($designtime.Database -ne 'ai_investment')

Record 'the default target on this machine is the test database' $defaultIsTest `
    ("database=$($default.Database) source=$($default.Source)")

Record 'the designtime variable on this machine is refused without an opt-in' $evidenceRefused `
    ("database=$($designtime.Database) exit=$($designtime.ExitCode)")

if (-not ($defaultIsTest -and $evidenceRefused)) { exit 2 }

Write-Host ''

# ---------------------------------------------------------------- 3. focused provenance tests
Write-Host '--- 3/4  focused provenance tests (fake transport, no vendor)'

$focusedExit = Invoke-Native { dotnet test $applicationTests -c Release --nologo --filter 'FullyQualifiedName~ProviderExchangeProvenance' } $focusedLog

Show $focusedLog '^(Passed!|Failed!)|\[FAIL\]'
Record 'focused provenance tests' ($focusedExit -eq 0) ("exit=$focusedExit")

if ($focusedExit -ne 0) { Write-Host ('  Log: ' + $focusedLog); exit 3 }

Write-Host ''

# ---------------------------------------------------------------- 4. composition
Write-Host '--- 4/4  composition test (the real container, no connection, no provider)'

$compositionExit = Invoke-Native { dotnet test $integrationTests -c Release --nologo --filter 'FullyQualifiedName~ProvenanceComposition' } $compositionLog

Show $compositionLog '^(Passed!|Failed!)|\[FAIL\]'
Record 'composition injects the exchange store into the gateway' ($compositionExit -eq 0) ("exit=$compositionExit")

if ($compositionExit -ne 0) { Write-Host ('  Log: ' + $compositionLog); exit 4 }

Write-Host ''

# ---------------------------------------------------------------- evidence
$summary = [PSCustomObject]@{
    generatedAtUtc        = (Get-Date).ToUniversalTime().ToString('o')
    steps                 = @($steps)
    defaultScaffoldTarget = [PSCustomObject]@{
        allowed  = $default.Allowed
        source   = $default.Source
        server   = $default.Server
        database = $default.Database
    }
    designtimeScaffoldTarget = [PSCustomObject]@{
        allowed  = $designtime.Allowed
        exitCode = $designtime.ExitCode
        source   = $designtime.Source
        server   = $designtime.Server
        database = $designtime.Database
    }
    migrationApplied     = $false
    migrationScaffolded  = $false
    providerContacted    = $false
    authorizationCreated = $false
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

Write-Host '  ----------------------------------------------------------------'
Write-Host '   All four steps passed.'
Write-Host '   No migration was applied or scaffolded. No provider was contacted.'
Write-Host '   No authorization was created or consumed. ai_investment was not written to.'
Write-Host ('   Evidence: ' + $summaryPath)
Write-Host '  ----------------------------------------------------------------'
Write-Host ''

exit 0

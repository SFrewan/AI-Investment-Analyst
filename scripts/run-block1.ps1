#requires -Version 5.1
<#
    BLOCK 1 - CORRECTNESS AND PRODUCTION WIRING.

    1. Read-only sweep for unregistered dependencies and negative-only endpoint tests.
    2. Release build. TreatWarningsAsErrors is on in Directory.Build.props, so a warning fails it.
    3. Focused tests: composition, portfolio endpoints, portfolio read model.
    4. Full Release suite.

    Makes no provider request, starts no cycle, waits for no cooldown, changes no rule.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'block1.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'

function Say([string]$text) {
    $null = $lines.Add($text)
    Write-Host $text
}

function Save-Log {
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
}

function Stamp {
    return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
}

function Say-Header([string]$title) {
    Say ''
    Say '---------------------------------------------------------------'
    Say ('  ' + $title + '   [' + (Stamp) + ']')
    Say '---------------------------------------------------------------'
    Save-Log
}

function Invoke-Dotnet([string]$label, [string[]]$dotnetArgs, [string]$logName) {
    $file = Join-Path $out $logName
    Say ('  running: dotnet ' + ($dotnetArgs -join ' '))

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 0
    try {
        & dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $file | Out-Null
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $tail = @()
    if (Test-Path $file) { $tail = @(Get-Content -Path $file -Tail 40) }

    $allSkipped = $false
    foreach ($t in $tail) {
        if ($t -match 'Skipped!\s+- Failed:\s+0, Passed:\s+0, Skipped:\s+[1-9]') { $allSkipped = $true }
    }

    if ($allSkipped -and $code -eq 0) {
        Say '  NOT PROVED  every test in this phase was SKIPPED, not run.'
        $code = 3
    }
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
Say ' BLOCK 1 - CORRECTNESS AND PRODUCTION WIRING'
Say (' started : ' + (Stamp))
Say (' repo    : ' + $root)
Say ' no EODHD request, no cycle, no cooldown wait, no rule changed'
Say '==============================================================='

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Say ''
    Say '  WARNING: AIINV_TEST_POSTGRES is not set. The database-backed proofs will SKIP,'
    Say '           and a skipped proof is not a passing one.'
}
else {
    Say ''
    Say '  Integration database configured (value not printed).'
}

$results = New-Object 'System.Collections.Generic.List[object]'

Say-Header 'STEP 1 - read-only composition and assertion sweep'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'audit-composition.ps1')
Say ('  report: ' + (Join-Path $root 'artifacts\audit\60-composition.md'))
Save-Log

Say-Header 'STEP 2 - Release build (warnings are errors)'
$build = Invoke-Dotnet 'Release build' @(
    'build'
    (Join-Path $root 'AI-Investment-Analyst.sln')
    '-c'
    'Release'
    '--nologo'
) 'block1-build.log'
$null = $results.Add($build)

if ($build.Code -ne 0) {
    Say ''
    Say ' STOPPING: the build failed. Tests cannot say anything about a tree that does not compile.'
    Save-Log
    exit 1
}

Say-Header 'STEP 3 - focused tests'
$focused = Invoke-Dotnet 'focused: composition + portfolio' @(
    'test'
    (Join-Path $root 'tests\AI.Investment.Api.Tests')
    '-c'
    'Release'
    '--no-build'
    '--nologo'
    '--filter'
    'FullyQualifiedName~CompositionTests|FullyQualifiedName~PortfolioEndpointTests|FullyQualifiedName~PortfolioReadModelTests'
) 'block1-focused.log'
$null = $results.Add($focused)

Say-Header 'STEP 4 - full Release suite'
$full = Invoke-Dotnet 'full suite' @(
    'test'
    (Join-Path $root 'AI-Investment-Analyst.sln')
    '-c'
    'Release'
    '--no-build'
    '--nologo'
) 'block1-full.log'
$null = $results.Add($full)

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

#requires -Version 5.1
<#
    VERIFY THE GUARDWRITES OWNED-ENTITY FIX.

    Builds Release, runs the focused WriteGuardTests first, then the whole suite, then
    builds the API. Writes everything to artifacts\verify\guardwrites-fix.txt.

    WHAT IT DOES NOT DO
      It does not start the API, does not touch OperationsHost:RunCycles, does not start a
      cycle and makes no EODHD request. It does not write to the development database:
      the integration fixture refuses any database whose name does not end in '_tests',
      so ai_investment cannot be reached from here even by mistake.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'guardwrites-fix.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

# Native command stderr becomes a terminating ErrorRecord under ErrorActionPreference=Stop.
# A failing build must be reportable, not fatal.
function Run([string]$title, [string]$exe, [string[]]$commandArgs) {
    Say ''
    Say ('=== ' + $title + ' ===')
    Say ('    ' + $exe + ' ' + ($commandArgs -join ' '))
    Say ''

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 1
    try {
        $output = @(& $exe @commandArgs 2>&1)
        $code = $LASTEXITCODE
        foreach ($line in $output) { Say ('    ' + [string]$line) }
    }
    catch {
        Say ('    RUN FAILED: ' + $_.Exception.Message)
        $code = 1
    }
    finally { $ErrorActionPreference = $previous }

    Say ''
    Say ('    exit code: ' + $code)
    return $code
}

$exitCode = 0

Say '=== GUARDWRITES OWNED-ENTITY FIX: BUILD AND TEST ==='
Say ("UTC now : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ("repo    : " + $root)

# ------------------------------------------------------------------------------------------
# 0. Preflight.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 0. PREFLIGHT ---'

$dotnet = $null
$found = @(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $dotnet = [string]$found[0].Source }

if ([string]::IsNullOrWhiteSpace($dotnet)) {
    Say '  dotnet was not found on PATH. STOPPING.'
    Save-Log; exit 1
}
Say ('  dotnet : ' + $dotnet)

# Nothing should be listening on 5143 - the API is meant to be stopped for this.
$listening = $false
try {
    $conns = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    $listening = ($conns.Count -gt 0)
}
catch { $listening = $false }
Say ('  port 5143 in use : ' + $listening + '   (expected False; nothing is started by this script)')

# What actually changed, so the transcript is self-evidencing.
$null = Run 'CHANGED FILES' 'git' @('-C', $root, 'diff', '--stat')

# ------------------------------------------------------------------------------------------
# 1. A database for the integration tests. Docker if it is there, otherwise a dedicated
#    local database. Never the development database - the fixture refuses that outright.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 1. TEST DATABASE ---'

$dockerUp = $false
try {
    $probe = @(& docker info 2>&1)
    $dockerUp = ($LASTEXITCODE -eq 0)
}
catch { $dockerUp = $false }

if ($dockerUp) {
    Say '  Docker is running. Testcontainers will start postgres:16-alpine by itself.'
    Say '  AIINV_TEST_POSTGRES is left unset.'
}
else {
    Say '  Docker is not available. Falling back to a dedicated local test database.'

    $psql = $null
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
    if ($cmd.Count -gt 0) { $psql = [string]$cmd[0].Source }
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $candidates = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($candidates.Count -gt 0) { $psql = [string]$candidates[0].FullName }
    }

    if ([string]::IsNullOrWhiteSpace($psql)) {
        Say '  psql was not found either. The integration tests will SKIP, and the fix will'
        Say '  therefore NOT be proven. Everything else still runs.'
    }
    else {
        $apiProject = Join-Path $root 'src\AI.Investment.Api'
        $cs = $null
        $text = $null
        try {
            $text = (& $dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
            if ($LASTEXITCODE -eq 0) {
                foreach ($line in @($text -split "`n")) {
                    $i = $line.IndexOf(' = ')
                    if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') {
                        $cs = $line.Substring($i + 3)
                    }
                }
            }
        }
        catch { }
        finally { $text = $null }

        if ([string]::IsNullOrWhiteSpace($cs)) {
            Say '  Database:ConnectionString was unavailable. Integration tests will SKIP.'
        }
        else {
            $parts = @{}
            foreach ($seg in @($cs -split ';')) {
                $j = $seg.IndexOf('=')
                if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
            }

            $H = 'localhost'; $P = '5432'; $U = ''
            if ($parts.ContainsKey('host'))     { $H = $parts['host'] }
            if ($parts.ContainsKey('port'))     { $P = $parts['port'] }
            if ($parts.ContainsKey('username')) { $U = $parts['username'] }
            if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }

            # CREATE DATABASE, never DROP. The development database is untouched, and the
            # fixture would refuse it anyway.
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            $created = @(& $psql -h $H -p $P -U $U -d 'postgres' -v ON_ERROR_STOP=0 `
                -c 'CREATE DATABASE ai_investment_tests' 2>&1)
            $ErrorActionPreference = $previous

            foreach ($line in $created) { Say ('  psql: ' + ([string]$line).Trim()) }

            $rebuilt = 'Host=' + $H + ';Port=' + $P + ';Database=ai_investment_tests;Username=' + $U
            if ($parts.ContainsKey('password')) { $rebuilt = $rebuilt + ';Password=' + $parts['password'] }

            $env:AIINV_TEST_POSTGRES = $rebuilt
            $parts = $null
            $cs = $null

            Say '  AIINV_TEST_POSTGRES points at ai_investment_tests (NOT ai_investment).'
            Say '  The fixture applies migrations to it and empties only its own tables.'
        }
    }
}

try {
    $sln = Join-Path $root 'AI-Investment-Analyst.sln'

    # --------------------------------------------------------------------------------------
    # 2. Build. TreatWarningsAsErrors is on, so this is also the analyzer gate.
    # --------------------------------------------------------------------------------------
    $buildCode = Run 'BUILD (Release, solution)' $dotnet @('build', $sln, '-c', 'Release', '--nologo')
    if ($buildCode -ne 0) {
        Say ''
        Say '  BUILD FAILED. Not running any tests - a red build makes every result meaningless.'
        $exitCode = 1
        Save-Log
        Write-Host ''
        Write-Host ('Written: ' + $log)
        exit $exitCode
    }

    # --------------------------------------------------------------------------------------
    # 3. The focused tests first.
    # --------------------------------------------------------------------------------------
    $focusedCode = Run 'FOCUSED TESTS (WriteGuardTests)' $dotnet @(
        'test',
        (Join-Path $root 'tests\AI.Investment.Integration.Tests'),
        '-c', 'Release',
        '--no-build',
        '--filter', 'FullyQualifiedName~WriteGuardTests',
        '--logger', 'console;verbosity=normal',
        '--nologo')

    if ($focusedCode -ne 0) { $exitCode = 1 }

    # --------------------------------------------------------------------------------------
    # 4. Then everything.
    # --------------------------------------------------------------------------------------
    $suiteCode = Run 'FULL SUITE (Release)' $dotnet @(
        'test', $sln, '-c', 'Release', '--no-build', '--nologo')

    if ($suiteCode -ne 0) { $exitCode = 1 }

    # --------------------------------------------------------------------------------------
    # 5. The API binary the observation run will use.
    # --------------------------------------------------------------------------------------
    $apiCode = Run 'BUILD API (Release)' $dotnet @(
        'build', (Join-Path $root 'src\AI.Investment.Api'), '-c', 'Release', '--nologo')

    if ($apiCode -ne 0) { $exitCode = 1 }

    Say ''
    Say '--- SUMMARY ---'
    Say ('  build (solution) : ' + $(if ($buildCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  focused tests    : ' + $(if ($focusedCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  full suite       : ' + $(if ($suiteCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  api build        : ' + $(if ($apiCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ''
    Say '  Nothing was started. RunCycles untouched. No EODHD request made.'
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_TEST_POSTGRES -ErrorAction SilentlyContinue
}

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit $exitCode

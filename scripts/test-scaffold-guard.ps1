#requires -Version 5.1
<#
    THE SCAFFOLDING GUARD, TESTED.

    Proves that scripts\Resolve-ScaffoldTarget.ps1 refuses what it is supposed to refuse, that the
    two scaffolding launchers actually go through it, and that the apply-time guard in
    scripts\run-provenance-migrate.ps1 was not weakened while this was done.

    IT TOUCHES NO DATABASE. It does not invoke the EF migrations tool, does not open a connection, and references
    no database client - and it proves that about itself and about the resolver by scanning both
    files for the tokens that would be needed to do any of it. Every connection string in here is a
    literal invented for the test; none of them is ever handed to anything.

    It also snapshots the migrations directory before and after and compares names and hashes, so
    "existing migration history is not modified" is a measurement rather than a claim.

    Exit code 0 when every check passes, 1 otherwise.
#>

[CmdletBinding()]
param(
    [string] $RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved in the body, not in the param default. Under Windows PowerShell 5.1 invoked with -File,
# $PSScriptRoot is not yet populated while parameter defaults are being evaluated, so a default of
# (Split-Path -Parent $PSScriptRoot) fails to bind before the script has run a single line.
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepoRoot = Split-Path -Parent $here
}

$scriptsDir = Join-Path $RepoRoot 'scripts'
$resolverPath = Join-Path $scriptsDir 'Resolve-ScaffoldTarget.ps1'
$applyGuardPath = Join-Path $scriptsDir 'run-provenance-migrate.ps1'
$migrationsDir = Join-Path $RepoRoot 'src\AI.Investment.Infrastructure\Persistence\Migrations'
$verifyDir = Join-Path $RepoRoot 'artifacts\verify'

$null = New-Item -ItemType Directory -Force -Path $verifyDir

# Synthetic connection strings. The password is a sentence with spaces in it - a shape no server
# issues - so that the "the report never prints the password" check cannot pass by coincidence.
$secret = 'this is not a real password'
$testCs = 'Host=127.0.0.1;Port=5432;Database=ai_investment_tests;Username=postgres;Password=' + $secret
$evidenceCs = 'Host=127.0.0.1;Port=5432;Database=ai_investment;Username=postgres;Password=' + $secret
$evidenceMixedCase = 'Host=127.0.0.1;Port=5432;Database=AI_Investment;Username=postgres;Password=' + $secret
$namelessCs = 'Host=127.0.0.1;Port=5432;Username=postgres;Password=' + $secret
$token = 'I-UNDERSTAND-THIS-IS-THE-EVIDENCE-DATABASE'

$results = New-Object System.Collections.ArrayList

function Check {
    param([string] $Name, [scriptblock] $Body)

    $ok = $false
    $detail = ''

    try {
        $detail = & $Body
        $ok = $true
    }
    catch {
        $detail = $_.Exception.Message
    }

    $null = $results.Add([PSCustomObject]@{ Name = $Name; Passed = $ok; Detail = "$detail" })
}

function Expect {
    param([bool] $Condition, [string] $Because)

    if (-not $Condition) { throw $Because }
}

function Snapshot-Migrations {
    if (-not (Test-Path -LiteralPath $migrationsDir)) { return @() }

    Get-ChildItem -LiteralPath $migrationsDir -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            [PSCustomObject]@{
                Name = $_.Name
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
}

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   MIGRATION SCAFFOLDING GUARD - TESTS'
Write-Host '   No database. No EF tooling. No connection. No migration.'
Write-Host '  ================================================================'
Write-Host ''

$before = @(Snapshot-Migrations)

. $resolverPath

# ---------------------------------------------------------------- resolution
Check 'default (no AIINV_SCAFFOLD_TARGET) resolves to the TEST database' {
    $r = Resolve-ScaffoldTarget -Requested '' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn ''
    Expect $r.Allowed 'the test database should be allowed'
    Expect ($r.Source -eq 'AIINV_TEST_POSTGRES') "resolved from $($r.Source), expected AIINV_TEST_POSTGRES"
    Expect ($r.Database -eq 'ai_investment_tests') "resolved database $($r.Database)"
    "database=$($r.Database) source=$($r.Source)"
}

Check 'default does NOT fall back to AIINV_DESIGNTIME_DB when the test variable is empty' {
    # The regression that mattered: the old scripts promoted the designtime variable silently, and
    # on this machine the designtime variable names the evidence database.
    $r = Resolve-ScaffoldTarget -Requested '' -TestConnection '' -DesigntimeConnection $evidenceCs -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'an empty test variable must not silently promote the designtime one'
    Expect ($r.ExitCode -eq 2) "exit $($r.ExitCode), expected 2"
    Expect ($r.Database -ne 'ai_investment') 'it must not even have resolved the evidence database'
    "refused exit=$($r.ExitCode)"
}

Check 'AIINV_DESIGNTIME_DB is used only when explicitly requested' {
    $designtimeDev = 'Host=127.0.0.1;Port=5432;Database=ai_investment_dev;Username=postgres;Password=' + $secret
    $default = Resolve-ScaffoldTarget -Requested '' -TestConnection $testCs -DesigntimeConnection $designtimeDev -EvidenceOptIn ''
    Expect ($default.Source -eq 'AIINV_TEST_POSTGRES') 'default must not read the designtime variable'

    $asked = Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection $testCs -DesigntimeConnection $designtimeDev -EvidenceOptIn ''
    Expect $asked.Allowed 'an explicitly requested non-evidence designtime database is allowed'
    Expect ($asked.Source -eq 'AIINV_DESIGNTIME_DB') "resolved from $($asked.Source)"
    Expect ($asked.Database -eq 'ai_investment_dev') "resolved database $($asked.Database)"
    "default=$($default.Database) requested=$($asked.Database)"
}

Check 'an unrecognised AIINV_SCAFFOLD_TARGET is refused rather than guessed' {
    $r = Resolve-ScaffoldTarget -Requested 'production' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'an unknown target must not resolve'
    Expect ($r.ExitCode -eq 6) "exit $($r.ExitCode), expected 6"
    "refused exit=$($r.ExitCode)"
}

Check "'designtime' is accepted case-insensitively" {
    $designtimeDev = 'Host=127.0.0.1;Database=ai_investment_dev;Username=postgres'
    $r = Resolve-ScaffoldTarget -Requested 'DESIGNTIME' -TestConnection $testCs -DesigntimeConnection $designtimeDev -EvidenceOptIn ''
    Expect $r.Allowed 'DESIGNTIME should mean designtime'
    Expect ($r.Source -eq 'AIINV_DESIGNTIME_DB') "resolved from $($r.Source)"
    "ok"
}

# ---------------------------------------------------------------- the evidence-database guard
Check 'the evidence database is REFUSED without an opt-in' {
    $r = Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'ai_investment must be refused'
    Expect ($r.ExitCode -eq 4) "exit $($r.ExitCode), expected 4"
    Expect ($r.Report -match 'YES - protected') 'the report must say the target is protected'
    "refused exit=$($r.ExitCode)"
}

Check 'a wrong or half-remembered opt-in token does not open the evidence database' {
    foreach ($attempt in @('yes', '1', 'true', 'i-understand-this-is-the-evidence-database', ($token + 'X'))) {
        $r = Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn $attempt
        Expect (-not $r.Allowed) "'$attempt' must not be accepted as the opt-in"
        Expect ($r.ExitCode -eq 4) "'$attempt' gave exit $($r.ExitCode), expected 4"
    }
    'five near-miss tokens all refused'
}

Check 'the exact opt-in token IS accepted, and the decision is visible' {
    $r = Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn $token
    Expect $r.Allowed 'the explicit opt-in should be honoured'
    Expect $r.OptInSupplied 'the result must record that an opt-in was supplied'
    Expect ($r.Report -match 'SUPPLIED') 'the report must show the opt-in'
    Expect ($r.Report -match 'ai_investment') 'the report must name the database it is about to target'
    Expect ($r.Report -match 'YES - protected') 'the report must still say the target is protected'
    'allowed, and reported'
}

Check 'the guard is on the database NAME, not on which variable carried it' {
    # ai_investment placed in AIINV_TEST_POSTGRES, reached by the DEFAULT path. Still refused.
    $r = Resolve-ScaffoldTarget -Requested '' -TestConnection $evidenceCs -DesigntimeConnection '' -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'the evidence database must be refused however it arrived'
    Expect ($r.ExitCode -eq 4) "exit $($r.ExitCode), expected 4"
    "refused exit=$($r.ExitCode) via AIINV_TEST_POSTGRES"
}

Check 'the guard is case-insensitive about the database name' {
    $r = Resolve-ScaffoldTarget -Requested '' -TestConnection $evidenceMixedCase -DesigntimeConnection '' -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'AI_Investment is ai_investment'
    Expect ($r.ExitCode -eq 4) "exit $($r.ExitCode), expected 4"
    'refused'
}

Check 'a connection string that names no database is refused, not assumed' {
    $r = Resolve-ScaffoldTarget -Requested '' -TestConnection $namelessCs -DesigntimeConnection '' -EvidenceOptIn ''
    Expect (-not $r.Allowed) 'an uncheckable target must not be allowed'
    Expect ($r.ExitCode -eq 5) "exit $($r.ExitCode), expected 5"
    'refused'
}

# ---------------------------------------------------------------- reporting
Check 'the target report names the server and the database, on every path' {
    $cases = @(
        (Resolve-ScaffoldTarget -Requested '' -TestConnection $testCs -DesigntimeConnection '' -EvidenceOptIn ''),
        (Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection '' -DesigntimeConnection $evidenceCs -EvidenceOptIn ''),
        (Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection '' -DesigntimeConnection $evidenceCs -EvidenceOptIn $token)
    )

    foreach ($case in $cases) {
        Expect ($case.Report -match '127\.0\.0\.1') 'the report must name the server'
        Expect ($case.Report -match 'database    :') 'the report must have a database line'
        Expect ($case.Report -match 'decision    :') 'the report must state a decision'
    }
    "$($cases.Count) reports, all naming server and database"
}

Check 'no report and no result field ever carries the password' {
    $cases = @(
        (Resolve-ScaffoldTarget -Requested '' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn ''),
        (Resolve-ScaffoldTarget -Requested 'designtime' -TestConnection $testCs -DesigntimeConnection $evidenceCs -EvidenceOptIn ''),
        (Resolve-ScaffoldTarget -Requested '' -TestConnection $namelessCs -DesigntimeConnection '' -EvidenceOptIn '')
    )

    foreach ($case in $cases) {
        Expect ($case.Report -notmatch [regex]::Escape($secret)) 'the printed report must not carry the password'
    }

    # The refusal paths must not even return the connection string, so a caller cannot log it.
    foreach ($case in $cases) {
        if (-not $case.Allowed) {
            Expect ([string]::IsNullOrEmpty($case.ConnectionString)) 'a refusal must not hand back the connection string'
        }
    }
    'password absent from every report; refusals carry no connection string'
}

# ---------------------------------------------------------------- the launchers go through it
Check 'both scaffolding launchers call the resolver and no longer contain the old fallback' {
    foreach ($name in @('add-migration.cmd', 'add-migration-provenance.cmd')) {
        $path = Join-Path $scriptsDir $name
        Expect (Test-Path -LiteralPath $path) "$name is missing"

        $text = Get-Content -LiteralPath $path -Raw
        Expect ($text -match 'Resolve-ScaffoldTarget') "$name does not call the resolver"
        Expect ($text -notmatch '\$cs\s*=\s*\$env:AIINV_DESIGNTIME_DB') "$name still prefers AIINV_DESIGNTIME_DB directly"
        Expect ($text -match '\$t\.Allowed') "$name does not act on the resolver's decision"
    }
    'both launchers resolve through the guard'
}

# ---------------------------------------------------------------- the apply guard is intact
Check 'the apply-time guard in run-provenance-migrate.ps1 was not weakened' {
    Expect (Test-Path -LiteralPath $applyGuardPath) 'run-provenance-migrate.ps1 is missing'

    $text = Get-Content -LiteralPath $applyGuardPath -Raw
    $testFirst = $text.IndexOf('$cs = $env:AIINV_TEST_POSTGRES', [StringComparison]::Ordinal)
    $designtimeAfter = $text.IndexOf('$cs = $env:AIINV_DESIGNTIME_DB', [StringComparison]::Ordinal)

    Expect ($testFirst -ge 0) 'the apply guard no longer reads AIINV_TEST_POSTGRES first'
    Expect ($designtimeAfter -gt $testFirst) 'the apply guard no longer prefers the test variable'
    Expect ($text -match "eq\s+'ai_investment'") 'the apply guard no longer names ai_investment'
    Expect ($text -match 'exit 4') 'the apply guard no longer exits 4 on the evidence database'

    $hash = (Get-FileHash -LiteralPath $applyGuardPath -Algorithm SHA256).Hash
    "intact; sha256=$hash"
}

# ---------------------------------------------------------------- it cannot touch a database
Check 'neither the resolver nor these tests can execute a migration or open a connection' {
    # The tokens are assembled from halves so that writing this check does not itself put the
    # forbidden strings into the files being scanned. A check written as a plain literal would match
    # itself, and fail for the wrong reason.
    $forbidden = @(
        @('dot', 'net ef'),
        @('database ', 'update'),
        @('migrations ', 'add'),
        @('Npg', 'sql'),
        @('Invoke-', 'Sqlcmd'),
        @('System.Data.', 'Common')
    )

    foreach ($path in @($resolverPath, $PSCommandPath)) {
        $text = Get-Content -LiteralPath $path -Raw

        foreach ($pair in $forbidden) {
            $needle = $pair[0] + $pair[1]
            Expect ($text -notmatch [regex]::Escape($needle)) "$([System.IO.Path]::GetFileName($path)) contains '$needle'"
        }
    }
    'no migration tooling and no database client in either file'
}

# ---------------------------------------------------------------- history untouched
Check 'the migration history on disk is byte-identical to how these tests found it' {
    $after = @(Snapshot-Migrations)

    Expect ($after.Count -eq $before.Count) "migration file count changed: $($before.Count) -> $($after.Count)"

    for ($i = 0; $i -lt $after.Count; $i++) {
        Expect ($after[$i].Name -eq $before[$i].Name) "migration file $i renamed"
        Expect ($after[$i].Hash -eq $before[$i].Hash) "migration file $($after[$i].Name) changed"
    }
    "$($after.Count) migration files, all unchanged"
}

# ---------------------------------------------------------------- report
$failed = @($results | Where-Object { -not $_.Passed })

foreach ($result in $results) {
    $mark = if ($result.Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ('  ' + $mark + '  ' + $result.Name)
    if ($result.Detail) { Write-Host ('          ' + $result.Detail) }
}

Write-Host ''
Write-Host ('  ' + $results.Count + ' checks, ' + $failed.Count + ' failed.')

$evidence = [PSCustomObject]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    repoRoot       = $RepoRoot
    checks         = @($results)
    total          = $results.Count
    failed         = $failed.Count
    migrationFiles = @($before)
    databaseUsed   = $false
    migrationRun   = $false
}

$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -Path (Join-Path $verifyDir 'scaffold-guard-tests.json') -Encoding UTF8

Write-Host ('  Evidence: ' + (Join-Path $verifyDir 'scaffold-guard-tests.json'))
Write-Host ''

if ($failed.Count -ne 0) { exit 1 }

exit 0

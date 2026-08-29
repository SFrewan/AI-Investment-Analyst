#requires -Version 5.1
<#
    DATABASE PROVENANCE INVESTIGATION. READ ONLY.

    Applies no migration, creates no table, alters no schema, writes no row, does not
    touch RunCycles, and makes no EODHD request. Every statement below is a SELECT, a
    catalogue read, or an EF Core command that reports rather than applies.

    WHY THIS EXISTS - AND A CORRECTION
    The observer reported "no EF Core migrations history table". That was very probably
    MY BUG, not your database. Test-Relation asked:

        to_regclass('public.__EFMigrationsHistory')

    PostgreSQL folds an UNQUOTED identifier to lower case, so that looks for
    __efmigrationshistory. EF Core creates the table as "__EFMigrationsHistory" - quoted,
    mixed case - so the lookup returns null whether or not the table exists. Every other
    relation this project uses is lower-case snake_case, which is exactly why they were
    all found and only this one was reported missing.

    Section C therefore asks three different ways, one of which cannot be fooled by
    identifier folding. Do not act on the earlier reading.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'database-investigation.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }

$apiProject  = Join-Path $root 'src\AI.Investment.Api'
$infraProject = Join-Path $root 'src\AI.Investment.Infrastructure'

Say '=== DATABASE PROVENANCE INVESTIGATION (READ ONLY) ==='
Say ("UTC now : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ("powershell : " + $PSVersionTable.PSVersion.ToString())
Say ''

# --------------------------------------------------------------------------------------
# Connect
# --------------------------------------------------------------------------------------
$psql = $null
try {
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
    }
    if ([string]::IsNullOrWhiteSpace($psql)) { $psql = $null }
}
catch { $psql = $null }

$script:Pg = $false
$script:H='localhost'; $script:P='5432'; $script:D=''; $script:U=''

if ($null -ne $psql) {
    $cs = $null; $text = $null
    try {
        $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
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

    if (-not [string]::IsNullOrWhiteSpace($cs)) {
        # Which keys are set is useful evidence; the values of secrets are not shown.
        $keys = @()
        $parts = @{}
        foreach ($seg in @($cs -split ';')) {
            $j = $seg.IndexOf('=')
            if ($j -gt 0) {
                $k = $seg.Substring(0,$j).Trim()
                $keys += $k
                $parts[$k.ToLowerInvariant()] = $seg.Substring($j+1).Trim()
            }
        }
        $cs = $null

        if ($parts.ContainsKey('host'))     { $script:H = $parts['host'] }
        if ($parts.ContainsKey('port'))     { $script:P = $parts['port'] }
        if ($parts.ContainsKey('database')) { $script:D = $parts['database'] }
        if ($parts.ContainsKey('username')) { $script:U = $parts['username'] }
        if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
        $parts = $null
        $script:Pg = $true

        Say '--- A. WHAT THE CONFIGURATION POINTS AT ---'
        Say ('  connection string keys : ' + ($keys -join ', ') + '   (values not shown)')
        Say ('  host / port / database : ' + $script:H + ' / ' + $script:P + ' / ' + $script:D)
        Say ('  username               : ' + $script:U)
    }
}

if (-not $script:Pg) {
    Say '  Could not resolve psql or the connection string. Nothing further can be read.'
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
    exit 1
}

function Sql([string]$sql, [switch]$Quiet) {
    $arguments = @('-h', $script:H, '-p', $script:P, '-U', $script:U, '-d', $script:D, '-A', '-F', ' | ', '-c', $sql)
    if ($Quiet) { $arguments = @('-t') + $arguments }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null; $code = 0
    try { $raw = & $psql @arguments 2>&1; $code = $LASTEXITCODE }
    catch { return @('QUERY FAILED: ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $previous }

    if ($code -ne 0) {
        $d = @(($raw | Out-String) -split "`n" | Where-Object { $_ -match 'ERROR' } | Select-Object -First 1)
        return @('QUERY FAILED: ' + $(if ($d.Count -gt 0) { $d[0].Trim() } else { 'query failed' }))
    }

    return @(($raw | Out-String) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Block([string]$title, [string]$sql) {
    Say ''
    Say ('--- ' + $title + ' ---')
    foreach ($r in (Sql $sql)) { Say ('  ' + $r.TrimEnd()) }
}

try {
    # ----------------------------------------------------------------------------------
    Block 'A2. WHAT THIS SESSION IS ACTUALLY CONNECTED TO' `
        'select current_database() as database, current_user as "user", current_schema() as schema, version() as server'

    # ----------------------------------------------------------------------------------
    # Q1, properly: which database the RUNNING API holds connections to. The configuration
    # says what it was told; pg_stat_activity says what it did.
    # ----------------------------------------------------------------------------------
    Block 'B. LIVE CONNECTIONS TO THIS SERVER (what the API is really using)' `
        ("select datname, usename, coalesce(application_name,'') as application, " +
         "coalesce(client_addr::text,'local') as client, state, " +
         "to_char(backend_start,'YYYY-MM-DD HH24:MI:SS') as since " +
         'from pg_stat_activity where backend_type = \x27client backend\x27 ' +
         'order by datname, backend_start')

    Block 'B2. EVERY DATABASE ON THIS SERVER' `
        ("select datname, pg_catalog.pg_get_userbyid(datdba) as owner, " +
         "pg_size_pretty(pg_database_size(datname)) as size " +
         'from pg_database where datistemplate = false order by datname')

    # ----------------------------------------------------------------------------------
    # C. The migrations history, asked three ways. The third cannot be fooled by folding.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- C. EF CORE MIGRATIONS HISTORY (the corrected check) ---'

    $unquoted = (Sql "select to_regclass('public.__EFMigrationsHistory') is not null" -Quiet) -join ''
    $quoted   = (Sql "select to_regclass('public.\`"__EFMigrationsHistory\`"') is not null" -Quiet) -join ''

    Say ('  to_regclass, UNQUOTED  (what the observer asked, and why it was wrong) : ' + $unquoted.Trim())
    Say ('  to_regclass, QUOTED    (the correct question)                          : ' + $quoted.Trim())
    Say ''
    Say '  Catalogue search, case-insensitive - this cannot be fooled by identifier folding:'
    foreach ($r in (Sql ("select table_schema, table_name from information_schema.tables " +
                         "where lower(table_name) like '%migrationshistory%'"))) {
        Say ('    ' + $r.TrimEnd())
    }

    Say ''
    Say '  Applied migrations, if the table is there:'
    foreach ($r in (Sql 'select "MigrationId", "ProductVersion" from "__EFMigrationsHistory" order by "MigrationId"')) {
        Say ('    ' + $r.TrimEnd())
    }

    # ----------------------------------------------------------------------------------
    Block 'D. EVERY TABLE IN THIS DATABASE' `
        ("select table_schema, table_name, " +
         "pg_catalog.pg_get_userbyid(c.relowner) as owner " +
         'from information_schema.tables t ' +
         'join pg_class c on c.relname = t.table_name ' +
         'join pg_namespace n on n.oid = c.relnamespace and n.nspname = t.table_schema ' +
         "where t.table_schema not in ('pg_catalog','information_schema') and t.table_type = 'BASE TABLE' " +
         'order by table_schema, table_name')

    Block 'D2. VIEWS, IF ANY' `
        ("select table_schema, table_name from information_schema.views " +
         "where table_schema not in ('pg_catalog','information_schema') order by 1,2")

    # ----------------------------------------------------------------------------------
    # G. Existing data. What a rebuild would destroy, stated as numbers.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- E. ROW COUNTS (what a rebuild would destroy) ---'
    foreach ($r in (Sql ("select relname as table_name, n_live_tup as approx_rows " +
                         'from pg_stat_user_tables order by n_live_tup desc, relname'))) {
        Say ('  ' + $r.TrimEnd())
    }
    Say ''
    Say '  n_live_tup is the planner estimate. Exact counts for the rows that matter:'
    foreach ($t in @('watches','data_sources','audit_records','action_executions','ingestion_runs',
                     'observations','opportunities','operating_cycles','escalations','ledger_entries')) {
        $exists = ((Sql ("select to_regclass('public." + $t + "') is not null") -Quiet) -join '').Trim()
        if ($exists -eq 't') {
            Say ('    ' + $t.PadRight(20) + ' : ' + ((Sql ("select count(*) from " + $t) -Quiet) -join ''))
        }
        else {
            Say ('    ' + $t.PadRight(20) + ' : NOT PRESENT')
        }
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# --------------------------------------------------------------------------------------
# F. What EF Core itself says. Both commands REPORT; neither applies anything.
# --------------------------------------------------------------------------------------
Say ''
Say '--- F. WHAT EF CORE REPORTS (nothing is applied) ---'

$efOk = $false
try {
    $v = (& dotnet ef --version 2>&1) -join ' '
    if ($LASTEXITCODE -eq 0) { $efOk = $true; Say ('  dotnet-ef : ' + $v.Trim()) }
    else { Say '  dotnet-ef : NOT INSTALLED. Install with: dotnet tool install --global dotnet-ef' }
}
catch { Say '  dotnet-ef : NOT INSTALLED.' }

if ($efOk) {
    Say ''
    Say '  migrations list (Applied / Pending, read from the database):'

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $listed = & dotnet ef migrations list --project $infraProject --startup-project $apiProject `
                    --configuration Release --no-color 2>&1
        foreach ($r in @(($listed | Out-String) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
            Say ('    ' + $r.TrimEnd())
        }
    }
    catch { Say ('    FAILED : ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $previous }

    # An idempotent script is the exact answer to "what would applying it do". Generating
    # it reads the model and writes a FILE. It does not touch the database.
    $sqlOut = Join-Path $out 'pending-migration.sql'

    Say ''
    Say '  Generating the idempotent migration script (writes a file; applies nothing):'

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $null = & dotnet ef migrations script --idempotent --project $infraProject `
                    --startup-project $apiProject --configuration Release --no-color `
                    --output $sqlOut 2>&1

        if (Test-Path -LiteralPath $sqlOut) {
            $text = Get-Content -LiteralPath $sqlOut -Raw
            Say ('    written : ' + $sqlOut)
            Say ''
            Say '    Statements it contains:'
            foreach ($verb in @('CREATE TABLE','ALTER TABLE','DROP TABLE','DROP COLUMN',
                                'CREATE INDEX','CREATE UNIQUE INDEX','DROP INDEX','DELETE','UPDATE','INSERT')) {
                $n = ([regex]::Matches($text, [regex]::Escape($verb), 'IgnoreCase')).Count
                if ($n -gt 0) { Say ('      ' + $verb.PadRight(22) + ' x ' + $n) }
            }
            Say ''
            Say '    Tables it would CREATE:'
            foreach ($m in [regex]::Matches($text, 'CREATE TABLE\s+(?:IF NOT EXISTS\s+)?([^\s(]+)', 'IgnoreCase')) {
                Say ('      ' + $m.Groups[1].Value)
            }
            Say ''
            Say '    Any DROP at all (this is the line to read carefully):'
            $drops = @(($text -split "`n") | Where-Object { $_ -match '(?i)\bDROP\b' })
            if ($drops.Count -eq 0) { Say '      none' }
            else { foreach ($d in ($drops | Select-Object -First 20)) { Say ('      ' + $d.Trim()) } }
        }
        else {
            Say '    the script was not produced; see the output above.'
        }
    }
    catch { Say ('    FAILED : ' + $_.Exception.Message) }
    finally { $ErrorActionPreference = $previous }
}

Say ''
Say '=== END. NOTHING WAS APPLIED OR ALTERED. ==='
Say 'No migration run. No table created. No schema altered. No row written.'
Say 'RunCycles untouched. No EODHD request.'

Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
Write-Host ''
Write-Host ('Written: ' + $log)

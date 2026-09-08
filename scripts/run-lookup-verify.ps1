#requires -Version 7.0
<#
    ARCHIVED-RUN LOOKUP - VERIFICATION AGAINST THE REAL DATABASE. READ ONLY.

    EVERY STATEMENT THIS SCRIPT ISSUES IS A SELECT. There is no INSERT, UPDATE, DELETE, COPY,
    TRUNCATE or DDL anywhere in it, and every connection is opened inside a READ ONLY transaction
    which is rolled back, so the server itself refuses a write even if one were somehow attempted.

    NO provider call. NO acquisition. NO authorisation consumed. NO payload replayed or normalised.
    NO observation written. Gate 6, SplitAdjustment, the declarations and the sealed manifest are
    neither read for a verdict nor touched.

    What it verifies: the jsonb containment predicate behind
    EfIngestionRunStore.RunsForArchivedPayloadAsync - the one part of the replay mechanism whose SQL
    compiled but had never executed. It runs that predicate verbatim against the six payloads that
    matter and reports the cardinality of each.

    Two things it learned the hard way and now handles openly.

    First, it has to find the right database. The connection the test harness is configured with
    holds no ingestion runs at all, so this enumerates the databases on that server and reports which
    one holds the ledger. Nothing is created; the finding is reported.

    Second, each optional query runs on its own connection. PostgreSQL aborts a transaction on the
    first error, so one query failing on a column name would otherwise silently take every later
    read down with it - and a verification that reports "could not read" for the wrong reason is
    worse than one that fails loudly.

    Requires PowerShell 7: Npgsql is a .NET 8 assembly and Windows PowerShell 5.1 runs on .NET
    Framework.

    NO CONNECTION STRING, HOST, USERNAME OR PASSWORD IS EVER PRINTED OR WRITTEN TO THE REPORT.
    Database names are printed, because which database holds the evidence is the question.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$verify = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $verify

$PriceSource = 'eodhd-eod'

# The six payloads. Five price targets, each of which must answer to exactly one price run, and the
# shared empty array - the ambiguous case the replay service has to refuse rather than resolve.
$targets = @(
    [pscustomobject]@{ Symbol = 'CCF.US';  Kind = 'target'; Hash = 'ae79be18dc5534772905b3d184472916a45d0446b090b0f29af64237086dd679' }
    [pscustomobject]@{ Symbol = 'EVBG.US'; Kind = 'target'; Hash = '438650fad031491550ee2f1c2d673d6da4a716d03cd2ab74f1124ce6212d270d' }
    [pscustomobject]@{ Symbol = 'GPP.US';  Kind = 'target'; Hash = '626806275c359cc70dc7bdc9021375b4109556abeefca6190d2b468c4c487fc0' }
    [pscustomobject]@{ Symbol = 'VLDR.US'; Kind = 'target'; Hash = 'a2b139e5e2c068b1c031c5ed0ca233550fdd9787f73f4b1fa63ada98771ac711' }
    [pscustomobject]@{ Symbol = 'WIRE.US'; Kind = 'target'; Hash = '6d3c18170963aab334c176d8bbfee4927b93d28429a0d150479d907469578a2f' }
    [pscustomobject]@{ Symbol = '(shared empty array)'; Kind = 'shared'; Hash = '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945' }
)

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   ARCHIVED-RUN LOOKUP - verification against the real database'
Write-Host '   READ ONLY. Every statement is a SELECT, in a READ ONLY tx.'
Write-Host '   NO recovery. NO replay. NO write of any kind.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host '  STOPPING. AIINV_TEST_POSTGRES is not set, so there is no database to verify against.'
    exit 3
}

Write-Host '  Database configured (value not printed).'

$bin = Join-Path $root 'tests\AI.Investment.Api.Tests\bin\Release\net8.0'
$npgsql = Join-Path $bin 'Npgsql.dll'

if (-not (Test-Path -LiteralPath $npgsql)) {
    Write-Host ('  STOPPING. Npgsql.dll was not found at ' + $npgsql + '. Build Release first.')
    exit 4
}

$resolver = {
    param($sender, $eventArgs)

    $name = (New-Object System.Reflection.AssemblyName($eventArgs.Name)).Name
    $candidate = Join-Path $bin ($name + '.dll')

    if (Test-Path -LiteralPath $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }

    return $null
}

[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type -Path $npgsql

Write-Host '  Npgsql loaded from the Release output - the same driver the application uses.'
Write-Host ''

# ---- read-only plumbing. One session per unit of work, so one failure cannot poison another. ----

function Use-ReadOnlySession {
    param([string] $Database, [scriptblock] $Body)

    $builder = [Npgsql.NpgsqlConnectionStringBuilder]::new($env:AIINV_TEST_POSTGRES)
    if (-not [string]::IsNullOrWhiteSpace($Database)) { $builder.Database = $Database }

    $connection = [Npgsql.NpgsqlConnection]::new($builder.ConnectionString)
    $connection.Open()
    $transaction = $connection.BeginTransaction()

    try {
        $cmd = $connection.CreateCommand()
        $cmd.Transaction = $transaction
        $cmd.CommandText = 'SET TRANSACTION READ ONLY'
        $null = $cmd.ExecuteNonQuery()

        $script:Session = [pscustomobject]@{ Connection = $connection; Transaction = $transaction }

        return & $Body
    }
    finally {
        $transaction.Rollback()
        $connection.Close()
        $script:Session = $null
    }
}

function Invoke-Rows {
    param([string] $Sql, [hashtable] $Parameters = @{})

    $cmd = $script:Session.Connection.CreateCommand()
    $cmd.Transaction = $script:Session.Transaction
    $cmd.CommandText = $Sql

    foreach ($key in $Parameters.Keys) {
        $null = $cmd.Parameters.AddWithValue($key, $Parameters[$key])
    }

    $reader = $cmd.ExecuteReader()
    $rows = [System.Collections.ArrayList]::new()

    while ($reader.Read()) {
        $row = [ordered]@{}

        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
            $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
        }

        $null = $rows.Add([pscustomobject]$row)
    }

    $reader.Close()

    # The unary comma matters: PowerShell unrolls an empty array to nothing, and "no rows" is one of
    # the answers this script exists to report. An earlier version wrapped the call in @() as well,
    # which produced an array-of-one holding the whole row set - so every count read 1. Do not add
    # @() at a call site.
    return , $rows.ToArray()
}

function Get-TableCensus {
    $tables = Invoke-Rows @'
SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
ORDER BY table_name
'@

    $census = [ordered]@{}

    foreach ($t in $tables) {
        $name = [string]$t.table_name
        $count = Invoke-Rows ('SELECT COUNT(*) AS n FROM public."' + $name + '"')
        $census[$name] = [long]$count[0].n
    }

    return $census
}

# ---- 1. which database on this server holds the ingestion ledger --------------------------------

Write-Host '--- which database on this server holds the ingestion ledger'

$serverVersion = ''
$configuredDatabase = ''

$catalogue = Use-ReadOnlySession -Database $null -Body {
    $script:serverVersionInner = $script:Session.Connection.PostgreSqlVersion.ToString()
    $script:configuredInner = $script:Session.Connection.Database

    Invoke-Rows @'
SELECT datname FROM pg_database
WHERE datistemplate = false AND datallowconn = true
ORDER BY datname
'@
}

$serverVersion = $script:serverVersionInner
$configuredDatabase = $script:configuredInner

$survey = @()

foreach ($row in $catalogue) {
    $name = [string]$row.datname
    $runs = $null
    $observations = $null
    $note = ''

    try {
        $counts = Use-ReadOnlySession -Database $name -Body {
            $present = Invoke-Rows @'
SELECT 1 AS present FROM information_schema.tables
WHERE table_schema = 'public' AND table_name = 'ingestion_runs'
'@

            if ($present.Count -eq 0) { return $null }

            [pscustomobject]@{
                Runs         = [long](Invoke-Rows 'SELECT COUNT(*) AS n FROM ingestion_runs')[0].n
                Observations = [long](Invoke-Rows 'SELECT COUNT(*) AS n FROM observations')[0].n
            }
        }

        if ($null -eq $counts) { $note = 'no ingestion_runs table' }
        else { $runs = $counts.Runs; $observations = $counts.Observations }
    }
    catch { $note = $_.Exception.GetType().Name }

    $survey += [pscustomobject]@{
        Database = $name; IsConfigured = ($name -eq $configuredDatabase)
        Runs = $runs; Observations = $observations; Note = $note
    }

    $marker = if ($name -eq $configuredDatabase) { '   <- the configured one' } else { '' }
    $shown = if ($null -eq $runs) { $note } else { 'runs ' + $runs + ', observations ' + $observations }

    Write-Host ('  ' + $name.PadRight(28) + $shown + $marker)
}

$withRuns = @($survey | Where-Object { $null -ne $_.Runs -and $_.Runs -gt 0 })

Write-Host ''

if ($withRuns.Count -eq 0) {
    Write-Host '  NO DATABASE ON THIS SERVER HOLDS ANY INGESTION RUN.'
    $chosen = $null
}
elseif ($withRuns.Count -gt 1) {
    Write-Host ('  MORE THAN ONE DATABASE HOLDS RUNS: ' + (($withRuns | ForEach-Object { $_.Database }) -join ', '))
    Write-Host '  Verifying against the one holding the most, and reporting the rest.'
    $chosen = (@($withRuns | Sort-Object -Property Runs -Descending)[0]).Database
}
else {
    $chosen = $withRuns[0].Database
    Write-Host ('  The ingestion ledger lives in: ' + $chosen)
}

Write-Host ''

$results = @()
$before = [ordered]@{}
$after = [ordered]@{}
$drift = @()
$held = @()
$quarantine = @()
$quarantineNote = ''

if ($null -ne $chosen) {

    # ---- 2. the state, before anything is asked --------------------------------------------------

    Write-Host ('--- ' + $chosen + ' as found')
    $before = Use-ReadOnlySession -Database $chosen -Body { Get-TableCensus }

    foreach ($key in $before.Keys) {
        if ($before[$key] -gt 0) { Write-Host ('    ' + $key.PadRight(32) + $before[$key]) }
    }

    # ---- 3. the containment predicate, verbatim ---------------------------------------------------

    Write-Host ''
    Write-Host '--- the containment predicate, run for each payload'
    Write-Host '    SELECT id FROM ingestion_runs WHERE artifacts @> $probe::jsonb'
    Write-Host ''

    foreach ($target in $targets) {

        $entry = Use-ReadOnlySession -Database $chosen -Body {
            $probe = '["' + $target.Hash + '"]'

            $rows = Invoke-Rows @'
SELECT id,
       source_id,
       category::text  AS category,
       region::text    AS region,
       subject_kind,
       subject_identifier,
       outcome::text   AS outcome,
       started_at_utc,
       request_fingerprint,
       jsonb_array_length(artifacts) AS artifact_count,
       artifacts::text AS artifacts
FROM ingestion_runs
WHERE artifacts @> @probe::jsonb
ORDER BY started_at_utc, id
'@ @{ probe = $probe }

            $priceRuns = @($rows | Where-Object { [string]$_.source_id -eq $PriceSource })

            [pscustomobject]@{
                Symbol           = $target.Symbol
                Kind             = $target.Kind
                ContentHash      = $target.Hash
                TotalRuns        = $rows.Count
                PriceRuns        = $priceRuns.Count
                BySource         = @($rows | Group-Object -Property { [string]$_.source_id + ' / ' + [string]$_.category } |
                    ForEach-Object { [pscustomobject]@{ SourceAndCategory = $_.Name; Runs = $_.Count } })
                Runs             = @($priceRuns | ForEach-Object {
                    [pscustomobject]@{
                        RunId              = [string]$_.id
                        SourceId           = [string]$_.source_id
                        Category           = [string]$_.category
                        Region             = [string]$_.region
                        SubjectKind        = [string]$_.subject_kind
                        SubjectIdentifier  = [string]$_.subject_identifier
                        Outcome            = [string]$_.outcome
                        StartedAtUtc       = ([datetime]$_.started_at_utc).ToString('o')
                        RequestFingerprint = [string]$_.request_fingerprint
                        ArtifactCount      = [int]$_.artifact_count
                        Artifacts          = [string]$_.artifacts
                    }
                })
            }
        }

        # A target is right when exactly one run in the whole ledger holds its payload. The shared
        # empty array is right when MORE than one does - that is the ambiguity the service refuses -
        # and its five price runs are reported separately because that is the number the earlier
        # forensic named.
        $isTarget = ($target.Kind -eq 'target')

        if ($isTarget) {
            $ok = ($entry.TotalRuns -eq 1)
            $expectation = 'exactly one run in the whole ledger'
        }
        else {
            $ok = ($entry.TotalRuns -gt 1)
            $expectation = 'more than one run - the service must refuse it'
        }

        $entry | Add-Member -NotePropertyName 'Expectation' -NotePropertyValue $expectation
        $entry | Add-Member -NotePropertyName 'MeetsExpectation' -NotePropertyValue $ok

        $results += $entry

        $verdict = if ($ok) { 'AS EXPECTED' } else { '*** NOT AS EXPECTED ***' }

        Write-Host ('  ' + $target.Symbol.PadRight(22) + $target.Hash.Substring(0, 12) +
            '  runs ' + ([string]$entry.TotalRuns).PadLeft(4) +
            '  (price runs ' + $entry.PriceRuns + ')  ' + $verdict)

        foreach ($run in $entry.Runs) {
            Write-Host ('        ' + $run.RunId + '  ' + $run.SourceId + ' / ' + $run.Category +
                '  ' + $run.SubjectKind + ':' + $run.SubjectIdentifier +
                '  ' + $run.Outcome + '  artifacts=' + $run.ArtifactCount)
        }

        if ($entry.TotalRuns -gt $entry.PriceRuns) {
            foreach ($group in $entry.BySource) {
                Write-Host ('        also ' + $group.Runs + ' run(s) from ' + $group.SourceAndCategory)
            }
        }
    }

    # ---- 4. what the five targets currently hold ---------------------------------------------------

    Write-Host ''
    Write-Host '--- observations currently held for the five targets'

    try {
        $held = Use-ReadOnlySession -Database $chosen -Body {
            Invoke-Rows @'
SELECT subject_identifier, COUNT(*) AS n
FROM observations
WHERE subject_identifier = ANY(@symbols)
GROUP BY subject_identifier
ORDER BY subject_identifier
'@ @{ symbols = [string[]]@('CCF.US', 'EVBG.US', 'GPP.US', 'VLDR.US', 'WIRE.US') }
        }

        if ($held.Count -eq 0) { Write-Host '  none - zero observations for all five' }
        else { foreach ($h in $held) { Write-Host ('  ' + ([string]$h.subject_identifier).PadRight(10) + $h.n) } }
    }
    catch { Write-Host ('  could not be read: ' + $_.Exception.Message) }

    # ---- 5. quarantine rows for these payloads, read and never touched ------------------------------

    Write-Host ''
    Write-Host '--- quarantine records for these payloads (read, never touched)'

    try {
        $quarantine = Use-ReadOnlySession -Database $chosen -Body {
            # The column names are read rather than assumed. A guess that misses would abort the
            # transaction and take every later read with it.
            $columns = Invoke-Rows @'
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'quarantined_payloads'
ORDER BY ordinal_position
'@

            $names = @($columns | ForEach-Object { [string]$_.column_name })
            $script:quarantineColumns = $names -join ', '

            $key = if ($names -contains 'id') { 'id' } elseif ($names -contains 'content_hash') { 'content_hash' } else { $null }
            if ($null -eq $key) { return , @() }

            $rule = if ($names -contains 'rule_id') { 'rule_id' } else { 'NULL' }

            Invoke-Rows ("SELECT $key AS payload, $rule AS rule_id FROM quarantined_payloads WHERE $key = ANY(@hashes) ORDER BY 1") `
                @{ hashes = [string[]]@($targets | ForEach-Object { $_.Hash }) }
        }

        $quarantineNote = 'columns: ' + $script:quarantineColumns

        if ($quarantine.Count -eq 0) { Write-Host '  none matched' }
        else { foreach ($q in $quarantine) { Write-Host ('  ' + ([string]$q.payload).Substring(0, 12) + '  ' + [string]$q.rule_id) } }
    }
    catch {
        $quarantineNote = 'could not be read: ' + $_.Exception.Message
        Write-Host ('  ' + $quarantineNote)
    }

    # ---- 6. the state again -------------------------------------------------------------------------

    Write-Host ''
    Write-Host ('--- ' + $chosen + ' after every read')
    $after = Use-ReadOnlySession -Database $chosen -Body { Get-TableCensus }

    foreach ($key in $before.Keys) {
        $b = $before[$key]
        $a = if ($after.Contains($key)) { $after[$key] } else { -1 }
        if ($a -ne $b) { $drift += ($key + ': ' + $b + ' -> ' + $a) }
    }

    foreach ($key in $after.Keys) {
        if (-not $before.Contains($key)) { $drift += ($key + ': table appeared') }
    }

    if ($drift.Count -eq 0) { Write-Host '  every table holds exactly the row count it held before. Nothing changed.' }
    else { foreach ($d in $drift) { Write-Host ('  CHANGED  ' + $d) } }
}

# ---- 7. the verdict --------------------------------------------------------------------------------

$targetRows = @($results | Where-Object { $_.Kind -eq 'target' })
$sharedRow = @($results | Where-Object { $_.Kind -eq 'shared' })

$targetsOk = ($targetRows.Count -eq 5) -and (@($targetRows | Where-Object { -not $_.MeetsExpectation }).Count -eq 0)
$sharedAmbiguous = ($sharedRow.Count -eq 1) -and $sharedRow[0].MeetsExpectation
$noObservations = ($held.Count -eq 0)
$ready = $targetsOk -and $sharedAmbiguous -and ($drift.Count -eq 0) -and $noObservations

if ($null -eq $chosen) { $verdict = 'NOT READY - no database on this server holds any ingestion run' }
elseif (-not $targetsOk) { $verdict = 'NOT READY - a target payload does not resolve to exactly one run' }
elseif (-not $noObservations) { $verdict = 'NOT READY - a target already holds observations' }
elseif ($drift.Count -gt 0) { $verdict = 'NOT READY - the database changed during a read-only verification' }
elseif (-not $sharedAmbiguous) { $verdict = 'NOT READY - the shared payload did not present as ambiguous' }
else { $verdict = 'READY FOR RECOVERY' }

$report = [pscustomobject]@{
    RanAtUtc                   = (Get-Date).ToUniversalTime().ToString('o')
    ServerVersion              = $serverVersion
    ConfiguredDatabase         = $configuredDatabase
    DatabaseSurvey             = $survey
    DatabaseVerified           = $chosen
    Payloads                   = $results
    ObservationsHeldForTargets = @($held | ForEach-Object { [pscustomobject]@{ Symbol = [string]$_.subject_identifier; Count = [long]$_.n } })
    QuarantineRows             = @($quarantine | ForEach-Object { [pscustomobject]@{ Payload = [string]$_.payload; RuleId = [string]$_.rule_id } })
    QuarantineNote             = $quarantineNote
    TableCountsBefore          = $before
    TableCountsAfter           = $after
    Drift                      = $drift
    FiveTargetsEachExactlyOneRun = $targetsOk
    SharedPayloadIsAmbiguous   = $sharedAmbiguous
    TargetsHoldNoObservations  = $noObservations
    NothingChanged             = ($drift.Count -eq 0)
    Verdict                    = $verdict
}

$out = Join-Path $verify 'replay-db-lookup.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding UTF8

Write-Host ''
Write-Host ('=== ' + $verdict)
Write-Host ('=== written: ' + $out)
Write-Host ''
Write-Host '  EVERY STATEMENT WAS A SELECT, IN A READ ONLY TRANSACTION, AND EVERY ONE WAS ROLLED BACK.'
Write-Host '  NO RECOVERY WAS PERFORMED. NO PAYLOAD WAS REPLAYED OR NORMALISED.'
Write-Host '  NO OBSERVATION WAS WRITTEN. NO AUTHORISATION WAS TOUCHED. NO QUARANTINE ROW CHANGED.'
Write-Host '  NO CONNECTION STRING, HOST, USERNAME OR PASSWORD WAS PRINTED OR WRITTEN TO THE REPORT.'

exit $(if ($ready) { 0 } else { 1 })

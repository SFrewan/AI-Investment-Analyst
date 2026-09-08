#requires -Version 7.0
<#
    FINAL PRE-RECOVERY PREFLIGHT - THE REAL C# PATH, AGAINST THE REAL DATABASE. READ ONLY.

    NO RECOVERY. NO REPLAY OF A TARGET. NO NORMALISATION. NO OBSERVATION WRITTEN. NO AUTHORISATION
    consumed or created. NO provider call. NO production file, test, Gate 6 artefact, declaration,
    sealed manifest or SplitAdjustment touched.

    What this exercises, in production code, against the real ai_investment database:

        ContentHash
          -> EfIngestionRunStore.RunsForArchivedPayloadAsync   (the jsonb containment SQL)
          -> IngestionRunId.Create                              (inside that method)
          -> EF materialisation of IngestionRun and its owned request
          -> deterministic ordering
          -> FileSystemRawResponseArchive.DescribeAsync         (the archive precondition)
          -> ArchivedPayloadReplayService.ReplayAsync           (for the AMBIGUOUS payload only)

    Two safety properties are structural rather than promised.

    The DbContext is constructed with a write authorisation that never authorises: any SaveChanges
    on this session throws, and an attempt to open an authorisation window throws before it opens.

    The replay service is constructed with a normalisation pipeline that throws if it is ever
    called and remembers that it was. It is invoked for the shared empty array only, where the
    service must refuse on ambiguity before reaching the pipeline. It is NEVER invoked for the five
    targets, because for them the preconditions pass and the service would go on to normalise - so
    each of their preconditions is evaluated individually instead, using the same components in the
    same order.

    NO CONNECTION STRING, USERNAME OR PASSWORD IS PRINTED OR WRITTEN. The resolved database name,
    host and server version are printed, because proving which database was used is the point.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$verify = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $verify

$RequiredDatabase = 'ai_investment'
$ForbiddenDatabase = 'ai_investment_tests'

$symbols = @('CCF.US', 'EVBG.US', 'GPP.US', 'VLDR.US', 'WIRE.US')

$hashes = @(
    'ae79be18dc5534772905b3d184472916a45d0446b090b0f29af64237086dd679'
    '438650fad031491550ee2f1c2d673d6da4a716d03cd2ab74f1124ce6212d270d'
    '626806275c359cc70dc7bdc9021375b4109556abeefca6190d2b468c4c487fc0'
    'a2b139e5e2c068b1c031c5ed0ca233550fdd9787f73f4b1fa63ada98771ac711'
    '6d3c18170963aab334c176d8bbfee4927b93d28429a0d150479d907469578a2f'
)

$expectedRunIds = @(
    '45d3fe5c-69ca-48a8-a7b2-4ad4dd2c8552'
    'e2f77177-4827-43bf-8951-2f56a28c0f2e'
    '4c1bc92f-ade8-4535-8322-053e1dcafbbb'
    'e4097a2d-1db7-4c85-9388-6b45cad106c4'
    'bc0eb767-473d-481d-ad3d-d9d9bc491c50'
)

$sharedHash = '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   FINAL PRE-RECOVERY PREFLIGHT - the real C# path, real database'
Write-Host '   READ ONLY. Writes are refused by construction, not by intent.'
Write-Host '   NO RECOVERY. NO NORMALISATION. NO REPLAY OF ANY TARGET.'
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

$bin = Join-Path $root 'tests\AI.Investment.Api.Tests\bin\Release\net8.0'
$archiveRoot = Join-Path $bin 'archive'

if (-not (Test-Path -LiteralPath $archiveRoot)) {
    Write-Host ('  STOPPING. The archive root does not exist: ' + $archiveRoot)
    exit 4
}

# The configured connection points at the test database. The evidence is in ai_investment, so the
# database is switched here EXPLICITLY and the switch is reported - the connection is never trusted
# for being named what we hoped.
$builder = $null
Add-Type -Path (Join-Path $bin 'Npgsql.dll')
$builder = [Npgsql.NpgsqlConnectionStringBuilder]::new($env:AIINV_TEST_POSTGRES)

Write-Host ('  Configured database  : ' + $builder.Database)

if ($builder.Database -eq $RequiredDatabase) {
    Write-Host '  Already the required database; no switch needed.'
}
else {
    Write-Host ('  Switching explicitly to: ' + $RequiredDatabase)
    $builder.Database = $RequiredDatabase
}

if ($builder.Database -eq $ForbiddenDatabase) {
    Write-Host ('  STOPPING. The resolved database is ' + $ForbiddenDatabase + ', which holds no evidence.')
    exit 5
}

$connectionString = $builder.ConnectionString

Write-Host ('  Archive root         : ' + $archiveRoot)
Write-Host ''

# ---- compile the shim in process ------------------------------------------------------------------

Write-Host '--- compiling the read-only preflight shim against the built assemblies'

$references = @(
    'AI.Investment.Domain.dll'
    'AI.Investment.Application.dll'
    'AI.Investment.Infrastructure.dll'
    'Npgsql.dll'
    'Npgsql.EntityFrameworkCore.PostgreSQL.dll'
    'Microsoft.EntityFrameworkCore.dll'
    'Microsoft.EntityFrameworkCore.Abstractions.dll'
    'Microsoft.EntityFrameworkCore.Relational.dll'
    'Microsoft.Extensions.Options.dll'
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
    'Microsoft.Extensions.Logging.Abstractions.dll'
    'Microsoft.Extensions.Caching.Memory.dll'
) | ForEach-Object { Join-Path $bin $_ }

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        Write-Host ('  STOPPING. Missing assembly: ' + $reference)
        exit 6
    }
}

# Loaded before compiling so the shim's references resolve to exactly these, and so that anything
# they pull in is found in the same folder rather than wherever PowerShell would otherwise look.
$resolver = {
    param($sender, $eventArgs)

    $name = (New-Object System.Reflection.AssemblyName($eventArgs.Name)).Name
    $candidate = Join-Path $bin ($name + '.dll')

    if (Test-Path -LiteralPath $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }

    return $null
}

[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

foreach ($reference in $references) { Add-Type -Path $reference -ErrorAction SilentlyContinue }

# System.Text.Json is not in the Release output - it belongs to the runtime this script is already
# running on. Resolved from the loaded type rather than a guessed path, so the reference is the very
# assembly in memory: nothing is downloaded, nothing is installed, and no version is assumed. Its
# absence from the explicit list is what CS0234 was reporting, because supplying
# -ReferencedAssemblies narrows the compilation to what is named.
$systemTextJson = [System.Text.Json.JsonSerializer].Assembly.Location

if ([string]::IsNullOrWhiteSpace($systemTextJson) -or -not (Test-Path -LiteralPath $systemTextJson)) {
    Write-Host '  STOPPING. System.Text.Json could not be located in this runtime.'
    exit 7
}

Write-Host ('  System.Text.Json : ' + $systemTextJson)
Write-Host ('  version          : ' + [System.Reflection.AssemblyName]::GetAssemblyName($systemTextJson).Version)

$references += $systemTextJson

$shim = Get-Content -Path (Join-Path $PSScriptRoot 'preflight-shim.cs') -Raw

Add-Type -TypeDefinition $shim -ReferencedAssemblies $references -Language CSharp

Write-Host '  compiled.'
Write-Host ''

# ---- run it ----------------------------------------------------------------------------------------

Write-Host '--- running the preflight'
Write-Host ''

$json = [ReplayPreflight]::Run($connectionString, $archiveRoot, $symbols, $hashes, $expectedRunIds, $sharedHash)

$out = Join-Path $verify 'replay-final-preflight.json'
Set-Content -Path $out -Value $json -Encoding UTF8

$result = $json | ConvertFrom-Json

if ($null -ne $result.PSObject.Properties['Refused']) {
    Write-Host ('  REFUSED: ' + $result.Refused)
    exit 5
}

Write-Host ('  database      : ' + $result.Identity.CurrentDatabase)
Write-Host ('  server        : ' + $result.Identity.DataSource + '  (' + $result.Identity.ServerVersion + ')')
Write-Host ('  schema        : ' + $result.Identity.CurrentSchema)
Write-Host ('  archive root  : ' + $result.ArchiveRoot)
Write-Host ''

Write-Host '--- the five targets, through the real lookup and materialisation'
Write-Host ''

$allPass = $true

foreach ($t in $result.Targets) {
    $ok = $t.AllPreconditionsPass
    if (-not $ok) { $allPass = $false }

    $verdict = if ($ok) { 'ALL PRECONDITIONS PASS' } else { '*** FAILED ***' }

    Write-Host ('  ' + ([string]$t.Symbol).PadRight(9) + $verdict)
    Write-Host ('      runs returned      ' + $t.RunsReturned + '   materialised: ' + $t.Materialised)

    if ($t.Materialised) {
        Write-Host ('      run id             ' + $t.RunId + '   matches expected: ' + $t.RunIdMatchesExpected)
        Write-Host ('      source / category  ' + $t.SourceId + ' / ' + $t.Category + '   (' + $t.Region + ')')
        Write-Host ('      subject            ' + $t.SubjectKind + ':' + $t.SubjectIdentifier + '   matches: ' + $t.SubjectMatchesSymbol)
        Write-Host ('      outcome            ' + $t.Outcome)
        Write-Host ('      artifacts          ' + $t.ArtifactCount + '   is the expected payload: ' + $t.ArtifactIsTheExpectedPayload)
        Write-Host ('      archive describes  ' + $t.ArchiveDescribesEveryArtifact)

        foreach ($p in $t.ArchivedPayloads) {
            Write-Host ('        payload ' + ([string]$p.Artifact).Substring(0, 12) + '  ' + $p.ByteLength + ' B  retrieved ' + $p.RetrievedAtUtc)
        }

        Write-Host ('      observations held  ' + $t.ObservationsHeldForSubject + '   holds none: ' + $t.HoldsNoObservations)
        Write-Host ('      idempotency key    ' + $t.IdempotencyKey)
        Write-Host ('      key is free        ' + $t.IdempotencyKeyIsFree)
    }

    Write-Host ''
}

Write-Host '--- the shared empty array, through the actual replay service'
$s = $result.SharedEmptyArray
Write-Host ('  runs returned            ' + $s.RunsReturned + '   (price runs ' + $s.PriceRuns + ')')
Write-Host ('  price run subjects       ' + ($s.PriceRunSubjects -join ', '))
Write-Host ('  service status           ' + $s.ServiceStatus)
Write-Host ('  chose a run              ' + $s.ServiceChoseARun)
Write-Host ('  recorded observations    ' + $s.ServiceRecordedObservations)
Write-Host ('  normalisation attempted  ' + $s.NormalizationWasAttempted)
Write-Host ('  reason                   ' + $s.Reason)
Write-Host ''

Write-Host ('--- lookup is deterministic: ' + $result.LookupIsDeterministic)
Write-Host ''

Write-Host '--- row counts, before and after'
$drift = @()

foreach ($name in @('observations', 'ingestion_runs', 'processed_actions', 'quarantined_payloads')) {
    $b = $result.Before.$name
    $a = $result.After.$name
    $same = ($b -eq $a)
    if (-not $same) { $drift += ($name + ': ' + $b + ' -> ' + $a) }

    Write-Host ('  ' + $name.PadRight(24) + ([string]$b).PadLeft(10) + ' -> ' + ([string]$a).PadLeft(10) +
        '   ' + $(if ($same) { 'identical' } else { '*** CHANGED ***' }))
}

Write-Host ''

$sharedOk = ($s.ServiceStatus -eq 'MoreThanOneRunHoldsThisPayload') -and
            (-not $s.ServiceChoseARun) -and
            (-not $s.NormalizationWasAttempted) -and
            ($s.ObservationsRecorded -eq 0)

$ready = $allPass -and $sharedOk -and ($drift.Count -eq 0) -and
         $result.LookupIsDeterministic -and (-not $result.NormalizationEverAttempted) -and
         ($result.Identity.CurrentDatabase -eq $RequiredDatabase)

if ($ready) { Write-Host '=== PREFLIGHT PASSED - READY FOR RECOVERY' }
else { Write-Host '=== PREFLIGHT FAILED - see the entries above. Do not recover.' }

Write-Host ('=== written: ' + $out)
Write-Host ''
Write-Host '  NO RECOVERY WAS PERFORMED. NO TARGET WAS REPLAYED. NORMALISATION WAS NEVER CALLED.'
Write-Host '  THE CONTEXT WAS BUILT WITH A WRITE AUTHORISATION THAT NEVER AUTHORISES.'
Write-Host '  NO OBSERVATION WAS WRITTEN. NO AUTHORISATION WAS CONSUMED. NO PROVIDER WAS CONTACTED.'
Write-Host '  NO CONNECTION STRING, USERNAME OR PASSWORD WAS PRINTED OR WRITTEN.'

exit $(if ($ready) { 0 } else { 1 })

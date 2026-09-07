#requires -Version 5.1
<#
    SEC BATCH 2. ONE COMPANY. ONE REQUEST. ONE AUTHORISATION UNIT.

    THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.

    It runs exactly one test - the execution door itself - and nothing else. The filter names that
    one fact rather than its class, deliberately: the class also carries the at-rest facts that
    assert no batch is authorised, and those are true of the resting state rather than of this
    window. Running them here would report the approval as a failure.

    SCOPE, and nothing wider:
      * sec-edgar ONLY. No prices, no splits, no dividends, no benchmark, no scoring.
      * ONE company: CIK 0001335112, LGIQ.US, the single member of batch 2.
      * ONE request: submissions/CIK0001335112.json. The connector declares supportsWindow=false,
        so the provider request carries NO from or to date. The authorisation's scope window
        2021-09-01..2026-08-31 is used for Covers() and is never sent to the provider.
      * At most ONE dispatch, against a ceiling of 6 that is enforced and NOT amended.
      * One unit is already spent by the QUMU canary, so this run must move 1 -> 2 and leave 4.

    LGIQ is the one member the installed declaration gives secondary reading scope: 424B*, S-1 and
    S-3, on top of the seventeen shared forms. Reading scope only, and member-bound - the runner
    hands any other CIK the shared list and nothing else.

    EODHD is switched OFF throughout. Nothing here runs batch 1, 3, 4, 5 or 6.

    FAIR ACCESS. This refuses before anything if AIINV_SEC_CONTACT is not set or is not shaped like
    an address, and never prints the value. The operator has confirmed the address is monitored.

    NO FULL SUITE RUNS HERE. The batch flag is returned to false afterwards, by hand, and the suite
    is run after that, so a suite result is never produced from a state that is not the resting one.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_SEC_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BACKFILL -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

# ---- pre-flight: the contact, by shape only ---------------------------------------------------

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host ''
    Write-Host '  STOPPING before any request. AIINV_SEC_CONTACT is not set.'
    Write-Host '  EDGAR fair access requires every request to name a contact, and this run'
    Write-Host '  will not invent one.'
    Write-Host ''
    exit 2
}

# Shape only. The value is never printed, never written, never interpolated into a message.
$wellFormed = $env:AIINV_SEC_CONTACT -match '^[^@\s]+@[^@\s]+\.[^@\s]+$'

if (-not $wellFormed) {
    Write-Host ''
    Write-Host '  STOPPING before any request. AIINV_SEC_CONTACT is set but is not shaped like an'
    Write-Host '  address. The value is not printed. Correct it and run again.'
    Write-Host ''
    exit 2
}

Write-Host ''
Write-Host '  contact address configured and well formed (value not printed).'

# ---- pre-flight: the state this run requires --------------------------------------------------

$partitionPath = Join-Path $root 'tests\AI.Investment.Api.Tests\SecEdgarSixMemberPartition.cs'
$partition = Get-Content $partitionPath -Raw

$flags = [regex]::Matches($partition, 'new\((\d), "(\d{10})", "([A-Z\.]+)", 1, (\d), Declaration, Authorised: (true|false)\)')

if ($flags.Count -ne 6) {
    Write-Host '  STOPPING. The partition did not read as six one-member batches.'
    exit 2
}

$bad = $false
foreach ($m in $flags) {
    $index = [int]$m.Groups[1].Value
    $cik = $m.Groups[2].Value
    $auth = $m.Groups[5].Value
    $expected = if ($index -eq 2) { 'true' } else { 'false' }

    if ($auth -ne $expected) {
        Write-Host ("  STOPPING. Batch {0} ({1}) is Authorised: {2}; this run requires {3}." -f $index, $cik, $auth, $expected)
        $bad = $true
    }

    if ($index -eq 2 -and $cik -ne '0001335112') {
        Write-Host ("  STOPPING. Batch 2 names CIK {0}, not 0001335112." -f $cik)
        $bad = $true
    }
}

if ($bad) { exit 2 }

Write-Host '  batch 2 approved; batches 1 and 3-6 unapproved (read from the partition source).'

$existing = @(Get-ChildItem (Join-Path $root 'artifacts\universe') -Filter 'acquisition-sec-*.json' -ErrorAction SilentlyContinue)

Write-Host ('  SEC attempt artefacts already recorded: ' + $existing.Count + ' (' + (($existing | ForEach-Object { $_.Name }) -join ', ') + ')')

if ($existing | Where-Object { $_.Name -like 'acquisition-sec-02-*' }) {
    Write-Host '  STOPPING. An LGIQ attempt has already been recorded under this authorisation.'
    exit 2
}

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   SEC BATCH 2 - LGIQ.US - CIK 0001335112'
Write-Host '   ONE request to data.sec.gov. ONE authorisation unit, the second.'
Write-Host '   submissions/CIK0001335112.json, RegulatoryFilings, NO window.'
Write-Host '   Ceiling 6, enforced and not amended. Batches 1 and 3-6 do NOT run.'
Write-Host '  ================================================================'
Write-Host ''

$code = 0

try {
    # The connector reads its section while the container is built, so the switch is set here for
    # the duration of this one step and cleared again below. appsettings.json still pins it false.
    $env:Providers__SecEdgar__Enabled = 'true'
    $env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
    $env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
    $env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

    $env:AIINV_SEC_BATCH = '1'
    $env:AIINV_SEC_BATCH_INDEX = '2'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests.One_approved_sec_batch_is_acquired_and_nothing_else_is' `
        -LogName 'sec-lgiq.log' `
        -Label 'SEC batch 2: LGIQ, exactly one EDGAR filing request' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_SEC_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue
    $env:Providers__SecEdgar__Enabled = 'false'
}

Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Clear-Switches

$artefact = Join-Path $root 'artifacts\universe\acquisition-sec-02-attempt-01.json'

Write-Host ''
Write-Host '=== batch 2 outcome'

if (-not (Test-Path $artefact)) {
    Write-Host '  NO ATTEMPT RECORD WAS WRITTEN. Read the log before doing anything else.'
    Write-Host ''
    exit $code
}

Write-Host ('  attempt record: ' + $artefact)
Get-Content $artefact -Raw | Out-Host

# ---- read-only inspection of what was archived ------------------------------------------------
# Derived from the payload the run archived, so the report can state forms and provenance fields
# rather than infer them. Reads only; writes one summary file under artifacts\verify.

try {
    $record = Get-Content $artefact -Raw | ConvertFrom-Json
    $hash = $record.Reading.ArchivedHash

    if ([string]::IsNullOrWhiteSpace($hash)) {
        Write-Host '  no archived hash on the record; nothing to inspect.'
        exit $code
    }

    $archiveRoot = Join-Path $root 'tests\AI.Investment.Api.Tests\bin\Release\net8.0\archive'
    $payloadPath = Join-Path $archiveRoot ($hash.Substring(0, 2) + '\' + $hash.Substring(2, 2) + '\' + $hash + '.bin')
    $sidecarPath = [System.IO.Path]::ChangeExtension($payloadPath, '.json')

    if (-not (Test-Path $payloadPath)) {
        $found = Get-ChildItem -Path $root -Recurse -Filter ($hash + '.bin') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $payloadPath = $found.FullName
            $sidecarPath = [System.IO.Path]::ChangeExtension($payloadPath, '.json')
        }
    }

    if (-not (Test-Path $payloadPath)) {
        Write-Host ('  archived payload not found for hash ' + $hash)
        exit $code
    }

    $payloadFile = Get-Item $payloadPath
    $document = Get-Content $payloadPath -Raw | ConvertFrom-Json
    $recent = $document.filings.recent

    $count = $recent.accessionNumber.Count
    $rows = for ($i = 0; $i -lt $count; $i++) {
        [pscustomobject]@{
            accession   = $recent.accessionNumber[$i]
            form        = $recent.form[$i]
            filingDate  = $recent.filingDate[$i]
            reportDate  = $recent.reportDate[$i]
            acceptance  = $recent.acceptanceDateTime[$i]
            primaryDoc  = $recent.primaryDocument[$i]
            description = $recent.primaryDocDescription[$i]
        }
    }

    $secondary = @($rows | Where-Object { $_.form -like '424B*' -or $_.form -eq 'S-1' -or $_.form -eq 'S-3' })
    $relevant = @($rows | Where-Object { $record.Reading.RelevantAccessions -contains $_.accession })

    $sidecar = $null
    $sidecarFile = $null
    if (Test-Path $sidecarPath) {
        $sidecar = Get-Content $sidecarPath -Raw | ConvertFrom-Json
        $sidecarFile = Get-Item $sidecarPath
    }

    $summary = [pscustomobject]@{
        Cik                 = $document.cik
        EntityName          = $document.name
        Tickers             = $document.tickers
        ArchivedHash        = $hash
        PayloadPath         = $payloadPath.Substring($root.Length + 1)
        PayloadBytes        = $payloadFile.Length
        PayloadWrittenUtc   = $payloadFile.LastWriteTimeUtc.ToString('o')
        PayloadCreatedUtc   = $payloadFile.CreationTimeUtc.ToString('o')
        SidecarBytes        = if ($sidecarFile) { $sidecarFile.Length } else { $null }
        SidecarWrittenUtc   = if ($sidecarFile) { $sidecarFile.LastWriteTimeUtc.ToString('o') } else { $null }
        Sidecar             = $sidecar
        InlineFilings       = $count
        EarliestFilingDate  = ($rows | Where-Object { $_.filingDate } | Sort-Object filingDate | Select-Object -First 1).filingDate
        LatestFilingDate    = ($rows | Where-Object { $_.filingDate } | Sort-Object filingDate | Select-Object -Last 1).filingDate
        OlderFileReferences = @($document.filings.files).Count
        FormDistribution    = ($rows | Group-Object form | Sort-Object Count -Descending | ForEach-Object { [pscustomobject]@{ form = $_.Name; count = $_.Count } })
        SecondaryScopeForms = $secondary
        RelevantFilings     = $relevant
    }

    $out = Join-Path $root 'artifacts\verify\sec-lgiq-evidence.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding UTF8

    Write-Host ''
    Write-Host ('=== evidence summary written: ' + $out)
    Write-Host ('  entity: ' + $document.name + '  cik: ' + $document.cik)
    Write-Host ('  inline filings: ' + $count + '   earliest filingDate: ' + $summary.EarliestFilingDate)
    Write-Host ('  older file references: ' + $summary.OlderFileReferences)
    Write-Host ('  424B*/S-1/S-3 filings present: ' + $secondary.Count)
    Write-Host ('  relevant filings named by the run: ' + $relevant.Count)
}
catch {
    Write-Host ('  inspection failed (the run itself is unaffected): ' + $_.Exception.Message)
}

Write-Host ''
Write-Host '  NOTHING ELSE WAS ACQUIRED. No price, no split, no dividend request was made.'
Write-Host '  NO NEXT BATCH RAN. Batches 3-6 are unauthorised and each needs its own approval.'
Write-Host '  BATCH 1 DID NOT RUN AGAIN. Its flag is down and its unit is already spent.'
Write-Host '  THE CEILING WAS NOT AMENDED. The declaration and its digest are unchanged.'
Write-Host '  THE BATCH 2 FLAG MUST NOW BE RETURNED TO FALSE BEFORE THE FULL SUITE IS RUN.'

exit $code

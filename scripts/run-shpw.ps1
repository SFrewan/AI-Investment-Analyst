#requires -Version 5.1
<#
    SEC BATCH 6. ONE COMPANY. ONE REQUEST. ONE AUTHORISATION UNIT.

    THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.

    It runs exactly one test - the execution door itself - and nothing else. The filter names that
    one fact rather than its class, deliberately: the class also carries the at-rest facts that
    assert no batch is authorised, and those are true of the resting state rather than of this
    window. Running them here would report the approval as a failure.

    SCOPE, and nothing wider:
      * sec-edgar ONLY. No prices, no splits, no dividends, no benchmark, no scoring.
      * ONE company: CIK 0001784851, SHPW.US, Shapeways Holdings, the single member of batch 6.
      * ONE request: submissions/CIK0001784851.json. The connector declares supportsWindow=false,
        so the provider request carries NO from or to date. The authorisation's scope window
        2021-09-01..2026-08-31 is used for Covers() and is never sent to the provider.
      * At most ONE dispatch, against a ceiling of 6 that is enforced and NOT amended.
      * Five units are already spent - QUMU, LGIQ, ONEM, NGM, SDC - so this run must move 5 -> 6,
        leaving NONE. This is the last unit of the authorisation and there is no seventh batch.

    SHPW takes the SEVENTEEN SHARED FORMS and nothing else. The secondary reading scope - 424B*,
    S-1, S-3 - belongs to LGIQ alone, by CIK, and the runner hands any other member the shared list
    whatever the context carries. This run's artefact must therefore list seventeen forms.

    SHPW carries FIFTEEN breaches - more than the other five members combined - spread over three
    clusters: October 2021, July to November 2024, and a single window in April 2025. Their
    magnitudes run from 51.47 per cent to 6308.45 per cent, nine up and six down, classified 8 x A,
    4 x B, 2 x E and 1 x C. The fifteen selection windows are NOT contiguous, so this run reads them
    from the declaration rather than collapsing them to a span: a filing between the clusters is not
    in any window. The earliest start - 2021-09-26 - is the date the filing history has to reach
    back to for a count from this document to mean anything.

    A caveat the operator has already been shown. From August 2024 onward SHPW trades at the
    sub-penny tick floor, and six of the fifteen breaches are single-tick prints on tiny volume,
    several of which the price stage itself marked not material or not comparable. A filing cannot
    explain a one-tick print. This run reads what the declaration scopes and reports what it finds;
    it does not promise that filings can answer every one of the fifteen.

    EODHD is switched OFF throughout. Nothing here runs batch 1, 2, 3, 4 or 5.

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

$ExpectedDigest = '499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682'
$ExpectedCorrelation = 'batch-4-a1-0001784851-RegulatoryFilings'

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
if (-not ($env:AIINV_SEC_CONTACT -match '^[^@\s]+@[^@\s]+\.[^@\s]+$')) {
    Write-Host ''
    Write-Host '  STOPPING before any request. AIINV_SEC_CONTACT is set but is not shaped like an'
    Write-Host '  address. The value is not printed. Correct it and run again.'
    Write-Host ''
    exit 2
}

Write-Host ''
Write-Host '  contact address configured and well formed (value not printed).'

# ---- pre-flight: the ten assertions, each read from disk --------------------------------------

Write-Host ''
Write-Host '=== pre-dispatch assertions'

$bad = $false

# 1, 2, 4, 5: the partition, parsed from its own source text.
$partitionPath = Join-Path $root 'tests\AI.Investment.Api.Tests\SecEdgarSixMemberPartition.cs'
$partition = Get-Content $partitionPath -Raw
$flags = [regex]::Matches($partition, 'new\((\d), "(\d{10})", "([A-Z\.]+)", 1, (\d), Declaration, Authorised: (true|false)\)')

if ($flags.Count -ne 6) {
    Write-Host '  FAIL  the partition did not read as six one-member batches.'
    exit 2
}

foreach ($m in $flags) {
    $index = [int]$m.Groups[1].Value
    $cik = $m.Groups[2].Value
    $symbol = $m.Groups[3].Value
    $prior = [int]$m.Groups[4].Value
    $auth = $m.Groups[5].Value
    $expected = if ($index -eq 6) { 'true' } else { 'false' }

    if ($auth -ne $expected) {
        Write-Host ("  FAIL  batch {0} ({1}) is Authorised: {2}; this run requires {3}." -f $index, $cik, $auth, $expected)
        $bad = $true
    }

    if ($index -eq 6) {
        if ($cik -ne '0001784851') { Write-Host ("  FAIL  batch 6 names CIK {0}, not 0001784851." -f $cik); $bad = $true }
        if ($symbol -ne 'SHPW.US') { Write-Host ("  FAIL  batch 6 names symbol {0}, not SHPW.US." -f $symbol); $bad = $true }
        if ($prior -ne 5) { Write-Host ("  FAIL  batch 6 declares prior consumption {0}, not 5." -f $prior); $bad = $true }
    }
}

if (-not $bad) {
    Write-Host '  PASS  1. batch 6 is SHPW.US / CIK 0001784851'
    Write-Host '  PASS  2. batch 6 ExpectedPriorConsumption = 5'
    Write-Host '  PASS  4. Authorised is true for batch 6'
    Write-Host '  PASS  5. batches 1, 2, 3, 4, 5 are false'
}

# 3, 7: the artefacts, which are the spending record.
$artefacts = @(Get-ChildItem (Join-Path $root 'artifacts\universe') -Filter 'acquisition-sec-*.json' -ErrorAction SilentlyContinue | Sort-Object Name)
$consumed = 0
$claimed = @()

foreach ($a in $artefacts) {
    $r = Get-Content $a.FullName -Raw | ConvertFrom-Json
    if ($r.Authorization -eq 'sec-edgar-gate6-six-members-2021-09-to-2026-08') {
        $consumed += $r.AuthorizationConsumedThisRun
    }
    $claimed += $r.Correlation
}

if ($consumed -ne 5) {
    Write-Host ("  FAIL  3. actual consumed is {0}, not 5." -f $consumed)
    $bad = $true
}
else { Write-Host '  PASS  3. actual consumed = 5' }

if ($artefacts | Where-Object { $_.Name -like 'acquisition-sec-06-*' }) {
    Write-Host '  FAIL  7. a batch 6 artefact already exists.'
    $bad = $true
}
else { Write-Host '  PASS  7. no batch 6 artefact exists' }

# 6: the correlation this run will claim, and that nothing has claimed it.
if ($claimed -contains $ExpectedCorrelation) {
    Write-Host ('  FAIL  6. correlation ' + $ExpectedCorrelation + ' is already claimed.')
    $bad = $true
}
else { Write-Host ('  PASS  6. correlation ' + $ExpectedCorrelation + ' is unclaimed') }

# 8, 9: the declaration.
$declaration = Get-Content (Join-Path $root 'declarations\acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json') -Raw | ConvertFrom-Json

if ($declaration.AuthorizationDigest -ne $ExpectedDigest) {
    Write-Host '  FAIL  8. the installed digest is not the approved one.'
    $bad = $true
}
else { Write-Host '  PASS  8. digest unchanged' }

if ([int]$declaration.DispatchCeiling -ne 6) {
    Write-Host ('  FAIL  9. dispatch ceiling is {0}, not 6.' -f $declaration.DispatchCeiling)
    $bad = $true
}
else { Write-Host '  PASS  9. dispatch ceiling remains 6' }

# 10: Gate 6, by content rather than by promise.
$gate6 = Join-Path $root 'declarations\coverage-gate6.json'
$gate6File = Get-Item $gate6
$gate6Doc = Get-Content $gate6 -Raw | ConvertFrom-Json

if ($gate6File.Length -ne 4025 -or $gate6Doc.Threshold.Value -ne 0 -or $gate6Doc.SealedAtUtc -ne '2026-09-04T00:00:00Z') {
    Write-Host '  FAIL  10. coverage-gate6.json is not the sealed rule.'
    $bad = $true
}
else { Write-Host '  PASS  10. Gate 6 rule untouched (4025 bytes, zero-faults, sealed 2026-09-04)' }

if ($bad) {
    Write-Host ''
    Write-Host '  STOPPING. A pre-dispatch assertion failed. Nothing was spent.'
    exit 2
}

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   SEC BATCH 6 - SHPW.US - CIK 0001784851'
Write-Host '   ONE request to data.sec.gov. THE LAST authorisation unit, the sixth.'
Write-Host '   submissions/CIK0001784851.json, RegulatoryFilings, NO window.'
Write-Host '   Seventeen shared forms; the secondary scope belongs to LGIQ only.'
Write-Host '   Ceiling 6, enforced and not amended. Batches 1-5 do NOT run again.'
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
    $env:AIINV_SEC_BATCH_INDEX = '6'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests.One_approved_sec_batch_is_acquired_and_nothing_else_is' `
        -LogName 'sec-shpw.log' `
        -Label 'SEC batch 6: SHPW, exactly one EDGAR filing request' | Out-Host

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

$artefact = Join-Path $root 'artifacts\universe\acquisition-sec-06-attempt-01.json'

Write-Host ''
Write-Host '=== batch 6 outcome'

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

    if (-not (Test-Path $payloadPath)) {
        $found = Get-ChildItem -Path $root -Recurse -Filter ($hash + '.bin') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $payloadPath = $found.FullName }
    }

    if (-not (Test-Path $payloadPath)) {
        Write-Host ('  archived payload not found for hash ' + $hash)
        exit $code
    }

    $sidecarPath = [System.IO.Path]::ChangeExtension($payloadPath, '.json')
    $payloadFile = Get-Item $payloadPath
    $document = Get-Content $payloadPath -Raw | ConvertFrom-Json
    $recent = $document.filings.recent
    $count = $recent.accessionNumber.Count

    # The seven attributes the normaliser writes, counted as the document actually states them.
    $present = @{ accession = 0; form = 0; primaryDocument = 0; description = 0; filingDate = 0; acceptance = 0; reportDate = 0 }
    $statedPeriodAfterAcceptance = 0
    $acceptanceMissing = 0
    $acceptanceBeforeFilingDate = 0
    $orderingHolds = 0
    $orderingBreaks = 0

    $sidecar = $null
    $sidecarFile = $null
    if (Test-Path $sidecarPath) {
        $sidecar = Get-Content $sidecarPath -Raw | ConvertFrom-Json
        $sidecarFile = Get-Item $sidecarPath
    }

    $retrieved = if ($sidecar) { [datetime]::Parse($sidecar.RetrievedAtUtc).ToUniversalTime() } else { [datetime]::UtcNow }

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

    foreach ($r in $rows) {
        if (-not [string]::IsNullOrWhiteSpace($r.accession)) { $present.accession++ }
        if (-not [string]::IsNullOrWhiteSpace($r.form)) { $present.form++ }
        if (-not [string]::IsNullOrWhiteSpace($r.primaryDoc)) { $present.primaryDocument++ }
        if (-not [string]::IsNullOrWhiteSpace($r.description)) { $present.description++ }
        if (-not [string]::IsNullOrWhiteSpace($r.filingDate)) { $present.filingDate++ }
        if (-not [string]::IsNullOrWhiteSpace($r.acceptance)) { $present.acceptance++ } else { $acceptanceMissing++ }
        if (-not [string]::IsNullOrWhiteSpace($r.reportDate)) { $present.reportDate++ }

        $published = if (-not [string]::IsNullOrWhiteSpace($r.acceptance)) {
            [datetime]::Parse($r.acceptance).ToUniversalTime()
        } else {
            [datetime]::Parse($r.filingDate + 'T00:00:00Z').ToUniversalTime()
        }

        if (-not [string]::IsNullOrWhiteSpace($r.acceptance) -and -not [string]::IsNullOrWhiteSpace($r.filingDate)) {
            if ($published -lt [datetime]::Parse($r.filingDate + 'T00:00:00Z').ToUniversalTime()) { $acceptanceBeforeFilingDate++ }
        }

        $asOf = $published
        if (-not [string]::IsNullOrWhiteSpace($r.reportDate)) {
            $period = [datetime]::Parse($r.reportDate + 'T00:00:00Z').ToUniversalTime()
            if ($period -gt $published) { $statedPeriodAfterAcceptance++ } else { $asOf = $period }
        }

        if ($asOf -le $published -and $published -le $retrieved) { $orderingHolds++ } else { $orderingBreaks++ }
    }

    $expected = 0
    foreach ($k in $present.Keys) { $expected += $present[$k] }

    $relevant = @($rows | Where-Object { $record.Reading.RelevantAccessions -contains $_.accession })

    # Informational only: these forms are NOT in scope for this member.
    $offering = @($rows | Where-Object { $_.form -like '424B*' -or $_.form -eq 'S-1' -or $_.form -eq 'S-3' })

    # Every filing that falls inside ANY of SHPW's fifteen selection windows, whatever its form.
    # The windows are read from the declaration and tested one at a time, because they are not
    # contiguous: collapsing them to a span would count filings that fall between the clusters.
    # This is what lets "no scoped filing" be told apart from "no filing at all".
    $declaredWindows = @()
    $decl = Get-Content (Join-Path $root 'declarations\acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json') -Raw | ConvertFrom-Json
    foreach ($b in $decl.FilingSelection.Breaches) {
        if ($b.Cik -eq '0001784851') {
            $declaredWindows += [pscustomobject]@{ From = $b.SelectFilingsFromUtc; To = $b.SelectFilingsToUtc; Breach = ($b.BreachFrom + '..' + $b.BreachTo); Move = $b.Move; Class = $b.Classification }
        }
    }

    $inWindow = @($rows | Where-Object {
        $r = $_
        @($declaredWindows | Where-Object { $r.filingDate -ge $_.From -and $r.filingDate -le $_.To }).Count -gt 0
    })

    # And which window each in-scope filing landed in, so the report can name them.
    $windowHits = foreach ($w in $declaredWindows) {
        $hits = @($rows | Where-Object { $_.filingDate -ge $w.From -and $_.filingDate -le $w.To })
        $scoped = @($hits | Where-Object { $record.Reading.RelevantAccessions -contains $_.accession })
        [pscustomobject]@{
            Breach = $w.Breach; Move = $w.Move; Class = $w.Class
            Window = ($w.From + '..' + $w.To)
            FilingsAnyForm = $hits.Count
            ScopedFilings = $scoped.Count
            Accessions = @($hits | ForEach-Object { $_.filingDate + ' ' + $_.form + ' ' + $_.accession })
        }
    }

    $summary = [pscustomobject]@{
        Cik                         = $document.cik
        EntityName                  = $document.name
        Tickers                     = $document.tickers
        ArchivedHash                = $hash
        PayloadPath                 = $payloadPath.Substring($root.Length + 1)
        PayloadBytes                = $payloadFile.Length
        PayloadWrittenUtc           = $payloadFile.LastWriteTimeUtc.ToString('o')
        SidecarWrittenUtc           = if ($sidecarFile) { $sidecarFile.LastWriteTimeUtc.ToString('o') } else { $null }
        Sidecar                     = $sidecar
        FormsInScopeCount           = $record.FormsInScope.Count
        InlineFilings               = $count
        EarliestFilingDate          = ($rows | Where-Object { $_.filingDate } | Sort-Object filingDate | Select-Object -First 1).filingDate
        LatestFilingDate            = ($rows | Where-Object { $_.filingDate } | Sort-Object filingDate | Select-Object -Last 1).filingDate
        OlderFileReferences         = @($document.filings.files).Count
        AttributesStated            = $present
        ExpectedObservations        = $expected
        ObservationsRecorded        = $record.ObservationsRecorded
        Difference                  = $record.ObservationsRecorded - $expected
        PayloadsQuarantined         = $record.PayloadsQuarantined
        StatedPeriodAfterAcceptance = $statedPeriodAfterAcceptance
        AcceptanceMissing           = $acceptanceMissing
        AcceptanceBeforeFilingDate  = $acceptanceBeforeFilingDate
        ProvenanceOrderingHolds     = $orderingHolds
        ProvenanceOrderingBreaks    = $orderingBreaks
        RetrievedAtUsedForOrdering  = $retrieved.ToString('o')
        SelectionWindows            = $declaredWindows
        WindowHits                  = $windowHits
        FilingsInSelectionWindow    = $inWindow
        FormDistribution            = ($rows | Group-Object form | Sort-Object Count -Descending | ForEach-Object { [pscustomobject]@{ form = $_.Name; count = $_.Count } })
        RelevantFilings             = $relevant
        OfferingFormsNotInScope     = $offering
    }

    $out = Join-Path $root 'artifacts\verify\sec-shpw-evidence.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding UTF8

    Write-Host ''
    Write-Host ('=== evidence summary written: ' + $out)
    Write-Host ('  entity: ' + $document.name + '  cik: ' + $document.cik)
    Write-Host ('  forms in scope for this member: ' + $record.FormsInScope.Count + '  (seventeen expected)')
    Write-Host ('  inline filings: ' + $count + '   earliest filingDate: ' + $summary.EarliestFilingDate + '   latest: ' + $summary.LatestFilingDate)
    Write-Host ('  older file references: ' + $summary.OlderFileReferences)
    Write-Host ('  coverage test: earliest <= 2021-09-26 ? ' + ($summary.EarliestFilingDate -le '2021-09-26'))
    Write-Host ('  expected observations: ' + $expected + '   recorded: ' + $record.ObservationsRecorded + '   difference: ' + ($record.ObservationsRecorded - $expected))
    Write-Host ('  quarantined: ' + $record.PayloadsQuarantined)
    Write-Host ('  stated period later than acceptance: ' + $statedPeriodAfterAcceptance + '  (attribute only, never AsOfUtc)')
    Write-Host ('  acceptance instant missing: ' + $acceptanceMissing)
    Write-Host ('  acceptance earlier than stated filing date: ' + $acceptanceBeforeFilingDate)
    Write-Host ('  AsOfUtc <= PublishedAtUtc <= RetrievedAtUtc holds for ' + $orderingHolds + ' of ' + $count + '; breaks: ' + $orderingBreaks)
    Write-Host ('  relevant filings named by the run: ' + $relevant.Count)
    Write-Host ('  declared selection windows: ' + $declaredWindows.Count)
    Write-Host ('  ANY filing inside ANY of them (any form): ' + $inWindow.Count)
    Write-Host '  per window - breach, move, class, window, filings any form, scoped filings:'
    foreach ($w in $windowHits) {
        Write-Host ('     ' + $w.Breach + '  ' + $w.Move.PadLeft(9) + '  ' + $w.Class.Substring(0,1) + '  ' + $w.Window + '   any=' + $w.FilingsAnyForm + '  scoped=' + $w.ScopedFilings)
        foreach ($a in $w.Accessions) { Write-Host ('        ' + $a) }
    }
    foreach ($f in $inWindow) { Write-Host ('     ' + $f.filingDate + '  ' + $f.form.PadRight(12) + ' ' + $f.accession) }
}
catch {
    Write-Host ('  inspection failed (the run itself is unaffected): ' + $_.Exception.Message)
}

Write-Host ''
Write-Host '  NOTHING ELSE WAS ACQUIRED. No price, no split, no dividend request was made.'
Write-Host '  THERE IS NO NEXT BATCH. This was the sixth and last of the partition.'
Write-Host '  BATCHES 1-5 DID NOT RUN AGAIN. Their flags are down and their units are spent.'
Write-Host '  THE CEILING WAS NOT AMENDED. The declaration and its digest are unchanged.'
Write-Host '  THE BATCH 6 FLAG MUST NOW BE RETURNED TO FALSE BEFORE THE FULL SUITE IS RUN.'

exit $code

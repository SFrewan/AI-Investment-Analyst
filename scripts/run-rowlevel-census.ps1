#requires -Version 5.1
<#
    ROW-LEVEL QUARANTINE - CENSUS OF THE ELEVEN ARCHIVED PAYLOADS. READ-ONLY.

    NO NETWORK CALL. NO ACQUISITION. NO AUTHORISATION. NOTHING IS RECOVERED OR STORED.
    GATE 6 IS NOT MODIFIED. Its rule file is read for its size only, as evidence it did not move.

    This reads the eleven price payloads already sitting in the content-addressed archive and
    measures what row-level refusal admits from each. It writes one summary under artifacts\verify
    and touches nothing else - not the observation store, not the quarantine store, not Gate 6.

    Measured per member:
      * rows in the document, rows with close <= 0, rows admitted
      * where the refused rows sit: leading, interior, or trailing
      * their topology - how many maximal contiguous runs, and which rows each run spans
      * the largest interior gap in expected weekday sessions BEFORE dropping (every dated row)
        and AFTER dropping (admitted rows only), so it is visible whether dropping OPENS a gap
      * both against the coverage rule's tolerance, which is READ from the evaluator, not restated

    Deliberately NOT measured here:
      SplitAdjustment's verdict on each recovered series. That was already obtained by calling
      production code directly (artifacts\verify\gate6-recovered-series-move-screen.md). Re-deriving
      it in PowerShell would be a second, worse implementation of a domain rule that must not move.
      Those verdicts are carried below as a LABELLED INPUT, not as something this script computed.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$root = Split-Path -Parent $PSScriptRoot

# The eleven, with the payload ids the earlier re-read recorded, the row counts it recorded
# (as a cross-check, NOT as a substitute for counting), and SplitAdjustment's verdict from the
# move screen. Symbol, abbreviated hash, expected rows, expected bad rows, adjuster verdict.
$members = @(
    [pscustomobject]@{ Symbol = 'CCF.US';  Payload = 'ae79be18dc55'; ExpectRows = 557; ExpectBad = 1;  Adjuster = 'accepted' }
    [pscustomobject]@{ Symbol = 'EVBG.US'; Payload = '438650fad031'; ExpectRows = 713; ExpectBad = 2;  Adjuster = 'accepted' }
    [pscustomobject]@{ Symbol = 'GPP.US';  Payload = '626806275c35'; ExpectRows = 593; ExpectBad = 1;  Adjuster = 'accepted' }
    [pscustomobject]@{ Symbol = 'LGIQ.US'; Payload = '6641c40203c2'; ExpectRows = 903; ExpectBad = 3;  Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'NGM.US';  Payload = 'f82a23c8fb29'; ExpectRows = 654; ExpectBad = 1;  Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'ONEM.US'; Payload = 'c4c76ee86cb0'; ExpectRows = 381; ExpectBad = 3;  Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'QUMU.US'; Payload = '906e15760883'; ExpectRows = 368; ExpectBad = 5;  Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'SDC.US';  Payload = '923871b18d3d'; ExpectRows = 571; ExpectBad = 2;  Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'SHPW.US'; Payload = 'a4527d807a70'; ExpectRows = 989; ExpectBad = 20; Adjuster = 'refused' }
    [pscustomobject]@{ Symbol = 'VLDR.US'; Payload = 'a2b139e5e2c0'; ExpectRows = 371; ExpectBad = 2;  Adjuster = 'accepted' }
    [pscustomobject]@{ Symbol = 'WIRE.US'; Payload = '6d3c18170963'; ExpectRows = 714; ExpectBad = 2;  Adjuster = 'accepted' }
)

# The tolerance, read from the evaluator rather than restated.
$evaluatorPath = Join-Path $root 'tests\AI.Investment.Api.Tests\CoverageEvaluation.cs'
$evaluator = Get-Content $evaluatorPath -Raw
$m0 = [regex]::Match($evaluator, 'InteriorGapTolerance\s*=\s*(\d+)')
if (-not $m0.Success) { throw "Could not read InteriorGapTolerance from $evaluatorPath" }
$tolerance = [int]$m0.Groups[1].Value

$gate6 = Get-Item (Join-Path $root 'declarations\coverage-gate6.json')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   ROW-LEVEL CENSUS - eleven archived payloads, read-only'
Write-Host ('   interior gap tolerance, read from the evaluator: ' + $tolerance + ' sessions')
Write-Host ('   coverage-gate6.json: ' + $gate6.Length + ' bytes, written ' + $gate6.LastWriteTimeUtc.ToString('o'))
Write-Host '   NO network call. NO acquisition. NOTHING recovered or stored.'
Write-Host '  ================================================================'
Write-Host ''

function Get-ExpectedSessions {
    param([datetime] $From, [datetime] $To)

    # Weekdays inclusive between two bounds. The same coarse expectation the coverage rule uses:
    # a weekday with no observation is a missing expected session.
    if ($To -lt $From) { return 0 }
    $n = 0
    $d = $From
    while ($d -le $To) {
        if ($d.DayOfWeek -ne [DayOfWeek]::Saturday -and $d.DayOfWeek -ne [DayOfWeek]::Sunday) { $n++ }
        $d = $d.AddDays(1)
    }
    return $n
}

function Get-WorstGap {
    param($Dates)

    $d = @($Dates)
    $worst = 0
    $at = ''
    for ($i = 1; $i -lt $d.Count; $i++) {
        $missing = Get-ExpectedSessions -From $d[$i - 1].AddDays(1) -To $d[$i].AddDays(-1)
        if ($missing -gt $worst) {
            $worst = $missing
            $at = $d[$i - 1].ToString('yyyy-MM-dd') + ' -> ' + $d[$i].ToString('yyyy-MM-dd')
        }
    }
    return [pscustomobject]@{ Worst = $worst; At = $at }
}

# Index every archived payload by the first twelve characters of its content hash.
$index = @{}
foreach ($f in Get-ChildItem -Path $root -Recurse -Filter '*.bin' -ErrorAction SilentlyContinue) {
    $name = $f.BaseName
    if ($name.Length -lt 12) { continue }
    $key = $name.Substring(0, 12)
    if (-not $index.ContainsKey($key)) { $index[$key] = $f.FullName }
}

Write-Host ('  archived payloads indexed: ' + $index.Count)
Write-Host ''

$report = @()

foreach ($member in $members) {

    if (-not $index.ContainsKey($member.Payload)) {
        Write-Host ('  ' + $member.Symbol.PadRight(9) + ' payload ' + $member.Payload + ' NOT FOUND in the archive')
        $report += [pscustomobject]@{ Symbol = $member.Symbol; Payload = $member.Payload; Found = $false }
        continue
    }

    $path = $index[$member.Payload]

    # Windows PowerShell 5.1's ConvertFrom-Json writes an array as ONE object rather than
    # enumerating it, so @(...) around the pipeline yields a single item holding the whole
    # document. Unwrap deliberately instead of relying on the pipeline to do it.
    $parsed = Get-Content $path -Raw | ConvertFrom-Json
    if ($parsed -is [System.Array]) { $rows = $parsed } else { $rows = @($parsed) }
    $total = @($rows).Count

    $allDates = @()
    $goodDates = @()
    $goodPositions = @()
    $badPositions = @()
    $badDetail = @()
    $unreadable = 0

    for ($i = 0; $i -lt $total; $i++) {
        $row = $rows[$i]
        $names = @($row.PSObject.Properties.Name)

        if (($names -notcontains 'close') -or ($names -notcontains 'date')) {
            $unreadable++
            continue
        }

        $date = [datetime]::ParseExact([string]$row.date, 'yyyy-MM-dd', $invariant)
        $allDates += $date

        $close = [decimal]$row.close

        if ($close -le 0) {
            $badPositions += $i
            $badDetail += ('row ' + ($i + 1) + ' @ ' + [string]$row.date + ' close=' + $close.ToString($invariant))
        }
        else {
            $goodPositions += $i
            $goodDates += $date
        }
    }

    $sortedAll = @($allDates | Sort-Object)
    $sortedGood = @($goodDates | Sort-Object)
    $badPositions = @($badPositions | Sort-Object)

    # Where the refused rows sit, by document position relative to the sound rows.
    if ($goodPositions.Count -gt 0) {
        $firstGood = $goodPositions[0]
        $lastGood = $goodPositions[$goodPositions.Count - 1]
    }
    else {
        $firstGood = [int]::MaxValue
        $lastGood = -1
    }

    $leading = @($badPositions | Where-Object { $_ -lt $firstGood }).Count
    $trailing = @($badPositions | Where-Object { $_ -gt $lastGood }).Count
    $interior = $badPositions.Count - $leading - $trailing

    # Topology: maximal runs of consecutive document positions.
    $runs = @()
    $start = $null
    $previous = -99
    foreach ($p in $badPositions) {
        if ($p -ne $previous + 1) {
            if ($null -ne $start) { $runs += ('rows ' + ($start + 1) + '-' + ($previous + 1) + ' (' + ($previous - $start + 1) + ')') }
            $start = $p
        }
        $previous = $p
    }
    if ($null -ne $start) { $runs += ('rows ' + ($start + 1) + '-' + ($previous + 1) + ' (' + ($previous - $start + 1) + ')') }

    $runCount = @($runs).Count

    if ($runCount -eq 0) { $topology = 'none' }
    elseif ($badPositions.Count -eq 1) { $topology = 'a single row' }
    elseif ($runCount -eq 1) { $topology = 'ONE CONTIGUOUS BLOCK' }
    else { $topology = 'SCATTERED - ' + $runCount + ' separate runs' }

    $before = Get-WorstGap $sortedAll
    $after = Get-WorstGap $sortedGood

    if ($sortedGood.Count -gt 0) {
        $firstAdmitted = $sortedGood[0].ToString('yyyy-MM-dd')
        $lastAdmitted = $sortedGood[$sortedGood.Count - 1].ToString('yyyy-MM-dd')
    }
    else {
        $firstAdmitted = $null
        $lastAdmitted = $null
    }

    # The projection. Order matters: an empty series faults first; a series the adjuster refuses
    # faults next; only a series the adjuster accepts is ever measured for an interior gap.
    if ($sortedGood.Count -eq 0) { $projection = 'no-series (STILL A FAULT)' }
    elseif ($member.Adjuster -eq 'refused') { $projection = 'refused by SplitAdjustment (STILL A FAULT)' }
    elseif ($after.Worst -gt $tolerance) { $projection = 'interior-gap (STILL A FAULT)' }
    else { $projection = 'no fault condition met' }

    $entry = [pscustomobject]@{
        Symbol                = $member.Symbol
        Payload               = $member.Payload
        Found                 = $true
        PayloadFile           = $path
        RowsInDocument        = $total
        RowsExpectedByReread  = $member.ExpectRows
        RowCountMatchesReread = ($total -eq $member.ExpectRows)
        RowsUnreadable        = $unreadable
        RowsAdmitted          = $sortedGood.Count
        RowsRefused           = $badPositions.Count
        RefusedExpectedByReread = $member.ExpectBad
        RefusedMatchesReread  = ($badPositions.Count -eq $member.ExpectBad)
        RefusedLeading        = $leading
        RefusedInterior       = $interior
        RefusedTrailing       = $trailing
        RefusedRunCount       = $runCount
        RefusedTopology       = $topology
        RefusedRuns           = $runs
        RefusedRows           = $badDetail
        FirstAdmitted         = $firstAdmitted
        LastAdmitted          = $lastAdmitted
        WorstGapBeforeDrop    = $before.Worst
        WorstGapBeforeDropAt  = $before.At
        WorstGapAfterDrop     = $after.Worst
        WorstGapAfterDropAt   = $after.At
        DroppingOpensGap      = ($after.Worst -gt $before.Worst)
        GapTolerance          = $tolerance
        WouldFaultOnGap       = ($after.Worst -gt $tolerance)
        AdjusterVerdictInput  = $member.Adjuster
        ProjectedGate6Class   = $projection
    }

    $report += $entry

    Write-Host ('  ' + $member.Symbol.PadRight(9) +
        ' rows ' + $total.ToString().PadLeft(4) +
        '  admitted ' + $sortedGood.Count.ToString().PadLeft(4) +
        '  refused ' + $badPositions.Count.ToString().PadLeft(3) +
        ' (lead ' + $leading + ' int ' + $interior + ' trail ' + $trailing + ')' +
        '  worst gap ' + $before.Worst.ToString().PadLeft(2) + ' -> ' + $after.Worst.ToString().PadLeft(2) +
        '  ' + $topology)
    Write-Host ('              adjuster: ' + $member.Adjuster.PadRight(9) + 'projected: ' + $projection)
}

$found = @($report | Where-Object { $_.Found })

$totals = [pscustomobject]@{
    PayloadsFound            = $found.Count
    RowsInDocumentTotal      = (@($found) | Measure-Object -Property RowsInDocument -Sum).Sum
    RowsAdmittedTotal        = (@($found) | Measure-Object -Property RowsAdmitted -Sum).Sum
    RowsRefusedTotal         = (@($found) | Measure-Object -Property RowsRefused -Sum).Sum
    RowCountsAllMatchReread  = (@($found | Where-Object { -not $_.RowCountMatchesReread }).Count -eq 0)
    RefusedAllMatchReread    = (@($found | Where-Object { -not $_.RefusedMatchesReread }).Count -eq 0)
    MembersLeavingNoSeries   = @($found | Where-Object { $_.RowsAdmitted -gt 0 } | ForEach-Object { $_.Symbol })
    MembersStillNoSeries     = @($found | Where-Object { $_.RowsAdmitted -eq 0 } | ForEach-Object { $_.Symbol })
    MembersRefusedByAdjuster = @($found | Where-Object { $_.AdjusterVerdictInput -eq 'refused' } | ForEach-Object { $_.Symbol })
    MembersFaultingOnGap     = @($found | Where-Object { $_.ProjectedGate6Class -like 'interior-gap*' } | ForEach-Object { $_.Symbol })
    MembersWithNoFault       = @($found | Where-Object { $_.ProjectedGate6Class -eq 'no fault condition met' } | ForEach-Object { $_.Symbol })
    MembersWhereDropOpensGap = @($found | Where-Object { $_.DroppingOpensGap } | ForEach-Object { $_.Symbol })
    GapTolerance             = $tolerance
    Gate6RuleFileBytes       = $gate6.Length
    Gate6RuleFileWrittenUtc  = $gate6.LastWriteTimeUtc.ToString('o')
}

function Show-List($label, $items) {
    $list = @($items)
    if ($list.Count -eq 0) { Write-Host ('  ' + $label + ': none') }
    else { Write-Host ('  ' + $label + ': ' + $list.Count + ' - ' + ($list -join ', ')) }
}

Write-Host ''
Write-Host ('  payloads found                      : ' + $totals.PayloadsFound + ' of 11')
Write-Host ('  rows in document, total             : ' + $totals.RowsInDocumentTotal)
Write-Host ('  rows admitted, total                : ' + $totals.RowsAdmittedTotal)
Write-Host ('  rows refused, total                 : ' + $totals.RowsRefusedTotal)
Write-Host ('  row counts match the earlier re-read: ' + $totals.RowCountsAllMatchReread)
Write-Host ('  refused counts match the re-read    : ' + $totals.RefusedAllMatchReread)
Write-Host ''
Show-List 'leave "no-series"                 ' $totals.MembersLeavingNoSeries
Show-List 'still "no-series"                 ' $totals.MembersStillNoSeries
Show-List 'refused by the adjuster (fault)   ' $totals.MembersRefusedByAdjuster
Show-List 'fault on interior gap             ' $totals.MembersFaultingOnGap
Show-List 'meet no fault condition           ' $totals.MembersWithNoFault
Show-List 'dropping OPENS a wider gap        ' $totals.MembersWhereDropOpensGap

$out = Join-Path $root 'artifacts\verify\rowlevel-census.json'
[pscustomobject]@{ Totals = $totals; Members = $report } |
    ConvertTo-Json -Depth 6 | Set-Content -Path $out -Encoding UTF8

Write-Host ''
Write-Host ('=== census written: ' + $out)
Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED, RECOVERED OR STORED. No network call was made.'
Write-Host '  GATE 6 WAS NOT MODIFIED. The projection above is a measurement, not a re-baseline.'
Write-Host '  No member has been reclassified anywhere. coverage-gate6.json is untouched.'

#requires -Version 5.1
<#
    ROW-LEVEL QUARANTINE - THE UNTOUCHED PROOF. READ-ONLY.

    NO NETWORK CALL. NO ACQUISITION. NOTHING IS WRITTEN except the report under artifacts\verify.

    This does not assert that protected files were left alone; it asks the repository and prints
    the answer. Two independent statements are taken:

      1. git's own list of every working-tree change since HEAD. If a protected path is not in
         that list, it was not edited - that is git's statement, not mine.
      2. the SHA-256 of each protected file, printed so it can be compared by anyone later.

    A protected path appearing in (1) is a FAILURE and is called out as one.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   ROW-LEVEL QUARANTINE - what changed, asked of git itself'
Write-Host '   READ-ONLY. No network call. No acquisition. Nothing recovered.'
Write-Host '  ================================================================'
Write-Host ''

$porcelain = @(& git status --porcelain 2>&1)
$changed = @()

Write-Host '--- git status --porcelain (artifacts\ is git-ignored and cannot appear here)'
if ($porcelain.Count -eq 0) {
    Write-Host '   (clean)'
}
else {
    foreach ($line in $porcelain) {
        Write-Host ('   ' + $line)
        $text = [string]$line
        if ($text.Length -gt 3) { $changed += $text.Substring(3).Trim('"') }
    }
}

Write-Host ''
Write-Host '--- git diff --stat'
& git diff --stat | ForEach-Object { Write-Host ('   ' + $_) }

# The paths that must NOT appear above. Each is checked as a prefix, so a directory covers
# everything under it.
$protected = @(
    @{ Label = 'Gate 6 rule file';                     Path = 'declarations/coverage-gate6.json' },
    @{ Label = 'Gate 6 evaluator';                     Path = 'src/AI.Investment.Domain/Coverage/CoverageEvaluation.cs' },
    @{ Label = 'SEC acquisition authorisation';        Path = 'declarations/acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json' },
    @{ Label = 'SEC draft declaration';                Path = 'declarations/sec-edgar-gate6-six-members-2021-2026.draft.json' },
    @{ Label = 'EODHD price declarations (sample400)'; Path = 'declarations/acquisition-eodhd-sample400.json' },
    @{ Label = 'EODHD split declarations (final)';     Path = 'declarations/acquisition-eodhd-splits-final-2021-09-to-2026-08.json' },
    @{ Label = 'EODHD split declarations (remainder)'; Path = 'declarations/acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json' },
    @{ Label = 'EODHD split declarations (sample400)'; Path = 'declarations/acquisition-eodhd-splits-sample400.json' },
    @{ Label = 'Sealed universe (400)';                Path = 'declarations/universe-400-2021-2026.json' },
    @{ Label = 'Sealed universe (sample400)';          Path = 'declarations/universe-sample400-2021-2026.json' },
    @{ Label = 'Sealed universe (pilot)';              Path = 'declarations/universe-pilot-2021-2026.json' },
    @{ Label = 'SEC six-member partition';             Path = 'tests/AI.Investment.Api.Tests/SecEdgarSixMemberPartition.cs' },
    @{ Label = 'SEC batch accounting';                 Path = 'tests/AI.Investment.Api.Tests/SecFilingsBatchTests.cs' },
    @{ Label = 'EODHD price connector';                Path = 'src/AI.Investment.Infrastructure/Ingestion/Providers/EodhdProvider.cs' },
    @{ Label = 'EODHD splits connector';               Path = 'src/AI.Investment.Infrastructure/Ingestion/Providers/EodhdSplitsProvider.cs' },
    @{ Label = 'SEC connector';                        Path = 'src/AI.Investment.Infrastructure/Ingestion/Providers/SecEdgarProvider.cs' },
    @{ Label = 'SEC endpoints';                        Path = 'src/AI.Investment.Infrastructure/Ingestion/Providers/SecEdgarEndpoints.cs' },
    @{ Label = 'Content-addressed archive';            Path = 'src/AI.Investment.Infrastructure/Ingestion/FileSystemRawResponseArchive.cs' },
    @{ Label = 'Quarantined payload record';           Path = 'src/AI.Investment.Domain/Normalization/QuarantinedPayload.cs' },
    @{ Label = 'Session calendar';                     Path = 'src/AI.Investment.Infrastructure/Normalization/UsEquitySessionCalendar.cs' },
    @{ Label = 'Splits normaliser';                    Path = 'src/AI.Investment.Infrastructure/Normalization/EodhdSplitsNormalizer.cs' }
)

# Files whose folder I will not assume. Located by leaf name, ignoring build output.
$byName = @(
    @{ Label = 'SplitAdjustment (domain rule)'; Name = 'SplitAdjustment.cs' },
    @{ Label = 'Operator CSV normaliser';       Name = 'OperatorCsvNormalizer.cs' }
)

$searchRoots = @('src', 'tests') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ }

foreach ($b in $byName) {
    $hits = @(Get-ChildItem -Path $searchRoots -Recurse -Filter $b.Name -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

    foreach ($h in $hits) {
        $rel = $h.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
        $protected += @{ Label = $b.Label; Path = $rel }
    }

    if ($hits.Count -eq 0) {
        Write-Host ('   NOTE: no file named ' + $b.Name + ' found under src\ or tests\')
    }
}

Write-Host ''
Write-Host '--- protected paths, checked against git'
$violations = 0

foreach ($p in $protected) {
    $hit = @($changed | Where-Object { $_.Replace('\', '/').StartsWith($p.Path) })
    $exists = Test-Path -LiteralPath (Join-Path $root ($p.Path -replace '/', '\'))
    $note = if ($exists) { '' } else { '   [path not present in this repository]' }

    if ($hit.Count -eq 0) {
        Write-Host ('   UNTOUCHED  ' + $p.Label.PadRight(38) + $p.Path + $note)
    }
    else {
        Write-Host ('   CHANGED !  ' + $p.Label.PadRight(38) + ($hit -join ', '))
        $violations++
    }
}

Write-Host ''
Write-Host '--- SHA-256 of the files that must not have moved'
foreach ($p in $protected) {
    $full = Join-Path $root ($p.Path -replace '/', '\')
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        $h = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $item = Get-Item -LiteralPath $full
        Write-Host ('   ' + $h.Substring(0, 16) + '  ' + $item.Length.ToString().PadLeft(7) + ' B  ' +
            $item.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ') + '  ' + $p.Path)
    }
}

Write-Host ''
Write-Host '--- SEC batch authorisation flags, read from the partition'
$partition = Get-Content (Join-Path $root 'tests\AI.Investment.Api.Tests\SecEdgarSixMemberPartition.cs') -Raw
$flags = [regex]::Matches($partition, 'Authorised:\s*(true|false)\)')
$approved = @($flags | Where-Object { $_.Groups[1].Value -eq 'true' }).Count
Write-Host ('   ' + $approved + ' of ' + $flags.Count + ' batches authorised')
if ($approved -ne 0) { $violations++ }

Write-Host ''
if ($violations -eq 0) {
    Write-Host '   RESULT: every protected path is untouched, and no SEC batch is authorised.'
}
else {
    Write-Host ('   RESULT: ' + $violations + ' PROBLEM(S) ABOVE. Read them before going further.')
}

Pop-Location
exit $violations

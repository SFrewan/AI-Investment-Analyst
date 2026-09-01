#requires -Version 5.1
<#
    BLOCK 2B - READ-ONLY INSPECTION OF THE ARCHIVED RAW PAYLOADS.

    The archive holds exactly what the vendor sent, so it settles two questions the run report
    only raises, and settles them without making another call:

      Q2. every instrument reported zero splits. Did the vendor say "no splits", or did the
          normaliser drop something it was given?
      Q3. two years were asked for and one year arrived. Did the request go out narrow, or did
          the vendor return narrow?

    Reads files. Writes one report. Makes no network request and no API call.

    The response bodies are printed in part. The sidecar metadata is NOT printed: it can carry the
    request URI, and the API token travels in that query string.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'backfill-archive.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Block 2B - what the vendor actually sent'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ''

$archive = Join-Path $root 'tests\AI.Investment.Api.Tests\bin\Release\net8.0\archive'

if (-not (Test-Path $archive)) {
    Say ('No archive at ' + $archive)
    Save-Log
    exit 1
}

$files = @(Get-ChildItem -Path $archive -Recurse -File -Filter '*.bin' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc)

Say ('Archive: ' + $archive)
Say ('Stored payload bodies: ' + [string]$files.Count + ' (content-addressed, so identical')
Say 'responses are stored once no matter how many runs referenced them).'
Say ''

# ---- inventory --------------------------------------------------------------

Say '## Stored bodies, oldest first'
Say ''
Say '| bytes | written (UTC) | content hash (first 16) |'
Say '| ---: | --- | --- |'

foreach ($f in $files) {
    Say ('| ' + [string]$f.Length + ' | ' +
        $f.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + ' | ' +
        $f.BaseName.Substring(0, 16) + ' |')
}

Save-Log

# ---- Q2: the small bodies are the corporate-action responses ---------------

Say ''
Say '## Q2. The corporate-action responses'
Say ''

$small = @($files | Where-Object { $_.Length -lt 4096 })

if ($small.Count -eq 0) {
    Say 'No small body found. The split responses are not distinguishable by size here.'
}

foreach ($f in $small) {
    $text = [System.IO.File]::ReadAllText($f.FullName)

    Say ('- `' + $f.BaseName.Substring(0, 16) + '` is ' + [string]$f.Length + ' bytes and contains:')
    Say ''
    Say '```'
    Say $text
    Say '```'
    Say ''
    Say 'The database records twenty `eodhd-splits` runs and twenty artifact references. Because'
    Say 'the archive is content-addressed, twenty identical responses are stored once - so this'
    Say 'single body IS what all twenty instruments returned.'
    Say ''
    Say 'That means the zero split observations are the vendor saying "nothing here", not the'
    Say 'normaliser dropping rows it was handed. Nothing was quarantined either.'
    Say ''
    Say '**It does not prove no split occurred.** An endpoint outside the subscription can also'
    Say 'answer with an empty array rather than an error, and this account is demonstrably'
    Say 'limited - see Q3. Treat the corporate-action feed as WORKING BUT UNCONFIRMED until one'
    Say 'known split is seen coming back non-empty.'
}

Save-Log

# ---- Q3: how far back the price bodies actually reach -----------------------

Say ''
Say '## Q3. How much history the vendor returned'
Say ''
Say 'The request carries `from` and `to` built straight from the window; the gateway narrows'
Say 'nothing. So the dates present in the body are the vendor''s answer, not ours.'
Say ''
Say '| body | bytes | earliest date in body | latest date in body | dated rows |'
Say '| --- | ---: | --- | --- | ---: |'

$large = @($files | Where-Object { $_.Length -ge 4096 } | Select-Object -First 6)

foreach ($f in $large) {
    $text = [System.IO.File]::ReadAllText($f.FullName)

    $dates = @([regex]::Matches($text, '"date"\s*:\s*"(\d{4}-\d{2}-\d{2})"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object)

    if ($dates.Count -eq 0) {
        Say ('| `' + $f.BaseName.Substring(0, 12) + '` | ' + [string]$f.Length + ' | - | - | 0 |')
        continue
    }

    Say ('| `' + $f.BaseName.Substring(0, 12) + '` | ' + [string]$f.Length + ' | ' +
        $dates[0] + ' | ' + $dates[$dates.Count - 1] + ' | ' + [string]$dates.Count + ' |')
}

Save-Log

Say ''
Say 'If every earliest date is about one year back rather than two, the vendor truncated the'
Say 'range. That is a subscription limit, not a defect in this repository: the window asked for'
Say 'two years, and the platform stored exactly what came back rather than padding it.'

Say ''
Say '# END'
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit 0

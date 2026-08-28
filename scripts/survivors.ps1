<#
    Distils the newest Stryker mutation report into a compact list of surviving mutants.

    The JSON report embeds the full source of every mutated file, so it is several megabytes and
    unreadable by hand. This writes artifacts\verify\survivors.txt: one line per surviving mutant,
    giving the file, the position, the mutator, the original expression and the replacement. That is
    the working list for "which behaviour is not pinned by a test".

        powershell -ExecutionPolicy Bypass -File scripts\survivors.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -Path $repo

$outDir = Join-Path $repo 'artifacts\verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$out = Join-Path $outDir 'survivors.txt'
$marker = Join-Path $outDir 'SURVIVORS-DONE.txt'
Remove-Item -Path $marker -ErrorAction SilentlyContinue

$report = Get-ChildItem -Path (Join-Path $repo 'StrykerOutput') -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $report) {
    'no mutation report found' | Set-Content -Path $out -Encoding utf8
    'count=-1' | Set-Content -Path $marker -Encoding utf8
    return
}

$json = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("report: $($report.FullName)")

$count = 0

foreach ($entry in $json.files.PSObject.Properties) {
    $path = $entry.Name
    $file = $entry.Value

    # Source is split once per file so a mutant's original text can be recovered from its span.
    $source = $file.source -split "`r?`n"

    foreach ($mutant in $file.mutants) {
        if ($mutant.status -ne 'Survived' -and $mutant.status -ne 'NoCoverage') { continue }

        $count++

        $startLine = [int]$mutant.location.start.line
        $startCol = [int]$mutant.location.start.column
        $endLine = [int]$mutant.location.end.line
        $endCol = [int]$mutant.location.end.column

        $original = ''
        if ($startLine -ge 1 -and $startLine -le $source.Count) {
            if ($startLine -eq $endLine) {
                $text = $source[$startLine - 1]
                $len = [Math]::Min($endCol - $startCol, [Math]::Max(0, $text.Length - ($startCol - 1)))
                if ($len -gt 0) { $original = $text.Substring($startCol - 1, $len) }
            }
            else {
                $original = ($source[($startLine - 1)]).Trim() + ' ... ' + ($source[[Math]::Min($endLine, $source.Count) - 1]).Trim()
            }
        }

        $shorten = {
            param($s)
            $s = ($s -replace "`r?`n", ' ') -replace '\s+', ' '
            if ($s.Length -gt 160) { $s.Substring(0, 160) + '...' } else { $s }
        }

        $lines.Add(("{0} | {1}:{2} | {3} | {4} | ORIG {5} | REPL {6}" -f `
            $mutant.status,
            (Split-Path -Leaf $path),
            $startLine,
            $startCol,
            $mutant.mutatorName,
            (& $shorten $original),
            (& $shorten $mutant.replacement)))
    }
}

$lines.Insert(1, "surviving or uncovered mutants: $count")
$lines | Set-Content -Path $out -Encoding utf8
"count=$count" | Set-Content -Path $marker -Encoding utf8

Write-Host "[survivors] done. count=$count"

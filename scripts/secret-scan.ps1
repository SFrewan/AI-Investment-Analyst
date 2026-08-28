<#
    Checks that no credential remains in tracked files, and reports what is still in git history.

    Written as a script rather than done by eye because "I looked and there was nothing" is not a
    verification result. It writes artifacts\verify\secret-scan.log, which .gitignore excludes.

    IT NEVER PRINTS A SECRET. Every finding is reported as a file path and a line number; the
    matched text is not written to the log, because a log that quotes the credential is a second
    copy of the problem.

        powershell -ExecutionPolicy Bypass -File scripts\secret-scan.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -Path $repo

$outDir = Join-Path $repo 'artifacts\verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$log = Join-Path $outDir 'secret-scan.log'
$marker = Join-Path $outDir 'SECRET-SCAN-DONE.txt'

Remove-Item -Path $marker -ErrorAction SilentlyContinue

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("[secret-scan] started $(Get-Date -Format o)")

# Patterns that indicate a credential embedded in configuration or a script. Deliberately broad:
# a false positive costs one line of reading, and a false negative is the whole point of the scan.
$patterns = @(
    'Password\s*=\s*[^;"''\s]',
    'Pwd\s*=\s*[^;"''\s]',
    'ApiKey\s*[:=]\s*["'']?[A-Za-z0-9]',
    'client_secret',
    'BEGIN [A-Z ]*PRIVATE KEY'
)

# Only tracked files are scanned: an ignored file is not what reaches the remote, and bin/obj are
# build output rather than source.
$tracked = & git ls-files
$lines.Add("[secret-scan] tracked files: $($tracked.Count)")

$findings = 0

foreach ($file in $tracked) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    if ($file -match '^(artifacts/|.*\.zip$)') { continue }

    foreach ($pattern in $patterns) {
        $hits = Select-String -LiteralPath $file -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue

        foreach ($hit in $hits) {
            # The known-safe placeholders, each allowed by name rather than by pattern so that adding
            # one is a deliberate act that shows up in a diff:
            #
            #   ApiFactory.cs               the API test host's throwaway database.
            #   verify.local.example.ps1    the tracked example, whose purpose is the shape, not a value.
            #   SECURITY.md                 this document quotes the shapes it is telling you to avoid.
            #   IngestionGatewayTests.cs    a fabricated "apikey=SECRET" inside the test that proves a
            #                               provider's exception message is never copied into the
            #                               ingestion ledger. The literal is the thing under test.
            $safe =
                ($file -eq 'tests/AI.Investment.Api.Tests/ApiFactory.cs') -or
                ($file -eq 'scripts/verify.local.example.ps1') -or
                ($file -eq 'docs/SECURITY.md') -or
                ($file -eq 'tests/AI.Investment.Application.UnitTests/Ingestion/IngestionGatewayTests.cs')

            if ($safe) {
                $lines.Add("[secret-scan] ALLOWED  $file`:$($hit.LineNumber) (documented placeholder)")
            }
            else {
                $findings++
                $lines.Add("[secret-scan] FINDING  $file`:$($hit.LineNumber) matched /$pattern/")
            }
        }
    }
}

$lines.Add("[secret-scan] findings in the working tree: $findings")

# History. Removing a value from the working tree does not un-disclose it, so the scan reports what
# is still reachable from the current branch rather than implying it is gone.
$lines.Add('[secret-scan] --- history ---')

$historyHits = & git log --all --oneline -S 'Password=' -- 'src/AI.Investment.Api/appsettings.json' 'src/AI.Investment.Api/appsettings.Development.json' 'scripts/verify.ps1' 2>&1

if ($LASTEXITCODE -eq 0 -and $historyHits) {
    foreach ($line in $historyHits) {
        $lines.Add("[secret-scan] history commit touching a credential line: $line")
    }
}
else {
    $lines.Add('[secret-scan] no history search result (git unavailable, or no such commits)')
}

$lines.Add("[secret-scan] finished $(Get-Date -Format o)")

$lines | Set-Content -Path $log -Encoding utf8
"findings=$findings" | Set-Content -Path $marker -Encoding utf8

Write-Host "[secret-scan] done. findings=$findings"

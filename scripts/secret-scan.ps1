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
# The known-safe files, each allowed by exact path rather than by pattern, and each carrying the
# reason it is allowed. Allowing by name means adding one is a deliberate act that shows up in a
# diff and is read with its justification; allowing by pattern would let a future file become exempt
# by accident. The reason is printed with every allowance, so the log states why rather than merely
# that. NOTHING HERE IS A CREDENTIAL: each entry was opened and checked before it was added.
$allowed = [ordered]@{
    'tests/AI.Investment.Api.Tests/ApiFactory.cs' =
        'the API test host''s throwaway database'
    'scripts/verify.local.example.ps1' =
        'the tracked example, whose purpose is the shape, not a value'
    'docs/SECURITY.md' =
        'the document quotes the shapes it is telling you to avoid'
    'tests/AI.Investment.Application.UnitTests/Ingestion/IngestionGatewayTests.cs' =
        'a fabricated provider credential inside the test proving a provider exception message is never copied into the ingestion ledger; the literal is the thing under test'
    'tests/AI.Investment.Safety.Tests/KillSwitchTests.cs' =
        'a deliberately unreachable connection string (loopback, port 1, user "nobody") whose whole purpose is that nothing can connect with it'
    'scripts/secret-scan.ps1' =
        'this scanner; its pattern list is by definition a list of the shapes it searches for, and its comments name the placeholders it allows'
    'docs/Phases/VERIFICATION-LOG.md' =
        'the append-only verification record narrates what past scans found and therefore quotes those shapes; it is a record rather than configuration, and it is never edited'
}

$tracked = & git ls-files
$lines.Add("[secret-scan] tracked files: $($tracked.Count)")

$findings = 0

foreach ($file in $tracked) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    if ($file -match '^(artifacts/|.*\.zip$)') { continue }

    foreach ($pattern in $patterns) {
        $hits = Select-String -LiteralPath $file -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue

        foreach ($hit in $hits) {
            if ($allowed.Contains($file)) {
                $lines.Add("[secret-scan] ALLOWED  $file`:$($hit.LineNumber) ($($allowed[$file]))")
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

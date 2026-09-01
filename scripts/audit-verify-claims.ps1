#requires -Version 5.1
<#
    READ-ONLY CLAIM VERIFICATION.

    The audit was produced partly from a staged copy of the tree, and a staged copy can be
    stale. Every load-bearing claim is re-asked here against the working tree itself, so
    that nothing in the report rests on a file that has since moved.

    Reads. Writes one report. Starts nothing, touches no database, makes no request.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\audit'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out '50-claims.md'

$lines = New-Object 'System.Collections.Generic.List[string]'

function Say([string]$text) {
    $null = $lines.Add($text)
    Write-Host $text
}

function Save-Log {
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
}

$excluded = '\\(bin|obj|node_modules|\.vs|\.git|StrykerOutput|TestResults|artifacts)\\'

function Sources([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -Path $path)) { return @() }

    return @(Get-ChildItem -Path $path -Recurse -File -Include '*.cs', '*.razor' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excluded })
}

function Relative([string]$path) {
    if ($path.StartsWith($root)) { return $path.Substring($root.Length).TrimStart('\') }
    return $path
}

$srcFiles = @(Sources 'src')
$testFiles = @(Sources 'tests')

Say '# Claim verification against the working tree'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ('Files scanned: ' + [string]$srcFiles.Count + ' under src, ' + [string]$testFiles.Count + ' under tests.')
Say ''

# ---- CLAIM 1: which policy rules does the policy engine actually declare? --

Say '## 1. PolicyEngine rule inventory'
Say ''
Say 'The staged copy declared eight rules; a live audit row listed ten. Which is current?'
Say ''
Say '```'

$policyEngine = @($srcFiles | Where-Object { $_.Name -eq 'PolicyEngine.cs' })

foreach ($f in $policyEngine) {
    Say (Relative $f.FullName)
    $found = @(Select-String -Path $f.FullName -Pattern 'policy\.[a-z\-]+@\d' -ErrorAction SilentlyContinue)
    foreach ($m in $found) { Say ('    ' + [string]$m.LineNumber + ': ' + $m.Line.Trim()) }
    Say ('    rule-string count: ' + [string]$found.Count)
}

if ($policyEngine.Count -eq 0) { Say '  PolicyEngine.cs NOT FOUND' }

Say '```'
Say ''

# ---- CLAIM 2: which registered components have no production caller? ------

Say '## 2. Registered but uncalled? (production callers, excluding the definition)'
Say ''
Say 'For each name: hits in src (excluding its own defining file and the DI files), then hits in tests.'
Say ''
Say '```'

$suspects = @(
    'OpportunityExecutor'
    'ApprovalWorkflow'
    'SimulatedExecutionProposal'
    'SimulatedVenue'
    'AnalysisPipeline'
    'AutonomyCircuitBreaker'
    'AutonomyAdministration'
    'UnattendedInvariants'
    'ScoringEngine'
    'FinancialHealthEngine'
    'OpportunityWorkflow'
    'ExpireOverdueAsync'
    'PortfolioReader'
    'PriceRecoveryDiscoverer'
    'LiveVenueService'
    'IssueWarrantAsync'
    'EvaluationHarness'
)

foreach ($name in $suspects) {
    $srcHits = New-Object 'System.Collections.Generic.List[string]'
    $testCount = 0

    foreach ($f in $srcFiles) {
        $found = @(Select-String -Path $f.FullName -Pattern ('\b' + $name + '\b') -ErrorAction SilentlyContinue)
        if ($found.Count -eq 0) { continue }

        $rel = Relative $f.FullName
        $isDefinition = ($f.BaseName -eq $name)
        $isDi = ($f.Name -eq 'DependencyInjection.cs' -or $f.Name -eq 'Program.cs')
        $tag = ''
        if ($isDefinition) { $tag = '  [its own file]' }
        if ($isDi) { $tag = '  [DI registration]' }

        foreach ($m in $found) {
            $null = $srcHits.Add(('    ' + $rel + ':' + [string]$m.LineNumber + $tag))
        }
    }

    foreach ($f in $testFiles) {
        $testCount += @(Select-String -Path $f.FullName -Pattern ('\b' + $name + '\b') -ErrorAction SilentlyContinue).Count
    }

    Say ''
    Say ($name + '   [src hits: ' + [string]$srcHits.Count + ', test hits: ' + [string]$testCount + ']')

    $shown = 0
    foreach ($h in $srcHits) {
        $shown++
        if ($shown -le 14) { Say $h }
    }
    if ($srcHits.Count -gt 14) { Say ('    ... and ' + [string]($srcHits.Count - 14) + ' more') }
}

Say '```'
Say ''

# ---- CLAIM 3: the exposure snapshot's inert inputs ------------------------

Say '## 3. LedgerExposureProvider - what it feeds the limit engine'
Say ''
Say '```'

foreach ($f in @($srcFiles | Where-Object { $_.Name -eq 'LedgerExposureProvider.cs' })) {
    Say (Relative $f.FullName)
    $found = @(Select-String -Path $f.FullName -Pattern 'ExposureSnapshot|exposureByInstrument|Money\.Zero|cycleCost|null' -ErrorAction SilentlyContinue)
    foreach ($m in $found) { Say ('    ' + [string]$m.LineNumber + ': ' + $m.Line.Trim()) }
}

Say '```'
Say ''

# ---- CLAIM 4: deployment machinery -----------------------------------------

Say '## 4. Deployment machinery'
Say ''
Say '```'

$deployPatterns = @('Dockerfile*', 'docker-compose*', '*.yml', '*.yaml', '*.tf', '*.bicep', 'dotnet-tools.json')

foreach ($pattern in $deployPatterns) {
    $hits = @(Get-ChildItem -Path $root -Recurse -File -Filter $pattern -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excluded })

    Say ($pattern + ' -> ' + [string]$hits.Count)
    foreach ($h in $hits) { Say ('    ' + (Relative $h.FullName)) }
}

$github = Join-Path $root '.github'
Say ('.github directory exists: ' + [string](Test-Path -Path $github))
if (Test-Path -Path $github) {
    foreach ($h in @(Get-ChildItem -Path $github -Recurse -File -ErrorAction SilentlyContinue)) {
        Say ('    ' + (Relative $h.FullName))
    }
}

Say '```'
Say ''

# ---- CLAIM 5: authorization coverage on controllers ------------------------

Say '## 5. Controllers and their authorization attributes'
Say ''
Say '```'

foreach ($f in @($srcFiles | Where-Object { $_.Name -match 'Controller\.cs$' } | Sort-Object Name)) {
    $auth = @(Select-String -Path $f.FullName -Pattern '^\s*\[Authorize' -ErrorAction SilentlyContinue)
    $verbs = @(Select-String -Path $f.FullName -Pattern '^\s*\[Http(Get|Post|Put|Patch|Delete)' -ErrorAction SilentlyContinue)
    $writes = @($verbs | Where-Object { $_.Line -match 'Post|Put|Patch|Delete' })

    Say ($f.BaseName + '   endpoints: ' + [string]$verbs.Count + ', writes: ' + [string]$writes.Count + ', [Authorize]: ' + [string]$auth.Count)
}

Say '```'
Say ''

# ---- CLAIM 6: chat model implementations -----------------------------------

Say '## 6. IChatModel implementations'
Say ''
Say '```'

foreach ($f in $srcFiles) {
    $found = @(Select-String -Path $f.FullName -Pattern ':\s*IChatModel|IChatModel,|class .*IChatModel' -ErrorAction SilentlyContinue)
    foreach ($m in $found) { Say ((Relative $f.FullName) + ':' + [string]$m.LineNumber + '  ' + $m.Line.Trim()) }
}

Say '```'
Say ''

# ---- CLAIM 7: is the raw archive ignored? ----------------------------------

Say '## 7. Raw archive and gitignore'
Say ''
Say '```'

$gitignore = Join-Path $root '.gitignore'
if (Test-Path $gitignore) {
    $found = @(Select-String -Path $gitignore -Pattern 'archive' -ErrorAction SilentlyContinue)
    Say ('.gitignore rules mentioning "archive": ' + [string]$found.Count)
    foreach ($m in $found) { Say ('    ' + [string]$m.LineNumber + ': ' + $m.Line.Trim()) }
}

$archive = Join-Path $root 'src\AI.Investment.Api\archive'
Say ('src\AI.Investment.Api\archive exists: ' + [string](Test-Path -Path $archive))
if (Test-Path -Path $archive) {
    $payloads = @(Get-ChildItem -Path $archive -Recurse -File -ErrorAction SilentlyContinue)
    Say ('    files inside: ' + [string]$payloads.Count)
}

Say '```'
Say ''

# ---- CLAIM 8: git remote currency ------------------------------------------

Say '## 8. Git remote state'
Say ''
Say '```'

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    Push-Location $root
    Say ('local  HEAD : ' + ((& git rev-parse --short HEAD 2>&1) | Out-String).Trim())
    Say ('remote ref  : ' + ((& git rev-parse --short origin/master 2>&1) | Out-String).Trim())
    Say ('ahead/behind: ' + ((& git rev-list --left-right --count origin/master...HEAD 2>&1) | Out-String).Trim())
    Say ''
    Say 'remotes:'
    foreach ($line in @(& git remote -v 2>&1)) { Say ('    ' + ([string]$line).Trim()) }
}
catch { Say ('git unavailable: ' + $_.Exception.Message) }
finally {
    Pop-Location
    $ErrorActionPreference = $previous
}

Say '```'
Say ''

# ---- CLAIM 9: the legacy skeleton projects ---------------------------------

Say '## 9. Legacy skeleton projects - are they in the solution?'
Say ''
Say '```'

$sln = Join-Path $root 'AI-Investment-Analyst.sln'
if (Test-Path $sln) {
    $found = @(Select-String -Path $sln -Pattern 'AI-Investment-' -ErrorAction SilentlyContinue)
    Say ('solution entries matching "AI-Investment-": ' + [string]$found.Count)
    foreach ($m in $found) { Say ('    ' + $m.Line.Trim()) }
}

foreach ($legacy in @('AI-Investment-API', 'AI-Investment-App', 'AI-Investment-Domain', 'AI-Investment-Infrastructure')) {
    $path = Join-Path $root $legacy
    Say ($legacy + ' on disk: ' + [string](Test-Path -Path $path))
}

Say '```'
Say ''

# ---- CLAIM 10: does anything sweep expired cycle leases? -------------------

Say '## 10. Lease expiry, autonomy sweep and expiry callers'
Say ''
Say '```'

foreach ($term in @('LeaseExpiresAtUtc', 'GetRunnableAsync', 'SweepAsync', 'ReleaseLease')) {
    Say ''
    Say ($term + ':')
    foreach ($f in $srcFiles) {
        $found = @(Select-String -Path $f.FullName -Pattern ('\b' + $term + '\b') -ErrorAction SilentlyContinue)
        foreach ($m in $found) { Say ('    ' + (Relative $f.FullName) + ':' + [string]$m.LineNumber) }
    }
}

Say '```'
Say ''
Say '# END'
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit 0

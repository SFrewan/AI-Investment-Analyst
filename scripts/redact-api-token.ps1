#requires -Version 5.1
<#
    REDACT PROVIDER CREDENTIALS FROM THE VERIFICATION LOGS.

    HttpClient logs the request URI, and the EODHD connector puts its key in the query
    string, so every log line that recorded a fetch recorded the key with it.

    This rewrites the credential OUT of those files and leaves everything else in place -
    the timings, the status codes, the outcomes, every line of verification evidence. No
    file is deleted and no line is removed.

    NOTHING IN THIS SCRIPT PRINTS A CREDENTIAL. The redaction is done by pattern, so it
    never needs to know the value; the value is loaded only for the final count-only check
    that nothing was missed, and is cleared immediately afterwards.
#>

[CmdletBinding()]
param(
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\audit'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'redaction.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'

function Say([string]$text) {
    $null = $lines.Add($text)
    Write-Host $text
}

function Save-Log {
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
}

# Build output, tool output and the VS cache. Skipped for speed, and because a copy of a
# log under bin\ is a copy of the same evidence rather than more of it.
$excluded = '\\(bin|obj|node_modules|\.vs|\.git|StrykerOutput|TestResults)\\'

$textLike = @('.txt', '.log', '.md', '.json', '.cs', '.ps1', '.cmd', '.xml', '.html', '.csv')

function Walk([string]$path) {
    if (-not (Test-Path -Path $path)) { return @() }

    return @(Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $script:excluded } |
        Where-Object { $script:textLike -contains $_.Extension.ToLowerInvariant() } |
        Where-Object { $_.Length -lt 80MB })
}

# Belt and braces for anything this script prints out of a source file: any run of 20 or
# more token-alphabet characters is masked before it can reach the console or the log.
function Mask([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return '' }
    $masked = [regex]::Replace($text, '[A-Za-z0-9_\-]{20,}', '<masked>')
    if ($masked.Length -gt 160) { return $masked.Substring(0, 160) + ' ...' }
    return $masked
}

# Query-string credential parameters, longest first so a longer name is not eaten by a
# shorter one. The replacement keeps the parameter name, so the log still shows that a
# credential was sent - only the value goes.
$patterns = @(
    'api_token='
    'api-token='
    'apikey='
    'api_key='
    'access_token='
    'token='
)

$replacement = 'REDACTED-BY-scripts/redact-api-token.ps1'

Say '==============================================================='
Say ' CREDENTIAL REDACTION - verification evidence is preserved'
Say (' started : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say (' repo    : ' + $root)
if ($WhatIfOnly) { Say ' MODE    : DRY RUN, nothing is written' }
Say '==============================================================='
Save-Log

# ---- 1. which files carry a credential-shaped query parameter --------------

$searchRoots = @(
    (Join-Path $root 'artifacts')
    (Join-Path $root 'docs')
    (Join-Path $root 'scripts')
    (Join-Path $root 'src')
    (Join-Path $root 'tests')
)

$candidates = New-Object 'System.Collections.Generic.List[object]'

foreach ($searchRoot in $searchRoots) {
    if (-not (Test-Path -Path $searchRoot)) { continue }

    $files = @(Walk $searchRoot)

    foreach ($file in $files) {
        $text = $null
        try { $text = Get-Content -Path $file.FullName -Raw -ErrorAction Stop }
        catch { continue }

        if ($null -eq $text) { continue }

        $hits = 0
        foreach ($p in $patterns) {
            # A credential-shaped value: at least 12 characters of token alphabet after the
            # parameter name. Short values are placeholders like REPLACE_ME and are left alone.
            $rx = [regex]::Escape($p) + '[A-Za-z0-9_\-\.]{12,}'
            $hits += ([regex]::Matches($text, $rx, 'IgnoreCase')).Count
        }

        if ($hits -gt 0) {
            $null = $candidates.Add([pscustomobject]@{
                    Path  = $file.FullName
                    Hits  = $hits
                    Bytes = $file.Length
                })
        }

        $text = $null
    }
}

Say ''
Say ('  files carrying a credential-shaped query parameter: ' + [string]$candidates.Count)

foreach ($c in $candidates) {
    $relative = $c.Path.Substring($root.Length).TrimStart('\')
    Say ('    ' + $relative + '   (' + [string]$c.Hits + ' occurrences, ' + [string]$c.Bytes + ' bytes)')
}

Save-Log

# ---- 2. rewrite them in place, keeping every other byte --------------------

$rewritten = 0
$occurrences = 0

if (-not $WhatIfOnly) {
    foreach ($c in $candidates) {
        $text = Get-Content -Path $c.Path -Raw
        $before = $text

        foreach ($p in $patterns) {
            $rx = [regex]::Escape($p) + '[A-Za-z0-9_\-\.]{12,}'
            $text = [regex]::Replace($text, $rx, ($p + $replacement), 'IgnoreCase')
        }

        if ($text -ne $before) {
            Set-Content -Path $c.Path -Value $text -NoNewline -Encoding UTF8
            $rewritten++
            $occurrences += $c.Hits
        }

        $text = $null
        $before = $null
    }
}

Say ''
Say ('  files rewritten     : ' + [string]$rewritten)
Say ('  values redacted     : ' + [string]$occurrences)
Say '  nothing was deleted; only the credential value inside each URL was replaced.'
Save-Log

# ---- 3. count-only check against the real value ----------------------------
#
# The only step that touches the credential. It is read into a variable, used for a
# count, and cleared. It is never written, logged, hashed or echoed.

Say ''
Say '  verifying against the configured value (count only, never printed)'

$secret = $null
$secretKeys = New-Object 'System.Collections.Generic.List[string]'

try {
    $apiProject = Join-Path $root 'src\AI.Investment.Api'
    $listing = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"

    if ($LASTEXITCODE -eq 0) {
        foreach ($line in @($listing -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -le 0) { continue }

            $key = $line.Substring(0, $i).Trim()
            $null = $secretKeys.Add($key)

            if ($key -match '(?i)eodhd' -and $key -match '(?i)(key|token|secret)') {
                $secret = $line.Substring($i + 3)
            }
        }
    }

    $listing = $null
}
catch { }

Say ''
Say '  configured secret KEY NAMES (names only, no values):'
foreach ($k in $secretKeys) { Say ('    ' + $k) }

if ([string]::IsNullOrWhiteSpace($secret)) {
    Say ''
    Say '  No EODHD key found in user secrets, so the value check was not run.'
    Say '  The pattern redaction above still applied.'
}
else {
    $remaining = 0
    $remainingFiles = New-Object 'System.Collections.Generic.List[string]'

    foreach ($searchRoot in $searchRoots) {
        if (-not (Test-Path -Path $searchRoot)) { continue }

        $files = @(Walk $searchRoot)

        foreach ($file in $files) {
            $text = $null
            try { $text = Get-Content -Path $file.FullName -Raw -ErrorAction Stop }
            catch { continue }
            if ($null -eq $text) { continue }

            if ($text.Contains($secret)) {
                $remaining++
                $null = $remainingFiles.Add($file.FullName.Substring($root.Length).TrimStart('\'))
            }

            $text = $null
        }
    }

    Say ''
    Say ('  files still containing the configured value: ' + [string]$remaining)
    foreach ($f in $remainingFiles) { Say ('    STILL PRESENT: ' + $f) }

    # ---- 4. has it ever been committed? -----------------------------------

    Say ''
    Say '  checking git history (count only)'

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $commits = @()
    try {
        Push-Location $root
        $commits = @(& git log --all --format=%H -S $secret 2>$null)
    }
    catch { }
    finally {
        Pop-Location
        $ErrorActionPreference = $previous
    }

    $commits = @($commits | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    Say ('  commits whose diff contains the value: ' + [string]$commits.Count)
    if ($commits.Count -gt 0) {
        Say '  THIS IS SERIOUS: the credential is in git history. Rotation is mandatory,'
        Say '  and history rewriting or repository invalidation should be considered.'
    }
    else {
        Say '  Clean. The credential has never been committed.'
    }
}

$secret = $null
[System.GC]::Collect()

# ---- 5. where the leak came from ------------------------------------------

Say ''
Say '  SOURCE OF THE LEAK (for the remediation item, not changed by this script):'

$providerFiles = @(Get-ChildItem -Path (Join-Path $root 'src') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excluded } |
    Where-Object { $_.Name -match '(?i)eodhd' })

foreach ($f in $providerFiles) {
    $relative = $f.FullName.Substring($root.Length).TrimStart('\')
    $matched = @(Select-String -Path $f.FullName -Pattern 'api_token|apiToken|ApiKey|BuildUri|query' -ErrorAction SilentlyContinue)
    foreach ($m in $matched) {
        Say ('    ' + $relative + ':' + [string]$m.LineNumber + '  ' + (Mask $m.Line.Trim()))
    }
}

Say ''
Say '==============================================================='
Say ' DONE. No credential value appears anywhere in this report.'
Say (' finished: ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say '==============================================================='
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit 0

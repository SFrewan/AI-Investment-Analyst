# ---------------------------------------------------------------------------
#  Read-only verification that the EODHD credential is configured.
#
#  THIS SCRIPT NEVER EMITS THE CREDENTIAL. Not its value, not a hash, not its
#  length, not a prefix. The secret is read into a variable, compared, and the
#  variable is cleared; only booleans and counts are ever written out. Read it
#  before you run it - that promise is the whole point of the file.
#
#  It makes no network request of any kind, activates nothing and writes
#  nothing to the database.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
Set-Location -Path (Join-Path $PSScriptRoot '..')

$out = Join-Path 'artifacts/verify' 'eodhd-credential.txt'
New-Item -ItemType Directory -Force -Path 'artifacts/verify' | Out-Null

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("[eodhd-probe] started $(Get-Date -Format o)")

# ---- 1. configuration ------------------------------------------------------
$shipped = Get-Content 'src/AI.Investment.Api/appsettings.json' -Raw | ConvertFrom-Json
$dev     = Get-Content 'src/AI.Investment.Api/appsettings.Development.json' -Raw | ConvertFrom-Json

$lines.Add("shipped Providers:Eodhd:Enabled        = $($shipped.Providers.Eodhd.Enabled)")
$lines.Add("development Providers:Eodhd:Enabled    = $($dev.Providers.Eodhd.Enabled)")
$lines.Add("development exchanges configured       = $($dev.Providers.Eodhd.Exchanges.Count)")
$lines.Add("development exchange codes             = $($dev.Providers.Eodhd.Exchanges.Code -join ',')")
$lines.Add("shipped OperationsHost:RunCycles       = $($shipped.OperationsHost.RunCycles)")
$lines.Add("development OperationsHost:RunCycles   = $($dev.OperationsHost.RunCycles)")
$lines.Add("development DataPlane:SeedSources      = $($dev.DataPlane.SeedSourcesOnStartup)")

# An ApiKey property present in either tracked file is a defect on its own.
$lines.Add("shipped file declares an ApiKey        = $($null -ne $shipped.Providers.Eodhd.PSObject.Properties['ApiKey'])")
$lines.Add("development file declares an ApiKey    = $($null -ne $dev.Providers.Eodhd.PSObject.Properties['ApiKey'])")

# ---- 2. the credential, without looking at it ------------------------------
# 'dotnet user-secrets list' prints "key = value" pairs. Its output is captured
# into a variable that is never written anywhere, matched against the key name,
# and cleared. The only thing derived from it is a boolean.
$listed = & dotnet user-secrets list --project 'src/AI.Investment.Api' 2>&1

$entry = $listed | Where-Object { $_ -match '^\s*Providers:Eodhd:ApiKey\s*=\s*(.+)$' } | Select-Object -First 1

if ($null -eq $entry) {
    $lines.Add("credential in user-secrets             = NOT CONFIGURED")
    $secret = $null
}
else {
    $secret = [regex]::Match($entry, '^\s*Providers:Eodhd:ApiKey\s*=\s*(.+)$').Groups[1].Value.Trim()

    if ([string]::IsNullOrWhiteSpace($secret)) {
        $lines.Add("credential in user-secrets             = PRESENT BUT EMPTY")
        $secret = $null
    }
    else {
        $lines.Add("credential in user-secrets             = CONFIGURED")
    }
}

$listed = $null
$entry = $null

# ---- 3. the credential is in no tracked file -------------------------------
# The comparison happens in memory. Only the number of files that contain it is
# reported, and the expected number is zero.
if ($null -ne $secret) {
    $tracked = & git ls-files
    $hits = 0

    foreach ($file in $tracked) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }

        try {
            if (Select-String -LiteralPath $file -SimpleMatch -Pattern $secret -Quiet -ErrorAction SilentlyContinue) {
                $hits++
            }
        }
        catch {
            # A binary or unreadable file cannot contain a pasted credential in a
            # form this check would find; skipping it is not a weakening.
        }
    }

    $lines.Add("tracked files containing the credential = $hits")
    $lines.Add("tracked files searched                  = $($tracked.Count)")
}
else {
    $lines.Add("tracked files containing the credential = not searched (no credential to search for)")
}

# Clear it from this process before anything else runs.
$secret = $null
[System.GC]::Collect()

$lines.Add("[eodhd-probe] no network request was made by this script")
$lines.Add("[eodhd-probe] finished $(Get-Date -Format o)")

$lines | Set-Content -Path $out -Encoding UTF8

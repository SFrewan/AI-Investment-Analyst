#requires -Version 5.1
<#
    OBSERVATION WINDOW - BLOCKER 1: ADD THE TWO MISSING OPERATOR PRIVILEGES.
    AND THE ARCHIVE WRITABILITY CHECK.

    WHAT THIS CHANGES
      Exactly two new User Secrets keys on the single existing operator account:
        Operators:Accounts:<i>:Privileges:<n>   = AnswerEscalations
        Operators:Accounts:<i>:Privileges:<n+1> = ViewPortfolio

      Nothing else. The key digest, Id, DisplayName, the existing AdministerWatches
      privilege and every other secret are untouched - this uses `dotnet user-secrets
      set` on the two new keys by name, which writes those keys and no others.

    WHAT IT REFUSES TO DO
      It stops rather than guessing if the account shape is not the one this change was
      written for: exactly one account, holding exactly AdministerWatches. An installation
      that has drifted from that needs a person to look, not a script to overwrite.

    DISCLOSURE RULE - UNCHANGED
      Booleans and counts only. The key digest is read to prove it did NOT change, by
      comparing it in memory with the value read before the change. Neither value, nor a
      hash, prefix, fragment or length of either, is written to the console or the log.

    THE ARCHIVE CHECK
      Section B. Read-only with respect to application data: it creates one zero-byte
      probe file with an obvious name and deletes it again, which is the only reliable
      way to know a directory is writable. It writes no archive payload, no database row
      and no configuration.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'blocker1-privileges-and-archive.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

function Show([object]$value, [string]$whenNull = '(not stated)') {
    if ($null -eq $value) { return $whenNull }
    return [string]$value
}

$apiProject = Join-Path $root 'src\AI.Investment.Api'

# Reads the secret store into a hashtable. The caller must never emit a value from it.
function Read-Secrets {
    $map = @{}
    $text = $null
    try {
        $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { return $null }
        foreach ($line in @($text -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -gt 0) { $map[$line.Substring(0, $i).Trim()] = $line.Substring($i + 3) }
        }
    }
    catch { return $null }
    finally { $text = $null }

    return $map
}

Say '=== BLOCKER 1: OPERATOR PRIVILEGES + ARCHIVE CHECK ==='
Say ("timestamp (local) : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ("powershell        : " + $PSVersionTable.PSVersion.ToString())
Say ''

# ======================================================================================
# A. OPERATOR PRIVILEGES
# ======================================================================================
<#
    Section A as a function, so that a refusal to touch the account returns rather than
    ending the script: the archive check in section B is a separate question and the
    operator asked for both. Every exit path here leaves User Secrets untouched.
#>
$script:PrivilegeExit = 0

function Invoke-PrivilegeUpdate {
    Say '--- A1. CURRENT ACCOUNT SHAPE (before) ---'

    $before = Read-Secrets

    if ($null -eq $before) {
        Say '  user-secrets could not be read. STOPPING. Nothing was changed.'
        $script:PrivilegeExit = 1
        return
    }

    Say ('  secret entries : ' + $before.Count)

    $rawIndices = @()
    foreach ($k in @($before.Keys)) {
        if ($k -match '^Operators:Accounts:(\d+):') { $rawIndices += [int]$Matches[1] }
    }
    $indices = @($rawIndices | Sort-Object -Unique)

    Say ('  operator accounts : ' + $indices.Count)

    if ($indices.Count -ne 1) {
        Say '  This change was written for exactly one configured account. STOPPING.'
        Say '  Nothing was changed.'
        $script:PrivilegeExit = 1
        return
    }

    $i = $indices[0]
    $idKey     = "Operators:Accounts:${i}:Id"
    $nameKey   = "Operators:Accounts:${i}:DisplayName"
    $digestKey = "Operators:Accounts:${i}:KeySha256"

    $beforeId     = if ($before.ContainsKey($idKey))   { $before[$idKey] }   else { $null }
    $beforeName   = if ($before.ContainsKey($nameKey)) { $before[$nameKey] } else { $null }
    $beforeDigest = if ($before.ContainsKey($digestKey)) { $before[$digestKey] } else { $null }

    $beforePrivKeys = @()
    foreach ($k in @($before.Keys)) {
        if ($k -match "^Operators:Accounts:${i}:Privileges:(\d+)$") { $beforePrivKeys += $k }
    }
    $beforePrivs = @($beforePrivKeys | ForEach-Object { $before[$_] } | Sort-Object)

    Say ('  account index  : ' + $i)
    Say ('  Id             : ' + (Show $beforeId))
    Say ('  DisplayName    : ' + (Show $beforeName))
    Say ('  digest present and well formed : ' +
         ($null -ne $beforeDigest -and $beforeDigest -cmatch '^[0-9a-f]{64}$'))
    Say ('  privileges     : ' + $(if ($beforePrivs.Count -eq 0) { '(none)' } else { $beforePrivs -join ', ' }))

    if ($beforePrivs.Count -ne 1 -or $beforePrivs[0] -ne 'AdministerWatches') {
        Say ''
        Say '  Expected exactly AdministerWatches and found something else. This script will not'
        Say '  overwrite privileges it did not put there. STOPPING. Nothing was changed.'
        $script:PrivilegeExit = 1
        return
    }

    # The next two free indices, so nothing existing is overwritten.
    $used = @()
    foreach ($k in $beforePrivKeys) {
        if ($k -match ':Privileges:(\d+)$') { $used += [int]$Matches[1] }
    }
    $next = 0
    while ($used -contains $next) { $next++ }
    $after = $next + 1
    while ($used -contains $after) { $after++ }

    $keyEscalations = "Operators:Accounts:${i}:Privileges:${next}"
    $keyPortfolio   = "Operators:Accounts:${i}:Privileges:${after}"

    Say ''
    Say '--- A2. WHAT WILL CHANGE ---'
    Say ('  SET ' + $keyEscalations + ' = AnswerEscalations')
    Say ('  SET ' + $keyPortfolio   + ' = ViewPortfolio')
    Say ''
    Say '  Two new keys. The key digest, Id, DisplayName, the existing AdministerWatches'
    Say '  privilege and every other secret are left exactly as they are. No application'
    Say '  code, configuration file, database row, watch, cycle or safety setting is touched.'

    Write-Host ''
    Write-Host 'Type UPDATE to apply, or anything else to abort:' -ForegroundColor Yellow
    $confirmation = Read-Host

    if ([string]$confirmation -cne 'UPDATE') {
        Say ''
        Say '  Not confirmed. STOPPING. Nothing was changed.'
        $script:PrivilegeExit = 0
        return
    }

    Say ''
    Say '--- A3. APPLYING ---'

    $failed = $false
    foreach ($pair in @(@($keyEscalations, 'AnswerEscalations'), @($keyPortfolio, 'ViewPortfolio'))) {
        $null = & dotnet user-secrets set $pair[0] $pair[1] --project $apiProject 2>&1
        if ($LASTEXITCODE -ne 0) {
            Say ('  FAILED to set ' + $pair[0])
            $failed = $true
        }
        else {
            Say ('  set ' + $pair[0] + ' = ' + $pair[1])
        }
    }

    if ($failed) {
        Say '  At least one write failed. Re-read the shape below before doing anything else.'
    }

    # ======================================================================================
    # A4. VERIFY
    # ======================================================================================
    Say ''
    Say '--- A4. RESULTING ACCOUNT SHAPE (after) ---'

    $afterMap = Read-Secrets

    if ($null -eq $afterMap) {
        Say '  user-secrets could not be re-read. Verify manually before proceeding.'
        $script:PrivilegeExit = 1
        return
    }

    $afterId     = if ($afterMap.ContainsKey($idKey))     { $afterMap[$idKey] }     else { $null }
    $afterName   = if ($afterMap.ContainsKey($nameKey))   { $afterMap[$nameKey] }   else { $null }
    $afterDigest = if ($afterMap.ContainsKey($digestKey)) { $afterMap[$digestKey] } else { $null }

    $afterPrivs = @()
    foreach ($k in @($afterMap.Keys)) {
        if ($k -match "^Operators:Accounts:${i}:Privileges:(\d+)$") { $afterPrivs += $afterMap[$k] }
    }
    $afterPrivs = @($afterPrivs | Sort-Object)

    # Compared in memory. Neither digest is emitted, nor anything derived from either.
    $digestUnchanged = ($null -ne $beforeDigest -and $beforeDigest -ceq $afterDigest)

    Say ('  secret entries : ' + $afterMap.Count + '   (was ' + $before.Count + ')')
    Say ('  Id unchanged            : ' + ($beforeId -ceq $afterId))
    Say ('  DisplayName unchanged   : ' + ($beforeName -ceq $afterName))
    Say ('  key digest UNCHANGED    : ' + $digestUnchanged)
    Say ('  digest still well formed: ' + ($null -ne $afterDigest -and $afterDigest -cmatch '^[0-9a-f]{64}$'))
    Say ('  privilege count         : ' + $afterPrivs.Count)
    Say ('  privileges              : ' + $(if ($afterPrivs.Count -eq 0) { '(none)' } else { $afterPrivs -join ', ' }))

    $expected = @('AdministerWatches', 'AnswerEscalations', 'ViewPortfolio')
    $correct = ($afterPrivs.Count -eq 3) -and
               (@(Compare-Object $expected $afterPrivs -SyncWindow 0).Count -eq 0)

    Say ('  exactly the three intended privileges : ' + $correct)

    $before = $null
    $afterMap = $null
    $beforeDigest = $null
    $afterDigest = $null

    Say ''
    if ($correct -and $digestUnchanged) {
        Say '  RESULT: privileges updated, credential untouched.'
        Say '  The API reads Operators at startup, so RESTART THE API for this to take effect.'
    }
    else {
        Say '  RESULT: the shape is not what was intended. Do not proceed; inspect it yourself.'
    }

    $script:PrivilegeExit = 0

    return
}

Invoke-PrivilegeUpdate

# ======================================================================================
# B. ARCHIVE WRITABILITY
# ======================================================================================
Say ''
Say '--- B. RAW ARCHIVE (RawArchive:RootPath) ---'

$rootPath = $null
try {
    $shipped = Join-Path $apiProject 'appsettings.json'
    $dev     = Join-Path $apiProject 'appsettings.Development.json'

    foreach ($file in @($dev, $shipped)) {
        if ($null -ne $rootPath) { continue }
        if (-not (Test-Path -LiteralPath $file)) { continue }

        $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        $section = $json.PSObject.Properties['RawArchive']
        if ($null -eq $section -or $null -eq $section.Value) { continue }
        $prop = $section.Value.PSObject.Properties['RootPath']
        if ($null -ne $prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
            $rootPath = [string]$prop.Value
            Say ('  configured RootPath : ' + $rootPath + '   (from ' + (Split-Path -Leaf $file) + ')')
        }
    }
}
catch { $rootPath = $null }

if ($null -eq $rootPath) {
    Say '  RawArchive:RootPath could not be read from configuration.'
}
else {
    # FileSystemRawResponseArchive does Path.GetFullPath(RootPath), so a relative path
    # resolves against the API process's working directory - the project folder under
    # `dotnet run --project`, which is the case here.
    $resolved = if ([System.IO.Path]::IsPathRooted($rootPath)) { $rootPath }
                else { Join-Path $apiProject $rootPath }

    Say ('  resolved for `dotnet run --project` : ' + $resolved)
    Say ('  exists : ' + (Test-Path -LiteralPath $resolved -PathType Container))

    # The archive calls Directory.CreateDirectory on demand, so a missing folder is fine
    # provided the parent can be written to. Probe whichever of the two exists.
    $probeDir = if (Test-Path -LiteralPath $resolved -PathType Container) { $resolved }
                else { Split-Path -Parent $resolved }

    Say ('  probing : ' + $probeDir)

    if (-not (Test-Path -LiteralPath $probeDir -PathType Container)) {
        Say '  Neither the archive folder nor its parent exists. The first ingestion would fail.'
    }
    else {
        $probe = Join-Path $probeDir ('.write-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [System.IO.File]::WriteAllBytes($probe, [byte[]]::new(0))
            $created = Test-Path -LiteralPath $probe
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
            $removed = -not (Test-Path -LiteralPath $probe)

            Say ('  writable : ' + $created)
            Say ('  probe file removed : ' + $removed)
            Say '  (one zero-byte probe file, created and deleted. No archive payload, no'
            Say '   database row and no configuration was written.)'
        }
        catch {
            Say ('  writable : False - ' + $_.Exception.Message)
        }
    }

    Say ''
    Say '  NOTE: this probes as the account running this script. If the API runs as a'
    Say '  different user or service account, its own access may differ.'

    try {
        $procs = @(Get-CimInstance Win32_Process -Filter "Name = 'AI.Investment.Api.exe'" -ErrorAction SilentlyContinue)
        if ($procs.Count -gt 0) {
            Say ('  AI.Investment.Api.exe running : ' + $procs.Count + ' process(es)')
        }
        else {
            Say '  AI.Investment.Api.exe running : not found by that name'
        }
    }
    catch { }
}

Say ''
Say '=== END ==='
Say 'RunCycles was not touched. No watch was created. No cycle was started.'
Say 'No EODHD request was made. No safety, limits, autonomy or licensing setting changed.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)

exit $script:PrivilegeExit

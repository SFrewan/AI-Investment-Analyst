#requires -Version 5.1
<#
    OBSERVATION WINDOW - STEP 4 PRE-ACTIVATION VERIFICATION.

    READ ONLY. This script changes nothing: no configuration is written, no source is
    activated, no watch is created, no EODHD request is made, no database row is written.

    DISCLOSURE RULE. The user-secrets store holds the operator key digest, the EODHD
    credential and the database password. This script reads it because that is the only
    place the operator account is configured, but it emits BOOLEANS AND COUNTS ONLY.
    No secret value, digest, fragment, prefix, hash or length is ever written to the
    console or to the log file. The raw secret text is held in one variable that is
    cleared before the section returns, and section A's error handler prints only an
    exception type and a line number, never a message that could carry secret text.

    ROBUSTNESS NOTE (fix for the 'Count' PropertyNotFoundStrict failure).
    Under Set-StrictMode -Version Latest, a pipeline that yields exactly one item assigns
    a SCALAR, and reading .Count on a scalar throws. Every pipeline result that is later
    counted or indexed is therefore wrapped in @( ), which preserves an array for zero,
    one or many items. Every property read from a JSON response goes through Get-Prop,
    which returns a fallback instead of throwing when the property is absent. Every
    section is independently wrapped, so one failure no longer aborts the probe and the
    log is always written.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'observation-step4.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }

# Safe property read: absent or null property yields the fallback rather than throwing
# under StrictMode.
function Get-Prop($obj, [string]$name, $fallback = '') {
    if ($null -eq $obj) { return $fallback }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $fallback }
    if ($null -eq $p.Value) { return $fallback }
    return $p.Value
}

$BaseUrl  = 'http://localhost:5143'
$SourceId = 'eodhd-eod'

Say '=== OBSERVATION WINDOW - STEP 4 PRE-ACTIVATION VERIFICATION (READ ONLY) ==='
Say ("timestamp (local) : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ("repository        : " + $root)
Say ("powershell        : " + $PSVersionTable.PSVersion.ToString())
Say ''

# --------------------------------------------------------------------------------------
# A. Operator account shape.  Booleans and counts only.
# --------------------------------------------------------------------------------------
Say '--- A. OPERATOR ACCOUNT (shape only; no key, no digest, no length, no prefix) ---'

$secretMap = @{}
$secretsReadable = $false

try {
    $apiProject = Join-Path $root 'src\AI.Investment.Api'
    $secretText = $null
    try {
        $secretText = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"

        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($secretText -split "`n")) {
                $i = $line.IndexOf(' = ')
                if ($i -gt 0) {
                    $k = $line.Substring(0, $i).Trim()
                    $v = $line.Substring($i + 3)
                    $secretMap[$k] = $v
                }
            }
            $secretsReadable = $true
        }
        else {
            Say 'user-secrets       : COULD NOT BE READ (dotnet user-secrets returned non-zero)'
        }
    }
    finally {
        $secretText = $null
    }

    if ($secretsReadable) {
        Say ('user-secrets       : readable, ' + $secretMap.Count + ' entries')

        # @( ) keeps this an array for zero, one or many accounts. This is the line whose
        # missing wrapper produced the reported PropertyNotFoundStrict on .Count.
        $rawIndices = @()
        foreach ($k in @($secretMap.Keys)) {
            if ($k -match '^Operators:Accounts:(\d+):') { $rawIndices += [int]$Matches[1] }
        }
        $indices = @($rawIndices | Sort-Object -Unique)

        Say ('configured operator accounts : ' + $indices.Count)

        if ($indices.Count -eq 0) {
            Say 'RESULT             : NO OPERATOR ACCOUNT CONFIGURED - the API authenticates nobody (fail-closed).'
        }

        foreach ($i in $indices) {
            $idKey     = "Operators:Accounts:${i}:Id"
            $nameKey   = "Operators:Accounts:${i}:DisplayName"
            $digestKey = "Operators:Accounts:${i}:KeySha256"

            $id   = if ($secretMap.ContainsKey($idKey))   { $secretMap[$idKey] }   else { '(absent)' }
            $name = if ($secretMap.ContainsKey($nameKey)) { $secretMap[$nameKey] } else { '(absent)' }

            # Boolean only. The digest is tested against the same shape the
            # OperatorAccountOptions RegularExpression requires, and is then never
            # referenced again. Nothing derived from its value is emitted.
            $digestOk = $false
            if ($secretMap.ContainsKey($digestKey)) {
                $digestOk = ($secretMap[$digestKey] -cmatch '^[0-9a-f]{64}$')
            }

            $rawPrivs = @()
            foreach ($k in @($secretMap.Keys)) {
                if ($k -match "^Operators:Accounts:${i}:Privileges:(\d+)$") { $rawPrivs += $secretMap[$k] }
            }
            $privs = @($rawPrivs | Sort-Object)

            $privText = if ($privs.Count -eq 0) { '(none)' } else { $privs -join ', ' }

            Say ''
            Say ('  account[' + $i + '] Id                           : ' + $id)
            Say ('  account[' + $i + '] DisplayName                  : ' + $name)
            Say ('  account[' + $i + '] key digest present + valid   : ' + $digestOk)
            Say ('  account[' + $i + '] privilege count              : ' + $privs.Count)
            Say ('  account[' + $i + '] privileges                   : ' + $privText)

            $exactlyWatches = ($privs.Count -eq 1 -and $privs[0] -eq 'AdministerWatches')
            Say ('  account[' + $i + '] has EXACTLY AdministerWatches : ' + $exactlyWatches)
            Say ('  account[' + $i + '] can activate a source        : ' + ($privs -contains 'AdministerWatches'))
        }

        $eodhdKeySet = ($secretMap.ContainsKey('Providers:Eodhd:ApiKey') -and
                        -not [string]::IsNullOrWhiteSpace($secretMap['Providers:Eodhd:ApiKey']))
        $dbSet = ($secretMap.ContainsKey('Database:ConnectionString') -and
                  -not [string]::IsNullOrWhiteSpace($secretMap['Database:ConnectionString']))

        Say ''
        Say ('Providers:Eodhd:ApiKey configured    : ' + $eodhdKeySet)
        Say ('Database:ConnectionString configured : ' + $dbSet)
    }
}
catch {
    # Deliberately terse: the raw secret listing passed through this scope, so no message
    # from this handler may carry text that originated in it.
    Say ('SECTION A FAILED : ' + $_.Exception.GetType().Name +
         ' at line ' + $_.InvocationInfo.ScriptLineNumber + ' (details suppressed by the disclosure rule)')
}

Say ''

# --------------------------------------------------------------------------------------
# B. API reachability.
# --------------------------------------------------------------------------------------
Say '--- B. API REACHABILITY ---'
Say ('base address       : ' + $BaseUrl)

$readyHealthy = $false

foreach ($probe in @('/health/live', '/health/ready', '/health')) {
    try {
        $r = Invoke-WebRequest -Uri ($BaseUrl + $probe) -UseBasicParsing -TimeoutSec 15
        $body = ($r.Content | Out-String).Trim()
        if ($body.Length -gt 200) { $body = $body.Substring(0, 200) + '...' }
        Say ('  GET ' + $probe.PadRight(14) + ' -> ' + [int]$r.StatusCode + ' ' + $body)
        if ($probe -eq '/health/ready' -and $body -match 'Healthy') { $readyHealthy = $true }
    }
    catch {
        $code = ''
        try {
            if ($null -ne $_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response) {
                $code = [string][int]$_.Exception.Response.StatusCode
            }
        }
        catch { $code = '' }
        Say ('  GET ' + $probe.PadRight(14) + ' -> FAILED ' + $code + ' ' + $_.Exception.Message)
    }
}

Say ''

# --------------------------------------------------------------------------------------
# C. PostgreSQL reachability, proved through the API's own readiness check.
# --------------------------------------------------------------------------------------
Say '--- C. POSTGRESQL REACHABILITY ---'
Say '  /health/ready runs the "postgresql" DatabaseHealthCheck. Healthy there means the'
Say '  API opened a connection to PostgreSQL with its configured credentials.'
Say ('  /health/ready reported Healthy : ' + $readyHealthy)

Say ''

# --------------------------------------------------------------------------------------
# D. Source registry: does eodhd-eod exist, and is it active?
# --------------------------------------------------------------------------------------
Say '--- D. SOURCE REGISTRY (GET /api/sources - anonymous read endpoint) ---'

try {
    # Two different normalizations are needed here, and both matter.
    #   Windows PowerShell 5.1 unrolls a JSON array onto the pipeline, so @(cmdlet) works.
    #   PowerShell 7 emits the whole array as ONE pipeline item, so @(cmdlet) yields a
    #   1-element array whose single element is the array - which is why the listing
    #   showed one nameless source.
    # Assigning first and wrapping the VARIABLE flattens correctly under both.
    $sourcesResponse = Invoke-RestMethod -Uri ($BaseUrl + '/api/sources') -TimeoutSec 30
    $sources = @($sourcesResponse)

    Say ('  registered sources : ' + $sources.Count)

    foreach ($s in $sources) {
        $sid = [string](Get-Prop $s 'id' '(no id)')
        Say ('    ' + $sid.PadRight(24) +
             ' isActive=' + (Get-Prop $s 'isActive' '?') +
             '  cadence=' + (Get-Prop $s 'cadence' '?') +
             '  name=' + (Get-Prop $s 'name' '?'))
    }

    $matching = @($sources | Where-Object { [string](Get-Prop $_ 'id' '') -eq $SourceId })

    Say ''
    Say ('  ' + $SourceId + ' registered : ' + ($matching.Count -gt 0))

    if ($matching.Count -gt 0) {
        $t = $matching[0]
        Say ('  ' + $SourceId + ' isActive   : ' + (Get-Prop $t 'isActive' '?'))
        Say ('  ' + $SourceId + ' verification : ' + (Get-Prop $t 'verificationPolicy' '?'))

        $lic = Get-Prop $t 'licensing' $null
        if ($null -ne $lic) {
            Say ('  ' + $SourceId + ' licensing.allowsStorage             : ' + (Get-Prop $lic 'allowsStorage' '?'))
            Say ('  ' + $SourceId + ' licensing.allowsAutomatedProcessing : ' + (Get-Prop $lic 'allowsAutomatedProcessing' '?'))
            Say '  (DataSource.Activate refuses when both of those are false.)'
        }
    }
}
catch {
    Say ('  FAILED : ' + $_.Exception.Message)
}

Say ''

# --------------------------------------------------------------------------------------
# E. Is a direct database read available for the post-activation audit check?
# --------------------------------------------------------------------------------------
Say '--- E. DATABASE READ PATH FOR THE AUDIT VERIFICATION ---'

$psql = $null
try {
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }

    if ([string]::IsNullOrWhiteSpace($psql)) {
        $candidates = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($candidates.Count -gt 0) { $psql = [string]$candidates[0].FullName }
    }

    if ([string]::IsNullOrWhiteSpace($psql)) {
        $psql = $null
        Say '  psql : NOT FOUND. Baseline audit counts cannot be read directly.'
    }
    else {
        Say ('  psql : FOUND at ' + $psql)
    }
}
catch {
    $psql = $null
    Say ('  psql lookup FAILED : ' + $_.Exception.Message)
}

if ($null -ne $psql -and $secretsReadable -and $secretMap.ContainsKey('Database:ConnectionString')) {
    try {
        $parts = @{}
        foreach ($seg in @($secretMap['Database:ConnectionString'] -split ';')) {
            $j = $seg.IndexOf('=')
            if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
        }

        $pgHost = if ($parts.ContainsKey('host'))     { $parts['host'] }     else { 'localhost' }
        $pgPort = if ($parts.ContainsKey('port'))     { $parts['port'] }     else { '5432' }
        $pgDb   = if ($parts.ContainsKey('database')) { $parts['database'] } else { '' }
        $pgUser = if ($parts.ContainsKey('username')) { $parts['username'] } else { '' }

        if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
        $parts = $null

        function Invoke-Sql([string]$sql) {
            $r = & $psql -h $pgHost -p $pgPort -U $pgUser -d $pgDb -t -A -c $sql 2>&1
            if ($LASTEXITCODE -ne 0) { return 'QUERY FAILED' }
            return (($r | Out-String).Trim())
        }

        try {
            Say ('  database host/port/name : ' + $pgHost + ':' + $pgPort + '/' + $pgDb + '   (password not shown)')
            Say ('  connection test         : ' + (Invoke-Sql "select 'ok'"))
            Say ''
            Say '  BASELINE COUNTS (before any activation):'
            Say ('    data_sources rows                                : ' + (Invoke-Sql 'select count(*) from data_sources'))
            Say ('    data_sources where id = eodhd-eod                : ' + (Invoke-Sql "select count(*) from data_sources where id = '$SourceId'"))
            Say ('    data_sources.is_active for eodhd-eod             : ' + (Invoke-Sql "select coalesce((select is_active::text from data_sources where id = '$SourceId'), '(row absent)')"))
            Say ('    audit_records total                              : ' + (Invoke-Sql 'select count(*) from audit_records'))
            Say ("    audit_records action_type = 'source.activate'     : " + (Invoke-Sql "select count(*) from audit_records where action_type = 'source.activate'"))
            Say ("    action_executions action_type = 'source.activate' : " + (Invoke-Sql "select count(*) from action_executions where action_type = 'source.activate'"))
        }
        finally {
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        }
    }
    catch {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Say ('  BASELINE READ FAILED : ' + $_.Exception.GetType().Name +
             ' at line ' + $_.InvocationInfo.ScriptLineNumber)
    }
}
elseif ($null -ne $psql) {
    Say '  Database:ConnectionString not available from user-secrets; baseline counts skipped.'
}

$secretMap = $null

Say ''
Say '=== END. NOTHING WAS CHANGED BY THIS SCRIPT. ==='

Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
Write-Host ''
Write-Host ('Written: ' + $log)

#requires -Version 5.1
<#
    SAFE STOP - DISABLE THE AAPL.US WATCH.

    POST http://localhost:5143/api/operator/watches/{id}/disablement
    authenticated with X-Operator-Key, routed through the same Action/Policy seam as
    everything else: policy evaluated, idempotency claimed, audited before and after,
    written inside an authorisation window.

    Disable, not delete. The row, the reason you give and the firing history all stay
    where they are, and IWatchStore.GetEnabledAsync already filters on Enabled - so the
    schedule ticker stops offering it a signal and nothing else has to learn a new rule.
    Reversible in principle: Watch.Enable exists in the domain, though this build exposes
    no endpoint for it, so re-enabling is a future block rather than a second click.

    This does NOT stop the API, does not change RunCycles, and does not touch any other
    watch, cycle, configuration or database row.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'watch-disablement.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

function Get-Prop($obj, [string]$name, $fallback = $null) {
    if ($null -eq $obj) { return $fallback }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $fallback }
    if ($null -eq $p.Value) { return $fallback }
    return $p.Value
}

function Get-First($response) {
    if ($null -eq $response) { return $null }
    $items = @($response)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

# Invoke-WebRequest.Content is a byte array in PowerShell 7 when the response declares no
# textual content type; Out-String would then print one number per line.
function Get-BodyText($response) {
    if ($null -eq $response) { return '' }
    $c = $response.Content
    if ($null -eq $c) { return '' }
    if ($c -is [string]) { return $c.Trim() }
    if ($c -is [byte[]]) { return ([Text.Encoding]::UTF8.GetString($c)).Trim() }
    return (($c | Out-String).Trim())
}

function Show([object]$v, [string]$whenNull = '(not stated)') {
    if ($null -eq $v) { return $whenNull }
    return [string]$v
}

function Get-ErrorStatus($e) {
    try {
        $rp = $e.Exception.PSObject.Properties['Response']
        if ($null -eq $rp -or $null -eq $rp.Value) { return $null }
        $sc = $rp.Value.PSObject.Properties['StatusCode']
        if ($null -eq $sc -or $null -eq $sc.Value) { return $null }
        return [string][int]$sc.Value
    }
    catch { return $null }
}

function Get-ErrorBody($e) {
    try {
        $d = $e.PSObject.Properties['ErrorDetails']
        if ($null -ne $d -and $null -ne $d.Value) {
            $m = $d.Value.PSObject.Properties['Message']
            if ($null -ne $m -and -not [string]::IsNullOrWhiteSpace([string]$m.Value)) {
                return ([string]$m.Value).Trim()
            }
        }
        $rp = $e.Exception.PSObject.Properties['Response']
        if ($null -ne $rp -and $null -ne $rp.Value) {
            $s = $rp.Value.GetResponseStream()
            if ($null -ne $s) {
                $reader = New-Object System.IO.StreamReader($s)
                try { return $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
            }
        }
        return $e.Exception.Message
    }
    catch { return $e.Exception.Message }
}

$BaseUrl    = 'http://localhost:5143'
$Symbol     = 'AAPL.US'
$Template   = 'equity-price-review'
$apiProject = Join-Path $root 'src\AI.Investment.Api'

Say '=== SAFE STOP: DISABLE THE AAPL.US WATCH ==='
Say ("UTC now : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ''

# --------------------------------------------------------------------------------------
# 1. Find the watch. Read-only, and before anything is asked for.
# --------------------------------------------------------------------------------------
Say '--- 1. THE WATCH ---'

$psql = $null
try {
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
    }
    if ([string]::IsNullOrWhiteSpace($psql)) { $psql = $null }
}
catch { $psql = $null }

if ($null -eq $psql) {
    Say '  psql not found, so the watch id cannot be read. STOPPING. Nothing was changed.'
    Save-Log; exit 1
}

$H='localhost'; $P='5432'; $D=''; $U=''
$cs = $null; $text = $null
try {
    $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in @($text -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') {
                $cs = $line.Substring($i + 3)
            }
        }
    }
}
catch { }
finally { $text = $null }

if ([string]::IsNullOrWhiteSpace($cs)) {
    Say '  Database:ConnectionString unavailable. STOPPING. Nothing was changed.'
    Save-Log; exit 1
}

$parts = @{}
foreach ($seg in @($cs -split ';')) {
    $j = $seg.IndexOf('=')
    if ($j -gt 0) { $parts[$seg.Substring(0,$j).Trim().ToLowerInvariant()] = $seg.Substring($j+1).Trim() }
}
$cs = $null
if ($parts.ContainsKey('host'))     { $H = $parts['host'] }
if ($parts.ContainsKey('port'))     { $P = $parts['port'] }
if ($parts.ContainsKey('database')) { $D = $parts['database'] }
if ($parts.ContainsKey('username')) { $U = $parts['username'] }
if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
$parts = $null

function Sql([string]$sql) {
    $r = & $psql -h $H -p $P -U $U -d $D -t -A -F ' | ' -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { return 'QUERY FAILED' }
    return (($r | Out-String).Trim())
}

try {
    $row = Sql ("select id, name, enabled, fire_count from watches " +
                "where target_identifier = '$Symbol' and cycle_template = '$Template'")

    if ($row -eq 'QUERY FAILED' -or [string]::IsNullOrWhiteSpace($row)) {
        Say ('  No watch found for ' + $Symbol + ' / ' + $Template + '. Nothing to disable.')
        Save-Log; exit 0
    }

    $rows = @($row -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($rows.Count -ne 1) {
        Say ('  Expected exactly one watch and found ' + $rows.Count + '. This script will not')
        Say '  guess which one you meant. STOPPING. Nothing was changed.'
        Save-Log; exit 1
    }

    $f = @($rows[0] -split '\|' | ForEach-Object { $_.Trim() })
    $watchId = $f[0]

    Say ('  id         : ' + $watchId)
    Say ('  name       : ' + $f[1])
    Say ('  enabled    : ' + $f[2])
    Say ('  fire_count : ' + $f[3])

    if ($f[2] -ne 't' -and $f[2] -ne 'true') {
        Say ''
        Say '  Already disabled. Nothing to do; not sending the request.'
        Save-Log; exit 0
    }

    # ----------------------------------------------------------------------------------
    # 2. What will change, then an explicit confirmation.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 2. WHAT WILL CHANGE ---'
    Say ('  POST /api/operator/watches/' + $watchId + '/disablement')
    Say ''
    Say '  One column on one row: watches.enabled true -> false, plus the reason you give'
    Say '  and one audit_records + action_executions pair for operator.disable-watch.'
    Say ''
    Say '  The schedule ticker asks the store for ENABLED watches, so from the next pass'
    Say '  this watch is not evaluated and starts no further cycles. A cycle already'
    Say '  running is NOT cancelled - it finishes its stages and completes.'
    Say ''
    Say '  RunCycles is not touched. The API keeps running. No other watch, source,'
    Say '  configuration or database row changes. No EODHD request is made.'

    Write-Host ''
    Write-Host 'Reason for disabling (required, max 120 chars):' -ForegroundColor Yellow
    $reason = Read-Host

    if ([string]::IsNullOrWhiteSpace($reason)) {
        Say ''
        Say '  No reason given. STOPPING. Nothing was changed.'
        Save-Log; exit 1
    }

    Write-Host ''
    Write-Host 'Type DISABLE to proceed, or anything else to abort:' -ForegroundColor Yellow
    $confirm = Read-Host

    if ([string]$confirm -cne 'DISABLE') {
        Say ''
        Say '  Not confirmed. STOPPING. Nothing was changed.'
        Save-Log; exit 0
    }

    # ----------------------------------------------------------------------------------
    # 3. The key.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 3. OPERATOR CREDENTIAL ---'
    Write-Host ''
    Write-Host 'Enter the operator key (input is hidden, and is not written to any file):' -ForegroundColor Yellow
    $secure = Read-Host -AsSecureString

    $key = $null
    if ($null -ne $secure) {
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $key = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    if ([string]::IsNullOrWhiteSpace($key)) {
        $key = $null
        Say '  No key entered. STOPPING. Nothing was changed.'
        Save-Log; exit 1
    }

    Say '  key received from the secure prompt (not shown, not logged, not measured).'

    $headers = @{ 'X-Operator-Key' = $key }
    $status = $null; $body = $null

    try {
        Say ''
        Say '--- 4. POST .../disablement ---'

        $resp = Invoke-WebRequest `
            -Uri ($BaseUrl + '/api/operator/watches/' + $watchId + '/disablement') `
            -Method Post -Headers $headers -ContentType 'application/json' `
            -Body (@{ reason = $reason.Trim() } | ConvertTo-Json) `
            -UseBasicParsing -TimeoutSec 60

        $status = [string][int]$resp.StatusCode
        $body = Get-BodyText $resp
    }
    catch {
        $status = Get-ErrorStatus $_
        $body = Get-ErrorBody $_
    }
    finally {
        $key = $null
        $headers = $null
        [System.GC]::Collect()
    }

    Say ('  HTTP status : ' + (Show $status '(no status)'))
    Say ('  response    : ' + (Show $body '(no body)'))
    Say ''
    Say '  200 Done or DuplicateSuppressed = the watch is off. 400 = no reason given.'
    Say '  401 = key not accepted. 403 = AdministerWatches missing. 404 = no such watch.'
    Say '  409 = policy denied it; nothing changed.'

    # ----------------------------------------------------------------------------------
    # 5. Verify from the database.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 5. PERSISTED STATE ---'
    Say ('  watches row : ' + (Sql ("select enabled, coalesce(disabled_reason,'') from watches where id = '$watchId'")))
    Say ('  audit rows for operator.disable-watch : ' +
         (Sql "select count(*) from audit_records where action_type = 'operator.disable-watch'"))
    Say ('  enabled schedule watches remaining    : ' +
         (Sql "select count(*) from watches where enabled = true and trigger_type = 'Schedule'"))
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Say ''
Say '=== END ==='
Say 'RunCycles unchanged. The API is still running. No EODHD request was made.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)

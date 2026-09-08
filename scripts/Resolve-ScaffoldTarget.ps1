#requires -Version 5.1
<#
    WHICH DATABASE DOES MIGRATION SCAFFOLDING POINT AT?

    Dot-source this file to get Resolve-ScaffoldTarget. It answers one question - which connection
    string may the EF migrations tool be given - and it answers it the same way for every
    scaffolding script in this repository, so that the answer cannot drift between them.

    WHY IT EXISTS.

    scripts\add-migration.cmd preferred AIINV_DESIGNTIME_DB and fell back to AIINV_TEST_POSTGRES.
    On this machine AIINV_DESIGNTIME_DB names ai_investment: the database holding 1,077,380
    observations and the whole recovery arc's evidence. Scaffolding does not connect to anything,
    so pointing it there was harmless in effect - but it made the evidence database the DEFAULT
    for a family of scripts whose siblings do connect, and a default nobody chose is the shape a
    later accident takes. scripts\run-provenance-migrate.ps1 had already reversed the order for
    itself and left a comment saying the scaffolder still had it backwards. This is that fix.

    WHAT IT GUARANTEES.

      1. The default target is AIINV_TEST_POSTGRES. Always.
      2. AIINV_DESIGNTIME_DB is used only when the operator asks for it by setting
         AIINV_SCAFFOLD_TARGET=designtime. There is no silent fallback in either direction: if the
         requested variable is empty the resolver refuses rather than quietly using the other one.
      3. The resolved server and database are reported before anything runs, and the report never
         contains the connection string, because that string carries a password.
      4. A connection naming the evidence database is refused whichever variable it came from,
         unless AIINV_SCAFFOLD_ALLOW_EVIDENCE_DB carries the exact opt-in token below. The guard is
         on the NAME, not on the variable - putting ai_investment into AIINV_TEST_POSTGRES does not
         get past it.

    WHAT IT IS NOT.

    It does not apply migrations, does not open a connection, and references no database client.
    scripts\run-provenance-migrate.ps1 owns the apply-time guard and is untouched by this file.

    EXIT CODES (returned on the result, for a caller to exit with):
      0  allowed
      2  no connection string is configured for the requested target
      4  refused - that is the evidence database and no opt-in was supplied
      5  the connection string does not name a database, so the guard cannot check it
      6  AIINV_SCAFFOLD_TARGET is neither 'test' nor 'designtime'
#>

Set-StrictMode -Version Latest

# The databases this guard protects. A name, not a variable: the point is that it holds however the
# connection string reached us.
$script:ScaffoldEvidenceDatabases = @('ai_investment')

# Long, specific, and unpleasant to type by reflex. An opt-in that reads like a shrug ('yes', '1')
# is one an operator supplies without having decided anything.
$script:ScaffoldEvidenceOptInToken = 'I-UNDERSTAND-THIS-IS-THE-EVIDENCE-DATABASE'

$script:ScaffoldTargetTest = 'test'
$script:ScaffoldTargetDesigntime = 'designtime'

<#
    .SYNOPSIS
    Reads one keyword out of a connection string.

    .DESCRIPTION
    A regex rather than DbConnectionStringBuilder. The generic builder's indexer throws for a key it
    has not seen, and this routine is called to decide whether to refuse - a guard that throws
    before it can refuse is not a guard. That failure was observed once already, in
    run-provenance-migrate.ps1, and cost a run.
#>
function Get-ScaffoldConnectionValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string] $ConnectionString,
        [string[]] $Keys
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) { return '' }

    foreach ($key in $Keys) {
        $pattern = '(?i)(^|;)\s*' + [regex]::Escape($key) + '\s*=\s*([^;]+)'

        if ($ConnectionString -match $pattern) {
            return $Matches[2].Trim()
        }
    }

    return ''
}

<#
    .SYNOPSIS
    Decides which database migration scaffolding may target, and refuses when it should.

    .DESCRIPTION
    Pure with respect to its inputs: every environment variable it consults is a parameter with an
    environment default, so a test can drive every branch without touching the machine. It writes
    nothing, connects to nothing, and never throws for a refusal - a refusal is a result with
    Allowed = $false and an ExitCode, so a caller reports it rather than catching it.

    .OUTPUTS
    A PSCustomObject with Allowed, ExitCode, Requested, Source, ConnectionString, Server, Port,
    Database, OptInSupplied, Message and Report. Every property is always present, so a caller under
    Set-StrictMode can read any of them.
#>
function Resolve-ScaffoldTarget {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [string] $Requested = $env:AIINV_SCAFFOLD_TARGET,
        [string] $TestConnection = $env:AIINV_TEST_POSTGRES,
        [string] $DesigntimeConnection = $env:AIINV_DESIGNTIME_DB,
        [string] $EvidenceOptIn = $env:AIINV_SCAFFOLD_ALLOW_EVIDENCE_DB
    )

    function New-Result {
        param(
            [bool] $Allowed,
            [int] $ExitCode,
            [string] $Requested,
            [string] $Source,
            [string] $ConnectionString,
            [string] $Server,
            [string] $Port,
            [string] $Database,
            [bool] $OptInSupplied,
            [string] $Message
        )

        $lines = @(
            '  ---------------------------------------------------------------'
            '   MIGRATION SCAFFOLDING TARGET'
            '  ---------------------------------------------------------------'
            ('   requested   : ' + $(if ($Requested) { $Requested } else { '(none - default)' }))
            ('   variable    : ' + $(if ($Source) { $Source } else { '(unresolved)' }))
            ('   server      : ' + $(if ($Server) { $Server } else { '(not named)' }))
            ('   port        : ' + $(if ($Port) { $Port } else { '(default)' }))
            ('   database    : ' + $(if ($Database) { $Database } else { '(not named)' }))
            ('   evidence db : ' + $(if ($Database -and ($script:ScaffoldEvidenceDatabases -contains $Database.ToLowerInvariant())) { 'YES - protected' } else { 'no' }))
            ('   opt-in      : ' + $(if ($OptInSupplied) { 'SUPPLIED' } else { 'not supplied' }))
            ('   decision    : ' + $(if ($Allowed) { 'ALLOWED' } else { ('REFUSED (exit ' + $ExitCode + ')') }))
            ('   ' + $Message)
            '  ---------------------------------------------------------------'
        )

        # The connection string is deliberately NOT on that list. It carries a password, and this
        # report is written to a log file and echoed to a console.
        return [PSCustomObject]@{
            Allowed          = $Allowed
            ExitCode         = $ExitCode
            Requested        = $Requested
            Source           = $Source
            ConnectionString = $ConnectionString
            Server           = $Server
            Port             = $Port
            Database         = $Database
            OptInSupplied    = $OptInSupplied
            Message          = $Message
            Report           = ($lines -join [Environment]::NewLine)
        }
    }

    # --- 1. which variable did the operator ask for? ---------------------------------
    $asked = if ([string]::IsNullOrWhiteSpace($Requested)) { $script:ScaffoldTargetTest } else { $Requested.Trim().ToLowerInvariant() }

    if ($asked -ne $script:ScaffoldTargetTest -and $asked -ne $script:ScaffoldTargetDesigntime) {
        return New-Result -Allowed $false -ExitCode 6 -Requested $asked -Source '' -ConnectionString '' `
            -Server '' -Port '' -Database '' -OptInSupplied $false `
            -Message ("AIINV_SCAFFOLD_TARGET must be '" + $script:ScaffoldTargetTest + "' or '" + $script:ScaffoldTargetDesigntime + "'.")
    }

    if ($asked -eq $script:ScaffoldTargetTest) {
        $source = 'AIINV_TEST_POSTGRES'
        $cs = $TestConnection
    }
    else {
        $source = 'AIINV_DESIGNTIME_DB'
        $cs = $DesigntimeConnection
    }

    # --- 2. no silent fallback, in either direction ----------------------------------
    # This is the behaviour that changed. The old scripts tried one variable and then the other, so
    # an empty AIINV_TEST_POSTGRES silently promoted AIINV_DESIGNTIME_DB - and on this machine that
    # meant silently promoting the evidence database. An unset variable is now a stop.
    if ([string]::IsNullOrWhiteSpace($cs)) {
        return New-Result -Allowed $false -ExitCode 2 -Requested $asked -Source $source -ConnectionString '' `
            -Server '' -Port '' -Database '' -OptInSupplied $false `
            -Message ($source + ' is not set. This does not fall back to the other variable - set it, or set AIINV_SCAFFOLD_TARGET deliberately.')
    }

    $server = Get-ScaffoldConnectionValue -ConnectionString $cs -Keys @('Host', 'Server', 'Data Source')
    $port = Get-ScaffoldConnectionValue -ConnectionString $cs -Keys @('Port')
    $database = Get-ScaffoldConnectionValue -ConnectionString $cs -Keys @('Database', 'Initial Catalog')

    # --- 3. an unnamed database cannot be checked, so it is not allowed ---------------
    if ([string]::IsNullOrWhiteSpace($database)) {
        return New-Result -Allowed $false -ExitCode 5 -Requested $asked -Source $source -ConnectionString '' `
            -Server $server -Port $port -Database '' -OptInSupplied $false `
            -Message ($source + ' does not name a database, so the guard cannot check which one it is.')
    }

    # -ceq: the token is compared case-sensitively, so a half-remembered lowercase version does not
    # open the evidence database.
    $optIn = (-not [string]::IsNullOrWhiteSpace($EvidenceOptIn)) -and
             ($EvidenceOptIn.Trim() -ceq $script:ScaffoldEvidenceOptInToken)
    $isEvidence = $script:ScaffoldEvidenceDatabases -contains $database.ToLowerInvariant()

    # --- 4. the guard that matters ----------------------------------------------------
    if ($isEvidence -and -not $optIn) {
        return New-Result -Allowed $false -ExitCode 4 -Requested $asked -Source $source -ConnectionString '' `
            -Server $server -Port $port -Database $database -OptInSupplied $false `
            -Message ("'" + $database + "' is the evidence database. Set AIINV_SCAFFOLD_ALLOW_EVIDENCE_DB=" + $script:ScaffoldEvidenceOptInToken + " to scaffold against it.")
    }

    $message = if ($isEvidence) {
        'ALLOWED AGAINST THE EVIDENCE DATABASE by explicit opt-in. Scaffolding writes files, not schema - nothing is applied here.'
    }
    else {
        'Scaffolding writes files only. Nothing is applied and nothing connects.'
    }

    return New-Result -Allowed $true -ExitCode 0 -Requested $asked -Source $source -ConnectionString $cs `
        -Server $server -Port $port -Database $database -OptInSupplied $optIn -Message $message
}

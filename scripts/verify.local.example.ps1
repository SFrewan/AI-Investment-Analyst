<#
    Machine-local settings for scripts\verify.ps1. COPY THIS FILE to scripts\verify.local.ps1
    and fill in your own value. verify.local.ps1 is git-ignored; this example is tracked and
    must never contain a real credential.

    Why a file rather than a constant in verify.ps1: the test suite needs a database, the
    database needs a password, and a password in a tracked file reaches the remote and stays in
    history after it is rotated. A git-ignored file is one 'git add -f' away from the same
    mistake, so anything genuinely sensitive belongs in an environment variable or a managed
    secret store even locally - this file exists for the double-click workflow, where no
    environment variable is in scope.

    The database name MUST end in '_tests'. The integration fixture truncates every table it
    finds, and it refuses to run against anything not named that way, because the development
    database sits on the same server under the same credentials one word away.
#>

$env:AIINV_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Database=ai_investment_tests;Username=postgres;Password=REPLACE_ME'

# Used by scripts\apply-migration.cmd, which applies migrations to the DEVELOPMENT database.
# Leave it unset if you would rather pass it in the environment.
# $env:AIINV_DESIGNTIME_DB = 'Host=127.0.0.1;Port=5432;Database=ai_investment;Username=postgres;Password=REPLACE_ME'

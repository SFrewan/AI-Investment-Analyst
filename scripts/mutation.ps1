<#
    Mutation testing for the safety-critical domain, required by the Phase 5 exit criterion.

    Unit tests demonstrate that the guard rails work on the inputs somebody thought of. Mutation
    testing demonstrates that they cannot be removed without a test going red, which is a different
    and stronger claim - and it is the one a safety control has to make.

    Scope is deliberately narrow: the policy engine, the risk tiering, the limit engine and set, the
    approval token and its fingerprint, and the ledger. Mutating the whole domain would take hours
    and would report a score dominated by value objects whose behaviour nobody's money depends on.

        powershell -ExecutionPolicy Bypass -File scripts\mutation.ps1

    Everything it writes lands in artifacts\, which .gitignore excludes.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -Path $repo

$outDir = Join-Path $repo 'artifacts\verify'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$log = Join-Path $outDir 'mutation.log'
$marker = Join-Path $outDir 'MUTATION-DONE.txt'

Remove-Item -Path $marker -ErrorAction SilentlyContinue

"[mutation] started $(Get-Date -Format o)" | Out-File -FilePath $log -Encoding utf8

if (-not (Test-Path -Path '.config\dotnet-tools.json')) {
    "[mutation] creating local tool manifest" | Out-File -FilePath $log -Append -Encoding utf8
    & dotnet new tool-manifest 2>&1 | Out-File -FilePath $log -Append -Encoding utf8
}

# Pinned with the repository rather than depending on whatever is installed globally.
& dotnet tool install dotnet-stryker --version 4.4.1 2>&1 | Out-File -FilePath $log -Append -Encoding utf8
& dotnet tool restore 2>&1 | Out-File -FilePath $log -Append -Encoding utf8

& dotnet stryker --config-file scripts\stryker-config.json 2>&1 |
    Out-File -FilePath $log -Append -Encoding utf8

$exit = $LASTEXITCODE

"[mutation] finished exit=$exit $(Get-Date -Format o)" | Out-File -FilePath $log -Append -Encoding utf8
"exit=$exit" | Set-Content -Path $marker -Encoding utf8

Write-Host "[mutation] done. exit=$exit"

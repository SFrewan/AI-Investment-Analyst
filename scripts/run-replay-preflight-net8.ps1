#requires -Version 5.1
<#
    FINAL PRE-RECOVERY PREFLIGHT - LAUNCHER FOR THE net8.0 TOOL. READ ONLY.

    PowerShell is only a launcher here. It compiles nothing: the previous approach compiled a C#
    shim in-process with Add-Type, and PowerShell 7 on this machine runs on .NET 9 while the
    production assemblies target .NET 8 - which produced CS1701 and could not be referenced away.
    The tool under tools\replay-preflight is a net8.0 executable, so the mismatch does not exist.

    NO RECOVERY. NO REPLAY OF ANY TARGET. NO NORMALISATION. NO WRITE OF ANY KIND.

    This script:
      1. takes AIINV_TEST_POSTGRES from scripts\verify.local.ps1, exactly as the test harness does
      2. prints the dotnet SDK and runtime in use
      3. builds tools\replay-preflight in Release, against net8.0
      4. runs it once, passing the repository root

    THE CONNECTION STRING IS NEVER PRINTED. The tool prints host, port and the live database name
    it actually reached, which is the point of the exercise.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\replay-preflight\ReplayPreflight.csproj'
$log = Join-Path $root 'artifacts\verify\replay-preflight-build.log'

$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\verify')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   FINAL PRE-RECOVERY PREFLIGHT - net8.0 tool'
Write-Host '   READ ONLY. NO RECOVERY. NO NORMALISATION.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host '  STOPPING. AIINV_TEST_POSTGRES is not set.'
    exit 3
}

Write-Host '  Database configured (value not printed).'
Write-Host ''

if (-not (Test-Path -LiteralPath $tool)) {
    Write-Host ('  STOPPING. The tool project was not found: ' + $tool)
    exit 4
}

Write-Host '--- toolchain'
Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host ('  project          : ' + $tool)
Write-Host '  target framework : net8.0'
Write-Host ''

# Both connectors pinned off. Nothing in the preflight can reach a provider, and leaving them on
# would invite an accident that has nothing to do with what is being verified.
$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host '--- building the tool (Release, net8.0)'

& dotnet build $tool -c Release --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host '  FAIL  the tool did not build. Nothing was run and nothing was read.'
    Write-Host ''

    foreach ($line in (Get-Content -Path $log -Tail 60)) {
        if ($line -match 'error|Error|Build FAILED') { Write-Host ('  ' + $line.Trim()) }
    }

    Write-Host ''
    Write-Host ('  Full build log: ' + $log)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

$exe = Join-Path $root 'tools\replay-preflight\bin\Release\net8.0\replay-preflight.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ('  STOPPING. The built executable was not found: ' + $exe)
    exit 7
}

Write-Host ('  executable       : ' + $exe)

& $exe $root
$code = $LASTEXITCODE

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

exit $code

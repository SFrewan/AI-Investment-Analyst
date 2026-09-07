#Requires -Version 5.1
<#
.SYNOPSIS
    Stores the EODHD API token as a Windows *user* environment variable.

.DESCRIPTION
    Prompts for the token and writes it to the current user's environment under
    Providers__Eodhd__ApiKey, which is the name .NET translates to the configuration path
    Providers:Eodhd:ApiKey - the key EodhdOptions binds.

    Three things this deliberately does not do.

    It does not take the token as a parameter. A parameter is a command line, and a command line
    is in the PowerShell history file, in the console scrollback, and in the process-creation
    audit log where one is enabled. Read-Host -AsSecureString is none of those.

    It does not use setx, for the same reason, and because setx silently truncates at 1024
    characters - which for a credential is a corruption that presents as an authentication
    failure.

    It does not echo the token back. It reports a length and a fingerprint, which is enough to
    confirm the value arrived intact and not enough to be worth capturing.

.NOTES
    A variable set here is not visible to the shell that set it, nor to any process started
    before it. Close this terminal and every editor or IDE that was already open.
#>

[CmdletBinding()]
param(
    # Clears the variable instead of setting one. For rotating out a token you no longer want.
    [switch] $Remove,

    # Also removes any token left in the .NET user-secrets store.
    #
    # Worth knowing about: user-secrets and the environment are both real configuration sources,
    # and the later one wins. An entry left over from an earlier subscription therefore does not
    # break anything visibly - it just means the token that gets spent is not the one you set,
    # which presents as a 401 against a key you know is correct. Clearing it leaves one source of
    # truth. check-eodhd-credential.cmd reports which source is in force either way.
    [switch] $ClearUserSecret
)

$ErrorActionPreference = 'Stop'

$VariableName = 'Providers__Eodhd__ApiKey'
$ConfigurationPath = 'Providers:Eodhd:ApiKey'

function Get-Fingerprint {
    param([string] $Value)

    # The same domain-separated digest CredentialPresence computes, so the fingerprint printed
    # here is the one the application will report back. Changing either without the other makes
    # two numbers that look comparable and are not.
    $domain = "AI.Investment/credential-fingerprint/v1`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($domain + $Value)
    $sha = [Security.Cryptography.SHA256]::Create()

    try {
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 12)
    }
    finally {
        $sha.Dispose()
    }
}

function Clear-UserSecret {
    $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\AI.Investment.Api'

    Write-Host "Clearing any token in the user-secrets store..." -ForegroundColor Yellow

    & dotnet user-secrets remove $ConfigurationPath --project $project 2>&1 |
        ForEach-Object { Write-Host "  $_" }
}

if ($Remove) {
    [Environment]::SetEnvironmentVariable($VariableName, $null, 'User')
    Write-Host "Removed $VariableName from the user environment." -ForegroundColor Yellow

    if ($ClearUserSecret) { Clear-UserSecret }

    Write-Host "Rotate the token at EODHD as well - removing it here does not un-disclose it."
    exit 0
}

Write-Host ""
Write-Host "EODHD API token" -ForegroundColor Cyan
Write-Host "  variable  : $VariableName  (user scope)"
Write-Host "  binds to  : $ConfigurationPath"
Write-Host ""
Write-Host "The value is not echoed, not logged, and not written to any file in this repository."
Write-Host ""

$secure = Read-Host -AsSecureString "Paste the token"

$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)

try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
}

if ([string]::IsNullOrWhiteSpace($plain)) {
    Write-Host "Nothing entered. No variable was set." -ForegroundColor Red
    exit 1
}

if ($plain -ne $plain.Trim()) {
    # Refused rather than trimmed. The credential travels in a query string, so the whitespace
    # would become part of it and EODHD would answer 401 - a failure that reads as a wrong token.
    # Trimming here would also leave the stored variable wrong for everything else that reads it.
    Write-Host ""
    Write-Host "The value has leading or trailing whitespace - almost certainly a newline that" -ForegroundColor Red
    Write-Host "came with the paste. Nothing was set. Paste it again without it." -ForegroundColor Red
    Remove-Variable plain
    exit 1
}

[Environment]::SetEnvironmentVariable($VariableName, $plain, 'User')

$length = $plain.Length
$fingerprint = Get-Fingerprint -Value $plain

Remove-Variable plain

if ($ClearUserSecret) {
    Write-Host ""
    Clear-UserSecret
}

Write-Host ""
Write-Host "Set." -ForegroundColor Green
Write-Host "  length      : $length characters"
Write-Host "  fingerprint : $fingerprint"
Write-Host ""
Write-Host "Close this terminal and any editor opened before now, then run:" -ForegroundColor Yellow
Write-Host "  scripts\check-eodhd-credential.cmd"
Write-Host ""
Write-Host "That check boots the composition the acquisition runs under, confirms the same"
Write-Host "fingerprint arrives, and reports which configuration provider supplied it. It makes"
Write-Host "no provider calls."
Write-Host ""

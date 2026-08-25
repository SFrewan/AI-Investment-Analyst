<#
.SYNOPSIS
    Removes the pre-Phase-0 project folders and the root-level documentation copies that
    were moved into docs/ during the Phase 0 restructure.

.DESCRIPTION
    The Phase 0 restructure (decision D-1) moved four projects from hyphenated root-level
    folders into src/ with dotted names, and moved three markdown documents into docs/.
    The new files have already been written. The OLD locations still exist and must be
    removed - they are not referenced by the new solution, so leaving them causes no build
    error, only confusion and a stale duplicate of every document.

    This script exists because the assistant that produced the restructure had write access
    to this folder but no ability to delete files on this machine. Deleting is therefore a
    deliberate action you take, having reviewed what is about to go.

    NOTHING IS DELETED unless the new structure is verified to be present first.

.PARAMETER WhatIf
    Show what would be removed without removing anything. Run this first.

.EXAMPLE
    # 1. See what would happen - nothing is touched
    .\scripts\phase0-remove-legacy-projects.ps1 -WhatIf

.EXAMPLE
    # 2. Actually remove
    .\scripts\phase0-remove-legacy-projects.ps1

.NOTES
    Everything removed here is recoverable from git while the commit that added it still
    exists (`git checkout HEAD -- <path>`). Commit before running if you want a clean point
    to return to. This script may be deleted once it has been run.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'

# --- Locate the repository root -------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
Write-Host "Repository root: $repoRoot" -ForegroundColor Cyan

# --- Guard: the replacement structure must exist before anything is removed -----------
$required = @(
    'AI-Investment-Analyst.sln',
    'Directory.Build.props',
    'Directory.Packages.props',
    'src/AI.Investment.Domain/AI.Investment.Domain.csproj',
    'src/AI.Investment.Application/AI.Investment.Application.csproj',
    'src/AI.Investment.Infrastructure/AI.Investment.Infrastructure.csproj',
    'src/AI.Investment.Api/AI.Investment.Api.csproj',
    'src/AI.Investment.Api/Program.cs',
    'docs/SYSTEM_ARCHITECTURE.md',
    'docs/AUDIT_AND_TARGET_ARCHITECTURE.md',
    'docs/PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md'
)

$missing = $required | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) {
    Write-Error ("Refusing to delete anything. The Phase 0 replacement files are not all present. Missing:`n  " + ($missing -join "`n  "))
}
Write-Host "Verified: all $($required.Count) Phase 0 replacement files are present." -ForegroundColor Green

# --- Guard: warn about uncommitted work ------------------------------------------------
$gitAvailable = $null -ne (Get-Command git -ErrorAction SilentlyContinue)
if ($gitAvailable) {
    $status = git status --porcelain
    if ($status) {
        Write-Warning "The working tree has uncommitted changes. Consider committing first so this is trivially reversible."
    }
}

# --- What goes ---------------------------------------------------------------------------
$legacyDirectories = @(
    'AI-Investment-API',
    'AI-Investment-App',
    'AI-Investment-Domain',
    'AI-Investment-Infrastructure'
)

# Root-level copies that now live under docs/
$relocatedDocuments = @(
    'SYSTEM_ARCHITECTURE.md',
    'AUDIT_AND_TARGET_ARCHITECTURE.md',
    'PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md'
)

# --- Safety check: the relocated copy must be byte-identical before the original goes ----
foreach ($doc in $relocatedDocuments) {
    if (-not (Test-Path -LiteralPath $doc)) { continue }

    $target = Join-Path 'docs' $doc
    $sourceHash = (Get-FileHash -LiteralPath $doc -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

    if ($sourceHash -ne $targetHash) {
        Write-Error "Refusing to delete '$doc': the copy at '$target' is NOT identical. Reconcile them by hand first."
    }
    Write-Host "Verified identical: $doc -> $target" -ForegroundColor Green
}

# --- Remove -------------------------------------------------------------------------------
foreach ($dir in $legacyDirectories) {
    if (-not (Test-Path -LiteralPath $dir)) {
        Write-Host "Already gone: $dir" -ForegroundColor DarkGray
        continue
    }

    if ($PSCmdlet.ShouldProcess($dir, 'Remove legacy project directory (including bin/obj)')) {
        Remove-Item -LiteralPath $dir -Recurse -Force
        Write-Host "Removed: $dir" -ForegroundColor Yellow
    }
}

foreach ($doc in $relocatedDocuments) {
    if (-not (Test-Path -LiteralPath $doc)) {
        Write-Host "Already gone: $doc" -ForegroundColor DarkGray
        continue
    }

    if ($PSCmdlet.ShouldProcess($doc, 'Remove root-level copy (relocated to docs/)')) {
        Remove-Item -LiteralPath $doc -Force
        Write-Host "Removed: $doc" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done. Next steps:" -ForegroundColor Cyan
Write-Host "  git add -A"
Write-Host "  git status          # review the rename/delete set before committing"
Write-Host "  dotnet restore AI-Investment-Analyst.sln"
Write-Host "  dotnet build   AI-Investment-Analyst.sln"

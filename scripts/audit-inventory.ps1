#requires -Version 5.1
<#
    READ-ONLY REPOSITORY INVENTORY.

    Reads. Writes nothing but its own reports under artifacts\audit. Starts nothing,
    touches no database, makes no network request.

    Five reports, split so each can be read on its own:
      00-overview.md   solution, projects, sizes, git, endpoints, migrations, config
      10-types.md      every public type, by namespace
      20-tests.md      every test method, by project, with its attribute
      30-debt.md       TODO / FIXME / HACK / NotImplemented / pragma / Skip
      40-docs.md       every markdown document and its headings
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\audit'
$null = New-Item -ItemType Directory -Force -Path $out

$excluded = '\\(bin|obj|node_modules|\.vs|\.git|StrykerOutput|TestResults|artifacts)\\'

function Relative([string]$path) {
    if ($path.StartsWith($root)) { return $path.Substring($root.Length).TrimStart('\') }
    return $path
}

function SourceFiles([string]$path, [string]$filter) {
    if (-not (Test-Path -Path $path)) { return @() }

    return @(Get-ChildItem -Path $path -Recurse -File -Filter $filter -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excluded })
}

function Emit([System.Collections.Generic.List[string]]$buffer, [string]$text) {
    $null = $buffer.Add($text)
}

Write-Host 'Reading the repository. Nothing is modified.'

# =========================================================== 00 - overview ===

$overview = New-Object 'System.Collections.Generic.List[string]'

Emit $overview '# Repository inventory - overview'
Emit $overview ''
Emit $overview ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z (read-only)')
Emit $overview ''

# ---- git ----
Emit $overview '## Git'
Emit $overview ''
Emit $overview '```'

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    Push-Location $root
    Emit $overview ('branch: ' + ((& git rev-parse --abbrev-ref HEAD 2>&1) | Out-String).Trim())
    Emit $overview ''
    Emit $overview 'last 20 commits:'
    foreach ($line in @(& git log -20 --format='%h %ad %s' --date=short 2>&1)) {
        Emit $overview ('  ' + ([string]$line).Trim())
    }
    Emit $overview ''
    Emit $overview 'working tree:'
    $status = @(& git status --short 2>&1)
    if ($status.Count -eq 0) { Emit $overview '  (clean)' }
    foreach ($line in $status) { Emit $overview ('  ' + ([string]$line).Trim()) }
}
catch { Emit $overview ('git unavailable: ' + $_.Exception.Message) }
finally {
    Pop-Location
    $ErrorActionPreference = $previous
}

Emit $overview '```'
Emit $overview ''

# ---- projects ----
Emit $overview '## Projects'
Emit $overview ''
Emit $overview '| Project | Path | .cs files | Lines |'
Emit $overview '| --- | --- | ---: | ---: |'

$projects = @(SourceFiles $root '*.csproj')

$projectRows = New-Object 'System.Collections.Generic.List[object]'

foreach ($project in $projects) {
    $dir = $project.Directory.FullName
    $files = @(SourceFiles $dir '*.cs')
    $lineCount = 0
    foreach ($f in $files) {
        $lineCount += @(Get-Content -Path $f.FullName -ErrorAction SilentlyContinue).Count
    }

    $null = $projectRows.Add([pscustomobject]@{
            Name  = $project.BaseName
            Path  = (Relative $project.FullName)
            Files = $files.Count
            Lines = $lineCount
        })
}

foreach ($r in ($projectRows | Sort-Object -Property Name)) {
    Emit $overview ('| ' + $r.Name + ' | ' + $r.Path + ' | ' + [string]$r.Files + ' | ' + [string]$r.Lines + ' |')
}

Emit $overview ''

# ---- directory shape ----
Emit $overview '## Directory shape (src and tests, two levels)'
Emit $overview ''
Emit $overview '```'

foreach ($top in @('src', 'tests')) {
    $topPath = Join-Path $root $top
    if (-not (Test-Path $topPath)) { continue }

    Emit $overview $top

    foreach ($level1 in @(Get-ChildItem -Path $topPath -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $count = @(SourceFiles $level1.FullName '*.cs').Count
        Emit $overview ('  ' + $level1.Name + '   [' + [string]$count + ' files]')

        foreach ($level2 in @(Get-ChildItem -Path $level1.FullName -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch $excluded } | Sort-Object Name)) {
            $inner = @(SourceFiles $level2.FullName '*.cs')
            $names = @($inner | ForEach-Object { $_.BaseName } | Sort-Object)
            Emit $overview ('    ' + $level2.Name + '/  ' + ($names -join ', '))
        }

        $loose = @(Get-ChildItem -Path $level1.FullName -File -Filter '*.cs' -ErrorAction SilentlyContinue | Sort-Object Name)
        if ($loose.Count -gt 0) {
            Emit $overview ('    (root)/  ' + (@($loose | ForEach-Object { $_.BaseName }) -join ', '))
        }
    }
}

Emit $overview '```'
Emit $overview ''

# ---- endpoints ----
Emit $overview '## HTTP surface'
Emit $overview ''
Emit $overview '```'

foreach ($file in (SourceFiles (Join-Path $root 'src') '*.cs')) {
    $found = @(Select-String -Path $file.FullName -ErrorAction SilentlyContinue `
            -Pattern '^\s*\[(Route|Http(Get|Post|Put|Patch|Delete))', '^\s*\[Authorize', '^\s*\[AllowAnonymous')

    if ($found.Count -eq 0) { continue }

    Emit $overview (Relative $file.FullName)
    foreach ($m in $found) {
        Emit $overview ('    ' + [string]$m.LineNumber + ': ' + $m.Line.Trim())
    }
}

Emit $overview '```'
Emit $overview ''

# ---- migrations ----
Emit $overview '## EF migrations'
Emit $overview ''
Emit $overview '```'

$migrations = @(SourceFiles $root '*.cs' | Where-Object { $_.FullName -match '\\Migrations\\' -and $_.Name -notmatch 'Designer|ModelSnapshot' })
foreach ($m in ($migrations | Sort-Object Name)) { Emit $overview ('  ' + $m.Name) }
if ($migrations.Count -eq 0) { Emit $overview '  (none found)' }

Emit $overview '```'
Emit $overview ''

# ---- configuration ----
Emit $overview '## Configuration files and their keys'
Emit $overview ''

foreach ($settings in @(SourceFiles $root 'appsettings*.json' | Sort-Object FullName)) {
    Emit $overview ('### ' + (Relative $settings.FullName))
    Emit $overview ''
    Emit $overview '```json'
    foreach ($line in @(Get-Content -Path $settings.FullName -ErrorAction SilentlyContinue)) {
        Emit $overview $line
    }
    Emit $overview '```'
    Emit $overview ''
}

# ---- options classes ----
Emit $overview '## Options classes (what is configurable)'
Emit $overview ''
Emit $overview '```'

foreach ($file in (SourceFiles (Join-Path $root 'src') '*Options.cs' | Sort-Object Name)) {
    Emit $overview (Relative $file.FullName)
    $props = @(Select-String -Path $file.FullName -Pattern '^\s*public\s+[^\(\)]+\s+\w+\s*\{\s*get' -ErrorAction SilentlyContinue)
    foreach ($p in $props) { Emit $overview ('    ' + $p.Line.Trim()) }
}

Emit $overview '```'

Set-Content -Path (Join-Path $out '00-overview.md') -Value ($overview -join "`r`n") -Encoding UTF8
Write-Host '  wrote 00-overview.md'

# ============================================================== 10 - types ===

$types = New-Object 'System.Collections.Generic.List[string]'

Emit $types '# Public and internal type inventory'
Emit $types ''

foreach ($project in ($projectRows | Where-Object { $_.Path -like 'src\*' } | Sort-Object Name)) {
    $dir = Join-Path $root (Split-Path -Parent $project.Path)

    Emit $types ('## ' + $project.Name)
    Emit $types ''
    Emit $types '```'

    foreach ($file in (SourceFiles $dir '*.cs' | Sort-Object FullName)) {
        $decls = @(Select-String -Path $file.FullName -ErrorAction SilentlyContinue `
                -Pattern '^\s*(public|internal)\s+(sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(class|record|interface|enum|struct)\s+\w+')

        if ($decls.Count -eq 0) { continue }

        Emit $types (Relative $file.FullName)
        foreach ($d in $decls) { Emit $types ('    ' + $d.Line.Trim()) }
    }

    Emit $types '```'
    Emit $types ''
}

Set-Content -Path (Join-Path $out '10-types.md') -Value ($types -join "`r`n") -Encoding UTF8
Write-Host '  wrote 10-types.md'

# ============================================================== 20 - tests ===

$tests = New-Object 'System.Collections.Generic.List[string]'

Emit $tests '# Test inventory'
Emit $tests ''
Emit $tests 'Every method carrying a test attribute, with the attribute it carries.'
Emit $tests ''

foreach ($project in ($projectRows | Where-Object { $_.Path -like 'tests\*' } | Sort-Object Name)) {
    $dir = Join-Path $root (Split-Path -Parent $project.Path)

    $total = 0
    $body = New-Object 'System.Collections.Generic.List[string]'

    foreach ($file in (SourceFiles $dir '*.cs' | Sort-Object FullName)) {
        $content = @(Get-Content -Path $file.FullName -ErrorAction SilentlyContinue)
        $names = New-Object 'System.Collections.Generic.List[string]'

        for ($i = 0; $i -lt $content.Count; $i++) {
            if ($content[$i] -notmatch '^\s*\[(Fact|Theory|SkippableFact|SkippableTheory)') { continue }

            $attribute = ($content[$i].Trim() -replace '[\[\]]', '')

            for ($j = $i + 1; $j -lt [Math]::Min($i + 12, $content.Count); $j++) {
                if ($content[$j] -match '^\s*\[') { continue }
                if ($content[$j] -match '^\s*(public|private|internal)\s') {
                    $signature = $content[$j].Trim()
                    $null = $names.Add(($attribute + '  ' + $signature))
                    break
                }
            }
        }

        if ($names.Count -eq 0) { continue }

        $total += $names.Count
        Emit $body (Relative $file.FullName)
        foreach ($n in $names) { Emit $body ('    ' + $n) }
    }

    Emit $tests ('## ' + $project.Name + '  (' + [string]$total + ' test methods)')
    Emit $tests ''
    Emit $tests '```'
    foreach ($b in $body) { Emit $tests $b }
    Emit $tests '```'
    Emit $tests ''
}

Set-Content -Path (Join-Path $out '20-tests.md') -Value ($tests -join "`r`n") -Encoding UTF8
Write-Host '  wrote 20-tests.md'

# =============================================================== 30 - debt ===

$debt = New-Object 'System.Collections.Generic.List[string]'

Emit $debt '# Declared debt, gaps and suppressions'
Emit $debt ''

$debtPatterns = @(
    [pscustomobject]@{ Title = 'TODO / FIXME / HACK / XXX'; Pattern = '(TODO|FIXME|HACK|XXX)\b' }
    [pscustomobject]@{ Title = 'NotImplementedException / NotSupportedException'; Pattern = 'NotImplementedException|NotSupportedException' }
    [pscustomobject]@{ Title = 'Analyzer suppressions (#pragma warning disable)'; Pattern = '#pragma warning disable' }
    [pscustomobject]@{ Title = 'SuppressMessage attributes'; Pattern = 'SuppressMessage' }
    [pscustomobject]@{ Title = 'Skipped tests (Skip = / Skip.If)'; Pattern = 'Skip\s*=|Skip\.If' }
    [pscustomobject]@{ Title = 'Placeholder / stub / not yet wording'; Pattern = '(?i)\b(placeholder|stubbed|stub for|not yet implemented|for now|temporar)' }
    [pscustomobject]@{ Title = 'Phase / block markers in code'; Pattern = '(?i)(Block \d|Phase \d)' }
)

foreach ($p in $debtPatterns) {
    Emit $debt ('## ' + $p.Title)
    Emit $debt ''
    Emit $debt '```'

    $count = 0
    foreach ($file in (SourceFiles $root '*.cs' | Sort-Object FullName)) {
        $found = @(Select-String -Path $file.FullName -Pattern $p.Pattern -ErrorAction SilentlyContinue)
        foreach ($m in $found) {
            $count++
            if ($count -le 400) {
                Emit $debt ((Relative $file.FullName) + ':' + [string]$m.LineNumber + '  ' + $m.Line.Trim())
            }
        }
    }

    if ($count -eq 0) { Emit $debt '(none)' }
    if ($count -gt 400) { Emit $debt ('... and ' + [string]($count - 400) + ' more') }

    Emit $debt '```'
    Emit $debt ('Total: ' + [string]$count)
    Emit $debt ''
}

Set-Content -Path (Join-Path $out '30-debt.md') -Value ($debt -join "`r`n") -Encoding UTF8
Write-Host '  wrote 30-debt.md'

# =============================================================== 40 - docs ===

$docs = New-Object 'System.Collections.Generic.List[string]'

Emit $docs '# Documentation index'
Emit $docs ''

foreach ($file in (SourceFiles $root '*.md' | Sort-Object FullName)) {
    Emit $docs ('## ' + (Relative $file.FullName) + '   (' + [string]$file.Length + ' bytes)')
    Emit $docs ''
    Emit $docs '```'

    $headings = @(Select-String -Path $file.FullName -Pattern '^#{1,3}\s' -ErrorAction SilentlyContinue)
    foreach ($h in $headings) { Emit $docs $h.Line.Trim() }
    if ($headings.Count -eq 0) { Emit $docs '(no headings)' }

    Emit $docs '```'
    Emit $docs ''
}

Set-Content -Path (Join-Path $out '40-docs.md') -Value ($docs -join "`r`n") -Encoding UTF8
Write-Host '  wrote 40-docs.md'

Write-Host ''
Write-Host ('Written to: ' + $out)
exit 0

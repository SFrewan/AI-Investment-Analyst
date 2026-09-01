#requires -Version 5.1
<#
    READ-ONLY SWEEP FOR THE TWO DEFECT CLASSES BLOCK 1 IS ABOUT.

    A. Injected dependencies that nothing registers.
       Every constructor parameter of every controller and hosted service, matched against
       every AddScoped/AddSingleton/AddTransient/AddHostedService in the composition files.
       A static approximation - CompositionTests asks the real container - but it also covers
       the types the container test cannot reach, and it names them all at once.

    B. Endpoint tests whose only assertions are negative.
       A test that asserts a response is NOT 401, NOT 403 and NOT 404 passes on a 500. That is
       how the PortfolioReader defect shipped, so every test with that shape is listed.

    Reads. Writes one report. Starts nothing, touches no database, makes no request.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\audit'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out '60-composition.md'

$lines = New-Object 'System.Collections.Generic.List[string]'

function Say([string]$text) {
    $null = $lines.Add($text)
    Write-Host $text
}

function Save-Log {
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
}

$excluded = '\\(bin|obj|node_modules|\.vs|\.git|StrykerOutput|TestResults|artifacts|Migrations)\\'

function Relative([string]$path) {
    if ($path.StartsWith($root)) { return $path.Substring($root.Length).TrimStart('\') }
    return $path
}

function CSharp([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -Path $path)) { return @() }

    return @(Get-ChildItem -Path $path -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excluded })
}

$srcFiles = @(CSharp 'src')
$testFiles = @(CSharp 'tests')

Say '# Composition and assertion sweep'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ''

# ============================================================ A. registrations

Say '## A. Injected dependencies that nothing registers'
Say ''

# ---- every registered service type ----

$registered = New-Object 'System.Collections.Generic.HashSet[string]'

$compositionFiles = @($srcFiles | Where-Object {
        $_.Name -eq 'DependencyInjection.cs' -or $_.Name -eq 'Program.cs'
    })

foreach ($file in $compositionFiles) {
    foreach ($line in @(Get-Content -Path $file.FullName -ErrorAction SilentlyContinue)) {
        # AddScoped<IFoo, Foo>() / AddScoped<Foo>() / AddSingleton<...> / AddHostedService<...>
        $found = [regex]::Matches(
            $line,
            'Add(?:Scoped|Singleton|Transient|HostedService)\s*<\s*([A-Za-z0-9_\.]+)')

        foreach ($m in $found) {
            $name = $m.Groups[1].Value
            $null = $registered.Add(($name -split '\.')[-1])
        }

        # AddScoped(typeof(IFoo), ...) and non-generic AddSingleton(provider => new Foo(...))
        $alt = [regex]::Matches($line, 'typeof\(\s*([A-Za-z0-9_\.]+)')
        foreach ($m in $alt) {
            $null = $registered.Add((($m.Groups[1].Value) -split '\.')[-1])
        }
    }
}

Say ('Registered service types found in the composition files: ' + [string]$registered.Count)
Say ''

# ---- framework-supplied types the container provides without a registration ----

$frameworkSupplied = @(
    'ILogger', 'ILoggerFactory', 'IOptions', 'IOptionsSnapshot', 'IOptionsMonitor',
    'IConfiguration', 'IServiceProvider', 'IServiceScopeFactory', 'IHostEnvironment',
    'IWebHostEnvironment', 'IHttpContextAccessor', 'IHostApplicationLifetime',
    'IMemoryCache', 'IHttpClientFactory', 'TimeProvider', 'IAuthorizationService',
    'IProblemDetailsService', 'IHostedService', 'IServiceCollection', 'ISystemClock',
    'IOptionsFactory', 'IMeterFactory', 'ILoggerProvider'
)

$injectionSites = @($srcFiles | Where-Object {
        $_.Name -match 'Controller\.cs$' -or $_.Name -match 'HostedService' -or $_.Name -match 'HostedServices'
    })

Say ('Injection sites inspected (controllers and hosted services): ' + [string]$injectionSites.Count)
Say ''
Say '```'

$unregistered = New-Object 'System.Collections.Generic.List[string]'

foreach ($file in ($injectionSites | Sort-Object Name)) {
    $text = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { continue }

    # public Foo(Type name, Type name) - the primary constructor of an injected component.
    $ctors = [regex]::Matches($text, 'public\s+(\w+)\s*\(([^\)]*)\)')

    foreach ($ctor in $ctors) {
        $owner = $ctor.Groups[1].Value
        if ($owner -ne $file.BaseName) { continue }

        $parameters = $ctor.Groups[2].Value
        if ([string]::IsNullOrWhiteSpace($parameters)) { continue }

        foreach ($parameter in @($parameters -split ',')) {
            $trimmed = $parameter.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }

            $parts = @($trimmed -split '\s+')
            if ($parts.Count -lt 2) { continue }

            $type = ($parts[0] -replace '<.*$', '') -replace '\?$', ''
            $type = ($type -split '\.')[-1]

            if ($frameworkSupplied -contains $type) { continue }
            if ($registered.Contains($type)) { continue }

            $null = $unregistered.Add(
                ('  ' + (Relative $file.FullName) + '  ' + $owner + ' needs ' + $type))
        }
    }
}

if ($unregistered.Count -eq 0) {
    Say '  (none - every injected type resolves to a registration)'
}
foreach ($u in $unregistered) { Say $u }

Say '```'
Say ''
Say ('Findings: ' + [string]$unregistered.Count)
Say ''
Say 'Static approximation. `CompositionTests` asks the real container the same question and is'
Say 'the authority; this list exists because it names every site at once.'
Say ''
Save-Log

# ============================================== B. tests that only assert negatives

Say '## B. Endpoint tests whose assertions are all negative'
Say ''
Say 'A test asserting only `NotEqual` on status codes passes on a 500. Each entry below is a test'
Say 'method that calls an endpoint and never asserts an expected status, body or value.'
Say ''
Say '```'

$weak = New-Object 'System.Collections.Generic.List[string]'

foreach ($file in ($testFiles | Sort-Object FullName)) {
    $content = @(Get-Content -Path $file.FullName -ErrorAction SilentlyContinue)
    if ($content.Count -eq 0) { continue }

    $method = ''
    $methodLine = 0
    $negatives = 0
    $positives = 0
    $callsEndpoint = $false

    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]

        if ($line -match '^\s*public\s+(async\s+)?(Task|void)\s+(\w+)\s*\(') {
            # Close off the previous method before starting the next.
            if ($method -ne '' -and $callsEndpoint -and $negatives -gt 0 -and $positives -eq 0) {
                $null = $weak.Add(
                    ('  ' + (Relative $file.FullName) + ':' + [string]$methodLine + '  ' + $method +
                        '   [' + [string]$negatives + ' negative assertions, 0 positive]'))
            }

            $method = $Matches[3]
            $methodLine = $i + 1
            $negatives = 0
            $positives = 0
            $callsEndpoint = $false
            continue
        }

        if ($method -eq '') { continue }

        if ($line -match 'GetAsync\(|PostAsync\(|PutAsync\(|DeleteAsync\(|SendAsync\(|PatchAsync\(') {
            $callsEndpoint = $true
        }

        if ($line -match 'Assert\.NotEqual\(') { $negatives++ }

        if ($line -match 'Assert\.(Equal|True|False|Contains|NotNull|NotEmpty|Single|Collection|Matches|StartsWith|EndsWith|InRange|IsType|Throws)\(') {
            $positives++
        }
    }

    if ($method -ne '' -and $callsEndpoint -and $negatives -gt 0 -and $positives -eq 0) {
        $null = $weak.Add(
            ('  ' + (Relative $file.FullName) + ':' + [string]$methodLine + '  ' + $method +
                '   [' + [string]$negatives + ' negative assertions, 0 positive]'))
    }
}

if ($weak.Count -eq 0) {
    Say '  (none)'
}
foreach ($w in $weak) { Say $w }

Say '```'
Say ''
Say ('Findings: ' + [string]$weak.Count)
Say ''

# ============================================== C. controllers with no test file

Say '## C. Controllers with no endpoint test file at all'
Say ''
Say '```'

$controllers = @($srcFiles | Where-Object { $_.Name -match 'Controller\.cs$' } | Sort-Object Name)
$untested = 0

foreach ($controller in $controllers) {
    $subject = $controller.BaseName -replace 'Controller$', ''

    $hits = 0
    foreach ($file in $testFiles) {
        $hits += @(Select-String -Path $file.FullName -Pattern ('api/' + $subject.ToLowerInvariant()) -ErrorAction SilentlyContinue).Count
        $hits += @(Select-String -Path $file.FullName -Pattern ($controller.BaseName) -ErrorAction SilentlyContinue).Count
    }

    if ($hits -eq 0) {
        $untested++
        Say ('  ' + $controller.BaseName + '   no test references it or its route')
    }
}

if ($untested -eq 0) { Say '  (none)' }

Say '```'
Say ''
Say ('Findings: ' + [string]$untested)
Say ''
Say '# END'
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit 0

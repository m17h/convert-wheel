# Checks that everything Convert Wheel ships is covered by THIRD-PARTY-NOTICES.md and the licenses folder:
# every NuGet package and library of the build, the native libraries, and the files inside the FFmpeg archive.
# Fails (exit code 1) with a list of what's missing. Run it after a build or publish:
#   powershell -ExecutionPolicy Bypass -File tools\check-notices.ps1 [-Published <publish folder>]
param(
    # A publish folder to check too: the notices have to be there, next to the exe.
    [string]$Published,
    # Which deps.json lists the shipped packages (found in obj or bin when not given).
    [string]$DepsJson
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$notices = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'THIRD-PARTY-NOTICES.md')
$map = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'notices.json') | ConvertFrom-Json
$problems = New-Object System.Collections.Generic.List[string]

function Add-Problem([string]$text) { $problems.Add($text) }

# The notices table row of a component: "| name | version | ...".
function Find-Row([string]$name, [string]$version) {
    $notices -split "`r?`n" | Where-Object { $_ -match ('^\|\s*' + [regex]::Escape($name) + '\s*\|\s*' + [regex]::Escape($version) + '\s*\|') }
}

if (-not $DepsJson) {
    $found = Get-ChildItem (Join-Path $root 'obj\Release'), (Join-Path $root 'bin\Release') -Recurse -Filter 'ConvertWheel.deps.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $found) { throw 'No ConvertWheel.deps.json found; build the app in Release first.' }
    $DepsJson = $found.FullName
}
$deps = Get-Content -Raw -Encoding UTF8 $DepsJson | ConvertFrom-Json

# Packages that put a file into the app, and those files.
$shipped = @{}
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($library in $target.Value.PSObject.Properties) {
        $name, $version = $library.Name -split '/', 2
        if ($name -eq 'ConvertWheel') { continue }
        $files = @()
        foreach ($group in 'runtime', 'native', 'resources') {
            if ($library.Value.$group) { $files += @($library.Value.$group.PSObject.Properties | ForEach-Object { Split-Path -Leaf $_.Name }) }
        }
        if ($library.Value.runtimeTargets) {
            $files += @($library.Value.runtimeTargets.PSObject.Properties | Where-Object { $_.Value.rid -in 'win', 'win-x64' } | ForEach-Object { Split-Path -Leaf $_.Name })
        }
        $files = @($files | Where-Object { $_ -notmatch '\.(pdb|xml)$' } | Sort-Object -Unique)
        if ($files.Count) { $shipped[$name] = @{ Version = $version; Files = $files } }
    }
}
if (-not $shipped.Count) { throw "$DepsJson lists no packages." }

$covered = @{}
foreach ($component in $map.components) {
    if (-not (Find-Row $component.name $component.version)) { Add-Problem "THIRD-PARTY-NOTICES.md has no row for $($component.name) $($component.version)." }
    foreach ($text in $component.texts) {
        $path = Join-Path (Join-Path $root 'licenses') $text
        if (-not (Test-Path -LiteralPath $path) -or ((Get-Item -LiteralPath $path) -is [IO.FileInfo] -and (Get-Item -LiteralPath $path).Length -lt 100)) { Add-Problem "licenses\$text is missing or empty ($($component.name))." }
    }
    if (-not @($component.texts).Count) { Add-Problem "tools\notices.json names no licence text for $($component.name)." }
    foreach ($package in @($component.packages)) {
        if (-not $package) { continue }
        $covered[$package.id] = $true
        $actual = $shipped[$package.id]
        if (-not $actual) { Add-Problem "tools\notices.json lists $($package.id), which isn't shipped any more."; continue }
        if ($actual.Version -ne $package.version) { Add-Problem "$($package.id) is $($actual.Version) now, but the notices were made for $($package.version)." }
        $new = @($actual.Files | Where-Object { @($package.files) -notcontains $_ })
        if ($new) { Add-Problem "$($package.id) ships files the notices don't know: $($new -join ', ')." }
    }
}
foreach ($name in $shipped.Keys) {
    if (-not $covered[$name]) { Add-Problem "The package $name $($shipped[$name].Version) ($($shipped[$name].Files -join ', ')) is shipped but has no notices." }
}

# Direct packages in the project file must match too (a build may be older than the project).
$project = [xml](Get-Content -Raw -Encoding UTF8 (Join-Path $root 'ConvertWheel.csproj'))
foreach ($reference in $project.SelectNodes('//PackageReference')) {
    $known = $map.components.packages | Where-Object { $_.id -eq $reference.Include }
    if (-not $known) { Add-Problem "ConvertWheel.csproj uses $($reference.Include), which has no notices." }
    elseif ($known.version -ne $reference.Version) { Add-Problem "ConvertWheel.csproj uses $($reference.Include) $($reference.Version); the notices were made for $($known.version)." }
}

# FFmpeg: the pinned build and the one described.
$pin = Get-Content -Raw (Join-Path $PSScriptRoot 'ffmpeg.json') | ConvertFrom-Json
$sources = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'licenses\ffmpeg\SOURCES.md')
foreach ($value in $pin.version, $pin.release, $pin.sha256, $pin.ffmpegCommit, $pin.buildScriptsCommit) {
    if (-not $sources.Contains($value)) { Add-Problem "licenses\ffmpeg\SOURCES.md doesn't mention $value from tools\ffmpeg.json." }
}
foreach ($file in $pin.files.PSObject.Properties.Name) {
    if (-not $sources.Contains($file)) { Add-Problem "licenses\ffmpeg\SOURCES.md doesn't mention $file." }
}
if (-not $notices.Contains($pin.version)) { Add-Problem "THIRD-PARTY-NOTICES.md doesn't name FFmpeg $($pin.version)." }
$row = [regex]'(?m)^\| ([a-z0-9][a-z0-9-]*) \|'
foreach ($match in $row.Matches($sources)) {
    $name = $match.Groups[1].Value -replace '-[23]$', ''
    if ($name -eq 'libmp3lame') { $name = 'lame' }
    # Build scripts that use several repositories share one text.
    $texts = @(Get-ChildItem (Join-Path $root 'licenses\ffmpeg\libraries') -Filter "$name*.txt")
    if (-not $texts) { Add-Problem "licenses\ffmpeg\libraries has no text for $name." }
}

if ($Published) {
    foreach ($item in 'THIRD-PARTY-NOTICES.md', 'licenses') {
        if (-not (Test-Path (Join-Path $Published $item))) { Add-Problem "$item isn't next to the exe in $Published." }
    }
    $licenses = Join-Path $root 'licenses'
    foreach ($file in Get-ChildItem $licenses -Recurse -File) {
        $relative = $file.FullName.Substring($licenses.Length + 1)
        $copy = Join-Path (Join-Path $Published 'licenses') $relative
        if (-not (Test-Path -LiteralPath $copy)) { Add-Problem "licenses\$relative wasn't published." }
        elseif ((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { Add-Problem "licenses\$relative was published with other contents." }
    }
    $knownFiles = @($map.components.packages.files) + 'ConvertWheel.exe'
    foreach ($file in Get-ChildItem $Published -File | Where-Object { $_.Extension -in '.dll', '.exe' }) {
        if ($knownFiles -notcontains $file.Name) { Add-Problem "$($file.Name) is in the publish folder but has no notices." }
    }
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "- $_" }
    Write-Host "$($problems.Count) problem(s) with the third-party notices."
    exit 1
}
Write-Host "Third-party notices cover all $($shipped.Count) shipped packages and FFmpeg $($pin.version)."
exit 0

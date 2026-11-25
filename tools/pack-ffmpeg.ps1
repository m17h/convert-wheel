# Downloads the FFmpeg build pinned in tools\ffmpeg.json (LGPL, unmodified) and packs the files Convert Wheel
# needs into tools\ffmpeg.tar.xz, which the build embeds into ConvertWheel.exe. Anything that doesn't match the
# recorded SHA-256 values stops the script.
#   powershell -ExecutionPolicy Bypass -File tools\pack-ffmpeg.ps1            packs it (replacing an older one)
#   powershell -ExecutionPolicy Bypass -File tools\pack-ffmpeg.ps1 -Verify    checks the one that's there
param(
    [switch]$Verify,
    # A zip that was already downloaded (it has to match the pinned hash too).
    [string]$Zip,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
$pin = Get-Content -Raw (Join-Path $PSScriptRoot 'ffmpeg.json') | ConvertFrom-Json
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'ffmpeg.tar.xz' }
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) "cw-ffmpeg-$([guid]::NewGuid().ToString('N'))"

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Assert-Contents([string]$folder) {
    $expected = @($pin.files.PSObject.Properties)
    foreach ($file in $expected) {
        $path = Join-Path $folder $file.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "FFmpeg: $($file.Name) is missing." }
        $hash = Get-Sha256 $path
        if ($hash -ne $file.Value) { throw "FFmpeg: $($file.Name) isn't the pinned file ($hash instead of $($file.Value)). Run tools\pack-ffmpeg.ps1 to get the pinned build." }
    }
    $extra = @(Get-ChildItem -LiteralPath $folder -Recurse | Where-Object { $expected.Name -notcontains $_.Name })
    if ($extra) { throw "FFmpeg: the archive holds unexpected files: $($extra.Name -join ', ')." }
}

function Assert-Archive([string]$archive) {
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "$archive doesn't exist. Run tools\pack-ffmpeg.ps1 first." }
    # Reading the archive back is what counts: the compressed bytes depend on the tar.exe that made them.
    $folder = Join-Path $work 'check'
    New-Item -ItemType Directory -Force $folder | Out-Null
    & $tar -xf $archive -C $folder
    if ($LASTEXITCODE -ne 0) { throw "$archive can't be read." }
    Assert-Contents $folder
    $hash = Get-Sha256 $archive
    if ($hash -ne $pin.packedSha256) { Write-Host "Note: every file in the archive matches, but it was compressed by another version of tar (SHA-256 $hash)." }
    $hash
}

New-Item -ItemType Directory $work | Out-Null
try {
    if ($Verify) {
        $hash = Assert-Archive $Out
        Write-Host "FFmpeg $($pin.version) verified ($hash)"
        return
    }

    if (-not $Zip) {
        $Zip = Join-Path $work 'ffmpeg.zip'
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest $pin.url -OutFile $Zip -UseBasicParsing
    }
    $hash = Get-Sha256 $Zip
    if ($hash -ne $pin.sha256) { throw "The FFmpeg download isn't the pinned one (SHA-256 $hash instead of $($pin.sha256))." }

    Expand-Archive -LiteralPath $Zip -DestinationPath (Join-Path $work 'x')
    $build = @(Get-ChildItem -LiteralPath (Join-Path $work 'x') -Directory)
    if ($build.Count -ne 1) { throw 'The FFmpeg download has an unexpected layout.' }
    $pack = Join-Path $work 'pack'
    New-Item -ItemType Directory $pack | Out-Null
    $names = @($pin.files.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($name in $names) {
        $source = if ($name -eq 'LICENSE.txt') { $build[0].FullName } else { Join-Path $build[0].FullName 'bin' }
        $copy = Copy-Item -LiteralPath (Join-Path $source $name) -Destination $pack -PassThru
        # Same times and owners on every machine, so the archive is the same too.
        $copy.LastWriteTimeUtc = [datetime]::SpecifyKind([datetime]'2026-10-31T00:00:00', 'Utc')
    }
    Assert-Contents $pack

    # Made next to its final place and moved there only when it's right.
    $made = "$Out.new"
    & $tar --format ustar --uid 0 --gid 0 --uname root --gname root -cJf $made -C $pack @names
    if ($LASTEXITCODE -ne 0) { throw 'Packing FFmpeg failed.' }
    $hash = Assert-Archive $made
    Move-Item -LiteralPath $made -Destination $Out -Force
    Write-Host "Packed FFmpeg $($pin.version): $($names.Count) files, $([math]::Round((Get-Item -LiteralPath $Out).Length / 1MB)) MB, SHA-256 $hash"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath "$Out.new") { Remove-Item -LiteralPath "$Out.new" -Force -ErrorAction SilentlyContinue }
}

# Builds dist\ConvertWheel-Setup-<version>.exe:
#   1. checks the FFmpeg archive against tools\ffmpeg.json and publishes the app (one exe, using .NET installed on
#      the PC when there is one, with its licence texts and ImageMagick's library next to it),
#   2. gathers a private .NET desktop runtime from this machine's .NET install (copied by the installer
#      only on PCs that don't have .NET 10), checking that Microsoft signed it,
#   3. compiles installer\ConvertWheel.iss with Inno Setup, fetched from NuGet into tools\.innosetup.
# Any problem stops it. The version comes from ConvertWheel.csproj.
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
# Signing is off unless a certificate (from this user's certificate store) is named:
#   ... -SignThumbprint <SHA-1 of the certificate> -TimestampUrl <the certificate authority's timestamp server>
param(
    [string]$SignThumbprint,
    [string]$TimestampUrl,
    # Where the installer goes; dist by default.
    [string]$OutputDir,
    # Testing: an installer that always includes the runtime.
    [switch]$ForceRuntime
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$appDir = Join-Path $artifacts 'app'
$runtimeDir = Join-Path $artifacts 'runtime'
$wizardDir = Join-Path $artifacts 'wizard'
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$innoVersion = '6.7.1'
$innoSha256 = '00a5a07e8a4bd3a30364cb579d744677b37adfc22e365099cb01b8bd1e651828'

# Empties one of this script's own folders (and nothing else).
function Reset-Staging([string]$folder) {
    if ((Split-Path -Parent $folder) -ne $artifacts) { throw "$folder isn't a staging folder." }
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    New-Item -ItemType Directory -Force $folder | Out-Null
}

# Runs one of the other scripts and stops if it fails.
function Invoke-Script([string]$name, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $name) @arguments
    if ($LASTEXITCODE -ne 0) { throw "$name failed." }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { return $null }
    Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$signTool = $null
if ($SignThumbprint -or $TimestampUrl) {
    $SignThumbprint = $SignThumbprint -replace '\s', ''
    if ($SignThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'SignThumbprint has to be the 40 hexadecimal digits of a certificate.' }
    if ($TimestampUrl -notmatch '^https?://[^\s"]+$') { throw 'TimestampUrl is needed too: without a timestamp, signatures end with the certificate.' }
    $signTool = Find-SignTool
    if (-not $signTool) { throw 'signtool.exe was not found. It comes with the Windows SDK.' }
}

# 1. The app.
Invoke-Script 'pack-ffmpeg.ps1' @('-Verify')
Reset-Staging $appDir
& dotnet publish (Join-Path $root 'ConvertWheel.csproj') -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o $appDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
$exe = Join-Path $appDir 'ConvertWheel.exe'
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "The app's version ($version) has to look like 1.2.3." }
Write-Host "Convert Wheel $version"
$config = Get-Content -Raw (Join-Path $root 'bin\Release\net10.0-windows\win-x64\ConvertWheel.runtimeconfig.json') | ConvertFrom-Json
$runtimeMinimum = ($config.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).version
if ($runtimeMinimum -notmatch '^10\.\d+\.\d+$') { throw "The app asks for .NET '$runtimeMinimum', which this script doesn't expect." }
Invoke-Script 'check-notices.ps1' @('-Published', $appDir, '-DepsJson', (Join-Path $root 'obj\Release\net10.0-windows\win-x64\ConvertWheel.deps.json'))
if ($signTool) {
    & $signTool sign /sha1 $SignThumbprint /fd sha256 /tr $TimestampUrl /td sha256 $exe
    if ($LASTEXITCODE -ne 0) { throw 'Signing the app failed.' }
}

# 2. The runtime: the newest .NET 10 desktop runtime on this machine (the same files Microsoft's installer puts down).
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$desktop = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
    Where-Object { $_.Name -match '^10\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $desktop) { throw 'No .NET 10 desktop runtime found on this machine.' }
$runtimeVersion = $desktop.Name
if ([version]$runtimeVersion -lt [version]$runtimeMinimum) { throw "The app needs .NET $runtimeMinimum, but the newest here is $runtimeVersion." }
$core = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$runtimeVersion"
$fxr = Join-Path $dotnetRoot "host\fxr\$runtimeVersion"
if (-not (Test-Path $core) -or -not (Test-Path $fxr)) { throw "The .NET $runtimeVersion install is incomplete." }
Reset-Staging $runtimeDir
# Each lands in its own version folder, exactly as in a .NET install: host\fxr\10.0.x, shared\<framework>\10.0.x.
foreach ($pair in @(@($fxr, 'host\fxr'), @($core, 'shared\Microsoft.NETCore.App'), @($desktop.FullName, 'shared\Microsoft.WindowsDesktop.App'))) {
    $target = Join-Path $runtimeDir (Join-Path $pair[1] $runtimeVersion)
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item -Recurse (Join-Path $pair[0] '*') $target
}
foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item (Join-Path $dotnetRoot $notice) $runtimeDir }
foreach ($needed in "host\fxr\$runtimeVersion\hostfxr.dll", "shared\Microsoft.NETCore.App\$runtimeVersion\hostpolicy.dll", "shared\Microsoft.WindowsDesktop.App\$runtimeVersion\PresentationFramework.dll") {
    if (-not (Test-Path (Join-Path $runtimeDir $needed))) { throw "The .NET $runtimeVersion install is incomplete: $needed is missing." }
}
$binaries = @(Get-ChildItem $runtimeDir -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' })
$unsigned = @($binaries | Get-AuthenticodeSignature |
    Where-Object { $_.Status -ne 'Valid' -or $_.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation' })
if ($unsigned) { throw "Not signed by Microsoft, so not shipped: $($unsigned.Path -join ', ')" }
Write-Host ".NET runtime $runtimeVersion gathered ($([math]::Round((Get-ChildItem $runtimeDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB, $($binaries.Count) signatures checked)"

# Wizard pictures from the app icon (normal and high-DPI sizes).
Add-Type -AssemblyName System.Drawing
Reset-Staging $wizardDir
$icon = Join-Path $root 'Assets\ConvertWheel.ico'
# The icon's largest image (PNG-compressed in the .ico), decoded with Windows imaging.
Add-Type -AssemblyName PresentationCore, WindowsBase
$stream = [System.IO.File]::OpenRead($icon)
$decoder = New-Object System.Windows.Media.Imaging.IconBitmapDecoder $stream, ([System.Windows.Media.Imaging.BitmapCreateOptions]::None), ([System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$largest = $decoder.Frames | Sort-Object PixelWidth | Select-Object -Last 1
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($largest))
$buffer = New-Object System.IO.MemoryStream
$encoder.Save($buffer)
$iconPng = $buffer.ToArray()
$stream.Dispose()
function Save-WizardImage([int]$width, [int]$height, [int]$iconSize, [string]$name, [System.Drawing.Color]$background) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear($background)
    $picture = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $iconPng)))
    $g.DrawImage($picture, [int](($width - $iconSize) / 2), [int](($height - $iconSize) / 2), $iconSize, $iconSize)
    $bitmap.Save((Join-Path $wizardDir $name), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $g.Dispose(); $bitmap.Dispose(); $picture.Dispose()
}
$panel = [System.Drawing.Color]::FromArgb(0x1E, 0x1F, 0x2E)
Save-WizardImage 164 314 112 'large.bmp' $panel
Save-WizardImage 328 628 224 'large-2x.bmp' $panel
Save-WizardImage 55 55 48 'small.bmp' ([System.Drawing.Color]::White)
Save-WizardImage 110 110 96 'small-2x.bmp' ([System.Drawing.Color]::White)

# 3. Inno Setup (a build tool only; nothing is installed on this PC).
$inno = Join-Path $root "tools\.innosetup\$innoVersion"
$iscc = Join-Path $inno 'tools\ISCC.exe'
if (-not (Test-Path $iscc)) {
    New-Item -ItemType Directory -Force $inno | Out-Null
    $package = Join-Path $inno 'innosetup.zip'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$innoVersion/tools.innosetup.$innoVersion.nupkg" -OutFile $package -UseBasicParsing
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    if ($hash -ne $innoSha256) {
        Remove-Item -LiteralPath $package
        throw "The Inno Setup download isn't the expected one (SHA-256 $hash)."
    }
    Expand-Archive -LiteralPath $package -DestinationPath $inno -Force
    Remove-Item -LiteralPath $package
}
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$setup = Join-Path $OutputDir "ConvertWheel-Setup-$version.exe"
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup }
$options = @('/Q', "/DAppDir=$appDir", "/DRuntimeDir=$runtimeDir", "/DRuntimeVersion=$runtimeVersion", "/DRuntimeMinimum=$runtimeMinimum", "/DWizardDir=$wizardDir", "/DOutputDir=$OutputDir")
if ($ForceRuntime) { $options += '/DForceRuntime' }
if ($signTool) {
    # $q is Inno's quote and $f the file to sign. The setup and its uninstaller are both signed.
    $options += '/DSign', ('/SConvertWheel=$q' + $signTool + '$q sign /sha1 ' + $SignThumbprint + ' /fd sha256 /tr ' + $TimestampUrl + ' /td sha256 $f')
}
& $iscc @options (Join-Path $root 'installer\ConvertWheel.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }

Write-Host "Convert Wheel $version, .NET runtime $runtimeVersion, $(if ($signTool) { 'signed' } else { 'not signed' })"
foreach ($file in $exe, $setup) {
    Write-Host "$file ($([math]::Round((Get-Item -LiteralPath $file).Length / 1MB)) MB)"
    Write-Host "  SHA-256 $((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())"
}

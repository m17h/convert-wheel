# Builds dist\ConvertWheel-Setup-<version>.exe:
#   1. publishes the app (one exe, using .NET installed on the PC when there is one),
#   2. gathers a private .NET desktop runtime from this machine's .NET install (copied by the installer
#      only on PCs that don't have .NET 10),
#   3. compiles installer\ConvertWheel.iss with Inno Setup, fetched from NuGet into tools\.innosetup.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$appDir = Join-Path $artifacts 'app'
$runtimeDir = Join-Path $artifacts 'runtime'
$dist = Join-Path $root 'dist'
$innoVersion = '6.7.1'

$project = [xml](Get-Content (Join-Path $root 'ConvertWheel.csproj'))
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Convert Wheel $version"

# 1. The app.
Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
& dotnet publish (Join-Path $root 'ConvertWheel.csproj') -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o $appDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }

# 2. The runtime: the newest .NET 10 desktop runtime on this machine (the same files Microsoft's installer puts down).
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$desktop = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
    Where-Object { $_.Name -match '^10\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $desktop) { throw 'No .NET 10 desktop runtime found on this machine.' }
$runtimeVersion = $desktop.Name
$core = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$runtimeVersion"
$fxr = Join-Path $dotnetRoot "host\fxr\$runtimeVersion"
if (-not (Test-Path $core) -or -not (Test-Path $fxr)) { throw "The .NET $runtimeVersion install is incomplete." }
Remove-Item -Recurse -Force $runtimeDir -ErrorAction SilentlyContinue
# Each lands in its own version folder, exactly as in a .NET install: host\fxr\10.0.x, shared\<framework>\10.0.x.
foreach ($pair in @(@($fxr, 'host\fxr'), @($core, 'shared\Microsoft.NETCore.App'), @($desktop.FullName, 'shared\Microsoft.WindowsDesktop.App'))) {
    $target = Join-Path $runtimeDir (Join-Path $pair[1] $runtimeVersion)
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item -Recurse (Join-Path $pair[0] '*') $target
}
foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $file = Join-Path $dotnetRoot $notice
    if (Test-Path $file) { Copy-Item $file $runtimeDir }
}
Write-Host ".NET runtime $runtimeVersion gathered ($([math]::Round((Get-ChildItem $runtimeDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB)"

# Wizard pictures from the app icon (normal and high-DPI sizes).
Add-Type -AssemblyName System.Drawing
$wizardDir = Join-Path $artifacts 'wizard'
New-Item -ItemType Directory -Force $wizardDir | Out-Null
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
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$innoVersion/tools.innosetup.$innoVersion.nupkg" -OutFile $package -UseBasicParsing
    Expand-Archive $package $inno -Force
    Remove-Item $package
}
New-Item -ItemType Directory -Force $dist | Out-Null
& $iscc /Q "/DAppVersion=$version" "/DAppDir=$appDir" "/DRuntimeDir=$runtimeDir" "/DRuntimeVersion=$runtimeVersion" "/DWizardDir=$wizardDir" "/DOutputDir=$dist" (Join-Path $root 'installer\ConvertWheel.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }
$setup = Join-Path $dist "ConvertWheel-Setup-$version.exe"
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"

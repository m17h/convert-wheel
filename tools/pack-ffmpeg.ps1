# Downloads the pinned LGPL FFmpeg build and packs the files Convert Wheel needs into
# Tools\ffmpeg.tar.xz, which the build embeds into ConvertWheel.exe.
# Run once before building:  powershell -ExecutionPolicy Bypass -File tools\pack-ffmpeg.ps1
$ErrorActionPreference = 'Stop'
$version = '8.1'
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n$version-latest-win64-lgpl-shared-$version.zip"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'Tools\ffmpeg.tar.xz'
$work = Join-Path ([IO.Path]::GetTempPath()) "cw-ffmpeg-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work, (Split-Path $out) | Out-Null
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $url -OutFile "$work\ffmpeg.zip" -UseBasicParsing
    Expand-Archive "$work\ffmpeg.zip" "$work\x"
    $bin = Get-ChildItem "$work\x" -Directory | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'bin' }
    $files = 'ffmpeg.exe', 'avcodec-*.dll', 'avdevice-*.dll', 'avfilter-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll' |
        ForEach-Object { Get-ChildItem $bin -Filter $_ } | ForEach-Object { $_.Name }
    Copy-Item (Join-Path (Split-Path $bin) 'LICENSE.txt') "$bin\LICENSE.txt"
    $files += 'LICENSE.txt'
    Push-Location $bin
    try { & "$env:SystemRoot\System32\tar.exe" -cJf $out @files } finally { Pop-Location }
    Write-Host "Packed $($files.Count) files into $out ($([math]::Round((Get-Item $out).Length / 1MB)) MB)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

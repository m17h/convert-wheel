# Third-party notices

Convert Wheel bundles the following open-source components. Each remains under its own license; the
license texts are available at the linked projects.

| Component | Version | License | Project |
| --- | --- | --- | --- |
| .NET desktop runtime (only on PCs without .NET 10; its LICENSE.txt and ThirdPartyNotices.txt sit in `runtime\`) | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/windowsdesktop |
| Inno Setup (builds the installer; its uninstaller is part of every install) | 6.7.1 | Inno Setup License (free, including commercial use) | https://jrsoftware.org/isinfo.php |
| FFmpeg (shared LGPL build by BtbN) | n8.1 | LGPL 2.1 or later | https://ffmpeg.org, builds: https://github.com/BtbN/FFmpeg-Builds |
| ImageMagick via Magick.NET-Q8-x64 | 15.0.0 | Apache 2.0 (ImageMagick License) | https://github.com/dlemstra/Magick.NET |
| PDFium via bblanchon.PDFium.Win32 | 158.0.8125 | Apache 2.0 / BSD-3 | https://pdfium.googlesource.com/pdfium, https://github.com/bblanchon/pdfium-binaries |
| PDFsharp | 6.2.4 | MIT | https://github.com/empira/PDFsharp |
| SharpCompress | 0.50.7 | MIT | https://github.com/adamhathcock/sharpcompress |
| AngleSharp | 1.8.6 | MIT | https://github.com/AngleSharp/AngleSharp |
| Markdig | 1.5.1 | BSD-2-Clause | https://github.com/xoofx/markdig |
| YamlDotNet | 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet |
| ExcelDataReader | 3.9.0 | MIT | https://github.com/ExcelDataReader/ExcelDataReader |
| SkiaSharp | 4.153.1 | MIT | https://github.com/mono/SkiaSharp |
| NVorbis | 0.10.5 | MIT | https://github.com/NVorbis/NVorbis |
| OggVorbisEncoder | 1.2.2 | MIT | https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder |
| Concentus / Concentus.OggFile | 2.2.2 / 1.0.7 | BSD-3-Clause / MIT | https://github.com/lostromb/concentus |

## FFmpeg (LGPL)

The app embeds an unmodified LGPL shared-library build of FFmpeg (`ffmpeg.exe` plus the `av*`/`sw*` DLLs
and FFmpeg's `LICENSE.txt`). It is unpacked on first use into `%LOCALAPPDATA%\ConvertWheel\tools\ffmpeg-*`
and run as a separate program, so the libraries there can be replaced with any compatible build.

- Source code for this FFmpeg version: https://github.com/FFmpeg/FFmpeg/tree/release/8.1
- Build scripts used to produce the binaries: https://github.com/BtbN/FFmpeg-Builds
- `tools\pack-ffmpeg.ps1` documents exactly which build is bundled.

No GPL-only or non-free FFmpeg components (such as x264 or fdk-aac) are included; H.264 video is
encoded with Windows' own encoder through FFmpeg's Media Foundation wrapper.

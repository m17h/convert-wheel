# Third-party notices

Convert Wheel includes the components below. Each stays under its own licence. The full texts are in the `licenses`
folder next to this file. Some are permissive; FFmpeg and parts of ImageMagick are LGPL, and the .NET runtime
comes under Microsoft's terms.

| Component | Version | Licence | Texts in `licenses` | Project |
| --- | --- | --- | --- | --- |
| FFmpeg | n8.1.3-41-ga8029b6528-20261125 | LGPL 3.0 or later | `ffmpeg` | https://ffmpeg.org, built by https://github.com/FFmpeg-Builds/FFmpeg-Builds |
| Magick.NET and ImageMagick | 15.0.0 | Apache-2.0 and ImageMagick License; contains LGPL and other libraries | `Magick.NET.txt`, `Magick.NET-Notice.txt` | https://github.com/dlemstra/Magick.NET |
| PDFium | 158.0.8125 | BSD-3-Clause and Apache-2.0; build scripts MIT | `pdfium` | https://pdfium.googlesource.com/pdfium, built by https://github.com/bblanchon/pdfium-binaries |
| SkiaSharp and Skia | 4.153.1 | MIT; Skia BSD-3-Clause and others | `SkiaSharp.txt`, `SkiaSharp-THIRD-PARTY-NOTICES.txt` | https://github.com/mono/SkiaSharp |
| PDFsharp | 6.2.4 | MIT | `PDFsharp.txt` | https://github.com/empira/PDFsharp |
| SharpCompress | 0.50.7 | MIT | `SharpCompress.txt` | https://github.com/adamhathcock/sharpcompress |
| AngleSharp | 1.8.6 | MIT | `AngleSharp.txt` | https://github.com/AngleSharp/AngleSharp |
| Markdig | 1.5.1 | BSD-2-Clause | `Markdig.txt` | https://github.com/xoofx/markdig |
| YamlDotNet | 18.1.0 | MIT | `YamlDotNet.txt` | https://github.com/aaubry/YamlDotNet |
| ExcelDataReader | 3.9.0 | MIT | `ExcelDataReader.txt` | https://github.com/ExcelDataReader/ExcelDataReader |
| NVorbis | 0.10.5 | MIT | `NVorbis.txt` | https://github.com/NVorbis/NVorbis |
| OggVorbisEncoder | 1.2.2 | MIT | `OggVorbisEncoder.txt` | https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder |
| Concentus | 2.2.2 | BSD-3-Clause | `Concentus.txt` | https://github.com/lostromb/concentus |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | `Microsoft.Extensions.txt`, `Microsoft.Extensions-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | `Microsoft.Extensions.txt`, `Microsoft.Extensions-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| Microsoft .NET desktop runtime | 10.0 | Microsoft .NET Library license terms; source MIT | `dotnet` | https://github.com/dotnet/runtime, https://github.com/dotnet/windowsdesktop |
| Inno Setup | 6.7.1 | Inno Setup License | `InnoSetup.txt` | https://jrsoftware.org/isinfo.php |

- The .NET runtime travels in the installer and is only copied (into `runtime`, unchanged) to PCs without .NET 10.
  `ConvertWheel.exe` also contains .NET's small program starter. Microsoft licenses the binaries under the
  ".NET Library" terms in `licenses\dotnet\LICENSE.txt`.
- Inno Setup makes the installer; its uninstaller is part of every install.

## FFmpeg (LGPL 3.0 or later)

Convert Wheel includes an unmodified shared-library build of FFmpeg (`ffmpeg.exe`, the `av*` and `sw*` DLLs and its
LICENSE.txt), made with `--enable-version3` and without `--enable-gpl` or `--enable-nonfree`. It is unpacked on first
use into `%LOCALAPPDATA%\ConvertWheel\tools` and run as a separate program, and its files there can be replaced
with other builds.

`licenses\ffmpeg\SOURCES.md` names the exact build, its configuration, every library in it with its licence, where the
corresponding source code of all of them is, and how to replace the files. The LGPL 3.0 and the GPL 3.0 it builds on
are in the same folder.

## ImageMagick's LGPL parts

`Magick.Native-Q8-x64.dll` (unmodified, from the Magick.NET-Q8-x64 package) contains libheif, libde265, cairo, GLib,
pango, librsvg, libcroco, gdk-pixbuf, FriBidi, liblqr and LibRaw among others, under LGPL versions or a choice that includes
one. `Magick.NET-Notice.txt` lists all of them with their versions and texts.

- Sources and build scripts: https://github.com/dlemstra/Magick.Native/tree/2026.1010.1943
  (commit 4dcdad28530c14c8d35687fdeb1ecc8d39b01e30); ImageMagick itself is
  https://github.com/ImageMagick/ImageMagick/tree/211e5b975a0019a88dcef5f84c1193f46738d815; the libraries, in the versions
  `Magick.NET-Notice.txt` gives, come from https://github.com/ImageMagick/Dependencies; the .NET part is https://github.com/dlemstra/Magick.NET/tree/79bb8700d7fe3a564f62926ab299fc2520cb2269.
- The DLL sits next to `ConvertWheel.exe`. Exit the app and replace it with your own build to use that instead. You may
  modify it and reverse engineer it for that purpose.

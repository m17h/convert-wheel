# Convert Wheel

A Windows tray app: hold **Ctrl** while hovering a file in File Explorer or on the desktop, and a radial
wheel pops up over everything so you can convert the file to another format.

## Using it

- **Hold Ctrl** over a file. After a short hold the wheel opens around the cursor.
- **Flick toward a format and let go** to convert. You can also release first and then **click** a slice.
- The inner ring holds **smart suggestions** for that file type (an `.ogg` offers MP3, WAV, FLAC…).
  Rest on (or pick) **All** to open the outer ring with every format the file can become.
- Keys while the wheel is open: `1`–`8` pick a slice, `Tab` shows all formats, `Enter` converts the
  highlighted slice, `Esc` closes.
- Shortcuts still work: pressing another key, clicking or scrolling while holding Ctrl cancels the wheel,
  so Ctrl+C and Ctrl+click behave normally.

The tray icon (in the taskbar's hidden icons) opens **Settings** and can pause or exit the app.

## Settings

- The hold key or combination (any key, e.g. `Ctrl`, `Alt`, `Ctrl + Shift`, `Caps Lock`) and the hold time.
- Keep the original and save a copy, or **replace the original**. Replaced originals go to the Recycle Bin.
- Save next to the original or into a chosen folder.
- Always show every format, start with Windows, completion notifications.

## Converters

Everything is converted by code that ships inside the app. Nothing needs to be installed or downloaded.
Microsoft Office is used when it happens to be installed, because it's the most faithful for its own formats.

| Files | Engine |
| --- | --- |
| Audio: MP3, M4A, AAC, WAV, FLAC, WMA ↔ each other, M4R ringtones, and the audio track of any video | Windows Media Foundation (Windows' own codecs) |
| Audio: OGG Vorbis and Opus (read and write), AIFF | Bundled open-source codecs (NVorbis, OggVorbisEncoder, Concentus) and our own AIFF/WAV code |
| Video → MP4, MOV, WMV (H.264/WMV, often GPU accelerated) | Windows Media Foundation |
| Video → MKV | Our Matroska writer (lossless copy when possible) |
| Video ↔ GIF, video/GIF → animated WebP, animated WebP → GIF or video | Our GIF encoder, WebP container writer and animation reader |
| WebM, AVI, MPG, AC3, and inputs Windows can't decode (FLV, old codecs) | FFmpeg (LGPL build), bundled compressed and unpacked once on first use |
| Images: PNG, JPG, BMP, GIF, TIFF, ICO, WebP | Windows imaging and Skia |
| Images: HEIC, AVIF, JPEG XL, PSD, TGA, SVG, camera RAW, EXR, DDS, EMF/WMF and more | ImageMagick (Magick.NET) |
| Images → multi-page PDF (searchable: an invisible text layer from OCR); multi-page TIFF → PDF | Our image-to-PDF writer |
| Text in pictures (screenshots, photos of pages) → TXT or DOCX; scanned PDFs → any document format | Windows' built-in OCR (Windows.Media.Ocr), called directly |
| Documents: DOCX, DOC (Word 6, 95 and 97–2003), ODT, RTF, TXT, Markdown, HTML, EPUB → PDF, DOCX, ODT, RTF, TXT, HTML, Markdown, EPUB | Built-in document engine |
| PDF → DOCX, ODT, RTF, TXT, HTML, Markdown, EPUB (text reflowed into paragraphs, headings, lists and pictures; scanned pages read with OCR) or PNG/JPG/TIFF pages | PDFium plus our layout analysis |
| Spreadsheets: XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV, JSON, PDF, HTML (charts and pictures included) | Built-in spreadsheet engine |
| Charts in PPTX, DOCX, XLSX, ODP, ODT and ODS (column, bar, line, area, pie, doughnut, scatter, radar; stacked and combo) | Our chart reader and renderer |
| Presentations: PPTX, PPT (97–2003), ODP → PDF, PNG/JPG per slide, PPTX, ODP | Built-in presentation engine |
| Archives: ZIP, 7z, RAR, TAR, GZ, BZ2, XZ → ZIP, TAR, TAR.GZ | SharpCompress |
| Subtitles: SRT, VTT, ASS/SSA, SBV → SRT, VTT, ASS, TXT | Our subtitle reader/writer |
| Data: JSON, YAML, XML ↔ each other, and to CSV/XLSX when the data is a list of records | Our data converter (YamlDotNet for YAML) |

Lossless shortcuts: H.264/AAC video moves between MP4, MOV and MKV without re-encoding, and an MP4's AAC
audio becomes M4A/AAC instantly. Song details (title, artist, album, track, year, genre) carry across audio
conversions. Phone videos keep their rotation. Pages and slides that become pictures go into a folder
named after the file when there is more than one.

### How documents are converted

For each document conversion the app tries, in order:

1. **Built-in engine** first for plain text/CSV sources and for PDF output. It's fast (about a second) and
   doesn't depend on Office's print pipeline, which can stall for a minute or more on some PCs.
2. **Microsoft Office** (Word, Excel, PowerPoint), run invisibly, for the most faithful result. A copy of
   Office the user already has open is never touched.
3. **LibreOffice**, if it happens to be installed.
4. **Built-in engine** for everything else.

If one engine fails the next is tried. The built-in engine reads every format into a small document
model (`Conversion/Documents/DocModel.cs`) and writes every output from that. PDFs are laid out by WPF's
own pagination and written with [PDFsharp](https://github.com/empira/PDFsharp), with the real Windows fonts embedded.

The built-in engine keeps headers and footers (including first-page and even-page versions, with live
"Page X of Y" fields), footnotes and endnotes, page breaks, sections with their own page size, orientation
and columns, lists, tables, charts, and positioned pictures and text boxes (wrapped, behind or in front of
text) across DOCX, ODT, RTF and PDF. Old `.doc` files (Word 6 to 2003) bring their text in its original code
page, formatting, styles, lists, tables, hyperlinks, pictures, floating drawings (shapes, text boxes, WordArt,
groups), headers, footers, footnotes and endnotes. Scanned PDFs and photos of pages are read with the OCR
engine built into Windows, in the languages installed there.

Not carried over: comments (tracked changes are shown accepted, as Word's "Simple Markup" does), a change to
columns at a *continuous* section break (the page keeps one layout), RTF output's sections, and WordArt's
3D/perspective effects (drawn flat with its colours). Charts become pictures when written to another
editable format (they look the same but aren't editable charts there).

### How presentations are converted

Slides are read into positioned shapes (`Conversion/Presentations/SlideModel.cs`): theme colours and fonts,
placeholders inheriting from their layout and master, text styles and bullets, shapes and connectors,
pictures (with crops and outlines), tables with their table styles, gradients, picture and duotone fills,
groups and SmartArt. Each slide is then drawn with WPF into a vector PDF page or a picture, or written out
as PPTX/ODP. Charts are drawn from their data, and speaker notes carry across PPTX, ODP and PPT. Slide
transitions and animations don't apply to PDF or pictures and aren't carried between PPTX and ODP.

### Third-party libraries

All permissively licensed and bundled into the exe. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- **PDFsharp 6.2.4** (MIT). Pinned on purpose: 6.3 and later are AGPL, which would force this app to be AGPL too.
- **PDFium** (Apache 2.0 / BSD, via bblanchon.PDFium) to read and render PDFs.
- **Magick.NET / ImageMagick** (Apache 2.0) for HEIC, AVIF, JPEG XL, PSD, SVG, RAW and other images.
- **FFmpeg** (LGPL 2.1+, shared-library build from BtbN) for the video and audio formats Windows lacks.
- **SharpCompress** (MIT) for archives; **AngleSharp** (MIT) for HTML; **Markdig** (BSD-2) for Markdown;
  **YamlDotNet** (MIT) for YAML.
- **ExcelDataReader** (MIT) for reading `.xlsx` and `.xls`.
- **SkiaSharp** (MIT) for WebP encoding and decoding.
- **NVorbis** (MIT) and **OggVorbisEncoder** (MIT) for Ogg Vorbis. The encoder drops the first half-block of
  audio and misbehaves above quality 0.5, so the app pads the start and uses 0.5 (~160 kbps).
- **Concentus** and **Concentus.OggFile** (BSD / MIT) for Opus.

## Installing

Users run `ConvertWheel-Setup-<version>.exe`. It needs nothing else and no administrator rights:

- Installs for the current user into `%LOCALAPPDATA%\Programs\Convert Wheel`, with a Start menu shortcut
  and (optional, on by default) a "start with Windows" entry.
- The .NET runtime travels inside the installer, but is only copied when the PC doesn't already have .NET 10
  for desktop apps. Then it goes into the app's own `runtime` folder (nothing system-wide changes); the app
  looks there first, then for the system's .NET. The Ready page says which case applies.
- A running copy is closed first (politely via `ConvertWheel.exe --exit`, forcefully if it doesn't respond).
- The last page offers to open the app and to delete the installer file; both are ticked by default.
- Uninstalling (Settings › Apps) also removes the settings, log, unpacked video tools and startup entry.
- Silent installs work as usual: `ConvertWheel-Setup-1.0.0.exe /VERYSILENT [/TASKS="startup,desktopicon"]`.

## Build & run

Requires the .NET 10 SDK.

```powershell
dotnet run -c Release                      # run from source
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1   # dist\ConvertWheel-Setup-<version>.exe
```

`build-installer.ps1` publishes the app, copies the .NET 10 desktop runtime from this machine's .NET install,
fetches [Inno Setup](https://jrsoftware.org/isinfo.php) from NuGet into `tools\.innosetup` (nothing is installed)
and compiles `installer\ConvertWheel.iss`. Compiling that script with `/DForceRuntime` makes an installer that
always includes the runtime, for testing the "no .NET" path on a PC that has it.

The first build fetches the LGPL FFmpeg build and packs it into `tools\ffmpeg.tar.xz` (see `tools\pack-ffmpeg.ps1`;
the file isn't committed), which is then embedded into the exe. Offline, or with `-p:SkipFfmpegPack=true`, the app
still builds; the FFmpeg-only formats then use an `ffmpeg.exe` found next to the app or on `PATH`.

Command-line extras:

- `ConvertWheel.exe --convert "file.ogg" mp3` converts without any UI and prints the output path.
- `ConvertWheel.exe --background` starts without opening the settings window (used by "Start with Windows").
- `ConvertWheel.exe --exit` closes a running copy (used by the installer).
- `ConvertWheel.exe --export-icon Assets\ConvertWheel.ico` regenerates the app icon.
- Setting `CONVERTWHEEL_DOC_ENGINE` to `built-in`, `Microsoft Office` or `LibreOffice` forces one document
  engine, which is handy for testing.
- Setting `CONVERTWHEEL_MEDIA_ENGINE` to `built-in` disables the FFmpeg fallback (or `ffmpeg` forces it), for testing.

Settings are saved in `%APPDATA%\ConvertWheel\settings.json`. Errors are logged to `%LOCALAPPDATA%\ConvertWheel\log.txt`.
The bundled FFmpeg is unpacked once into `%LOCALAPPDATA%\ConvertWheel\tools`.

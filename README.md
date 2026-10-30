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
| Images → multi-page PDF; multi-page TIFF → PDF | Our image-to-PDF writer |
| Documents: DOCX, DOC (Word 97–2003), ODT, RTF, TXT, Markdown, HTML, EPUB → PDF, DOCX, ODT, RTF, TXT, HTML, Markdown, EPUB | Built-in document engine |
| PDF → DOCX, ODT, RTF, TXT, HTML, Markdown, EPUB (text reflowed into paragraphs, headings, lists and pictures) or PNG/JPG/TIFF pages | PDFium plus our layout analysis |
| Spreadsheets: XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV, JSON, PDF, HTML | Built-in spreadsheet engine |
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
"Page X of Y" fields), footnotes and endnotes, page breaks, lists, tables, and positioned pictures and text
boxes (wrapped, behind or in front of text) across DOCX, ODT, RTF and PDF. Old `.doc` files bring their text,
formatting, styles, lists, tables, hyperlinks, inline pictures and footnotes. Not yet handled: multiple
sections with different page sizes, columns, charts, comments and tracked changes (accepted text is kept),
floating drawings in `.doc`, and OCR (a scanned PDF has no text to extract; convert it to PNG instead).

### How presentations are converted

Slides are read into positioned shapes (`Conversion/Presentations/SlideModel.cs`): theme colours and fonts,
placeholders inheriting from their layout and master, text styles and bullets, shapes and connectors,
pictures (with crops and outlines), tables with their table styles, gradients, picture and duotone fills,
groups and SmartArt. Each slide is then drawn with WPF into a vector PDF page or a picture, or written out
as PPTX/ODP. Charts show their saved preview picture; animations and speaker notes are not carried over.

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

## Build & run

Requires the .NET 10 SDK.

```powershell
.\tools\pack-ffmpeg.ps1                    # once: fetches the LGPL FFmpeg build and packs tools\ffmpeg.tar.xz
dotnet run -c Release                      # run from source
dotnet publish -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

`tools\ffmpeg.tar.xz` is embedded into the exe when present (it isn't committed). Without it the app still
builds; the FFmpeg-only formats then use an `ffmpeg.exe` found next to the app or on `PATH`.

Command-line extras:

- `ConvertWheel.exe --convert "file.ogg" mp3` converts without any UI and prints the output path.
- `ConvertWheel.exe --background` starts without opening the settings window (used by "Start with Windows").
- `ConvertWheel.exe --export-icon Assets\ConvertWheel.ico` regenerates the app icon.
- Setting `CONVERTWHEEL_DOC_ENGINE` to `built-in`, `Microsoft Office` or `LibreOffice` forces one document
  engine, which is handy for testing.
- Setting `CONVERTWHEEL_MEDIA_ENGINE` to `built-in` disables the FFmpeg fallback (or `ffmpeg` forces it), for testing.

Settings are saved in `%APPDATA%\ConvertWheel\settings.json`. Errors are logged to `%LOCALAPPDATA%\ConvertWheel\log.txt`.
The bundled FFmpeg is unpacked once into `%LOCALAPPDATA%\ConvertWheel\tools`.

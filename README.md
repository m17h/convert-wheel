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

Almost everything is converted by code that ships inside the app. Downloads are optional and always asked for first.

| Files | Engine |
| --- | --- |
| Audio: MP3, M4A, AAC, WAV, FLAC, WMA ↔ each other, and the audio track of any video | Windows Media Foundation (Windows' own codecs) |
| Audio: OGG Vorbis and Opus (read and write), AIFF | Bundled open-source codecs (NVorbis, OggVorbisEncoder, Concentus) and our own AIFF/WAV code |
| Video → MP4, MOV, WMV (H.264/WMV, often GPU accelerated) | Windows Media Foundation |
| Video → MKV | Our Matroska writer (lossless copy when possible) |
| Video ↔ GIF, GIF → animated WebP | Our GIF encoder (median-cut palette, ordered dithering, frame differencing) and WebP container writer |
| Images: PNG, JPG, BMP, GIF, TIFF, ICO, WebP | Windows imaging, plus Skia for WebP and images Windows can't read |
| Documents: Word, OpenDocument, RTF, TXT → PDF, DOCX, ODT, RTF, TXT, HTML | Built-in document engine |
| Spreadsheets: Excel, OpenDocument, CSV → XLSX, ODS, CSV, PDF, HTML | Built-in spreadsheet engine |
| Any Office format, including PowerPoint and old `.doc` | Microsoft Office, when installed |
| WebM, AVI, AVIF, and inputs Windows can't decode (e.g. FLV) | FFmpeg: optional ~110 MB download, offered when needed |
| Office files nothing else can read (e.g. PowerPoint without Office) | LibreOffice: optional ~360 MB download, offered when needed |

Lossless shortcuts: H.264/AAC video moves between MP4, MOV and MKV without re-encoding, and an MP4's AAC
audio becomes M4A/AAC instantly. Song details (title, artist, album, track, year, genre) carry across audio
conversions. Phone videos keep their rotation.

### How documents are converted

For each document conversion the app tries, in order:

1. **Built-in engine** first for plain text/CSV sources and for PDF output. It's fast (about a second) and
   doesn't depend on Office's print pipeline, which can stall for a minute or more on some PCs.
2. **Microsoft Office** (Word, Excel, PowerPoint), run invisibly, for the most faithful result. A copy of
   Office the user already has open is never touched.
3. **LibreOffice**, if it's installed or was downloaded earlier.
4. **Built-in engine** for everything else it can read.

If one engine fails the next is tried. The built-in engine reads DOCX/ODT/RTF/TXT into a small document
model (`Conversion/Documents/DocModel.cs`) and writes every output from that. PDFs are laid out by WPF's
own pagination and written with [PDFsharp](https://github.com/empira/PDFsharp), with the real Windows fonts embedded.

The built-in engine keeps headers and footers (including first-page and even-page versions, with live
"Page X of Y" fields), footnotes and endnotes, page breaks, and positioned pictures and text boxes
(wrapped, behind or in front of text) across DOCX, ODT, RTF and PDF. Plain text and HTML get the header,
footer and a notes list. Not yet handled: multiple sections with different page sizes, columns, charts
and SmartArt, comments and tracked changes (accepted text is kept), and RTF pictures stay inline.

### Third-party libraries

All permissively licensed and bundled into the exe:

- **PDFsharp 6.2.4** (MIT). Pinned on purpose: 6.3 and later are AGPL, which would force this app to be AGPL too.
- **ExcelDataReader** (MIT) for reading `.xlsx` and `.xls`.
- **SkiaSharp** (MIT) for WebP encoding and decoding.
- **NVorbis** (MIT) and **OggVorbisEncoder** (MIT) for Ogg Vorbis. The encoder drops the first half-block of
  audio and misbehaves above quality 0.5, so the app pads the start and uses 0.5 (~160 kbps).
- **Concentus** and **Concentus.OggFile** (BSD / MIT) for Opus.

## Build & run

Requires the .NET 10 SDK.

```powershell
dotnet run -c Release                      # run from source
dotnet publish -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

Command-line extras:

- `ConvertWheel.exe --convert "file.ogg" mp3` converts without any UI and prints the output path.
- `ConvertWheel.exe --background` starts without opening the settings window (used by "Start with Windows").
- `ConvertWheel.exe --setup-libreoffice` fetches the optional LibreOffice helper ahead of time.
- `ConvertWheel.exe --export-icon Assets\ConvertWheel.ico` regenerates the app icon.
- Setting `CONVERTWHEEL_DOC_ENGINE` to `built-in`, `Microsoft Office` or `LibreOffice` forces one document
  engine, which is handy for testing.
- Setting `CONVERTWHEEL_MEDIA_ENGINE` to `built-in` disables the FFmpeg fallback (or `ffmpeg` forces it), for testing.

Settings are saved in `%APPDATA%\ConvertWheel\settings.json`. Errors are logged to `%LOCALAPPDATA%\ConvertWheel\log.txt`.

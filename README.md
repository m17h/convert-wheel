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

| Files | Engine |
| --- | --- |
| PNG, JPG, BMP, GIF, TIFF, ICO output from images | Windows imaging, built in |
| Audio, video, GIF↔video, WebP, AVIF | FFmpeg. Downloaded automatically (about 110 MB) to `%LOCALAPPDATA%\ConvertWheel\tools` the first time it's needed. |
| Word (.docx), OpenDocument (.odt), RTF, TXT → PDF, DOCX, ODT, RTF, TXT, HTML | Built-in document engine |
| Excel (.xlsx, .xls), OpenDocument (.ods), CSV → XLSX, ODS, CSV, PDF, HTML | Built-in spreadsheet engine |
| Any Office format, including PowerPoint and old `.doc` | Microsoft Office, when installed |
| Anything the above can't handle (e.g. PowerPoint without Office) | LibreOffice, only after the user agrees to a one-time ~360 MB download. Unpacked privately, no install or admin rights. |

Video to MP4/MKV/MOV/WebM is a lossless remux when the streams already fit, so it's instant.

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

Not yet handled by the built-in engine: headers/footers, footnotes, text boxes keep their text but not
their position, floating images become inline, and page breaks inside RTF files are lost.

### Third-party libraries

- **PDFsharp 6.2.4** (MIT). Pinned on purpose: 6.3 and later are AGPL, which would force this app to be AGPL too.
- **ExcelDataReader** (MIT) for reading `.xlsx` and `.xls`.

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

Settings are saved in `%APPDATA%\ConvertWheel\settings.json`. Errors are logged to `%LOCALAPPDATA%\ConvertWheel\log.txt`.

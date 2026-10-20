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
| Audio, video, GIF↔video, WebP, AVIF | FFmpeg. Downloaded automatically (about 200 MB) to `%LOCALAPPDATA%\ConvertWheel\tools` the first time it's needed. |
| Documents, spreadsheets, slides | LibreOffice, if it's installed |

Video to MP4/MKV/MOV/WebM is a lossless remux when the streams already fit, so it's instant.

## Build & run

Requires the .NET 10 SDK.

```powershell
dotnet run -c Release                      # run from source
dotnet publish -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

Command-line extras:

- `ConvertWheel.exe --convert "file.ogg" mp3` converts without any UI and prints the output path.
- `ConvertWheel.exe --background` starts without opening the settings window (used by "Start with Windows").
- `ConvertWheel.exe --export-icon Assets\ConvertWheel.ico` regenerates the app icon.

Settings are saved in `%APPDATA%\ConvertWheel\settings.json`. Errors are logged to `%LOCALAPPDATA%\ConvertWheel\log.txt`.

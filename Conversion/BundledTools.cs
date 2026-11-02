using System.Reflection;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ConvertWheel.Conversion;

/// <summary>
/// FFmpeg (LGPL build) ships inside ConvertWheel.exe as a compressed resource and is unpacked into the
/// app's data folder the first time a rare audio/video format needs it. No download, no install.
/// </summary>
internal static class BundledTools
{
    const string FfmpegResource = "ffmpeg.tar.xz";
    static readonly object Gate = new();

    static string FfmpegDir
    {
        get
        {
            // A folder per bundle size, so a new app version with a new FFmpeg unpacks afresh.
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FfmpegResource);
            return Path.Combine(ToolLocator.ToolsDir, $"ffmpeg-{stream?.Length ?? 0:x}");
        }
    }

    public static bool HasFfmpeg => Assembly.GetExecutingAssembly().GetManifestResourceInfo(FfmpegResource) != null;

    /// <summary>The unpacked ffmpeg.exe if it's ready, without unpacking.</summary>
    public static string? ReadyFfmpeg()
    {
        if (!HasFfmpeg) return null;
        var exe = Path.Combine(FfmpegDir, "ffmpeg.exe");
        return File.Exists(Path.Combine(FfmpegDir, ".complete")) ? exe : null;
    }

    /// <summary>Unpacks the bundled FFmpeg if needed (a few seconds, once) and returns ffmpeg.exe.</summary>
    public static string UnpackFfmpeg()
    {
        lock (Gate)
        {
            if (ReadyFfmpeg() is { } ready) return ready;
            var target = FfmpegDir;
            var staging = target + ".unpacking";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FfmpegResource)
                         ?? throw new ConversionException("This build doesn't include the video tools."))
            using (var reader = ReaderFactory.OpenReader(stream, new ReaderOptions()))
            {
                var options = new ExtractionOptions { ExtractFullPath = false, Overwrite = true };
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory) reader.WriteEntryToDirectory(staging, options);
            }
            File.WriteAllText(Path.Combine(staging, ".complete"), DateTime.UtcNow.ToString("O"));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);

            // Remove copies left by older versions.
            foreach (var old in Directory.EnumerateDirectories(ToolLocator.ToolsDir, "ffmpeg-*").Where(d => d != target))
                try { Directory.Delete(old, recursive: true); } catch { }
            return Path.Combine(target, "ffmpeg.exe");
        }
    }
}

/// <summary>Conversions that produce one file per page or slide.</summary>
internal static class Outputs
{
    /// <summary>
    /// One page: written to the reserved file. Several: the reserved file is replaced by a folder
    /// "name (PNG)" holding name-01.png, name-02.png… Returns the path to show the user.
    /// </summary>
    public static string Pages(string reserved, int count, Action<int, string> write)
    {
        if (count <= 0) throw new ConversionException("This file has no pages to convert.");
        if (count == 1)
        {
            write(0, reserved);
            return reserved;
        }
        var ext = Path.GetExtension(reserved);
        var stem = Path.GetFileNameWithoutExtension(reserved);
        var parent = Path.GetDirectoryName(reserved)!;
        var staging = Path.Combine(parent, $".cw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            int digits = Math.Max(2, count.ToString().Length);
            for (int i = 0; i < count; i++)
                write(i, Path.Combine(staging, $"{stem}-{(i + 1).ToString().PadLeft(digits, '0')}{ext}"));
            File.Delete(reserved);
            for (int i = 1; ; i++)
            {
                var name = $"{stem} ({ext.TrimStart('.').ToUpperInvariant()})";
                var folder = Path.Combine(parent, i == 1 ? name : $"{name} ({i})");
                try
                {
                    Directory.Move(staging, folder);
                    return folder;
                }
                catch (IOException) when (Directory.Exists(staging) && (Directory.Exists(folder) || File.Exists(folder))) { }
            }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }
}

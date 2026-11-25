using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion;

/// <summary>Decides when "replace the original" must not, and replaces it safely when it may.</summary>
internal static class Originals
{
    /// <summary>What the original looked like when its conversion started.</summary>
    public sealed record Snapshot(long Length, DateTime Modified)
    {
        public static Snapshot? Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new Snapshot(info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// Conversions that only take something out of a file (a video's sound, a page's picture, a picture's text)
    /// aren't a replacement for it. Returns the sentence to show, or null when replacing is fine.
    /// </summary>
    public static string? WhyKept(ConversionPlan plan, FormatInfo target) => (plan.SourceKind, target.Kind) switch
    {
        (FormatKind.Video, not FormatKind.Video) => "The original video was kept.",
        (FormatKind.Audio, not FormatKind.Audio) => "The original was kept.",
        (FormatKind.Image, _) when target.Ext is "txt" or "docx" => "The original picture was kept.",
        (FormatKind.Image, FormatKind.Image) when LosesFrames(plan, target.Ext) => "The original was kept, because it has more than one picture.",
        (FormatKind.Image, FormatKind.Document) when plan.SourceExt != "tiff" && LosesFrames(plan, target.Ext) => "The original was kept, because it has more than one picture.",
        (FormatKind.Document, FormatKind.Image) => "The original document was kept.",
        (FormatKind.Presentation, FormatKind.Image) => "The original presentation was kept.",
        (FormatKind.Spreadsheet, FormatKind.Document) when target.Ext is "txt" or "md" => "The original spreadsheet was kept.",
        (FormatKind.Subtitle, not FormatKind.Subtitle) => "The original subtitles were kept.",
        _ when Path.GetExtension(plan.SourcePath).ToLowerInvariant() is ".docm" or ".xlsm" => "The original was kept, because the new file has no macros.",
        _ => null,
    };

    /// <summary>An animation or a multi-page TIFF going to a format that only gets its first picture.</summary>
    static bool LosesFrames(ConversionPlan plan, string ext)
    {
        try
        {
            switch (plan.SourceExt)
            {
                case "webp": return ext != "gif" && MagickImages.IsAnimatedWebP(plan.SourcePath);
                case "gif" when ext == "webp": return false;
                case "gif" or "tiff":
                    using (var file = new FileStream(plan.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        return BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count > 1;
                default: return false;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't count the pictures in {plan.SourcePath}: {ex.Message}");
            return true; // when in doubt, keep it
        }
    }

    /// <summary>
    /// Moves the original to the Recycle Bin once its replacement is safely on disk. Returns whether it did,
    /// and otherwise why not, in words for the user.
    /// </summary>
    public static (bool replaced, string? why) Replace(ConversionPlan plan, FormatInfo target, ConversionResult result, Snapshot? before)
    {
        if (result.Problem != null) return (false, "The original was kept.");
        if (WhyKept(plan, target) is { } reason) return (false, reason);
        if (before == null || Snapshot.Of(plan.SourcePath) is not { } now) return (false, "The original couldn't be checked, so it was kept.");
        if (now != before) return (false, "The original changed while it was being converted, so it was kept.");
        if (!Secure(result.Path)) return (false, "The new file couldn't be checked, so the original was kept.");
        return RecycleBin.Send(plan.SourcePath) switch
        {
            RecycleBin.Outcome.Recycled => (true, null),
            RecycleBin.Outcome.Kept => (false, "The original couldn't go to the Recycle Bin, so it was kept."),
            _ => (false, "The original was removed, but Windows didn't confirm it's in the Recycle Bin."),
        };
    }

    /// <summary>Makes sure the new file is really on the drive, not only in memory, before the old one goes.</summary>
    static bool Secure(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length == 0) return false;
            file.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't check {path}: {ex.Message}");
            return false;
        }
    }
}

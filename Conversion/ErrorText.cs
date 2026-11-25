using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ConvertWheel.Conversion;

/// <summary>Says what went wrong in plain words. The raw error is kept for "Copy details" and the log.</summary>
internal static partial class ErrorText
{
    public const string DiskFull = "There isn't enough free space on the drive.";
    public const string Denied = "Windows didn't allow access to the file or its folder. Try saving to another folder in Settings.";
    public const string InUse = "The file is open in another program. Close it there and try again.";
    public const string TooLong = "The file's name or folder path is too long for Windows. Try a shorter one.";
    public const string Password = "This file is password-protected.";
    public const string Damaged = "This file is damaged, or it isn't really the type its name says.";
    public const string NoMemory = "There isn't enough memory to convert this file.";
    public const string NoCodec = "This file uses a codec that isn't available on this PC.";
    public const string Missing = "The file was moved or deleted before it could be converted.";
    public const string Unreachable = "The drive or network folder can't be reached.";
    public const string NotSupported = "This kind of file can't be converted.";
    public const string Other = "Something went wrong while converting this file.";
    public const string NoAudio = "This video has no audio track.";

    public static string Describe(Exception error)
    {
        while (error is AggregateException or TargetInvocationException or TypeInitializationException && error.InnerException != null) error = error.InnerException;
        if (error is ConversionException or ToolMissingException) return error.Message;
        if (error is OutOfMemoryException or InsufficientMemoryException) return NoMemory;
        if (error is PathTooLongException) return TooLong;
        if (error is FileNotFoundException or DirectoryNotFoundException) return Missing;
        if (error is UnauthorizedAccessException or SecurityException) return Denied;
        if (FromCode(error) is { } known) return known;
        if (PasswordWords().IsMatch(error.Message)) return Password;
        if (error is TimeoutException) return "The conversion stopped responding.";
        if (error is NotSupportedException) return NotSupported;
        if (error is InvalidDataException or XmlException or EndOfStreamException or JsonException or FormatException or FileFormatException
            || DamageTypes().IsMatch(error.GetType().FullName ?? "")) return Damaged;
        return Other;
    }

    static string? FromCode(Exception error)
    {
        int code = error is Win32Exception win32 ? win32.NativeErrorCode : error.HResult;
        switch (unchecked((uint)error.HResult))
        {
            case 0xC00D36C4: // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
            case 0xC00D5212: // MF_E_TOPO_CODEC_NOT_FOUND
            case 0xC00D36B4: // MF_E_INVALIDMEDIATYPE
            case 0x88982F50: // WINCODEC_ERR_COMPONENTNOTFOUND
                return NoCodec;
            case 0x88982F07 or 0x88982F60 or 0x88982F61 or 0x88982F62: // damaged picture
                return Damaged;
            case 0x8007000E: return NoMemory;
            case 0x88980003: return NoMemory;
        }
        // Windows error numbers come wrapped as 0x8007xxxx.
        if (error is not Win32Exception)
        {
            if ((code & 0xFFFF0000) != 0x80070000) return null;
            code &= 0xFFFF;
        }
        return code switch
        {
            39 or 112 or 1295 => DiskFull,
            32 or 33 => InUse,
            5 or 19 or 1920 => Denied,
            2 or 3 => Missing,
            111 or 206 => TooLong,
            8 or 14 or 1455 => NoMemory,
            53 or 55 or 59 or 64 or 67 or 21 or 1167 or 1231 or 1232 or 362 or 389 or 395 or 426 => Unreachable,
            23 or 1117 or 1392 or 1393 => "The drive reported an error while reading or writing the file.",
            _ => null,
        };
    }

    /// <summary>What FFmpeg's own messages mean.</summary>
    public static string FromFfmpeg(string stderr)
    {
        bool Says(string text) => stderr.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Says("No space left on device")) return DiskFull;
        if (Says("Permission denied")) return Denied;
        if (Says("Cannot allocate memory")) return NoMemory;
        if (Says("not on whitelist") || Says("not on blacklist"))
            return "This file only points to other files or the web, so there's nothing in it to convert.";
        if (Says("Unknown decoder") || Says("no decoder found") || Says("Decoder not found") || Says("Decoding requested, but no decoder")) return NoCodec;
        if (Says("No such file or directory")) return Missing;
        if (Says("Input/output error")) return "The drive reported an error while reading or writing the file.";
        if (Says("encrypted") && Says("decryption")) return "This file is copy-protected.";
        if (Says("does not contain any stream") || Says("matches no streams")) return "There's nothing in this file to convert to that format.";
        if (Says("Invalid data found") || Says("moov atom not found") || Says("could not find codec parameters")) return Damaged;
        return "The video tools couldn't convert this file.";
    }

    /// <summary>Everything worth pasting into a bug report.</summary>
    public static string Details(Exception error, string source, string target)
    {
        var text = $"{Path.GetFileName(source)} to {target}\r\n{(error as ConversionException)?.Details}\r\n{error}".Replace("\r\n\r\n", "\r\n");
        return text.Length > 6000 ? text[..6000] + "…" : text;
    }

    [GeneratedRegex(@"password|encrypted|decrypt", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordWords();

    // Libraries throw their own kinds of "this file is broken".
    [GeneratedRegex(@"^(SharpCompress|ImageMagick|PdfSharp|ExcelDataReader|YamlDotNet|NVorbis|Concentus|AngleSharp)\.")]
    private static partial Regex DamageTypes();
}

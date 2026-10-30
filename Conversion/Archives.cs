using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace ConvertWheel.Conversion;

/// <summary>Repacks archives: ZIP, 7z, RAR, TAR, GZ, BZ2 and XZ in; ZIP, TAR and TAR.GZ out.</summary>
internal static class Archives
{
    public static void Convert(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        var (type, options) = ext switch
        {
            "zip" => (ArchiveType.Zip, WriterOptions.ForZip(CompressionType.Deflate)),
            "tar" => (ArchiveType.Tar, WriterOptions.ForTar(CompressionType.None)),
            "tgz" => (ArchiveType.Tar, WriterOptions.ForTar(CompressionType.GZip)),
            _ => throw new ConversionException($"Can't create {ext.ToUpperInvariant()} archives."),
        };
        if (ext == "zip") options = options with { CompressionLevel = 6 };

        using var outStream = File.Create(output);
        using var writer = WriterFactory.OpenWriter(outStream, type, options);
        long total = new FileInfo(input).Length, done = 0;
        int files = 0;

        foreach (var (key, isDirectory, modified, open) in Entries(input))
        {
            ct.ThrowIfCancellationRequested();
            var name = key.Replace('\\', '/');
            while (name.StartsWith("./")) name = name[2..];
            name = name.TrimStart('/');
            if (name.Length == 0 || name.Split('/').Contains("..")) continue;
            if (isDirectory)
            {
                writer.WriteDirectory(name, modified);
                continue;
            }
            // Tar needs each entry's size up front, so buffer through a temporary file.
            using var source = open();
            using var buffer = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            source.CopyTo(buffer);
            done += buffer.Length;
            buffer.Position = 0;
            writer.Write(name, buffer, modified);
            files++;
            if (total > 0) progress.Report(Math.Min(0.99, (double)done / Math.Max(total, done * 1.5)));
        }
        if (files == 0) throw new ConversionException("This archive is empty.");
    }

    static IEnumerable<(string key, bool isDirectory, DateTime? modified, Func<Stream> open)> Entries(string input)
    {
        var lower = input.ToLowerInvariant();
        bool singleCompressed = (lower.EndsWith(".gz") || lower.EndsWith(".bz2") || lower.EndsWith(".xz")) &&
                                !lower.EndsWith(".tar.gz") && !lower.EndsWith(".tar.bz2") && !lower.EndsWith(".tar.xz") && !IsTarInside(input);
        if (singleCompressed)
        {
            // A single compressed file (e.g. log.txt.gz): the archive gets that one file.
            var name = Path.GetFileNameWithoutExtension(input);
            yield return (name, false, File.GetLastWriteTime(input), () => Decompress(input));
            yield break;
        }

        // 7z needs random access; everything else (zip, rar, tar and compressed tars) streams with a reader.
        IArchive? archive = null;
        Stream? file = null;
        IReader reader;
        try
        {
            if (lower.EndsWith(".7z"))
            {
                archive = ArchiveFactory.OpenArchive(input, new ReaderOptions());
                reader = archive.ExtractAllEntries();
            }
            else
            {
                file = File.OpenRead(input);
                reader = ReaderFactory.OpenReader(file, new ReaderOptions());
            }
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex is CryptographicException)
        {
            archive?.Dispose();
            file?.Dispose();
            throw new ConversionException("This archive is password-protected.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArchiveException or InvalidFormatException or IOException)
        {
            archive?.Dispose();
            file?.Dispose();
            throw new ConversionException("This archive is damaged or in a format that can't be read.");
        }
        using (archive)
        using (file)
        {
            using (reader)
            {
                while (true)
                {
                    bool more;
                    try { more = reader.MoveToNextEntry(); }
                    catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex is CryptographicException)
                    {
                        throw new ConversionException("This archive is password-protected.");
                    }
                    if (!more) break;
                    var entry = reader.Entry;
                    yield return (entry.Key ?? "file", entry.IsDirectory, entry.LastModifiedTime, () => reader.OpenEntryStream());
                }
            }
        }
    }

    static Stream Decompress(string input)
    {
        var file = File.OpenRead(input);
        var lower = input.ToLowerInvariant();
        if (lower.EndsWith(".gz")) return new GZipStream(file, CompressionMode.Decompress);
        if (lower.EndsWith(".bz2")) return BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress, false);
        return new XZStream(file);
    }

    /// <summary>Whether a .gz/.bz2/.xz holds a tar (names like backup.tgz renamed to .gz).</summary>
    static bool IsTarInside(string input)
    {
        try
        {
            using var stream = Decompress(input);
            var header = new byte[512];
            int read = 0, n;
            while (read < header.Length && (n = stream.Read(header, read, header.Length - read)) > 0) read += n;
            return read == 512 && header[257] == 'u' && header[258] == 's' && header[259] == 't' && header[260] == 'a' && header[261] == 'r';
        }
        catch { return false; }
    }
}

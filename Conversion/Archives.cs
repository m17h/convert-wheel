using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;

namespace ConvertWheel.Conversion;

/// <summary>
/// Repacks archives: ZIP, 7z, RAR, TAR, GZ, BZ2 and XZ in; ZIP, TAR and TAR.GZ out. Everything is streamed
/// from one archive to the other, so nothing is unpacked to disk and size is no limit.
/// </summary>
internal static class Archives
{
    enum ItemKind { File, Directory, SymbolicLink, HardLink }

    /// <param name="Size">Uncompressed size, or null when it's only known after reading.</param>
    /// <param name="Link">Where a link points.</param>
    /// <param name="Crc">Expected CRC-32, when the format has one that isn't checked while reading.</param>
    /// <param name="Mode">Unix permissions, from a ZIP or 7z made on Linux or macOS.</param>
    /// <param name="Tar">The original entry when the source is a tar (owner and permissions).</param>
    sealed record Item(string Name, ItemKind Kind, DateTimeOffset? Modified, long? Size, Func<Stream>? Open,
        string? Link = null, uint? Crc = null, TarEntry? Tar = null, UnixFileMode? Mode = null);

    /// <summary>What's being read, and how far along it is (0-1).</summary>
    sealed class Source(string path)
    {
        public string Path => path;
        public long Length { get; } = new FileInfo(path).Length;
        public long? TotalSize { get; set; }
        public Func<double>? Fraction { get; set; }
    }

    public static void Convert(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        if (ext is not ("zip" or "tar" or "tgz")) throw new ConversionException($"Can't create {ext.ToUpperInvariant()} archives.");
        try
        {
            var source = new Source(input);
            using var writer = ArchiveWriter.Create(output, ext);
            var budget = new Budget(source, output, ext == "tar", progress, ct);
            var names = new EntryNames(windows: ext == "zip");
            var links = new Dictionary<string, List<Item>>();
            int entries = 0;

            foreach (var item in Items(source, budget))
            {
                ct.ThrowIfCancellationRequested();
                var name = names.Claim(item.Name, item.Kind == ItemKind.Directory);
                if (name == null) continue;
                entries++;
                switch (item.Kind)
                {
                    case ItemKind.Directory:
                        writer.Directory(name, item);
                        break;
                    case ItemKind.SymbolicLink:
                        writer.Link(name, item);
                        break;
                    case ItemKind.HardLink when writer.KeepsHardLinks:
                        writer.Link(name, item with { Link = EntryNames.Clean(item.Link ?? "") });
                        break;
                    case ItemKind.HardLink:
                        // ZIP has no links, so each name gets its own copy of the contents, in a second pass.
                        var target = EntryNames.Clean(item.Link ?? "");
                        if (!links.TryGetValue(target, out var list)) links[target] = list = [];
                        list.Add(item with { Name = name });
                        break;
                    default:
                        writer.File(name, item, budget);
                        break;
                }
            }
            if (links.Count > 0)
                foreach (var item in Items(source, budget))
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Kind != ItemKind.File || !links.Remove(EntryNames.Clean(item.Name), out var copies)) continue;
                    // A tar entry can only be read once, so further names are copied from the first one's spool.
                    using var spool = budget.Spool(item);
                    foreach (var copy in copies)
                    {
                        spool.Position = 0;
                        writer.File(copy.Name, item with { Modified = copy.Modified, Open = () => new Unclosed(spool) }, budget);
                    }
                }
            if (entries == 0) throw new ConversionException("This archive is empty.");
        }
        catch (Exception ex) when (IsPassword(ex)) { throw new ConversionException("This archive is password-protected."); }
        catch (Exception ex) when (ex is SharpCompressException or InvalidDataException or InvalidOperationException or EndOfStreamException
            or NotSupportedException or ArgumentException or OverflowException or IndexOutOfRangeException or NullReferenceException)
        {
            Log.Write(ex);
            throw new ConversionException("This archive is damaged or in a format that can't be read.");
        }
    }

    static bool IsPassword(Exception ex) =>
        ex is CryptographicException or System.Security.Cryptography.CryptographicException || ex is not (ConversionException or IOException) && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ limits

    /// <summary>Checks everything read: cancelling, disk space, and archives built to expand without end.</summary>
    sealed class Budget(Source source, string output, bool uncompressed, IProgress<double?> progress, CancellationToken ct)
    {
        // Only deliberately hostile files get near this: plain deflate tops out near 1000:1.
        const long BombSize = 16L << 30, BombRatio = 5000;
        const long KeepFree = 256L << 20, CheckEvery = 64L << 20;
        long _read, _checked;

        public void Expect(long size)
        {
            CheckRatio(size);
            if (uncompressed && FreeSpace() is long free && size > free - KeepFree)
                throw new ConversionException("There isn't enough free disk space for this archive's contents.");
        }

        public void Count(int bytes)
        {
            ct.ThrowIfCancellationRequested();
            _read += bytes;
            if (_read - _checked < CheckEvery && bytes > 0) return;
            _checked = _read;
            CheckRatio(_read);
            if (FreeSpace() is long free && free < KeepFree)
                throw new ConversionException("The disk is nearly full, so the conversion was stopped.");
        }

        void CheckRatio(long size)
        {
            if (size > BombSize && size / BombRatio > source.Length)
                throw new ConversionException("This archive expands to an unreasonable size, so it was left alone.");
        }

        public void Report()
        {
            double? fraction = source.TotalSize > 0 ? (double)_read / source.TotalSize : source.Fraction?.Invoke();
            if (fraction is double f) progress.Report(Math.Min(0.99, f));
        }

        long? FreeSpace()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(output))!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; } // network shares
        }

        /// <summary>The contents of an entry in a temporary file that deletes itself.</summary>
        public FileStream Spool(Item item)
        {
            var spool = new FileStream(Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            try
            {
                using var stream = new CheckedStream(item, this);
                stream.CopyTo(spool, 1 << 16);
                spool.Position = 0;
                return spool;
            }
            catch
            {
                spool.Dispose();
                throw;
            }
        }
    }

    /// <summary>An entry's contents on their way through: counted, and compared with the size and checksum the archive promised.</summary>
    sealed class CheckedStream(Item item, Budget budget) : Stream
    {
        static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        readonly Stream _inner = item.Open!();
        long _position;
        uint _crc = uint.MaxValue;
        bool _ended;

        public override int Read(Span<byte> buffer)
        {
            if (_ended || buffer.Length == 0) return 0;
            int n = _inner.Read(buffer);
            budget.Count(n);
            _position += n;
            if (item.Size is long size && (_position > size || n == 0 && _position < size)) throw new InvalidDataException($"{item.Name}: {_position} of {size} bytes.");
            if (item.Crc != null) foreach (var b in buffer[..n]) _crc = Table[(byte)(_crc ^ b)] ^ (_crc >> 8);
            if (n == 0)
            {
                _ended = true;
                if (item.Crc is uint crc && crc != ~_crc) throw new InvalidDataException($"{item.Name}: checksum mismatch.");
            }
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        // Seekable in name only: the tar writer needs to know the length up front.
        public override bool CanSeek => item.Size != null;
        public override long Length => item.Size ?? throw new NotSupportedException();
        public override long Position { get => _position; set { if (value != _position) throw new NotSupportedException(); } }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream someone else will close.</summary>
    sealed class Unclosed(Stream inner) : Decompressed
    {
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// Entry names that are safe to unpack: no drive letters, leading slashes or "..", and no two entries
    /// with the same name. For ZIP, which is mostly unpacked on Windows, that includes names differing only
    /// in case and names Windows can't create.
    /// </summary>
    sealed class EntryNames(bool windows)
    {
        readonly HashSet<string> _files = new(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        readonly HashSet<string> _directories = new(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public static string Clean(string name)
        {
            name = name.Replace('\\', '/');
            if (name.Length >= 3 && name[1] == ':' && name[2] == '/' && char.IsAsciiLetter(name[0])) name = name[2..];
            return string.Join('/', name.Split('/').Where(p => p.Length > 0 && p is not ("." or "..")));
        }

        /// <summary>The name to use, or null when there's nothing to add (the root, or a folder that's already there).</summary>
        public string? Claim(string original, bool directory)
        {
            var name = Clean(original);
            if (windows) name = string.Join('/', name.Split('/').Select(WindowsName));
            if (name.Length == 0) return null;
            if (directory)
            {
                if (_files.Contains(name)) name = Unique(name);
                return _directories.Add(name) ? name : null;
            }
            if (_files.Contains(name) || _directories.Contains(name)) name = Unique(name);
            _files.Add(name);
            return name;
        }

        string Unique(string name)
        {
            int slash = name.LastIndexOf('/') + 1, dot = name.LastIndexOf('.');
            if (dot <= slash) dot = name.Length;
            for (int i = 2; ; i++)
            {
                var candidate = $"{name[..dot]} ({i}){name[dot..]}";
                if (!_files.Contains(candidate) && !_directories.Contains(candidate)) return candidate;
            }
        }

        static string WindowsName(string part)
        {
            var sb = new StringBuilder(part.Length);
            foreach (var c in part) sb.Append(c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
            while (sb.Length > 0 && sb[^1] is '.' or ' ') sb[^1] = '_';
            var stem = sb.ToString().Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && stem[..3] is "COM" or "LPT" && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
            return reserved ? "_" + sb : sb.ToString();
        }
    }

    // ------------------------------------------------------------------ writing

    abstract class ArchiveWriter : IDisposable
    {
        public static ArchiveWriter Create(string output, string ext) => ext == "zip" ? new ZipWriter(output) : new TarFileWriter(output, gzip: ext == "tgz");

        public virtual bool KeepsHardLinks => false;
        public abstract void Directory(string name, Item item);
        public abstract void File(string name, Item item, Budget budget);
        public abstract void Link(string name, Item item);
        public abstract void Dispose();
    }

    sealed class ZipWriter(string output) : ArchiveWriter
    {
        readonly ZipArchive _zip = new(System.IO.File.Create(output), ZipArchiveMode.Create);

        ZipArchiveEntry Entry(string name, Item item)
        {
            var entry = _zip.CreateEntry(name, CompressionLevel.Optimal);
            // ZIP stores local time, from 1980 to 2107.
            if (item.Modified is { } modified && modified.LocalDateTime is { Year: >= 1980 and <= 2107 } local) entry.LastWriteTime = new DateTimeOffset(local);
            else if (item.Modified != null) entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            return entry;
        }

        public override void Directory(string name, Item item) => Entry(name + "/", item).ExternalAttributes = (int)FileAttributes.Directory;

        public override void File(string name, Item item, Budget budget)
        {
            var entry = Entry(name, item);
            using var from = new CheckedStream(item, budget);
            using var to = entry.Open();
            var buffer = new byte[1 << 17];
            int n;
            while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                to.Write(buffer, 0, n);
                budget.Report();
            }
        }

        /// <summary>Windows' ZIP has no links: a small text file says where the link pointed.</summary>
        public override void Link(string name, Item item)
        {
            using var to = Entry(name, item).Open();
            to.Write(Encoding.UTF8.GetBytes(item.Link ?? ""));
        }

        public override void Dispose() => _zip.Dispose();
    }

    sealed class TarFileWriter : ArchiveWriter
    {
        readonly Stream _file;
        readonly TarWriter _tar;

        public TarFileWriter(string output, bool gzip)
        {
            _file = System.IO.File.Create(output);
            if (gzip) _file = new GZipStream(_file, CompressionLevel.Optimal);
            _tar = new TarWriter(_file, TarEntryFormat.Pax, leaveOpen: true);
        }

        public override bool KeepsHardLinks => true;

        void Write(TarEntryType type, string name, Item item, Stream? data)
        {
            // The plain ustar format reads everywhere; PAX is for what it can't hold.
            static bool Simple(string s) => s.Length < 100 && s.All(c => c is >= ' ' and < (char)127);
            bool ustar = Simple(name) && Simple(item.Link ?? "") && item.Size is null or < 1L << 33;
            PosixTarEntry entry = ustar ? new UstarTarEntry(type, name) : new PaxTarEntry(type, name);
            if (item.Modified is { } modified) entry.ModificationTime = modified < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : modified;
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = item.Link ?? "";
            const UnixFileMode Read = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            entry.Mode = item.Tar?.Mode ?? item.Mode ?? (Read | UnixFileMode.UserWrite | (type == TarEntryType.Directory ? Execute : 0));
            if (item.Tar is { } original)
            {
                entry.Uid = original.Uid;
                entry.Gid = original.Gid;
                if (original is PosixTarEntry posix && Simple(posix.UserName) && Simple(posix.GroupName) && posix.UserName.Length < 32 && posix.GroupName.Length < 32)
                {
                    entry.UserName = posix.UserName;
                    entry.GroupName = posix.GroupName;
                }
            }
            if (data != null) entry.DataStream = data;
            _tar.WriteEntry(entry);
        }

        public override void Directory(string name, Item item) => Write(TarEntryType.Directory, name + "/", item, null);
        public override void Link(string name, Item item) => Write(item.Kind == ItemKind.HardLink ? TarEntryType.HardLink : TarEntryType.SymbolicLink, name, item, null);

        public override void File(string name, Item item, Budget budget)
        {
            if (item.Size == null)
            {
                // Tar needs each entry's size before its contents.
                using var spool = budget.Spool(item);
                Write(TarEntryType.RegularFile, name, item with { Size = spool.Length }, spool);
            }
            else
            {
                using var data = new CheckedStream(item, budget);
                Write(TarEntryType.RegularFile, name, item, data);
                if (data.ReadByte() >= 0) throw new InvalidDataException(); // also checks the checksum
            }
            budget.Report();
        }

        public override void Dispose()
        {
            _tar.Dispose();
            _file.Dispose();
        }
    }

    // ------------------------------------------------------------------ reading

    static IEnumerable<Item> Items(Source source, Budget budget)
    {
        var head = new byte[6];
        using (var file = File.OpenRead(source.Path)) file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        // Go by the contents: archives often carry the wrong extension.
        if (head is [(byte)'P', (byte)'K', ..] or [(byte)'7', (byte)'z', 0xBC, 0xAF, ..] or [(byte)'R', (byte)'a', (byte)'r', (byte)'!', ..])
            return FromArchive(source, budget);
        if (Decompressor(head) is { } decompress)
            return IsTar(source.Path, decompress) ? FromTar(source, decompress) : Single(source, decompress);
        return IsTar(source.Path, f => f) ? FromTar(source, f => f) : FromReader(source);
    }

    static Func<Stream, Stream>? Decompressor(byte[] head) => head switch
    {
        [0x1F, 0x8B, ..] => f => new CheckedGzip((FileStream)f),
        [(byte)'B', (byte)'Z', (byte)'h', ..] => f => BZip2Stream.Create(f, SharpCompress.Compressors.CompressionMode.Decompress, true, false, false),
        [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0] => f => new MultiXz((FileStream)f),
        _ => null,
    };

    abstract class Decompressed : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>GZip that notices a file cut short, which .NET's own stream takes for the end.</summary>
    sealed class CheckedGzip(FileStream file) : Decompressed
    {
        readonly GZipStream _gzip = new(file, CompressionMode.Decompress);
        long _total;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _gzip.Read(buffer, offset, count);
            _total += n;
            if (n == 0 && count > 0 && file.Length >= 18)
            {
                // The file ends with its uncompressed size (per member, if several were joined).
                var tail = new byte[4];
                file.Position = file.Length - 4;
                file.ReadExactly(tail);
                if (BitConverter.ToUInt32(tail) != (uint)_total && !tail.All(b => b == 0) && !HasMoreMembers()) throw new InvalidDataException("The compressed data is cut short.");
            }
            return n;
        }

        bool HasMoreMembers()
        {
            file.Position = 10;
            var buffer = new byte[1 << 16];
            int kept = 0, n;
            while ((n = file.Read(buffer, kept, buffer.Length - kept)) > 0)
            {
                int length = kept + n;
                for (int i = 0; i + 10 <= length; i++)
                    if (buffer[i] == 0x1F && buffer[i + 1] == 0x8B && buffer[i + 2] == 8 && (buffer[i + 3] & 0xE0) == 0 && buffer[i + 8] is 0 or 2 or 4 && buffer[i + 9] is <= 13 or 255) return true;
                kept = Math.Min(9, length);
                Array.Copy(buffer, length - kept, buffer, 0, kept);
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _gzip.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>XZ files can be several streams one after the other.</summary>
    sealed class MultiXz(FileStream file) : Decompressed
    {
        XZStream? _current;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_current == null)
                {
                    // Padding between streams is zero bytes.
                    int b;
                    while ((b = file.ReadByte()) == 0) { }
                    if (b < 0) return 0;
                    file.Position--;
                    _current = new XZStream(file);
                }
                int n = _current.Read(buffer, offset, count);
                if (n > 0 || count == 0) return n;
                _current = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Whether the (decompressed) contents start with a tar header, going by its checksum.</summary>
    static bool IsTar(string path, Func<Stream, Stream> decompress)
    {
        var header = new byte[512];
        try
        {
            using var file = File.OpenRead(path);
            using var stream = decompress(file);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or SharpCompressException) { return false; }
        if (header.AsSpan(257, 5).SequenceEqual("ustar"u8)) return true;
        if (header.All(b => b == 0)) return Path.GetExtension(path).Equals(".tar", StringComparison.OrdinalIgnoreCase); // an empty tar
        int sum = 0;
        for (int i = 0; i < header.Length; i++) sum += i is >= 148 and < 156 ? ' ' : header[i];
        var digits = Encoding.ASCII.GetString(header, 148, 8).Trim('\0', ' ');
        return digits.Length > 0 && digits.All(c => c is >= '0' and <= '7') && System.Convert.ToInt32(digits, 8) == sum;
    }

    /// <summary>A single compressed file (e.g. log.txt.gz): the archive gets that one file.</summary>
    static IEnumerable<Item> Single(Source source, Func<Stream, Stream> decompress)
    {
        var name = Path.GetFileNameWithoutExtension(source.Path);
        yield return new Item(name, ItemKind.File, File.GetLastWriteTime(source.Path), null, () =>
        {
            var file = File.OpenRead(source.Path);
            source.Fraction = () => file.CanRead ? (double)file.Position / Math.Max(1, source.Length) : 1;
            return decompress(file);
        });
    }

    static IEnumerable<Item> FromTar(Source source, Func<Stream, Stream> decompress)
    {
        using var file = File.OpenRead(source.Path);
        using var stream = decompress(file);
        source.Fraction = () => (double)file.Position / Math.Max(1, source.Length);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.SparseFile || entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(k => k.StartsWith("GNU.sparse")))
                throw new ConversionException("This archive contains sparse files, which can't be repacked.");
            var kind = entry.EntryType switch
            {
                TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => entry.Name.EndsWith('/') ? ItemKind.Directory : ItemKind.File,
                TarEntryType.Directory or TarEntryType.DirectoryList => ItemKind.Directory,
                TarEntryType.SymbolicLink => ItemKind.SymbolicLink,
                TarEntryType.HardLink => ItemKind.HardLink,
                _ => (ItemKind?)null, // devices, pipes and the like have nothing to repack
            };
            if (kind is not { } k2) continue;
            yield return new Item(entry.Name, k2, entry.ModificationTime, kind == ItemKind.File ? entry.Length : 0,
                () => entry.DataStream is { } data ? new Unclosed(data) : Stream.Null, entry.LinkName, null, entry);
        }
    }

    static ReaderOptions Options()
    {
        // Names without the UTF-8 flag are usually UTF-8 anyway, or else in the DOS code page of the PC that made them.
        Documents.TextDecoding.EnsureRegistered();
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var dos = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage is var page and > 0 ? page : 437);
        return new ReaderOptions
        {
            ArchiveEncoding = new ArchiveEncoding
            {
                CustomDecoder = (bytes, index, count, _) =>
                {
                    try { return strict.GetString(bytes, index, count); }
                    catch (DecoderFallbackException) { return dos.GetString(bytes, index, count); }
                },
            },
        };
    }

    /// <summary>ZIP, 7z and RAR, read through their tables of contents.</summary>
    static IEnumerable<Item> FromArchive(Source source, Budget budget)
    {
        using var archive = ArchiveFactory.OpenArchive(source.Path, Options());
        if (archive.Type != ArchiveType.Zip && !archive.IsComplete) throw new ConversionException("This archive is split into several parts, and not all of them are here.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.IsEncrypted) throw new ConversionException("This archive is password-protected.");
            if (entry.IsSplitAfter) throw new ConversionException("This archive is split into several parts, which can't be repacked.");
            if (!entry.IsDirectory) total += Math.Max(0, entry.Size);
        }
        source.TotalSize = total;
        budget.Expect(total);

        // Solid archives unpack in order only.
        if (archive.Type == ArchiveType.SevenZip || archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry()) yield return ToItem(reader.Entry, archive.Type, reader.OpenEntryStream);
        }
        else foreach (var entry in archive.Entries) yield return ToItem(entry, archive.Type, entry.OpenEntryStream);
    }

    /// <summary>Anything else that can be read from start to finish.</summary>
    static IEnumerable<Item> FromReader(Source source)
    {
        using var file = File.OpenRead(source.Path);
        source.Fraction = () => (double)file.Position / Math.Max(1, source.Length);
        using var reader = ReaderFactory.OpenReader(file, Options());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsEncrypted) throw new ConversionException("This archive is password-protected.");
            yield return ToItem(reader.Entry, null, reader.OpenEntryStream) with { Size = null };
        }
    }

    static Item ToItem(IEntry entry, ArchiveType? type, Func<Stream> open)
    {
        DateTimeOffset? modified = entry.LastModifiedTime is { } time && time > DateTime.MinValue.AddDays(2) ? new DateTimeOffset(time) : null;
        var name = entry.Key ?? "file";
        if (entry.IsDirectory) return new Item(name, ItemKind.Directory, modified, 0, null);
        if (!string.IsNullOrEmpty(entry.LinkTarget)) return new Item(name, ItemKind.SymbolicLink, modified, 0, null, entry.LinkTarget);
        uint? crc = type is ArchiveType.Zip or ArchiveType.SevenZip && entry.Crc != 0 ? (uint)entry.Crc : null;
        // ZIP and 7z files made on Linux or macOS keep the Unix file mode in the upper half of the attributes.
        uint mode = type is ArchiveType.Zip or ArchiveType.SevenZip && entry.Attrib is int attributes ? (uint)attributes >> 16 : 0;
        const uint TypeMask = 0xF000, LinkType = 0xA000, FileType = 0x8000;
        if ((mode & TypeMask) == LinkType && entry.Size is > 0 and <= 4096)
        {
            using var target = open();
            var bytes = new byte[entry.Size];
            target.ReadExactly(bytes);
            return new Item(name, ItemKind.SymbolicLink, modified, 0, null, Encoding.UTF8.GetString(bytes));
        }
        return new Item(name, ItemKind.File, modified, entry.Size, open, null, crc, null, (mode & TypeMask) == FileType ? (UnixFileMode)(mode & 0x1FF) : null);
    }
}

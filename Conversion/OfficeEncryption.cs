using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ConvertWheel.Conversion;

/// <summary>
/// Spots password-protected Office and OpenDocument files from their headers alone, because handing one to
/// Office would leave it waiting for a password on a screen nobody can see.
/// </summary>
internal static class OfficeEncryption
{
    public static bool IsProtected(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var magic = new byte[8];
            if (file.ReadAtLeast(magic, 8, throwOnEndOfStream: false) < 8) return false;
            if (magic[0] == 'P' && magic[1] == 'K') return ProtectedOpenDocument(file);
            return BinaryPrimitives.ReadUInt64BigEndian(magic) == 0xD0CF11E0A1B11AE1 && new Compound(file).IsProtected();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // not for us to judge: the converter will say what's wrong
        }
    }

    static bool ProtectedOpenDocument(Stream file)
    {
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.GetEntry("META-INF/manifest.xml") is not { } manifest) return false;
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var text = new char[1 << 18];
        return new string(text, 0, reader.ReadBlock(text)).Contains(":encryption-data", StringComparison.Ordinal);
    }

    /// <summary>Just enough of the OLE container (old Office files, and the wrapper around encrypted new ones).</summary>
    sealed class Compound
    {
        readonly Stream _file;
        readonly int _sector, _mini, _miniCutoff;
        readonly uint _directory, _miniFat;
        readonly List<uint> _fatSectors = [];

        public Compound(Stream file)
        {
            _file = file;
            var head = At(0, 512);
            _sector = 1 << Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(30)), 7, 16);
            _mini = 1 << Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(32)), 4, 12);
            _directory = U32(head, 48);
            _miniCutoff = (int)U32(head, 56);
            _miniFat = U32(head, 60);
            for (int i = 0; i < 109; i++) AddFat(U32(head, 76 + i * 4));
            uint difat = U32(head, 68);
            for (int n = 0; n < 4096 && IsSector(difat); n++)
            {
                var block = Sector(difat);
                for (int i = 0; i < _sector / 4 - 1; i++) AddFat(U32(block, i * 4));
                difat = U32(block, _sector - 4);
            }
            void AddFat(uint sector) { if (IsSector(sector)) _fatSectors.Add(sector); }
        }

        public bool IsProtected()
        {
            var entries = new Dictionary<string, (uint start, long size)>(StringComparer.OrdinalIgnoreCase);
            (uint start, long size) root = default;
            foreach (var block in Chain(_directory))
            for (int at = 0; at + 128 <= block.Length; at += 128)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 64));
                if (length is < 2 or > 64) continue;
                var entry = (U32(block, at + 116), (long)U32(block, at + 120));
                if (block[at + 66] == 5) root = entry;
                else entries[Encoding.Unicode.GetString(block, at, length - 2)] = entry;
            }

            // .docx, .xlsx and .pptx with a password are these two streams in an old-style container.
            if (entries.ContainsKey("EncryptedPackage") && entries.ContainsKey("EncryptionInfo")) return true;
            if (entries.TryGetValue("WordDocument", out var word))
                return Start(word, root.start, 12) is { Length: 12 } fib && (BinaryPrimitives.ReadUInt16LittleEndian(fib.AsSpan(10)) & 0x0100) != 0;
            if (entries.TryGetValue("Current User", out var user) && entries.ContainsKey("PowerPoint Document"))
                return Start(user, root.start, 16) is { Length: 16 } atom && U32(atom, 12) == 0xF3D1C4DF;
            return false;
        }

        /// <summary>The first bytes of a stream.</summary>
        byte[]? Start((uint start, long size) stream, uint miniStream, int count)
        {
            if (stream.size < count || !IsSector(stream.start)) return null;
            if (stream.size >= _miniCutoff) return Sector(stream.start)[..count];
            // Small streams live in slices of another stream.
            long offset = (long)stream.start * _mini;
            long skip = offset / _sector;
            foreach (var block in Chain(miniStream))
                if (skip-- == 0) return block.AsSpan((int)(offset % _sector), count).ToArray();
            return null;
        }

        IEnumerable<byte[]> Chain(uint sector)
        {
            // No chain is longer than the file, so a damaged one can't go round in circles.
            for (long n = _file.Length / _sector; n >= 0 && IsSector(sector); n--)
            {
                yield return Sector(sector);
                int perSector = _sector / 4, index = (int)(sector / perSector);
                if (index >= _fatSectors.Count) yield break;
                sector = U32(Sector(_fatSectors[index]), (int)(sector % perSector) * 4);
            }
        }

        static bool IsSector(uint id) => id < 0xFFFFFFFA;
        byte[] Sector(uint id) => At((id + 1L) * _sector, _sector);
        static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

        byte[] At(long offset, int count)
        {
            var data = new byte[count];
            if (offset + 1 > _file.Length) throw new InvalidDataException("Past the end of the file.");
            _file.Position = offset;
            _file.ReadAtLeast(data, count, throwOnEndOfStream: false);
            return data;
        }
    }
}

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

internal static class PackageResources
{
    const long XmlLimit = 256L * 1024 * 1024, MediaLimit = 512L * 1024 * 1024, ArchiveLimit = 1024L * 1024 * 1024;
    sealed class Budget { public long Used; }
    // Counts actual decompressed bytes across every part opened from this archive.
    static readonly ConditionalWeakTable<ZipArchive, Budget> Budgets = new();

    /// <param name="streamed">The part is read piece by piece rather than held in memory, so only the limit for the whole archive applies.</param>
    public static Stream Open(ZipArchiveEntry entry, bool media = false, bool streamed = false)
    {
        long limit = streamed ? ArchiveLimit : media ? MediaLimit : XmlLimit;
        if (entry.Length > limit) throw TooLarge();
        return new LimitedStream(entry.Open(), Budgets.GetOrCreateValue(entry.Archive), limit);
    }

    public static XDocument LoadXml(Stream stream, LoadOptions options = LoadOptions.None)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = XmlLimit,
            IgnoreWhitespace = (options & LoadOptions.PreserveWhitespace) == 0,
        });
        return XDocument.Load(reader, options);
    }

    public static XDocument ParseXml(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = XmlLimit, IgnoreWhitespace = true,
        });
        return XDocument.Load(reader);
    }

    static ConversionException TooLarge() => new("This file is too large or damaged.");

    sealed class LimitedStream(Stream inner, Budget budget, long limit) : Stream
    {
        long _used;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            lock (budget)
            {
                if (_used > limit || budget.Used > ArchiveLimit) throw TooLarge();
                long remaining = Math.Min(limit - _used, ArchiveLimit - budget.Used);
                int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining + 1)]);
                _used += read;
                budget.Used += read;
                if (_used > limit || budget.Used > ArchiveLimit) throw TooLarge();
                return read;
            }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

internal static class RasterLimits
{
    public static double Dimension(double value, double fallback) => double.IsFinite(value) && value > 0 && double.IsFinite(Units.ToDip(value)) ? value : fallback;

    public static bool TrySize(double width, double height, ref double scale, out int pixelsWide, out int pixelsHigh)
    {
        pixelsWide = pixelsHigh = 0;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || !double.IsFinite(scale) || scale <= 0)
            return false;
        scale = Math.Min(scale, Math.Min(16384 / width, 16384 / height));
        double w = width * scale, h = height * scale;
        scale *= Math.Min(1, Math.Sqrt(64_000_000 / (Math.Ceiling(w) * Math.Ceiling(h))));
        pixelsWide = Math.Max(1, (int)Math.Floor(width * scale));
        pixelsHigh = Math.Max(1, (int)Math.Floor(height * scale));
        return scale > 0 && double.IsFinite(scale);
    }
}

internal sealed class OdfRepetitions
{
    const long CellLimit = 5_000_000;
    long _cells;
    static readonly XNamespace Table = OdtReader.Table;

    public static long Count(XElement element, string name)
    {
        var value = (string?)element.Attribute(Table + name);
        if (value == null) return 1;
        return long.TryParse(value, out long count) && count > 0 ? count : 1;
    }

    static long Pending(long current, long count) => count > CellLimit - current ? CellLimit + 1 : current + count;

    public IEnumerable<(XElement source, List<XElement?> cells)> Rows(XElement table, CancellationToken ct = default)
    {
        // Empty runs are materialized only when later content needs their positions.
        long pendingRows = 0;
        foreach (var row in table.Descendants(Table + "table-row").Where(r => r.Ancestors(Table + "table").First() == table))
        {
            ct.ThrowIfCancellationRequested();
            var cells = new List<XElement?>();
            long pendingCells = 0;
            var emptyRuns = new List<(XElement cell, long count)>();
            foreach (var cell in row.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
            {
                long repeat = Count(cell, "number-columns-repeated");
                bool content = cell.Attributes().Any(a => a.Name.Namespace == OdtReader.Office && a.Name.LocalName is "value" or "string-value" or "date-value" or "time-value" or "boolean-value")
                    || cell.Descendants().Any(e => e.Name.LocalName is "image" or "object" or "table" or "frame" or "s" or "tab" or "line-break" || e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)));
                if (!content)
                {
                    pendingCells = Pending(pendingCells, repeat);
                    emptyRuns.Add((cell, repeat));
                    continue;
                }
                Check(cells.Count + Pending(pendingCells, repeat));
                foreach (var (empty, count) in emptyRuns)
                    for (long i = 0; i < count; i++) cells.Add(empty);
                emptyRuns.Clear();
                pendingCells = 0;
                for (long i = 0; i < repeat; i++) cells.Add(cell);
            }
            long rowRepeat = Count(row, "number-rows-repeated");
            if (cells.Count == 0) { pendingRows = Pending(pendingRows, rowRepeat); continue; }
            Check(pendingRows + (long)cells.Count * Math.Min(rowRepeat, CellLimit + 1));
            _cells += pendingRows + (long)cells.Count * rowRepeat;
            for (long i = 0; i < pendingRows; i++) yield return (row, []);
            pendingRows = 0;
            for (long i = 0; i < rowRepeat; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                yield return (row, cells);
            }
        }
    }

    void Check(long count)
    {
        if (count > CellLimit - _cells) throw new ConversionException("This file is too large or damaged.");
    }

    public static List<double> Widths(XElement table, int columns, Func<XElement, double> width)
    {
        var result = new List<double>();
        foreach (var col in table.Descendants(Table + "table-column").Where(c => c.Ancestors(Table + "table").First() == table))
        {
            long repeat = Math.Min(Count(col, "number-columns-repeated"), columns - result.Count);
            double value = width(col);
            for (long i = 0; i < repeat; i++) result.Add(value);
            if (result.Count == columns) break;
        }
        return result;
    }
}

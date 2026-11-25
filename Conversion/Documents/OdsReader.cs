using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Reads OpenDocument spreadsheets a row at a time; repeated blank cells and rows take no memory.</summary>
internal sealed class OdsReader
{
    readonly CancellationToken _ct;
    readonly SheetLimits _limits = new();
    /// <summary>Cell styles and data styles by name ("cell:x", "data:x", "table:x").</summary>
    readonly Dictionary<string, XElement> _styles = [];
    readonly Dictionary<string, string?> _formats = [];

    OdsReader(CancellationToken ct) => _ct = ct;

    public static List<Sheet> Read(Stream stream, bool charts, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return new OdsReader(ct).Read(zip, charts);
    }

    List<Sheet> Read(ZipArchive zip, bool charts)
    {
        var content = zip.GetEntry("content.xml") ?? throw new ConversionException("This OpenDocument spreadsheet has no content.");
        if (zip.GetEntry("META-INF/manifest.xml") is { } manifest)
        {
            using var s = PackageResources.Open(manifest);
            if (PackageResources.LoadXml(s).Descendants().Any(e => e.Name.LocalName == "encryption-data")) throw new ConversionException("This spreadsheet is password-protected.");
        }
        if (zip.GetEntry("styles.xml") is { } styles)
        {
            using var s = PackageResources.Open(styles);
            foreach (var set in PackageResources.LoadXml(s).Root!.Elements()) AddStyles(set);
        }

        var sheets = new List<Sheet>();
        using (var xml = XmlReader.Create(PackageResources.Open(content, streamed: true), new XmlReaderSettings
               { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true }))
        {
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "automatic-styles" && xml.NamespaceURI == Office.NamespaceName) AddStyles(Subtree(xml));
                else if (xml.LocalName == "table" && xml.NamespaceURI == Table.NamespaceName)
                {
                    var (sheet, frames) = ReadTable(xml, $"Sheet{sheets.Count + 1}");
                    sheets.Add(charts && frames.Count > 0 ? sheet with { Charts = SpreadsheetCharts.Ods(zip, frames) } : sheet);
                }
            }
        }
        return sheets;
    }

    /// <summary>The current element with everything inside it; the reader is left on its end.</summary>
    static XElement Subtree(XmlReader xml)
    {
        using var inner = xml.ReadSubtree();
        return XElement.Load(inner, LoadOptions.PreserveWhitespace);
    }

    // ------------------------------------------------------------------ styles

    void AddStyles(XElement set)
    {
        foreach (var e in set.Elements())
        {
            if ((string?)e.Attribute(St + "name") is not { } name) continue;
            if (e.Name.Namespace == OdfNumberStyles.Number) _styles["data:" + name] = e;
            else if (e.Name == St + "style" && (string?)e.Attribute(St + "family") is "table-cell") _styles["cell:" + name] = e;
            else if (e.Name == St + "style" && (string?)e.Attribute(St + "family") is "table") _styles["table:" + name] = e;
        }
    }

    /// <summary>The Excel number format of a cell style, null for General.</summary>
    string? Format(string? cellStyle)
    {
        if (cellStyle == null) return null;
        if (_formats.TryGetValue(cellStyle, out var known)) return known;
        string? format = null;
        var name = cellStyle;
        for (int depth = 0; depth < 10 && name != null && _styles.TryGetValue("cell:" + name, out var style); depth++)
        {
            if ((string?)style.Attribute(St + "data-style-name") is { } data)
            {
                if (_styles.TryGetValue("data:" + data, out var dataStyle)) format = OdfNumberStyles.ToExcel(dataStyle, n => _styles.GetValueOrDefault("data:" + n));
                break;
            }
            name = (string?)style.Attribute(St + "parent-style-name");
        }
        return _formats[cellStyle] = format;
    }

    // ------------------------------------------------------------------ tables

    (Sheet sheet, List<XElement> frames) ReadTable(XmlReader xml, string fallbackName)
    {
        var name = xml.GetAttribute("name", Table.NamespaceName) ?? fallbackName;
        bool hidden = xml.GetAttribute("style-name", Table.NamespaceName) is { } tableStyle && _styles.TryGetValue("table:" + tableStyle, out var ts)
            && (string?)ts.Element(St + "table-properties")?.Attribute(Table + "display") == "false";
        var rows = new List<object?[]>();
        var frames = new List<XElement>();
        var hiddenRows = new HashSet<int>();
        var hiddenColumns = new HashSet<int>();
        var columnStyles = new List<(int until, string? style)>();
        long rowIndex = 0;

        if (xml.IsEmptyElement) return (new Sheet(name, rows), frames);
        int depth = xml.Depth;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element) continue;
            if (xml.NamespaceURI != Table.NamespaceName) { if (xml.Depth == depth + 1 && !xml.IsEmptyElement) Subtree(xml); continue; }
            switch (xml.LocalName)
            {
                case "table-column":
                    int from = columnStyles.Count == 0 ? 0 : columnStyles[^1].until;
                    int until = (int)Math.Min(SheetLimits.Columns, from + Repeat(xml.GetAttribute("number-columns-repeated", Table.NamespaceName)));
                    columnStyles.Add((until, xml.GetAttribute("default-cell-style-name", Table.NamespaceName)));
                    if (xml.GetAttribute("visibility", Table.NamespaceName) is "collapse" or "filter")
                        for (int c = from; c < until; c++) hiddenColumns.Add(c);
                    break;
                case "table-row":
                    _ct.ThrowIfCancellationRequested();
                    var row = Subtree(xml);
                    frames.AddRange(row.Descendants(Draw + "frame"));
                    long repeat = Repeat((string?)row.Attribute(Table + "number-rows-repeated"));
                    var cells = Cells(row, columnStyles);
                    if ((string?)row.Attribute(Table + "visibility") is "collapse" or "filter")
                        for (long r = rowIndex; r < Math.Min(rowIndex + repeat, SheetLimits.Rows); r++) hiddenRows.Add((int)r);
                    if (cells.Length > 0)
                    {
                        // Styled rows often repeat to the end of the sheet; only rows with something in them count.
                        if (rowIndex + repeat > SheetLimits.Rows) throw SheetLimits.TooLarge();
                        _limits.Cells(cells.Length * repeat);
                        while (rows.Count < rowIndex) rows.Add([]);
                        for (long r = 0; r < repeat; r++) rows.Add(cells);
                    }
                    rowIndex += repeat;
                    break;
                case "shapes":
                    frames.AddRange(Subtree(xml).Descendants(Draw + "frame"));
                    break;
                case "table-header-rows" or "table-rows" or "table-row-group" or "table-header-columns" or "table-columns" or "table-column-group":
                    break; // only wrappers: their rows and columns come next
                default:
                    if (!xml.IsEmptyElement) Subtree(xml);
                    break;
            }
        }
        return (new Sheet(name, rows) { Hidden = hidden, HiddenRows = hiddenRows, HiddenColumns = hiddenColumns }, frames);
    }

    static long Repeat(string? value) => long.TryParse(value, out long n) && n > 0 ? Math.Min(n, int.MaxValue) : 1;

    object?[] Cells(XElement row, List<(int until, string? style)> columnStyles)
    {
        var rowStyle = (string?)row.Attribute(Table + "default-cell-style-name");
        var cells = new List<object?>();
        long column = 0; // where the next cell goes; blanks are filled in once something follows them
        int styleAt = 0;
        foreach (var tc in row.Elements())
        {
            if (tc.Name != Table + "table-cell" && tc.Name != Table + "covered-table-cell") continue;
            long repeat = Repeat((string?)tc.Attribute(Table + "number-columns-repeated"));
            if (tc.HasElements || tc.Attribute(Office + "value-type") != null)
            {
                while (styleAt < columnStyles.Count - 1 && columnStyles[styleAt].until <= column) styleAt++;
                var style = (string?)tc.Attribute(Table + "style-name") ?? rowStyle ?? (styleAt < columnStyles.Count ? columnStyles[styleAt].style : null);
                if (Value(tc, style) is { } value)
                {
                    if (column + repeat > SheetLimits.Columns) throw SheetLimits.TooLarge();
                    while (cells.Count < column) cells.Add(null);
                    for (long i = 0; i < repeat; i++) cells.Add(value);
                }
            }
            column += repeat;
        }
        return [.. cells];
    }

    // ------------------------------------------------------------------ values

    object? Value(XElement tc, string? style)
    {
        string? Attribute(string name) => (string?)tc.Attribute(Office + name);
        string? Shown() => tc.Elements(Text + "p").Any() ? string.Join("\n", tc.Elements(Text + "p").Select(PlainText)) : null;

        // LibreOffice writes errors as blank text and says so in its own attribute.
        if (tc.Attributes().Any(a => a.Name.LocalName == "value-type" && a.Name != Office + "value-type" && a.Value == "error") && Shown() is { Length: > 0 } error) return new ErrorCell(error);
        switch (Attribute("value-type"))
        {
            case "float" or "percentage" or "currency":
                if (!double.TryParse(Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) break;
                var format = Format(style);
                if (format == null && Attribute("value-type") == "percentage") format = "0.00%";
                if (format == null && Attribute("value-type") == "currency") format = Attribute("currency") is { Length: > 0 } currency ? $"#,##0.00 [${currency}]" : "#,##0.00";
                return format == null ? n : new NumberCell(n, format);
            case "date":
                if (!DateTime.TryParse(Attribute("date-value"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) break;
                // Excel's calendar starts in 1900; earlier dates stay as text.
                if (NumberFormat.ToSerial(date) is not { } serial) return Shown() ?? Attribute("date-value");
                return new NumberCell(serial, Format(style) is { } f && NumberFormat.IsDate(f) ? f : date.TimeOfDay == TimeSpan.Zero ? NumberFormat.IsoDate : NumberFormat.IsoDateTime);
            case "time":
                if (Time(Attribute("time-value")) is not { } days) break;
                return new NumberCell(days, Format(style) is { } t && NumberFormat.IsDate(t) ? t : Math.Abs(days) >= 1 ? "[h]:mm:ss" : "hh:mm:ss");
            case "boolean":
                return Attribute("boolean-value") == "true";
        }
        var text = Attribute("string-value") ?? Shown();
        if (string.IsNullOrEmpty(text)) return null;
        _limits.Text(text);
        return text;
    }

    /// <summary>"PT30H15M00S" in days.</summary>
    static double? Time(string? value)
    {
        if (value == null) return null;
        try { return XmlConvert.ToTimeSpan(value).TotalDays; }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
    }

    static string PlainText(XElement p)
    {
        var sb = new StringBuilder();
        Append(p);
        return sb.ToString();

        void Append(XElement e)
        {
            foreach (var node in e.Nodes())
            {
                if (node is XText t) sb.Append(t.Value.Replace('\n', ' '));
                else if (node is XElement { Name.LocalName: "s" } s) sb.Append(' ', Units.OdfSpaceCount((string?)s.Attribute(Text + "c")));
                else if (node is XElement { Name.LocalName: "tab" }) sb.Append('\t');
                else if (node is XElement { Name.LocalName: "line-break" }) sb.Append('\n');
                else if (node is XElement child && child.Name.LocalName is not ("annotation" or "note" or "ruby-text")) Append(child);
            }
        }
    }
}

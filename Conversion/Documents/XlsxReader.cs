using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Reads .xlsx/.xlsm sheets cell by cell, so a stray value in the last row or column costs nothing
/// and the memory used depends on the cells that are really there.
/// </summary>
internal sealed partial class XlsxReader
{
    readonly ZipArchive _zip;
    readonly CancellationToken _ct;
    readonly SheetLimits _limits = new();
    readonly List<string> _strings = [];
    readonly List<string?> _formats = []; // by cell style: the number format, null for General
    bool _date1904;

    XlsxReader(ZipArchive zip, CancellationToken ct) => (_zip, _ct) = (zip, ct);

    public static List<Sheet> Read(Stream stream, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return new XlsxReader(zip, ct).Read();
    }

    List<Sheet> Read()
    {
        var root = Relationships("").FirstOrDefault(r => r.type == "officeDocument").target ?? "xl/workbook.xml";
        var workbook = Load(root) ?? throw new ConversionException("This workbook is damaged: its contents page is missing.");
        var rels = Relationships(root);
        string? Part(string type) => rels.FirstOrDefault(r => r.type == type).target;
        _date1904 = IsTrue(Child(workbook, "workbookPr")?.Attribute("date1904")?.Value);
        ReadStyles(Part("styles"));
        ReadStrings(Part("sharedStrings"));

        var sheets = new List<Sheet>();
        foreach (var sheet in Child(workbook, "sheets")?.Elements().Where(e => e.Name.LocalName == "sheet") ?? [])
        {
            _ct.ThrowIfCancellationRequested();
            var rid = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value;
            var (_, type, target) = rels.FirstOrDefault(r => r.id == rid);
            if (target == null || type != "worksheet" || _zip.GetEntry(target) is not { } entry) continue; // chart sheets hold no cells
            sheets.Add(ReadSheet(entry, (string?)sheet.Attribute("name") ?? $"Sheet{sheets.Count + 1}") with
            {
                Hidden = (string?)sheet.Attribute("state") is "hidden" or "veryHidden",
            });
        }
        return sheets;
    }

    // ------------------------------------------------------------------ package

    XElement? Load(string part)
    {
        if (_zip.GetEntry(part) is not { } entry) return null;
        using var s = PackageResources.Open(entry);
        return PackageResources.LoadXml(s).Root;
    }

    static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    List<(string id, string type, string target)> Relationships(string part)
    {
        var dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
        var rels = Load((dir.Length > 0 ? dir + "/" : "") + $"_rels/{part[(dir.Length > 0 ? dir.Length + 1 : 0)..]}.rels");
        return rels?.Elements().Where(r => (string?)r.Attribute("TargetMode") != "External" && r.Attribute("Target") != null)
            .Select(r => ((string?)r.Attribute("Id") ?? "", ((string?)r.Attribute("Type") ?? "").Split('/')[^1], SpreadsheetCharts.Resolve(dir, (string)r.Attribute("Target")!))).ToList() ?? [];
    }

    XmlReader Open(ZipArchiveEntry entry) => XmlReader.Create(PackageResources.Open(entry, streamed: true), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true,
    });

    static bool IsTrue(string? value) => value is "1" or "true" or "on";

    void ReadStyles(string? part)
    {
        if (part == null || Load(part) is not { } styles) return;
        var custom = new Dictionary<int, string>();
        foreach (var f in Child(styles, "numFmts")?.Elements() ?? [])
            if (int.TryParse((string?)f.Attribute("numFmtId"), out int id) && (string?)f.Attribute("formatCode") is { } code) custom[id] = code;
        foreach (var xf in Child(styles, "cellXfs")?.Elements().Where(e => e.Name.LocalName == "xf") ?? [])
        {
            int.TryParse((string?)xf.Attribute("numFmtId"), out int id);
            var code = custom.GetValueOrDefault(id) ?? NumberFormat.BuiltInCode(id);
            _formats.Add(string.IsNullOrEmpty(code) || code.Equals("General", StringComparison.OrdinalIgnoreCase) ? null : code);
        }
    }

    void ReadStrings(string? part)
    {
        if (part == null || _zip.GetEntry(part) is not { } entry) return;
        using var xml = Open(entry);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "si") continue;
            if ((_strings.Count & 1023) == 0) _ct.ThrowIfCancellationRequested();
            _strings.Add(RichString(xml));
            _limits.Text(_strings[^1]);
        }
    }

    /// <summary>The text of an &lt;si&gt; or &lt;is&gt; element, joining its formatted pieces and leaving out phonetic guides.</summary>
    static string RichString(XmlReader xml)
    {
        if (xml.IsEmptyElement) return "";
        string? single = null;
        StringBuilder? joined = null;
        int depth = xml.Depth, phonetic = -1;
        bool text = false;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                if (phonetic < 0 && xml.LocalName is "rPh" or "phoneticPr") { if (!xml.IsEmptyElement) phonetic = xml.Depth; }
                text = xml.LocalName == "t" && phonetic < 0 && !xml.IsEmptyElement;
            }
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                if (xml.Depth == phonetic) phonetic = -1;
                text = false;
            }
            else if (text && xml.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
            {
                if (single == null) single = xml.Value;
                else (joined ??= new StringBuilder(single)).Append(xml.Value);
            }
        }
        return Unescape(joined?.ToString() ?? single ?? "");
    }

    /// <summary>Characters XML can't hold are stored as "_x000D_".</summary>
    static string Unescape(string text) =>
        text.Contains("_x", StringComparison.Ordinal) ? EscapeRegex().Replace(text, m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString()).Replace("\r\n", "\n") : text;

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex EscapeRegex();

    // ------------------------------------------------------------------ sheets

    Sheet ReadSheet(ZipArchiveEntry entry, string name)
    {
        var rows = new List<object?[]>();
        var hiddenRows = new HashSet<int>();
        var hiddenColumns = new HashSet<int>();
        var cells = new List<object?>();
        using var xml = Open(entry);
        int rowNumber = 0, column = 0;
        bool more = xml.Read();
        while (more)
        {
            if (xml.NodeType != XmlNodeType.Element) { more = xml.Read(); continue; }
            switch (xml.LocalName)
            {
                case "col" when IsTrue(xml.GetAttribute("hidden")) && int.TryParse(xml.GetAttribute("min"), out int min) && int.TryParse(xml.GetAttribute("max"), out int max):
                    for (int c = Math.Max(1, min); c <= Math.Min(max, SheetLimits.Columns); c++) hiddenColumns.Add(c - 1);
                    break;
                case "row":
                    _ct.ThrowIfCancellationRequested();
                    EndRow();
                    rowNumber = int.TryParse(xml.GetAttribute("r"), out int r) && r > 0 ? r : rowNumber + 1;
                    if (rowNumber > SheetLimits.Rows) throw SheetLimits.TooLarge();
                    if (IsTrue(xml.GetAttribute("hidden"))) hiddenRows.Add(rowNumber - 1);
                    column = 0;
                    break;
                case "c":
                    column = ColumnOf(xml.GetAttribute("r")) is { } at ? at + 1 : column + 1;
                    if (column > SheetLimits.Columns) throw SheetLimits.TooLarge();
                    var value = Cell(xml);
                    if (value != null)
                    {
                        while (cells.Count < column - 1) cells.Add(null);
                        if (cells.Count == column - 1) cells.Add(value);
                        else cells[column - 1] = value;
                    }
                    break;
            }
            more = xml.Read();
        }
        EndRow();
        return new Sheet(name, rows) { HiddenRows = hiddenRows, HiddenColumns = hiddenColumns };

        void EndRow()
        {
            if (cells.Count == 0) return;
            _limits.Cells(cells.Count);
            // Blank rows are only worth keeping when something follows them; rows may also come out of order.
            while (rows.Count < rowNumber - 1) rows.Add([]);
            if (rows.Count == rowNumber - 1) rows.Add([.. cells]);
            else rows[rowNumber - 1] = [.. cells];
            cells.Clear();
        }
    }

    /// <summary>Zero-based column of a reference such as "AB12".</summary>
    static int? ColumnOf(string? reference)
    {
        if (reference == null) return null;
        int column = 0, i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]) && i < 4; i++) column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? null : column - 1;
    }

    object? Cell(XmlReader xml)
    {
        var type = xml.GetAttribute("t");
        var format = int.TryParse(xml.GetAttribute("s"), out int style) && style >= 0 && style < _formats.Count ? _formats[style] : null;
        if (xml.IsEmptyElement) return null;

        string? value = null, inline = null;
        int depth = xml.Depth;
        bool inValue = false;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                inValue = xml.LocalName == "v" && xml.Depth == depth + 1;
                if (xml.LocalName == "is") inline = RichString(xml);
            }
            else if (xml.NodeType == XmlNodeType.EndElement) inValue = false;
            else if (inValue && xml.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
                value = value == null ? xml.Value : value + xml.Value;
        }

        switch (type)
        {
            case "s":
                return int.TryParse(value, out int index) && index >= 0 && index < _strings.Count ? Text(_strings[index]) : null;
            case "inlineStr":
                return Text(inline ?? value);
            case "str":
                return Text(value == null ? null : Unescape(value));
            case "b":
                return value == null ? null : IsTrue(value.Trim());
            case "e":
                return value == null ? null : new ErrorCell(value);
            case "d":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) return Text(value);
                return NumberFormat.ToSerial(date) is { } serial
                    ? new NumberCell(serial, NumberFormat.IsDate(format) ? format! : date.TimeOfDay == TimeSpan.Zero ? NumberFormat.IsoDate : NumberFormat.IsoDateTime) : Text(value);
            default:
                if (string.IsNullOrWhiteSpace(value)) return inline == null ? null : Text(inline);
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return Text(value);
                if (format == null) return number;
                // Workbooks from old Macs count days from 1904.
                if (_date1904 && NumberFormat.IsDate(format) && NumberFormat.DateParts(format).date) number += 1462;
                return new NumberCell(number, format);
        }

        object? Text(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return NumberFormat.FormatText(text, format);
        }
    }
}

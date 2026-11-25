using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

/// <summary>What the workbook writers share.</summary>
internal static class SheetParts
{
    /// <summary>Splits sheets with more rows than a workbook allows, and refuses ones that are too wide.</summary>
    public static List<Sheet> Fit(List<Sheet> sheets, string format)
    {
        var result = new List<Sheet>();
        foreach (var sheet in sheets)
        {
            if (sheet.ColumnCount > SheetLimits.Columns)
                throw new ConversionException($"This table has {sheet.ColumnCount:N0} columns; {format} files hold at most {SheetLimits.Columns:N0}.");
            if (sheet.Rows.Count <= SheetLimits.Rows) { result.Add(sheet); continue; }
            for (int at = 0; at < sheet.Rows.Count; at += SheetLimits.Rows)
                result.Add(sheet with { Rows = sheet.Rows.GetRange(at, Math.Min(SheetLimits.Rows, sheet.Rows.Count - at)), HiddenRows = [], Charts = at == 0 ? sheet.Charts : [] });
        }
        // A workbook must have something to show.
        if (result.All(s => s.Hidden)) result[0] = result[0] with { Hidden = false };
        return result;
    }

    /// <summary>The sheet's values ready for typed cells: delimited text gets its numbers and dates here.</summary>
    public static Func<object?, int, object?> Typed(Sheet sheet, CancellationToken ct)
    {
        if (sheet.Delimiter == null) return (v, _) => v;
        var types = DelimitedText.Types(sheet, ct);
        return (v, c) => v is string s && c < types.Length ? types[c](s) : v;
    }

    /// <summary>Text as a cell can hold it.</summary>
    public static string CellText(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length <= SheetLimits.CellText) return text;
        Log.Write($"A cell's text was shortened from {text.Length:N0} characters: spreadsheets hold at most {SheetLimits.CellText:N0} in one cell.");
        int end = SheetLimits.CellText - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    /// <summary>Approximate column widths in characters, from the longest value in each column.</summary>
    public static List<double> ColumnWidths(Sheet sheet, CancellationToken ct)
    {
        var longest = new int[sheet.ColumnCount];
        foreach (var row in sheet.Rows.Take(2000))
        {
            ct.ThrowIfCancellationRequested();
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c] is null) continue;
                foreach (var line in Spreadsheets.Display(row[c]).AsSpan().EnumerateLines()) longest[c] = Math.Max(longest[c], line.Length);
            }
        }
        return longest.Select(n => (double)Math.Clamp(n + 2, 6, 60)).ToList();
    }

    public static List<string> UniqueNames(List<Sheet> sheets, int maxLength, string invalid)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var sheet in sheets)
        {
            var name = new string(SafeXml.Text(sheet.Name).Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('\'');
            // Excel keeps "History" for itself.
            if (name.Length == 0 || name.Equals("History", StringComparison.OrdinalIgnoreCase)) name = name.Length == 0 ? $"Sheet{names.Count + 1}" : name + "_";
            name = Cut(name, maxLength);
            var candidate = name;
            for (int i = 2; !used.Add(candidate); i++)
            {
                var suffix = $" ({i})";
                candidate = Cut(name, maxLength - suffix.Length) + suffix;
            }
            names.Add(candidate);
        }
        return names;

        static string Cut(string name, int length) =>
            name.Length <= length ? name : name[..(char.IsHighSurrogate(name[length - 1]) ? length - 1 : length)].TrimEnd(' ', '\'');
    }
}

/// <summary>Writes .xlsx workbooks straight to disk, row by row.</summary>
internal sealed partial class XlsxWriter
{
    const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Main = S, Rel = R;
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    static readonly XNamespace Dml = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    readonly ZipArchive _zip;
    readonly CancellationToken _ct;
    /// <summary>Number formats in use and the cell style each one got; style 0 is General.</summary>
    readonly List<string> _formats = [];
    readonly Dictionary<string, int> _styles = [];

    XlsxWriter(ZipArchive zip, CancellationToken ct) => (_zip, _ct) = (zip, ct);

    public static void Write(List<Sheet> sheets, string path, CancellationToken ct)
    {
        sheets = SheetParts.Fit(sheets, "XLSX");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        new XlsxWriter(zip, ct).Write(sheets);
    }

    void Write(List<Sheet> sheets)
    {
        var names = SheetParts.UniqueNames(sheets, 31, "[]:*?/\\");
        var types = new XElement(CT + "Types",
            new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        foreach (var kind in sheets.SelectMany(s => s.Charts).GroupBy(Extension))
            types.Add(new XElement(CT + "Default", new XAttribute("Extension", kind.Key), new XAttribute("ContentType", Spreadsheets.MediaType(kind.First()))));
        void Override(string part, string type) =>
            types.Add(new XElement(CT + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.{type}+xml")));
        Override("/xl/workbook.xml", "spreadsheetml.sheet.main");
        Override("/xl/styles.xml", "spreadsheetml.styles");
        var workbookRels = new XElement(PR + "Relationships", Relationship("rIdStyles", "styles", "styles.xml"));
        var sheetList = new XElement(Main + "sheets");

        for (int i = 0; i < sheets.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            int n = i + 1;
            Override($"/xl/worksheets/sheet{n}.xml", "spreadsheetml.worksheet");
            workbookRels.Add(Relationship($"rId{n}", "worksheet", $"worksheets/sheet{n}.xml"));
            sheetList.Add(new XElement(Main + "sheet", new XAttribute("name", names[i]), new XAttribute("sheetId", n),
                sheets[i].Hidden ? new XAttribute("state", "hidden") : null, new XAttribute(Rel + "id", $"rId{n}")));
            WriteSheet(sheets[i], n);
            if (sheets[i].Charts.Count == 0) continue;
            WriteDrawing(sheets[i], n);
            Override($"/xl/drawings/drawing{n}.xml", "drawing");
        }

        Put("[Content_Types].xml", types);
        Put("_rels/.rels", new XElement(PR + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
        var workbook = new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", R));
        int active = sheets.FindIndex(s => !s.Hidden);
        if (active > 0) workbook.Add(new XElement(Main + "bookViews", new XElement(Main + "workbookView", new XAttribute("activeTab", active))));
        workbook.Add(sheetList);
        Put("xl/workbook.xml", workbook);
        Put("xl/_rels/workbook.xml.rels", workbookRels);
        Put("xl/styles.xml", Styles());
    }

    static XElement Relationship(string id, string type, string target) =>
        new(PR + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelBase + type), new XAttribute("Target", target));

    static string Extension(DImage image) => image.Ext.ToLowerInvariant() switch { "jpg" or "jpeg" => "jpeg", "gif" => "gif", "bmp" => "bmp", _ => "png" };

    // ------------------------------------------------------------------ sheets

    void WriteSheet(Sheet sheet, int n)
    {
        var typed = SheetParts.Typed(sheet, _ct);
        using var stream = _zip.CreateEntry($"xl/worksheets/sheet{n}.xml", CompressionLevel.Optimal).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", S);
        xml.WriteAttributeString("xmlns", "r", null, R);

        var widths = SheetParts.ColumnWidths(sheet, _ct);
        if (widths.Count > 0)
        {
            xml.WriteStartElement("cols", S);
            for (int c = 0; c < widths.Count; c++)
            {
                xml.WriteStartElement("col", S);
                xml.WriteAttributeString("min", Text(c + 1));
                xml.WriteAttributeString("max", Text(c + 1));
                xml.WriteAttributeString("width", widths[c].ToString("0.##", CultureInfo.InvariantCulture));
                if (sheet.HiddenColumns.Contains(c)) xml.WriteAttributeString("hidden", "1");
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData", S);
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            if ((r & 63) == 0) _ct.ThrowIfCancellationRequested();
            var row = sheet.Rows[r];
            bool hidden = sheet.HiddenRows.Contains(r);
            if (row.Length == 0 && !hidden) continue;
            var number = Text(r + 1);
            xml.WriteStartElement("row", S);
            xml.WriteAttributeString("r", number);
            if (hidden) xml.WriteAttributeString("hidden", "1");
            for (int c = 0; c < row.Length; c++)
                if (typed(row[c], c) is { } value and not string { Length: 0 }) WriteCell(xml, value, ColumnName(c) + number);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        if (sheet.Charts.Count > 0)
        {
            xml.WriteStartElement("drawing", S);
            xml.WriteAttributeString("id", R, "rIdDrawing");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    static string Text(int n) => n.ToString(CultureInfo.InvariantCulture);

    void WriteCell(XmlWriter xml, object value, string reference)
    {
        if (value is long or int or decimal)
        {
            // Excel keeps 15 digits; longer numbers are IDs that must stay whole.
            var digits = Spreadsheets.Display(value);
            value = digits.Count(char.IsAsciiDigit) <= 15 ? System.Convert.ToDouble(value, CultureInfo.InvariantCulture) : digits;
        }
        if (value is DateTime date) value = NumberFormat.ToSerial(date) is { } serial ? new NumberCell(serial, date.TimeOfDay == TimeSpan.Zero ? NumberFormat.IsoDate : NumberFormat.IsoDateTime) : Spreadsheets.Display(date);
        if (value is double.NaN or double.PositiveInfinity or double.NegativeInfinity || value is NumberCell n && !double.IsFinite(n.Value)) value = Spreadsheets.Display(value);

        xml.WriteStartElement("c", S);
        xml.WriteAttributeString("r", reference);
        switch (value)
        {
            case double d:
                Value(Number(d));
                break;
            case NumberCell number:
                if (Style(number.Format) is > 0 and var style) xml.WriteAttributeString("s", Text(style));
                Value(Number(number.Value));
                break;
            case bool b:
                xml.WriteAttributeString("t", "b");
                Value(b ? "1" : "0");
                break;
            case ErrorCell { IsExcel: true } error:
                xml.WriteAttributeString("t", "e");
                Value(error.Code);
                break;
            default:
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteStartElement("is", S);
                xml.WriteStartElement("t", S);
                xml.WriteAttributeString("xml", "space", null, "preserve");
                // "_x0041_" in real text would read back as "A".
                xml.WriteString(EscapeRegex().Replace(SafeXml.Text(SheetParts.CellText(Spreadsheets.Display(value))), "_x005F$0"));
                xml.WriteEndElement();
                xml.WriteEndElement();
                break;
        }
        xml.WriteEndElement();

        void Value(string text) => xml.WriteElementString("v", S, text);
        static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("_(?=x[0-9A-Fa-f]{4}_)")]
    private static partial Regex EscapeRegex();

    int Style(string format)
    {
        if (_styles.TryGetValue(format, out int style)) return style;
        // Excel allows 255 characters in a format and gets slow with thousands of them.
        style = format.Length is 0 or > 255 || _formats.Count >= 3000 ? 0 : _formats.Count + 1;
        if (style > 0) _formats.Add(format);
        return _styles[format] = style;
    }

    XElement Styles()
    {
        static int BuiltIn(string format) => format switch { NumberFormat.SystemDate => 14, NumberFormat.SystemDateTime => 22, _ => -1 };
        var custom = _formats.Where(f => BuiltIn(f) < 0).ToList();
        XElement Xf(int id) => new(Main + "xf", new XAttribute("numFmtId", id), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0),
            new XAttribute("xfId", 0), id > 0 ? new XAttribute("applyNumberFormat", 1) : null);
        return new XElement(Main + "styleSheet",
            custom.Count == 0 ? null : new XElement(Main + "numFmts", new XAttribute("count", custom.Count),
                custom.Select((f, i) => new XElement(Main + "numFmt", new XAttribute("numFmtId", 164 + i), new XAttribute("formatCode", f)))),
            new XElement(Main + "fonts", new XAttribute("count", 1), new XElement(Main + "font", new XElement(Main + "sz", new XAttribute("val", 11)), new XElement(Main + "name", new XAttribute("val", "Calibri")))),
            new XElement(Main + "fills", new XAttribute("count", 2),
                new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125")))),
            new XElement(Main + "borders", new XAttribute("count", 1), new XElement(Main + "border")),
            new XElement(Main + "cellStyleXfs", new XAttribute("count", 1), new XElement(Main + "xf")),
            new XElement(Main + "cellXfs", new XAttribute("count", _formats.Count + 1), Xf(0), _formats.Select(f => Xf(BuiltIn(f) is >= 0 and var id ? id : 164 + custom.IndexOf(f)))));
    }

    static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    // ------------------------------------------------------------------ charts

    /// <summary>Charts come along as pictures stacked below the data.</summary>
    void WriteDrawing(Sheet sheet, int n)
    {
        Put($"xl/worksheets/_rels/sheet{n}.xml.rels", new XElement(PR + "Relationships", Relationship("rIdDrawing", "drawing", $"../drawings/drawing{n}.xml")));
        var drawing = new XElement(Xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", Xdr), new XAttribute(XNamespace.Xmlns + "a", Dml), new XAttribute(XNamespace.Xmlns + "r", Rel));
        var rels = new XElement(PR + "Relationships");
        int row = Math.Min(sheet.Rows.Count + 1, SheetLimits.Rows - 1);
        for (int k = 0; k < sheet.Charts.Count; k++)
        {
            var chart = sheet.Charts[k];
            var media = $"chart{n}_{k + 1}.{Extension(chart)}";
            using (var s = _zip.CreateEntry($"xl/media/{media}", CompressionLevel.NoCompression).Open()) s.Write(chart.Data);
            rels.Add(Relationship($"rIdImg{k + 1}", "image", $"../media/{media}"));
            long cx = (long)(chart.Width * 12700), cy = (long)(chart.Height * 12700);
            drawing.Add(new XElement(Xdr + "oneCellAnchor",
                new XElement(Xdr + "from", new XElement(Xdr + "col", 0), new XElement(Xdr + "colOff", 0), new XElement(Xdr + "row", row), new XElement(Xdr + "rowOff", 0)),
                new XElement(Xdr + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(Xdr + "pic",
                    new XElement(Xdr + "nvPicPr", new XElement(Xdr + "cNvPr", new XAttribute("id", k + 2), new XAttribute("name", $"Chart {k + 1}")), new XElement(Xdr + "cNvPicPr")),
                    new XElement(Xdr + "blipFill", new XElement(Dml + "blip", new XAttribute(Rel + "embed", $"rIdImg{k + 1}")), new XElement(Dml + "stretch", new XElement(Dml + "fillRect"))),
                    new XElement(Xdr + "spPr",
                        new XElement(Dml + "xfrm", new XElement(Dml + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(Dml + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                        new XElement(Dml + "prstGeom", new XAttribute("prst", "rect"), new XElement(Dml + "avLst")))),
                new XElement(Xdr + "clientData")));
            row = Math.Min(row + (int)Math.Ceiling(chart.Height / 15) + 1, SheetLimits.Rows - 1);
        }
        Put($"xl/drawings/drawing{n}.xml", drawing);
        Put($"xl/drawings/_rels/drawing{n}.xml.rels", rels);
    }

    void Put(string name, XElement root)
    {
        SafeXml.Clean(root);
        using var stream = _zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(stream, SaveOptions.DisableFormatting);
    }
}

/// <summary>Writes OpenDocument spreadsheets straight to disk, row by row.</summary>
internal sealed class OdsWriter
{
    readonly CancellationToken _ct;
    readonly Dictionary<string, string?> _styles = [];
    readonly List<XElement> _automatic = [];

    OdsWriter(CancellationToken ct) => _ct = ct;

    public static void Write(List<Sheet> sheets, string path, CancellationToken ct)
    {
        sheets = SheetParts.Fit(sheets, "ODS");
        new OdsWriter(ct).Save(sheets, path);
    }

    void Save(List<Sheet> sheets, string path)
    {
        const string type = "application/vnd.oasis.opendocument.spreadsheet";
        XNamespace manifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var names = SheetParts.UniqueNames(sheets, 100, "[]*?:/\\'");
        var pictures = new List<(string name, DImage image)>();

        // Styles come first in the file, so they're gathered before any rows are written.
        var typed = sheets.Select(s => SheetParts.Typed(s, _ct)).ToList();
        var columnStyles = new List<string[]>();
        for (int i = 0; i < sheets.Count; i++)
        {
            var widths = SheetParts.ColumnWidths(sheets[i], _ct);
            columnStyles.Add(widths.Select((w, c) => ColumnStyle(w)).ToArray());
            foreach (var row in sheets[i].Rows)
            {
                _ct.ThrowIfCancellationRequested();
                for (int c = 0; c < row.Length; c++)
                    if (Ready(typed[i](row[c], c)) is NumberCell n) CellStyle(n.Format);
            }
        }
        _automatic.Add(new XElement(St + "style", new XAttribute(St + "name", "hiddenTable"), new XAttribute(St + "family", "table"),
            new XElement(St + "table-properties", new XAttribute(Table + "display", "false"))));

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        // The mimetype entry must come first and be stored uncompressed.
        using (var mime = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false))) mime.Write(type);
        using (var stream = zip.CreateEntry("content.xml", CompressionLevel.Optimal).Open())
        using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("office", "document-content", Office.NamespaceName);
            foreach (var a in OdfPackage.Namespaces().Append(new XAttribute(XNamespace.Xmlns + "number", OdfNumberStyles.Number)))
            {
                if (a.IsNamespaceDeclaration) xml.WriteAttributeString("xmlns", a.Name.LocalName, null, a.Value);
                else xml.WriteAttributeString(a.Name.LocalName, a.Name.NamespaceName, a.Value);
            }
            var automatic = new XElement(Office + "automatic-styles", _automatic);
            SafeXml.Clean(automatic);
            automatic.WriteTo(xml);
            xml.WriteStartElement("body", Office.NamespaceName);
            xml.WriteStartElement("spreadsheet", Office.NamespaceName);
            for (int i = 0; i < sheets.Count; i++) WriteTable(xml, sheets[i], i, names[i], columnStyles[i], typed[i], pictures);
            xml.WriteEndDocument();
        }
        Put("styles.xml", new XElement(Office + "document-styles", OdfPackage.Namespaces(), new XElement(Office + "styles")));
        foreach (var (name, image) in pictures)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            s.Write(image.Data);
        }
        XElement Entry(string fullPath, string media) => new(manifestNs + "file-entry", new XAttribute(manifestNs + "full-path", fullPath), new XAttribute(manifestNs + "media-type", media));
        Put("META-INF/manifest.xml", new XElement(manifestNs + "manifest", new XAttribute(XNamespace.Xmlns + "manifest", manifestNs), new XAttribute(manifestNs + "version", "1.3"),
            Entry("/", type), Entry("content.xml", "text/xml"), Entry("styles.xml", "text/xml"), pictures.Select(p => Entry(p.name, Spreadsheets.MediaType(p.image)))));

        void Put(string name, XElement root)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(stream, SaveOptions.DisableFormatting);
        }
    }

    string ColumnStyle(double width)
    {
        var name = "co" + width.ToString("0", CultureInfo.InvariantCulture);
        if (_styles.TryAdd(name, name))
            _automatic.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "table-column"),
                new XElement(St + "table-column-properties", new XAttribute(St + "column-width", OdfPackage.Len(width * 5.6)))));
        return name;
    }

    /// <summary>The cell style for a number format, null for General.</summary>
    string? CellStyle(string format)
    {
        var key = "format:" + format;
        if (_styles.TryGetValue(key, out var known)) return known;
        var data = OdfNumberStyles.ToOdf(Plain(format), $"N{_styles.Count + 100}");
        if (data.Count == 0) return _styles[key] = null;
        _automatic.AddRange(data);
        var name = $"ce{_styles.Count + 1}";
        _automatic.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "table-cell"),
            new XAttribute(St + "data-style-name", (string)data[^1].Attribute(St + "name")!)));
        return _styles[key] = name;
    }

    /// <summary>Excel's system dates mean nothing elsewhere; ISO dates are understood everywhere.</summary>
    static string Plain(string format) => format switch { NumberFormat.SystemDate => NumberFormat.IsoDate, NumberFormat.SystemDateTime => NumberFormat.IsoDateTime, _ => format };

    /// <summary>The value as one of the kinds a cell can hold.</summary>
    static object? Ready(object? value) => value switch
    {
        long or int or decimal => Spreadsheets.Display(value) is var digits && digits.Count(char.IsAsciiDigit) <= 15 ? System.Convert.ToDouble(value, CultureInfo.InvariantCulture) : digits,
        DateTime date => NumberFormat.ToSerial(date) is { } serial ? new NumberCell(serial, date.TimeOfDay == TimeSpan.Zero ? NumberFormat.IsoDate : NumberFormat.IsoDateTime) : Spreadsheets.Display(date),
        double d when !double.IsFinite(d) => Spreadsheets.Display(d),
        NumberCell n when !double.IsFinite(n.Value) => Spreadsheets.Display(n),
        NumberCell n when Plain(n.Format) != n.Format => new NumberCell(n.Value, Plain(n.Format)),
        string { Length: 0 } => null,
        _ => value,
    };

    void WriteTable(XmlWriter xml, Sheet sheet, int index, string name, string[] columns, Func<object?, int, object?> typed, List<(string name, DImage image)> pictures)
    {
        var ns = Table.NamespaceName;
        void Start(string element) => xml.WriteStartElement("table", element, ns);
        void Attribute(string attribute, string value) => xml.WriteAttributeString("table", attribute, ns, value);

        Start("table");
        Attribute("name", name);
        if (sheet.Hidden) Attribute("style-name", "hiddenTable");
        if (sheet.Charts.Count > 0)
        {
            // Charts come along as pictures stacked below the data (shapes go before the columns).
            Start("shapes");
            double y = (sheet.Rows.Count + 1) * 12.8;
            for (int k = 0; k < sheet.Charts.Count; k++)
            {
                var chart = sheet.Charts[k];
                var ext = Spreadsheets.MediaType(chart).Split('/')[1];
                var picture = $"Pictures/chart{index + 1}_{k + 1}.{ext}";
                pictures.Add((picture, chart));
                new XElement(Draw + "frame", new XAttribute(Draw + "name", $"Chart {k + 1}"),
                    new XAttribute(Svg + "x", "0cm"), new XAttribute(Svg + "y", OdfPackage.Len(y)),
                    new XAttribute(Svg + "width", OdfPackage.Len(chart.Width)), new XAttribute(Svg + "height", OdfPackage.Len(chart.Height)),
                    new XElement(Draw + "image", new XAttribute(XLink + "href", picture), new XAttribute(XLink + "type", "simple"),
                        new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad"))).WriteTo(xml);
                y += chart.Height + 12;
            }
            xml.WriteEndElement();
        }
        for (int c = 0; c < columns.Length; c++)
        {
            Start("table-column");
            Attribute("style-name", columns[c]);
            if (sheet.HiddenColumns.Contains(c)) Attribute("visibility", "collapse");
            xml.WriteEndElement();
        }
        if (columns.Length == 0) { Start("table-column"); xml.WriteEndElement(); }

        int blank = 0;
        void FlushBlankRows()
        {
            if (blank == 0) return;
            Start("table-row");
            if (blank > 1) Attribute("number-rows-repeated", blank.ToString(CultureInfo.InvariantCulture));
            Start("table-cell");
            xml.WriteEndElement();
            xml.WriteEndElement();
            blank = 0;
        }
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            if ((r & 63) == 0) _ct.ThrowIfCancellationRequested();
            var row = sheet.Rows[r];
            bool hidden = sheet.HiddenRows.Contains(r);
            if (row.Length == 0 && !hidden) { blank++; continue; }
            FlushBlankRows();
            Start("table-row");
            if (hidden) Attribute("visibility", "collapse");
            int gap = 0;
            for (int c = 0; c < row.Length; c++)
            {
                var value = Ready(typed(row[c], c));
                if (value == null) { gap++; continue; }
                if (gap > 0)
                {
                    Start("table-cell");
                    if (gap > 1) Attribute("number-columns-repeated", gap.ToString(CultureInfo.InvariantCulture));
                    xml.WriteEndElement();
                    gap = 0;
                }
                WriteCell(xml, value);
            }
            if (row.Length == 0 || gap == row.Length) { Start("table-cell"); xml.WriteEndElement(); }
            xml.WriteEndElement();
        }
        FlushBlankRows();
        if (sheet.Rows.Count == 0) { Start("table-row"); Start("table-cell"); xml.WriteEndElement(); xml.WriteEndElement(); }
        xml.WriteEndElement();
    }

    void WriteCell(XmlWriter xml, object value)
    {
        xml.WriteStartElement("table", "table-cell", Table.NamespaceName);
        void Type(string type) => xml.WriteAttributeString("office", "value-type", Office.NamespaceName, type);
        void Typed(string attribute, string text) => xml.WriteAttributeString("office", attribute, Office.NamespaceName, text);
        void Paragraph(string text)
        {
            xml.WriteStartElement("text", "p", Text.NamespaceName);
            foreach (var node in OdtWriter.TextNodes(text))
            {
                if (node is XElement e) e.WriteTo(xml);
                else xml.WriteString(node.ToString());
            }
            xml.WriteEndElement();
        }

        switch (value)
        {
            case double d:
                Type("float");
                Typed("value", d.ToString("R", CultureInfo.InvariantCulture));
                Paragraph(Spreadsheets.Display(d));
                break;
            case NumberCell n:
                if (CellStyle(n.Format) is { } style) xml.WriteAttributeString("table", "style-name", Table.NamespaceName, style);
                var (date, time, elapsed) = NumberFormat.IsDate(n.Format) ? NumberFormat.DateParts(n.Format) : default;
                if (date && n.Value is >= 1 and < NumberFormat.MaxSerial)
                {
                    var moment = NumberFormat.ToDate(n.Value);
                    Type("date");
                    Typed("date-value", moment.ToString(moment.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : moment.Millisecond == 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                }
                else if (time && !date && Math.Abs(n.Value) < 1e6)
                {
                    var span = TimeSpan.FromMilliseconds(Math.Round(Math.Abs(n.Value) * 86400000));
                    Type("time");
                    Typed("time-value", $"{(n.Value < 0 ? "-" : "")}PT{(long)span.TotalHours:00}H{span.Minutes:00}M{span.Seconds:00}{(span.Milliseconds > 0 ? $".{span.Milliseconds:000}" : "")}S");
                }
                else
                {
                    Type(n.Format.Contains('%') && NumberFormat.Sections(n.Format)[0].Has(NumberFormat.Part.Percent) ? "percentage" : "float");
                    Typed("value", n.Value.ToString("R", CultureInfo.InvariantCulture));
                }
                Paragraph(Spreadsheets.Display(n));
                break;
            case bool b:
                Type("boolean");
                Typed("boolean-value", b ? "true" : "false");
                Paragraph(Spreadsheets.Display(b));
                break;
            default:
                Type("string");
                foreach (var line in SheetParts.CellText(Spreadsheets.Display(value)).Split('\n')) Paragraph(line);
                break;
        }
        xml.WriteEndElement();
    }
}

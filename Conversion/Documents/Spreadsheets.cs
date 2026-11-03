using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using ExcelDataReader;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Cell values are string, double, DateTime, bool or null.</summary>
internal sealed record Sheet(string Name, List<object?[]> Rows)
{
    /// <summary>The sheet's charts, already drawn (for PDF and HTML output).</summary>
    public List<DImage> Charts { get; init; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);
}

internal static class Spreadsheets
{
    // ------------------------------------------------------------------ reading

    public static List<Sheet> Read(string path, CancellationToken ct = default) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" or ".tsv" => [ReadCsv(path, ct)],
        ".ods" => ReadOds(path, ct),
        _ => ReadExcel(path, ct),
    };

    static Sheet ReadCsv(string path, CancellationToken ct)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(path));
        char delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(text);
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var field = new StringBuilder();
        bool quoted = false, rowStarted = false;

        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            rowStarted = true;
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) EndField();
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndField();
                rows.Add([.. row]);
                row.Clear();
                rowStarted = false;
            }
            else field.Append(c);
        }
        if (rowStarted)
        {
            EndField();
            rows.Add([.. row]);
        }
        return new Sheet(Path.GetFileNameWithoutExtension(path), rows);

        void EndField()
        {
            // Many exporters quote every field, so quoted values get the same number/date detection.
            row.Add(Infer(field.ToString(), delimiter));
            field.Clear();
        }
    }

    static char DetectDelimiter(string text)
    {
        var sample = string.Join('\n', text.Split('\n').Take(20));
        char best = ',';
        int bestScore = 0;
        foreach (var candidate in new[] { ',', ';', '\t', '|' })
        {
            int score = 0;
            bool quoted = false;
            foreach (var c in sample)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == candidate) score++;
            }
            if (score > bestScore) { best = candidate; bestScore = score; }
        }
        return best;
    }

    /// <summary>Numbers become numbers (except ones like "007" or phone numbers), ISO dates become dates.</summary>
    static object? Infer(string value, char delimiter)
    {
        if (value.Length == 0) return null;
        var trimmed = value.Trim();
        bool leadingZero = trimmed.Length > 1 && trimmed[0] == '0' && char.IsDigit(trimmed[1]);
        if (!leadingZero && trimmed.Length < 16)
        {
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && trimmed.All(ch => !char.IsLetter(ch) || ch is 'e' or 'E'))
                return n;
            // Semicolon CSVs come from locales that use a decimal comma.
            if (delimiter == ';' && double.TryParse(trimmed, NumberStyles.Float, new CultureInfo("de-DE"), out n) && !trimmed.Contains('.'))
                return n;
        }
        if (trimmed.Length is 10 or 19 && DateTime.TryParseExact(trimmed, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return false;
        return value;
    }

    static List<Sheet> ReadExcel(string path, CancellationToken ct)
    {
        TextDecoding.EnsureRegistered();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm")
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    using var part = PackageResources.Open(entry, media: !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase));
                    part.CopyTo(Stream.Null);
                }
            stream.Position = 0;
        }
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new List<Sheet>();
        do
        {
            if (reader.VisibleState is "hidden" or "veryhidden") continue;
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++)
                {
                    var v = reader.GetValue(i);
                    row[i] = v switch
                    {
                        null or DBNull => null,
                        int or long or float or decimal or short => Convert.ToDouble(v, CultureInfo.InvariantCulture),
                        TimeSpan ts => ts.ToString(),
                        _ => v,
                    };
                }
                rows.Add(row);
            }
            sheets.Add(new Sheet(reader.Name, Trim(rows)));
        } while (reader.NextResult());
        if (Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm")
        {
            var charts = SpreadsheetCharts.Xlsx(path);
            sheets = sheets.Select(s => charts.TryGetValue(s.Name, out var c) ? s with { Charts = c } : s).ToList();
        }
        return sheets.Count > 0 ? sheets : [new Sheet("Sheet1", [])];
    }

    static List<Sheet> ReadOds(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new ConversionException("This OpenDocument spreadsheet has no content.");
        XElement root;
        using (var stream = PackageResources.Open(entry)) root = PackageResources.LoadXml(stream).Root!;

        var sheets = new List<Sheet>();
        var repeats = new OdfRepetitions();
        foreach (var table in root.Descendants(Table + "table"))
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<object?[]>();
            foreach (var (_, cells) in repeats.Rows(table, ct))
                rows.Add(cells.Select(tc => tc == null ? null : OdsValue(tc)).ToArray());
            sheets.Add(new Sheet((string?)table.Attribute(Table + "name") ?? $"Sheet{sheets.Count + 1}", rows));
        }
        var odsCharts = SpreadsheetCharts.Ods(zip, root);
        sheets = sheets.Select(s => odsCharts.TryGetValue(s.Name, out var c) ? s with { Charts = c } : s).ToList();
        return sheets.Count > 0 ? sheets : [new Sheet("Sheet1", [])];
    }

    static object? OdsValue(XElement tc)
    {
        var type = (string?)tc.Attribute(Office + "value-type");
        switch (type)
        {
            case "float" or "percentage" or "currency":
                if (double.TryParse((string?)tc.Attribute(Office + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
                break;
            case "date":
                if (DateTime.TryParse((string?)tc.Attribute(Office + "date-value"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
                break;
            case "boolean":
                return (string?)tc.Attribute(Office + "boolean-value") == "true";
        }
        if ((string?)tc.Attribute(Office + "string-value") is { } text) return text;
        var paragraphs = tc.Elements(Text + "p").Select(p => OdsText(p)).ToList();
        return paragraphs.Count == 0 ? null : string.Join("\n", paragraphs);
    }

    static string OdsText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.DescendantNodes())
        {
            if (node is XText t) sb.Append(t.Value);
            else if (node is XElement { Name.LocalName: "s" } s) sb.Append(' ', Units.OdfSpaceCount((string?)s.Attribute(Text + "c")));
            else if (node is XElement { Name.LocalName: "tab" }) sb.Append('\t');
            else if (node is XElement { Name.LocalName: "line-break" }) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Drops trailing empty rows and columns.</summary>

    static List<object?[]> Trim(List<object?[]> rows)
    {
        static bool Empty(object? v) => v is null || (v is string s && s.Length == 0);
        int lastRow = rows.FindLastIndex(r => r.Any(v => !Empty(v)));
        rows = rows.Take(lastRow + 1).ToList();
        int width = rows.Count == 0 ? 0 : rows.Max(r => Array.FindLastIndex(r, v => !Empty(v)) + 1);
        return rows.Select(r => r.Length > width ? r[..width] : r).ToList();
    }

    // ------------------------------------------------------------------ writing

    public static void Write(List<Sheet> sheets, string path, string ext, string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (ext)
        {
            case "csv": WriteCsv(sheets[0], path, ',', ct); break;
            case "tsv": WriteCsv(sheets[0], path, '\t', ct); break;
            case "json": DataFiles.FromSheets(sheets, path); break;
            case "xlsx": WriteXlsx(sheets, path, ct); break;
            case "ods": WriteOds(sheets, path, ct); break;
            case "html": WriteHtml(sheets, path, title, ct); break;
            case "pdf": PdfRenderer.Render(ToDocument(sheets, title, ct), path, ct); break;
            default: throw new ConversionException($"Spreadsheets can't be saved as {ext.ToUpperInvariant()} without Office or LibreOffice.");
        }
        ct.ThrowIfCancellationRequested();
    }

    static string Display(object? v) => v switch
    {
        null => "",
        double d => d.ToString("G15", CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    static void WriteCsv(Sheet sheet, string path, char delimiter, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int width = sheet.ColumnCount;
        foreach (var row in sheet.Rows)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < width; i++)
            {
                if (i > 0) sb.Append(delimiter);
                var text = Display(i < row.Length ? row[i] : null);
                if (text.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 || text.StartsWith(' ') || text.EndsWith(' '))
                    text = "\"" + text.Replace("\"", "\"\"") + "\"";
                sb.Append(text);
            }
            sb.Append("\r\n");
        }
        // The BOM makes Excel read UTF-8 correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static void WriteXlsx(List<Sheet> sheets, string path, CancellationToken ct)
    {
        XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        var names = UniqueSheetNames(sheets, 31, "[]:*?/\\");

        var types = new XElement(CT + "Types",
            new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(CT + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(CT + "Override", new XAttribute("PartName", "/xl/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
        var workbookRels = new XElement(PR + "Relationships",
            new XElement(PR + "Relationship", new XAttribute("Id", "rIdStyles"), new XAttribute("Type", RelBase + "styles"), new XAttribute("Target", "styles.xml")));
        var sheetList = new XElement(S + "sheets");

        for (int i = 0; i < sheets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int n = i + 1;
            types.Add(new XElement(CT + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{n}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            workbookRels.Add(new XElement(PR + "Relationship", new XAttribute("Id", $"rId{n}"), new XAttribute("Type", RelBase + "worksheet"), new XAttribute("Target", $"worksheets/sheet{n}.xml")));
            sheetList.Add(new XElement(S + "sheet", new XAttribute("name", names[i]), new XAttribute("sheetId", n), new XAttribute(R + "id", $"rId{n}")));

            var sheet = sheets[i];
            var data = new XElement(S + "sheetData");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                var row = new XElement(S + "row", new XAttribute("r", r + 1));
                for (int c = 0; c < sheet.Rows[r].Length; c++)
                {
                    var v = sheet.Rows[r][c];
                    if (v is null || v is string { Length: 0 }) continue;
                    var cell = new XElement(S + "c", new XAttribute("r", ColumnName(c) + (r + 1)));
                    switch (v)
                    {
                        case double d when double.IsFinite(d):
                            cell.Add(new XElement(S + "v", d.ToString("R", CultureInfo.InvariantCulture)));
                            break;
                        case DateTime dt:
                            cell.Add(new XAttribute("s", dt.TimeOfDay == TimeSpan.Zero ? 1 : 2), new XElement(S + "v", dt.ToOADate().ToString("R", CultureInfo.InvariantCulture)));
                            break;
                        case bool b:
                            cell.Add(new XAttribute("t", "b"), new XElement(S + "v", b ? 1 : 0));
                            break;
                        default:
                            cell.Add(new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), XmlSafe(Display(v)))));
                            break;
                    }
                    row.Add(cell);
                }
                data.Add(row);
            }

            var worksheet = new XElement(S + "worksheet", new XAttribute(XNamespace.Xmlns + "r", R));
            var widths = ColumnWidths(sheet, ct);
            if (widths.Count > 0)
                worksheet.Add(new XElement(S + "cols", widths.Select((w, c) => new XElement(S + "col",
                    new XAttribute("min", c + 1), new XAttribute("max", c + 1), new XAttribute("width", w.ToString("0.##", CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)))));
            worksheet.Add(data);
            if (sheet.Charts.Count > 0)
            {
                // Charts come along as pictures stacked below the data.
                XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
                XNamespace Dml = "http://schemas.openxmlformats.org/drawingml/2006/main";
                worksheet.Add(new XElement(S + "drawing", new XAttribute(R + "id", "rIdDrawing")));
                Put(zip, $"xl/worksheets/_rels/sheet{n}.xml.rels", new XElement(PR + "Relationships",
                    new XElement(PR + "Relationship", new XAttribute("Id", "rIdDrawing"), new XAttribute("Type", RelBase + "drawing"), new XAttribute("Target", $"../drawings/drawing{n}.xml"))));
                var drawing = new XElement(Xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", Xdr), new XAttribute(XNamespace.Xmlns + "a", Dml), new XAttribute(XNamespace.Xmlns + "r", R));
                var drawingRels = new XElement(PR + "Relationships");
                int row = sheet.Rows.Count + 1;
                for (int k = 0; k < sheet.Charts.Count; k++)
                {
                    var chart = sheet.Charts[k];
                    var media = $"chart{n}_{k + 1}.png";
                    using (var s = zip.CreateEntry($"xl/media/{media}", CompressionLevel.NoCompression).Open()) s.Write(chart.Data);
                    drawingRels.Add(new XElement(PR + "Relationship", new XAttribute("Id", $"rIdImg{k + 1}"), new XAttribute("Type", RelBase + "image"), new XAttribute("Target", $"../media/{media}")));
                    long cx = (long)(chart.Width * 12700), cy = (long)(chart.Height * 12700);
                    drawing.Add(new XElement(Xdr + "oneCellAnchor",
                        new XElement(Xdr + "from", new XElement(Xdr + "col", 0), new XElement(Xdr + "colOff", 0), new XElement(Xdr + "row", row), new XElement(Xdr + "rowOff", 0)),
                        new XElement(Xdr + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                        new XElement(Xdr + "pic",
                            new XElement(Xdr + "nvPicPr", new XElement(Xdr + "cNvPr", new XAttribute("id", k + 2), new XAttribute("name", $"Chart {k + 1}")), new XElement(Xdr + "cNvPicPr")),
                            new XElement(Xdr + "blipFill", new XElement(Dml + "blip", new XAttribute(R + "embed", $"rIdImg{k + 1}")), new XElement(Dml + "stretch", new XElement(Dml + "fillRect"))),
                            new XElement(Xdr + "spPr",
                                new XElement(Dml + "xfrm", new XElement(Dml + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(Dml + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(Dml + "prstGeom", new XAttribute("prst", "rect"), new XElement(Dml + "avLst")))),
                        new XElement(Xdr + "clientData")));
                    row += (int)Math.Ceiling(chart.Height / 15) + 1;
                }
                Put(zip, $"xl/drawings/drawing{n}.xml", drawing);
                Put(zip, $"xl/drawings/_rels/drawing{n}.xml.rels", drawingRels);
                types.Add(new XElement(CT + "Override", new XAttribute("PartName", $"/xl/drawings/drawing{n}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));
                if (!types.Elements(CT + "Default").Any(d => (string?)d.Attribute("Extension") == "png"))
                    types.AddFirst(new XElement(CT + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")));
            }
            Put(zip, $"xl/worksheets/sheet{n}.xml", worksheet);
        }

        Put(zip, "[Content_Types].xml", types);
        Put(zip, "_rels/.rels", new XElement(PR + "Relationships",
            new XElement(PR + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", RelBase + "officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
        Put(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), sheetList));
        Put(zip, "xl/_rels/workbook.xml.rels", workbookRels);
        Put(zip, "xl/styles.xml", new XElement(S + "styleSheet",
            new XElement(S + "fonts", new XAttribute("count", 1), new XElement(S + "font", new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "Calibri")))),
            new XElement(S + "fills", new XAttribute("count", 2),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "gray125")))),
            new XElement(S + "borders", new XAttribute("count", 1), new XElement(S + "border")),
            new XElement(S + "cellStyleXfs", new XAttribute("count", 1), new XElement(S + "xf")),
            new XElement(S + "cellXfs", new XAttribute("count", 3),
                new XElement(S + "xf"),
                new XElement(S + "xf", new XAttribute("numFmtId", 14), new XAttribute("applyNumberFormat", 1)),
                new XElement(S + "xf", new XAttribute("numFmtId", 22), new XAttribute("applyNumberFormat", 1)))));

        static void Put(ZipArchive zip, string name, XElement root)
        {
            SafeXml.Clean(root);
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(stream, SaveOptions.DisableFormatting);
        }
    }

    static void WriteOds(List<Sheet> sheets, string path, CancellationToken ct)
    {
        var names = UniqueSheetNames(sheets, 100, "[]*?:/\\'");
        var spreadsheet = new XElement(Office + "spreadsheet");
        var pictures = new List<(string name, byte[] data, string type)>();
        var autoStyles = new XElement(Office + "automatic-styles",
            new XElement(OdtReader.St + "style", new XAttribute(OdtReader.St + "name", "dateCell"), new XAttribute(OdtReader.St + "family", "table-cell"),
                new XAttribute(OdtReader.St + "data-style-name", "dateFormat")));
        XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
        autoStyles.AddFirst(new XElement(Number + "date-style", new XAttribute(OdtReader.St + "name", "dateFormat"),
            new XElement(Number + "year", new XAttribute(Number + "style", "long")), new XElement(Number + "text", "-"),
            new XElement(Number + "month", new XAttribute(Number + "style", "long")), new XElement(Number + "text", "-"),
            new XElement(Number + "day", new XAttribute(Number + "style", "long"))));

        for (int i = 0; i < sheets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = sheets[i];
            var table = new XElement(Table + "table", new XAttribute(Table + "name", names[i]));
            var widths = ColumnWidths(sheet, ct);
            for (int c = 0; c < widths.Count; c++)
            {
                var styleName = $"co{i}_{c}";
                autoStyles.Add(new XElement(OdtReader.St + "style", new XAttribute(OdtReader.St + "name", styleName), new XAttribute(OdtReader.St + "family", "table-column"),
                    new XElement(OdtReader.St + "table-column-properties", new XAttribute(OdtReader.St + "column-width", OdfPackage.Len(widths[c] * 5.6)))));
                table.Add(new XElement(Table + "table-column", new XAttribute(Table + "style-name", styleName)));
            }
            if (widths.Count == 0) table.Add(new XElement(Table + "table-column"));

            foreach (var row in sheet.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var tr = new XElement(Table + "table-row");
                foreach (var v in row)
                {
                    var tc = new XElement(Table + "table-cell");
                    switch (v)
                    {
                        case null: break;
                        case double d:
                            tc.Add(new XAttribute(Office + "value-type", "float"), new XAttribute(Office + "value", d.ToString("R", CultureInfo.InvariantCulture)),
                                new XElement(Text + "p", Display(d)));
                            break;
                        case DateTime dt:
                            tc.Add(new XAttribute(Table + "style-name", "dateCell"), new XAttribute(Office + "value-type", "date"),
                                new XAttribute(Office + "date-value", dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)), new XElement(Text + "p", Display(dt)));
                            break;
                        case bool b:
                            tc.Add(new XAttribute(Office + "value-type", "boolean"), new XAttribute(Office + "boolean-value", b ? "true" : "false"), new XElement(Text + "p", Display(b)));
                            break;
                        default:
                            tc.Add(new XAttribute(Office + "value-type", "string"),
                                Display(v).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => new XElement(Text + "p", OdtWriter.TextNodes(line))));
                            break;
                    }
                    tr.Add(tc);
                }
                if (!tr.HasElements) tr.Add(new XElement(Table + "table-cell"));
                table.Add(tr);
            }
            if (sheet.Rows.Count == 0) table.Add(new XElement(Table + "table-row", new XElement(Table + "table-cell")));
            if (sheet.Charts.Count > 0)
            {
                // Charts come along as pictures stacked below the data (shapes go before the columns).
                var shapes = new XElement(Table + "shapes");
                double y = (sheet.Rows.Count + 1) * 12.8;
                for (int k = 0; k < sheet.Charts.Count; k++)
                {
                    var chart = sheet.Charts[k];
                    var name = $"Pictures/chart{i + 1}_{k + 1}.png";
                    pictures.Add((name, chart.Data, "image/png"));
                    shapes.Add(new XElement(Draw + "frame", new XAttribute(Draw + "name", $"Chart {k + 1}"),
                        new XAttribute(Svg + "x", "0cm"), new XAttribute(Svg + "y", OdfPackage.Len(y)),
                        new XAttribute(Svg + "width", OdfPackage.Len(chart.Width)), new XAttribute(Svg + "height", OdfPackage.Len(chart.Height)),
                        new XElement(Draw + "image", new XAttribute(XLink + "href", name), new XAttribute(XLink + "type", "simple"),
                            new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad"))));
                    y += chart.Height + 12;
                }
                table.AddFirst(shapes);
            }
            spreadsheet.Add(table);
        }

        var content = new XElement(Office + "document-content", OdfPackage.Namespaces(), new XAttribute(XNamespace.Xmlns + "number", Number),
            autoStyles, new XElement(Office + "body", spreadsheet));
        var styles = new XElement(Office + "document-styles", OdfPackage.Namespaces(), new XElement(Office + "styles"));
        OdfPackage.Save(path, "application/vnd.oasis.opendocument.spreadsheet", content, styles, pictures);
    }

    static void WriteHtml(List<Sheet> sheets, string path, string title, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body { font-family: Calibri, Arial, sans-serif; font-size: 11pt; margin: 2em; }\n");
        sb.Append("table { border-collapse: collapse; margin-bottom: 2em; }\ntd { border: 1px solid #bbb; padding: 3px 8px; white-space: pre-wrap; }\n");
        sb.Append("td.n { text-align: right; }\n</style>\n</head>\n<body>\n");
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            if (sheets.Count > 1) sb.Append("<h2>").Append(WebUtility.HtmlEncode(sheet.Name)).Append("</h2>\n");
            sb.Append("<table>\n");
            int width = sheet.ColumnCount;
            foreach (var row in sheet.Rows)
            {
                ct.ThrowIfCancellationRequested();
                sb.Append("<tr>");
                for (int c = 0; c < width; c++)
                {
                    var v = c < row.Length ? row[c] : null;
                    sb.Append(v is double ? "<td class=\"n\">" : "<td>").Append(WebUtility.HtmlEncode(Display(v))).Append("</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</table>\n");
            foreach (var chart in sheet.Charts)
                sb.Append("<p><img alt=\"Chart\" style=\"max-width:100%\" width=\"").Append((int)(chart.Width * 4 / 3))
                  .Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(chart.Data)).Append("\"></p>\n");
        }
        sb.Append("</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Lays sheets out as tables for PDF output, landscape when they're wide.</summary>
    static DDocument ToDocument(List<Sheet> sheets, string title, CancellationToken ct)
    {
        var doc = new DDocument { Title = title, Font = "Calibri", FontSize = 9, Margins = new Thickness(36) };
        int widest = sheets.Max(s => s.ColumnCount);
        if (widest > 6) (doc.PageWidth, doc.PageHeight) = (doc.PageHeight, doc.PageWidth);

        for (int i = 0; i < sheets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = sheets[i];
            if (sheets.Count > 1)
            {
                var heading = new DParagraph { Heading = 2, SpaceAfter = 6, PageBreakBefore = i > 0 };
                heading.Inlines.Add(new DText(sheet.Name, new TextStyle { Size = 13 }));
                doc.Blocks.Add(heading);
            }
            int width = sheet.ColumnCount;
            if (width == 0) { AddCharts(doc, sheet); continue; }
            var widths = ColumnWidths(sheet, ct);
            double total = widths.Sum();
            var table = new DTable { Borders = true, ColumnWidths = widths.Select(w => w / total * doc.ContentWidth).ToList() };
            foreach (var row in sheet.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var r = new DRow();
                for (int c = 0; c < width; c++)
                {
                    var v = c < row.Length ? row[c] : null;
                    var p = new DParagraph { Align = v is double ? TextAlignment.Right : TextAlignment.Left };
                    var text = Display(v);
                    if (text.Length > 0) p.Inlines.Add(new DText(text, TextStyle.Plain));
                    var cell = new DCell();
                    cell.Blocks.Add(p);
                    r.Cells.Add(cell);
                }
                table.Rows.Add(r);
            }
            doc.Blocks.Add(table);
            AddCharts(doc, sheet);
        }
        return doc;
    }

    /// <summary>A sheet's charts below its data, shrunk to fit the page width.</summary>
    static void AddCharts(DDocument doc, Sheet sheet)
    {
        foreach (var chart in sheet.Charts)
        {
            double scale = Math.Min(1, doc.ContentWidth / chart.Width);
            var p = new DParagraph { SpaceBefore = 12, SpaceAfter = 6 };
            p.Inlines.Add(new DImage(chart.Data, chart.Ext, chart.Width * scale, chart.Height * scale));
            doc.Blocks.Add(p);
        }
    }

    /// <summary>Approximate column widths in characters, from the longest value in each column.</summary>
    static List<double> ColumnWidths(Sheet sheet, CancellationToken ct)
    {
        int width = sheet.ColumnCount;
        var widths = new List<double>();
        for (int c = 0; c < width; c++)
        {
            ct.ThrowIfCancellationRequested();
            int longest = sheet.Rows.Take(2000).Select(r => c < r.Length ? Display(r[c]).Split('\n').Max(l => l.Length) : 0).DefaultIfEmpty(0).Max();
            widths.Add(Math.Clamp(longest + 2, 6, 60));
        }
        return widths;
    }

    static List<string> UniqueSheetNames(List<Sheet> sheets, int maxLength, string invalid)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var sheet in sheets)
        {
            var name = new string(SafeXml.Text(sheet.Name).Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('\'');
            if (name.Length == 0) name = $"Sheet{names.Count + 1}";
            if (name.Length > maxLength) name = name[..maxLength];
            var candidate = name;
            for (int i = 2; !used.Add(candidate); i++)
            {
                var suffix = $" ({i})";
                candidate = (name.Length + suffix.Length > maxLength ? name[..(maxLength - suffix.Length)] : name) + suffix;
            }
            names.Add(candidate);
        }
        return names;
    }

    static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    static string XmlSafe(string s) => SafeXml.Text(s);
}

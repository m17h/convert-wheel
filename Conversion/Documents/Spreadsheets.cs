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
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);
}

internal static class Spreadsheets
{
    const int MaxRepeat = 1000; // ODS stores "the rest of the sheet" as huge repeated empty cells

    // ------------------------------------------------------------------ reading

    public static List<Sheet> Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => [ReadCsv(path)],
        ".ods" => ReadOds(path),
        _ => ReadExcel(path),
    };

    static Sheet ReadCsv(string path)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(path));
        char delimiter = DetectDelimiter(text);
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
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
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
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

    static List<Sheet> ReadExcel(string path)
    {
        TextDecoding.EnsureRegistered();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new List<Sheet>();
        do
        {
            if (reader.VisibleState is "hidden" or "veryhidden") continue;
            var rows = new List<object?[]>();
            while (reader.Read())
            {
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
        return sheets.Count > 0 ? sheets : [new Sheet("Sheet1", [])];
    }

    static List<Sheet> ReadOds(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new ConversionException("This OpenDocument spreadsheet has no content.");
        XElement root;
        using (var stream = entry.Open()) root = XDocument.Load(stream).Root!;

        var sheets = new List<Sheet>();
        foreach (var table in root.Descendants(Table + "table"))
        {
            var rows = new List<object?[]>();
            foreach (var tr in table.Descendants(Table + "table-row"))
            {
                var cells = new List<object?>();
                foreach (var tc in tr.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                {
                    int repeat = Math.Min((int?)tc.Attribute(Table + "number-columns-repeated") ?? 1, MaxRepeat);
                    var value = OdsValue(tc);
                    for (int i = 0; i < repeat; i++) cells.Add(value);
                }
                int rowRepeat = Math.Min((int?)tr.Attribute(Table + "number-rows-repeated") ?? 1, MaxRepeat);
                for (int i = 0; i < rowRepeat; i++) rows.Add([.. cells]);
            }
            sheets.Add(new Sheet((string?)table.Attribute(Table + "name") ?? $"Sheet{sheets.Count + 1}", Trim(rows)));
        }
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
        var paragraphs = tc.Elements(Text + "p").Select(p => OdsText(p)).ToList();
        return paragraphs.Count == 0 ? null : string.Join("\n", paragraphs);
    }

    static string OdsText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.DescendantNodes())
        {
            if (node is XText t) sb.Append(t.Value);
            else if (node is XElement { Name.LocalName: "s" } s) sb.Append(' ', (int?)s.Attribute(Text + "c") ?? 1);
            else if (node is XElement { Name.LocalName: "tab" }) sb.Append('\t');
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

    public static void Write(List<Sheet> sheets, string path, string ext, string title)
    {
        switch (ext)
        {
            case "csv": WriteCsv(sheets[0], path); break;
            case "xlsx": WriteXlsx(sheets, path); break;
            case "ods": WriteOds(sheets, path); break;
            case "html": WriteHtml(sheets, path, title); break;
            case "pdf": PdfRenderer.Render(ToDocument(sheets, title), path); break;
            default: throw new ConversionException($"Spreadsheets can't be saved as {ext.ToUpperInvariant()} without Office or LibreOffice.");
        }
    }

    static string Display(object? v) => v switch
    {
        null => "",
        double d => d.ToString("G15", CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    static void WriteCsv(Sheet sheet, string path)
    {
        var sb = new StringBuilder();
        int width = sheet.ColumnCount;
        foreach (var row in sheet.Rows)
        {
            for (int i = 0; i < width; i++)
            {
                if (i > 0) sb.Append(',');
                var text = Display(i < row.Length ? row[i] : null);
                if (text.IndexOfAny([',', '"', '\n', '\r']) >= 0 || text.StartsWith(' ') || text.EndsWith(' '))
                    text = "\"" + text.Replace("\"", "\"\"") + "\"";
                sb.Append(text);
            }
            sb.Append("\r\n");
        }
        // The BOM makes Excel read UTF-8 correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static void WriteXlsx(List<Sheet> sheets, string path)
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
            int n = i + 1;
            types.Add(new XElement(CT + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{n}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            workbookRels.Add(new XElement(PR + "Relationship", new XAttribute("Id", $"rId{n}"), new XAttribute("Type", RelBase + "worksheet"), new XAttribute("Target", $"worksheets/sheet{n}.xml")));
            sheetList.Add(new XElement(S + "sheet", new XAttribute("name", names[i]), new XAttribute("sheetId", n), new XAttribute(R + "id", $"rId{n}")));

            var sheet = sheets[i];
            var data = new XElement(S + "sheetData");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
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
            var widths = ColumnWidths(sheet);
            if (widths.Count > 0)
                worksheet.Add(new XElement(S + "cols", widths.Select((w, c) => new XElement(S + "col",
                    new XAttribute("min", c + 1), new XAttribute("max", c + 1), new XAttribute("width", w.ToString("0.##", CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)))));
            worksheet.Add(data);
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
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(stream, SaveOptions.DisableFormatting);
        }
    }

    static void WriteOds(List<Sheet> sheets, string path)
    {
        var names = UniqueSheetNames(sheets, 100, "[]*?:/\\'");
        var spreadsheet = new XElement(Office + "spreadsheet");
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
            var sheet = sheets[i];
            var table = new XElement(Table + "table", new XAttribute(Table + "name", names[i]));
            var widths = ColumnWidths(sheet);
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
                                Display(v).Split('\n').Select(line => new XElement(Text + "p", XmlSafe(line))));
                            break;
                    }
                    tr.Add(tc);
                }
                if (!tr.HasElements) tr.Add(new XElement(Table + "table-cell"));
                table.Add(tr);
            }
            if (sheet.Rows.Count == 0) table.Add(new XElement(Table + "table-row", new XElement(Table + "table-cell")));
            spreadsheet.Add(table);
        }

        var content = new XElement(Office + "document-content", OdfPackage.Namespaces(), new XAttribute(XNamespace.Xmlns + "number", Number),
            autoStyles, new XElement(Office + "body", spreadsheet));
        var styles = new XElement(Office + "document-styles", OdfPackage.Namespaces(), new XElement(Office + "styles"));
        OdfPackage.Save(path, "application/vnd.oasis.opendocument.spreadsheet", content, styles, []);
    }

    static void WriteHtml(List<Sheet> sheets, string path, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body { font-family: Calibri, Arial, sans-serif; font-size: 11pt; margin: 2em; }\n");
        sb.Append("table { border-collapse: collapse; margin-bottom: 2em; }\ntd { border: 1px solid #bbb; padding: 3px 8px; white-space: pre-wrap; }\n");
        sb.Append("td.n { text-align: right; }\n</style>\n</head>\n<body>\n");
        foreach (var sheet in sheets)
        {
            if (sheets.Count > 1) sb.Append("<h2>").Append(WebUtility.HtmlEncode(sheet.Name)).Append("</h2>\n");
            sb.Append("<table>\n");
            int width = sheet.ColumnCount;
            foreach (var row in sheet.Rows)
            {
                sb.Append("<tr>");
                for (int c = 0; c < width; c++)
                {
                    var v = c < row.Length ? row[c] : null;
                    sb.Append(v is double ? "<td class=\"n\">" : "<td>").Append(WebUtility.HtmlEncode(Display(v))).Append("</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</table>\n");
        }
        sb.Append("</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Lays sheets out as tables for PDF output, landscape when they're wide.</summary>
    static DDocument ToDocument(List<Sheet> sheets, string title)
    {
        var doc = new DDocument { Title = title, Font = "Calibri", FontSize = 9, Margins = new Thickness(36) };
        int widest = sheets.Max(s => s.ColumnCount);
        if (widest > 6) (doc.PageWidth, doc.PageHeight) = (doc.PageHeight, doc.PageWidth);

        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            if (sheets.Count > 1)
            {
                var heading = new DParagraph { Heading = 2, SpaceAfter = 6, PageBreakBefore = i > 0 };
                heading.Inlines.Add(new DText(sheet.Name, new TextStyle { Size = 13 }));
                doc.Blocks.Add(heading);
            }
            int width = sheet.ColumnCount;
            if (width == 0) continue;
            var widths = ColumnWidths(sheet);
            double total = widths.Sum();
            var table = new DTable { Borders = true, ColumnWidths = widths.Select(w => w / total * doc.ContentWidth).ToList() };
            foreach (var row in sheet.Rows)
            {
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
        }
        return doc;
    }

    /// <summary>Approximate column widths in characters, from the longest value in each column.</summary>
    static List<double> ColumnWidths(Sheet sheet)
    {
        int width = sheet.ColumnCount;
        var widths = new List<double>();
        for (int c = 0; c < width; c++)
        {
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
            var name = new string(sheet.Name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
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

    static string XmlSafe(string s) => new(s.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray());
}

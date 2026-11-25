using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Old Excel workbooks, and the web pages and XML files that other programs save with an .xls name.</summary>
internal static class LegacySheets
{
    // ------------------------------------------------------------------ binary workbooks

    public static List<Sheet> ReadBinary(Stream stream, CancellationToken ct)
    {
        TextDecoding.EnsureRegistered();
        var limits = new SheetLimits();
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { LeaveOpen = true });
            var sheets = new List<Sheet>();
            do
            {
                var rows = new List<object?[]>();
                var hiddenRows = new HashSet<int>();
                var cells = new List<object?>();
                for (int r = 0; reader.Read(); r++)
                {
                    if ((r & 255) == 0) ct.ThrowIfCancellationRequested();
                    cells.Clear();
                    for (int c = 0; c < reader.FieldCount; c++)
                    {
                        if (Value(reader, c) is not { } value) continue;
                        while (cells.Count < c) cells.Add(null);
                        cells.Add(value);
                    }
                    if (reader.RowHeight == 0) hiddenRows.Add(r);
                    if (cells.Count == 0) continue;
                    limits.Cells(cells.Count);
                    while (rows.Count < r) rows.Add([]);
                    rows.Add([.. cells]);
                }
                var hiddenColumns = new HashSet<int>();
                for (int c = 0; c < reader.FieldCount; c++)
                    if (reader.GetColumnWidth(c) == 0) hiddenColumns.Add(c);
                sheets.Add(new Sheet(reader.Name ?? $"Sheet{sheets.Count + 1}", rows) { Hidden = reader.VisibleState is "hidden" or "veryhidden", HiddenRows = hiddenRows, HiddenColumns = hiddenColumns });
            } while (reader.NextResult());
            return sheets;
        }
        catch (InvalidPasswordException) { throw new ConversionException("This spreadsheet is password-protected."); }
        catch (Exception ex) when (ex is ExcelReaderException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException
                                       or NullReferenceException or OverflowException or InvalidCastException or FormatException or IOException && ex is not FileNotFoundException)
        {
            Log.Write($"Couldn't read the workbook: {ex}");
            throw new ConversionException(IsEncrypted(stream) ? "This spreadsheet is password-protected." : "This isn't an Excel workbook, or it's damaged.");
        }
    }

    /// <summary>Password-protected .xlsx files are compound files holding a stream called EncryptedPackage.</summary>
    static bool IsEncrypted(Stream stream)
    {
        if (stream.Length > 256 * 1024 * 1024) return false;
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return bytes.AsSpan(0, count).IndexOf(Encoding.Unicode.GetBytes("EncryptedPackage")) >= 0;
    }

    static object? Value(IExcelDataReader reader, int c)
    {
        if (reader.GetCellError(c) is { } error)
            return new ErrorCell(error switch
            {
                CellError.NULL => "#NULL!", CellError.DIV0 => "#DIV/0!", CellError.VALUE => "#VALUE!", CellError.REF => "#REF!",
                CellError.NAME => "#NAME?", CellError.NUM => "#NUM!", CellError.NA => "#N/A", _ => "#GETTING_DATA",
            });
        var value = reader.GetValue(c);
        if (value is null or DBNull or string { Length: 0 }) return null;

        int id = reader.GetNumberFormatIndex(c);
        var format = NumberFormat.BuiltInCode(id) ?? reader.GetNumberFormatString(c);
        if (string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase)) format = null;
        switch (value)
        {
            case string text: return NumberFormat.FormatText(text, format);
            case bool: return value;
            case DateTime date:
                if (NumberFormat.ToSerial(date) is not { } serial) serial = (date - DateTime.FromOADate(0)).TotalDays; // a time of day
                return new NumberCell(serial, NumberFormat.IsDate(format) ? format! : NumberFormat.IsoDateTime);
            case TimeSpan time:
                return new NumberCell(time.TotalDays, NumberFormat.IsDate(format) ? format! : "[h]:mm:ss");
        }
        double number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return format == null ? number : new NumberCell(number, format);
    }

    // ------------------------------------------------------------------ XML Spreadsheet 2003

    static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    public static bool IsXmlSpreadsheet(string start) => start.Contains("urn:schemas-microsoft-com:office:spreadsheet", StringComparison.Ordinal) && start.Contains("Workbook", StringComparison.Ordinal);

    public static List<Sheet> ReadXml(Stream stream, CancellationToken ct)
    {
        var root = PackageResources.LoadXml(stream, LoadOptions.PreserveWhitespace).Root!;
        var formats = new Dictionary<string, string?>();
        foreach (var style in root.Elements(Ss + "Styles").Elements(Ss + "Style"))
            if ((string?)style.Attribute(Ss + "ID") is { } id) formats[id] = NamedFormat((string?)style.Element(Ss + "NumberFormat")?.Attribute(Ss + "Format"));

        var limits = new SheetLimits();
        var sheets = new List<Sheet>();
        foreach (var worksheet in root.Elements(Ss + "Worksheet"))
        {
            var rows = new List<object?[]>();
            var hiddenRows = new HashSet<int>();
            int rowIndex = 0;
            foreach (var row in worksheet.Elements(Ss + "Table").Elements(Ss + "Row"))
            {
                ct.ThrowIfCancellationRequested();
                rowIndex = Index(row, "Index", rowIndex + 1);
                if (rowIndex > SheetLimits.Rows) throw SheetLimits.TooLarge();
                if ((string?)row.Attribute(Ss + "Hidden") == "1") hiddenRows.Add(rowIndex - 1);
                var cells = new List<object?>();
                int column = 0;
                foreach (var cell in row.Elements(Ss + "Cell"))
                {
                    column = Index(cell, "Index", column + 1);
                    if (column > SheetLimits.Columns) throw SheetLimits.TooLarge();
                    var style = (string?)cell.Attribute(Ss + "StyleID") ?? (string?)row.Attribute(Ss + "StyleID") ?? "Default";
                    if (cell.Element(Ss + "Data") is { } data && Value(data, formats.GetValueOrDefault(style)) is { } value && column > cells.Count)
                    {
                        while (cells.Count < column - 1) cells.Add(null);
                        cells.Add(value);
                    }
                    column += Math.Max(0, Index(cell, "MergeAcross", 0));
                }
                if (cells.Count > 0 && rowIndex > rows.Count)
                {
                    limits.Cells(cells.Count);
                    while (rows.Count < rowIndex - 1) rows.Add([]);
                    rows.Add([.. cells]);
                }
                rowIndex += Math.Max(0, Index(row, "Span", 0));
            }
            bool hidden = worksheet.Descendants().FirstOrDefault(e => e.Name.LocalName == "Visible")?.Value is "SheetHidden" or "SheetVeryHidden";
            sheets.Add(new Sheet((string?)worksheet.Attribute(Ss + "Name") ?? $"Sheet{sheets.Count + 1}", rows) { Hidden = hidden, HiddenRows = hiddenRows });
        }
        return sheets;

        static int Index(XElement e, string name, int fallback) => int.TryParse((string?)e.Attribute(Ss + name), out int n) && n >= 0 ? n : fallback;

        static object? Value(XElement data, string? format)
        {
            var text = data.Value;
            switch ((string?)data.Attribute(Ss + "Type"))
            {
                case "Number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n):
                    return format == null ? n : new NumberCell(n, format);
                case "DateTime" when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date):
                    // Times of day are stored on 31 December 1899.
                    double serial = NumberFormat.ToSerial(date) ?? (date - new DateTime(1899, 12, 31)).TotalDays;
                    if (serial < 0) return text;
                    return new NumberCell(serial, NumberFormat.IsDate(format) ? format! : serial < 1 ? "hh:mm:ss" : date.TimeOfDay == TimeSpan.Zero ? NumberFormat.IsoDate : NumberFormat.IsoDateTime);
                case "Boolean": return text.Trim() == "1";
                case "Error": return new ErrorCell(text.Trim());
                default: return text.Length == 0 ? null : text;
            }
        }
    }

    static string? NamedFormat(string? format) => format switch
    {
        null or "" or "General" or "General Number" => null,
        "General Date" => NumberFormat.SystemDateTime,
        "Short Date" => NumberFormat.SystemDate,
        "Medium Date" => "d-mmm-yy",
        "Long Date" => "[$-F800]dddd, mmmm d, yyyy",
        "Long Time" => "h:mm:ss AM/PM",
        "Medium Time" => "h:mm AM/PM",
        "Short Time" => "h:mm",
        "Currency" => "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
        "Euro Currency" => "[$€-2] #,##0.00",
        "Fixed" => "0.00",
        "Standard" => "#,##0.00",
        "Percent" => "0.00%",
        "Scientific" => "0.00E+00",
        "Yes/No" => "\"Yes\";\"Yes\";\"No\"",
        "True/False" => "\"True\";\"True\";\"False\"",
        "On/Off" => "\"On\";\"On\";\"Off\"",
        "@" => null,
        _ => format,
    };

    // ------------------------------------------------------------------ web pages

    /// <summary>The tables of a web page, one sheet each.</summary>
    public static List<Sheet> ReadHtml(Stream stream, string name, CancellationToken ct)
    {
        var dom = new HtmlParser().ParseDocument(stream);
        var limits = new SheetLimits();
        var sheets = new List<Sheet>();
        foreach (var table in dom.QuerySelectorAll("table"))
        {
            // Tables inside tables are part of their cell's text; a table that only holds other tables is page layout.
            if (Inside(table, "table")) continue;
            ct.ThrowIfCancellationRequested();
            var rows = new List<object?[]>();
            var spans = new Dictionary<int, int>(); // column -> rows still covered from above
            foreach (var tr in table.QuerySelectorAll("tr").Where(r => ClosestTable(r) == table))
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<object?>();
                int column = 0;
                var next = spans.Where(s => s.Value > 1).ToDictionary(s => s.Key, s => s.Value - 1);
                foreach (var td in tr.Children.Where(c => c.LocalName is "td" or "th"))
                {
                    while (spans.ContainsKey(column)) column++;
                    int across = Span(td, "colspan", 1000), down = Span(td, "rowspan", 65534);
                    if (column + across > SheetLimits.Columns) throw SheetLimits.TooLarge();
                    var text = CellText(td);
                    if (text.Length > 0)
                    {
                        while (cells.Count < column) cells.Add(null);
                        cells.Add(text);
                        limits.Text(text);
                    }
                    if (down > 1) for (int i = 0; i < across; i++) next[column + i] = down - 1;
                    column += across;
                }
                spans = next;
                limits.Cells(Math.Max(1, cells.Count));
                rows.Add([.. cells]);
            }
            while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
            if (rows.Count > 0) sheets.Add(new Sheet(sheets.Count == 0 ? name : $"Table {sheets.Count + 1}", rows) { Delimiter = ',' });
        }
        if (sheets.Count == 0) throw new ConversionException("This is a web page without a table, not an Excel workbook.");
        return sheets;

        static int Span(IElement td, string attribute, int most) => int.TryParse(td.GetAttribute(attribute), out int n) ? Math.Clamp(n, 1, most) : 1;
        static IElement? ClosestTable(IElement e)
        {
            for (var p = e.ParentElement; p != null; p = p.ParentElement) if (p.LocalName == "table") return p;
            return null;
        }
        static bool Inside(IElement e, string tag) => ClosestTable(e) != null;
    }

    /// <summary>A cell's text: runs of space collapse, and line breaks and paragraphs become new lines.</summary>
    static string CellText(IElement td)
    {
        var sb = new StringBuilder();
        // Walked without recursion: pages can nest as deep as they like.
        INode? node = td.FirstChild;
        while (node != null)
        {
            bool descend = node is IElement { LocalName: not ("style" or "script") };
            if (node is IText text)
            {
                foreach (char c in text.Data)
                {
                    if (c is ' ' or '\t' or '\r' or '\n') { if (sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' '); }
                    else sb.Append(c == ' ' ? ' ' : c);
                }
            }
            else if (node is IElement { LocalName: "br" or "p" or "div" or "li" or "tr" } && sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            else if (node is IElement { LocalName: "td" or "th" } && sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' ');

            if (descend && node.FirstChild != null) { node = node.FirstChild; continue; }
            while (node != null && node.NextSibling == null)
            {
                node = node.Parent;
                if (node == td) node = null;
            }
            node = node?.NextSibling;
        }
        return sb.ToString().Replace(" \n", "\n").Trim();
    }
}

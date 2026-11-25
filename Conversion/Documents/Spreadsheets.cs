using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace ConvertWheel.Conversion.Documents;

internal static class Spreadsheets
{
    // ------------------------------------------------------------------ reading

    /// <param name="charts">Whether the sheets' charts are wanted (they take a while to draw).</param>
    public static List<Sheet> Read(string path, CancellationToken ct = default, bool charts = true)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) throw new ConversionException("This file is empty.");
        try
        {
            var sheets = ext is ".csv" or ".tsv" ? [DelimitedText.Read(path, Path.GetFileNameWithoutExtension(path), ext == ".tsv", ct)] : ReadWorkbook(path, stream, charts, ct);
            foreach (var sheet in sheets) Trim(sheet.Rows);
            return sheets.Count > 0 ? sheets : [new Sheet("Sheet1", [])];
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            Log.Write($"Couldn't read {path}: {ex}");
            throw new ConversionException("This spreadsheet is damaged and can't be read.");
        }
    }

    /// <summary>Workbooks are told apart by what's inside: programs often save other things with an .xls name.</summary>
    static List<Sheet> ReadWorkbook(string path, FileStream stream, bool charts, CancellationToken ct)
    {
        var head = new byte[4096];
        int count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        bool Starts(params byte[] magic) => count >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic);

        if (Starts((byte)'P', (byte)'K'))
        {
            bool ods, binary;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                (ods, binary) = (zip.GetEntry("content.xml") != null, zip.GetEntry("xl/workbook.bin") != null);
            stream.Position = 0;
            if (ods) return OdsReader.Read(stream, charts, ct);
            if (binary) return LegacySheets.ReadBinary(stream, ct);
            var sheets = XlsxReader.Read(stream, ct);
            if (!charts) return sheets;
            var drawn = SpreadsheetCharts.Xlsx(path);
            return sheets.Select(s => drawn.TryGetValue(s.Name, out var c) ? s with { Charts = c } : s).ToList();
        }
        // Compound files, and the bare streams of the oldest Excel versions.
        if (Starts(0xD0, 0xCF, 0x11, 0xE0) || count > 1 && head[0] == 0x09 && head[1] is 0x00 or 0x02 or 0x04 or 0x08) return LegacySheets.ReadBinary(stream, ct);

        bool wide = Starts(0xFF, 0xFE) || Starts(0xFE, 0xFF);
        var start = (wide ? Starts(0xFF, 0xFE) ? Encoding.Unicode : Encoding.BigEndianUnicode : Encoding.UTF8).GetString(head, 0, count).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (start.StartsWith('<'))
        {
            if (LegacySheets.IsXmlSpreadsheet(start)) return LegacySheets.ReadXml(stream, ct);
            return LegacySheets.ReadHtml(stream, Path.GetFileNameWithoutExtension(path), ct);
        }
        // Reports "exported to Excel" are often just tab-separated text.
        if (head.AsSpan(0, count).IndexOfAnyInRange((byte)1, (byte)8) < 0 && (wide || head.AsSpan(0, count).IndexOf((byte)0) < 0) && start.AsSpan().IndexOfAny("\t,;|") >= 0)
            return [DelimitedText.Read(path, Path.GetFileNameWithoutExtension(path), start.Contains('\t'), ct)];
        throw new ConversionException("This isn't a spreadsheet, or the file is damaged.");
    }

    /// <summary>Drops trailing empty rows and cells.</summary>
    static void Trim(List<object?[]> rows)
    {
        static bool Empty(object? v) => v is null or string { Length: 0 };
        for (int r = 0; r < rows.Count; r++)
        {
            int used = Array.FindLastIndex(rows[r], v => !Empty(v)) + 1;
            if (used < rows[r].Length) rows[r] = rows[r][..used];
        }
        int last = rows.FindLastIndex(r => r.Length > 0);
        if (last + 1 < rows.Count) rows.RemoveRange(last + 1, rows.Count - last - 1);
    }

    // ------------------------------------------------------------------ writing

    public static void Write(List<Sheet> sheets, string path, string ext, string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (ext)
        {
            case "csv": WriteDelimited(First(sheets, ext), path, ',', ct); break;
            case "tsv": WriteDelimited(First(sheets, ext), path, '\t', ct); break;
            case "json": WriteJson(Shown(sheets), path, ct); break;
            case "xlsx": XlsxWriter.Write(sheets, path, ct); break;
            case "ods": OdsWriter.Write(sheets, path, ct); break;
            case "html": WriteHtml(Shown(sheets), path, title, ct); break;
            case "txt": WriteText(Shown(sheets), path, ct); break;
            case "md": WriteMarkdown(Shown(sheets), path, ct); break;
            case "pdf": SheetPdf.Write(Shown(sheets), path, title, ct); break;
            default: throw new ConversionException($"Spreadsheets can't be saved as {ext.ToUpperInvariant()} without Office or LibreOffice.");
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The sheets a reader would see: hidden ones stay out, and so do blank ones next to real ones.</summary>
    static List<Sheet> Shown(List<Sheet> sheets)
    {
        var visible = sheets.Where(s => !s.Hidden).ToList();
        if (visible.Count == 0) visible = sheets;
        var filled = visible.Where(s => !s.IsBlank).ToList();
        return filled.Count > 0 ? filled : visible.Take(1).ToList();
    }

    /// <summary>Formats with room for one table take the first visible sheet that has something in it.</summary>
    static Sheet First(List<Sheet> sheets, string ext)
    {
        var shown = Shown(sheets);
        if (shown.Count > 1) Log.Write($"Only the sheet \"{shown[0].Name}\" was saved: {ext.ToUpperInvariant()} holds one table ({shown.Count} sheets in the workbook).");
        return shown[0];
    }

    /// <summary>A value as people see it in the spreadsheet.</summary>
    public static string Display(object? v) => v switch
    {
        null => "",
        string s => s,
        double d => NumberFormat.General(d),
        NumberCell n => NumberFormat.Display(n.Value, n.Format),
        ErrorCell e => e.Code,
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static bool IsNumeric(object? v) => v is double or NumberCell or long or int or decimal or float || v is string s && s.Length < 24 && DelimitedText.LooksNumeric(s);

    static StreamWriter Create(string path, bool bom) => new(path, false, new UTF8Encoding(bom), 1 << 16);

    static void WriteDelimited(Sheet sheet, string path, char delimiter, CancellationToken ct)
    {
        SheetLimits.Grid(sheet, 25_000_000, delimiter == ',' ? "CSV" : "TSV");
        // The BOM makes Excel read UTF-8 correctly.
        using var writer = Create(path, bom: true);
        int width = sheet.ColumnCount;
        foreach (var row in sheet.Rows)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < width; i++)
            {
                if (i > 0) writer.Write(delimiter);
                var text = Display(i < row.Length ? row[i] : null);
                if (text.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 || text.StartsWith(' ') || text.EndsWith(' '))
                    text = "\"" + text.Replace("\"", "\"\"") + "\"";
                writer.Write(text);
            }
            writer.Write("\r\n");
        }
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>The first row names the fields; several sheets become an object of tables.</summary>
    static void WriteJson(List<Sheet> sheets, string path, CancellationToken ct)
    {
        foreach (var sheet in sheets) SheetLimits.Grid(sheet, 10_000_000, "JSON");
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (sheets.Count == 1) Records(sheets[0]);
        else
        {
            var names = Unique(sheets.Select(s => s.Name));
            json.WriteStartObject();
            for (int i = 0; i < sheets.Count; i++)
            {
                json.WritePropertyName(names[i]);
                Records(sheets[i]);
            }
            json.WriteEndObject();
        }

        void Records(Sheet sheet)
        {
            json.WriteStartArray();
            int width = sheet.ColumnCount;
            if (width > 0)
            {
                // Values beyond the headings get headings of their own, so nothing is lost.
                var headings = sheet.Rows[0];
                var fields = Unique(Enumerable.Range(0, width).Select(i => Display(i < headings.Length ? headings[i] : null).Trim() is { Length: > 0 } s ? s : $"column{i + 1}"))
                    .Select(f => JsonEncodedText.Encode(f, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).ToList();
                // Delimited text doesn't say what's a number: a column of plain numbers is, anything else stays as written.
                var numbers = new bool[width];
                if (sheet.Delimiter != null)
                    for (int c = 0; c < width; c++)
                        numbers[c] = sheet.Rows.Skip(1).All(r => c >= r.Length || r[c] is not string s || DelimitedText.IsJsonNumber(s))
                            && (sheet.Delimiter != ';' || !DelimitedText.MaybeThousands(sheet, c));
                foreach (var row in sheet.Rows.Skip(1))
                {
                    ct.ThrowIfCancellationRequested();
                    json.WriteStartObject();
                    for (int c = 0; c < width; c++)
                    {
                        json.WritePropertyName(fields[c]);
                        Value(c < row.Length ? row[c] : null, numbers[c]);
                    }
                    json.WriteEndObject();
                    if (json.BytesPending > 1 << 16) json.Flush();
                }
            }
            json.WriteEndArray();
        }

        void Value(object? v, bool numeric)
        {
            switch (v)
            {
                case null: json.WriteNullValue(); break;
                case string s when numeric: json.WriteRawValue(s); break;
                case string s: json.WriteStringValue(s); break;
                case bool b: json.WriteBooleanValue(b); break;
                case long l: json.WriteNumberValue(l); break;
                case int i: json.WriteNumberValue(i); break;
                case decimal m: json.WriteNumberValue(m); break;
                case double d when double.IsFinite(d): Number(d); break;
                case NumberCell n when NumberFormat.IsDate(n.Format): json.WriteStringValue(IsoDate(n)); break;
                case NumberCell n when !double.IsFinite(n.Value) || NumberFormat.IsCode(n.Format): json.WriteStringValue(Display(n)); break;
                case NumberCell n: Number(n.Value); break;
                default: json.WriteStringValue(Display(v)); break;
            }
        }

        void Number(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) json.WriteNumberValue((long)d);
            else json.WriteNumberValue(d);
        }
    }

    /// <summary>Dates in JSON are ISO 8601, whatever the cell shows.</summary>
    static string IsoDate(NumberCell n)
    {
        var (date, time, elapsed) = NumberFormat.DateParts(n.Format);
        if (elapsed || n.Value is < 0 or >= NumberFormat.MaxSerial || n.Value < 1 && date) return Display(n);
        var value = NumberFormat.ToDate(n.Value);
        if (!date) return value.ToString(value.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return value.ToString(value.TimeOfDay != TimeSpan.Zero ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Repeated names get a number: "value", "value_2".</summary>
    static List<string> Unique(IEnumerable<string> names)
    {
        var list = names.ToList();
        var taken = list.ToHashSet();
        var used = new HashSet<string>();
        for (int i = 0; i < list.Count; i++)
        {
            if (used.Add(list[i])) continue;
            int n = 2;
            while (taken.Contains($"{list[i]}_{n}")) n++;
            list[i] = $"{list[i]}_{n}";
            taken.Add(list[i]);
            used.Add(list[i]);
        }
        return list;
    }

    // ------------------------------------------------------------------ pages and text

    static void WriteHtml(List<Sheet> sheets, string path, string title, CancellationToken ct)
    {
        foreach (var sheet in sheets) SheetLimits.Grid(sheet, 10_000_000, "a web page");
        using var writer = Create(path, bom: false);
        writer.Write("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>" + WebUtility.HtmlEncode(title) + "</title>\n<style>\n");
        writer.Write("body { font-family: Calibri, Arial, sans-serif; font-size: 11pt; margin: 2em; }\n");
        writer.Write("table { border-collapse: collapse; margin-bottom: 2em; }\ntd { border: 1px solid #bbb; padding: 3px 8px; white-space: pre-wrap; }\n");
        writer.Write("td.n { text-align: right; }\n</style>\n</head>\n<body>\n");
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            if (sheets.Count > 1) writer.Write("<h2>" + WebUtility.HtmlEncode(sheet.Name) + "</h2>\n");
            int width = sheet.ColumnCount;
            if (width > 0) writer.Write("<table>\n");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                if (sheet.HiddenRows.Contains(r)) continue;
                var row = sheet.Rows[r];
                writer.Write("<tr>");
                for (int c = 0; c < width; c++)
                {
                    if (sheet.HiddenColumns.Contains(c)) continue;
                    var v = c < row.Length ? row[c] : null;
                    writer.Write(IsNumeric(v) ? "<td class=\"n\">" : "<td>");
                    writer.Write(WebUtility.HtmlEncode(Display(v)));
                    writer.Write("</td>");
                }
                writer.Write("</tr>\n");
            }
            if (width > 0) writer.Write("</table>\n");
            foreach (var chart in sheet.Charts)
                writer.Write($"<p><img alt=\"Chart\" style=\"max-width:100%\" width=\"{(int)(chart.Width * 4 / 3)}\" src=\"data:{MediaType(chart)};base64,{System.Convert.ToBase64String(chart.Data)}\"></p>\n");
        }
        writer.Write("</body>\n</html>\n");
    }

    public static string MediaType(DImage image) => image.Ext.ToLowerInvariant() switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", _ => "image/png" };

    static void WriteText(List<Sheet> sheets, string path, CancellationToken ct)
    {
        using var writer = Create(path, bom: false);
        for (int i = 0; i < sheets.Count; i++)
        {
            if (i > 0) writer.Write("\r\n");
            if (sheets.Count > 1) writer.Write(sheets[i].Name + "\r\n\r\n");
            foreach (var row in sheets[i].Rows)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(string.Join('\t', row.Select(v => Display(v).ReplaceLineEndings(" "))) + "\r\n");
            }
        }
    }

    static void WriteMarkdown(List<Sheet> sheets, string path, CancellationToken ct)
    {
        foreach (var sheet in sheets) SheetLimits.Grid(sheet, 10_000_000, "Markdown");
        using var writer = Create(path, bom: false);
        for (int i = 0; i < sheets.Count; i++)
        {
            if (i > 0) writer.Write("\n");
            if (sheets.Count > 1) writer.Write($"## {Escape(sheets[i].Name)}\n\n");
            int width = sheets[i].ColumnCount;
            for (int r = 0; r < sheets[i].Rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                var row = sheets[i].Rows[r];
                writer.Write("| " + string.Join(" | ", Enumerable.Range(0, width).Select(c => Escape(Display(c < row.Length ? row[c] : null)))) + " |\n");
                if (r == 0) writer.Write("|" + string.Concat(Enumerable.Range(0, width).Select(c => Numbers(sheets[i], c) ? " ---: |" : " --- |")) + "\n");
            }
        }

        static bool Numbers(Sheet sheet, int c) => sheet.Rows.Skip(1).Take(100).Any(r => c < r.Length && r[c] != null) && sheet.Rows.Skip(1).Take(100).All(r => c >= r.Length || r[c] == null || IsNumeric(r[c]));
        static string Escape(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.ReplaceLineEndings("\n"))
            {
                if (c == '\n') sb.Append("<br>");
                else if (c is '|' or '\\' or '*' or '_' or '`' or '<' or '[') sb.Append('\\').Append(c);
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}

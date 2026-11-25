using System.Globalization;
using System.Text;

namespace ConvertWheel.Conversion.Documents;

/// <summary>CSV, TSV and other delimited text. Values stay exactly as written; <see cref="Types"/> decides what a workbook may treat as numbers.</summary>
internal static class DelimitedText
{
    const string Candidates = ",;\t|";
    const int Block = 64 * 1024;

    // ------------------------------------------------------------------ reading

    public static Sheet Read(string path, string name, bool tabs, CancellationToken ct)
    {
        using var reader = new StreamReader(path, DetectEncoding(path, ct), detectEncodingFromByteOrderMarks: true);
        // The beginning is enough to see how the file is laid out.
        var buffer = new char[4 * Block];
        int length = reader.ReadBlock(buffer);
        var start = new string(buffer, 0, length);

        int skip = 0;
        char delimiter = Declared(start, ref skip) ?? (tabs && start.Contains('\t') ? '\t' : Detect(start, wholeFile: length < buffer.Length));
        return new Sheet(name, Parse(start[skip..], reader, delimiter, new SheetLimits(), ct)) { Delimiter = delimiter };
    }

    /// <summary>A byte order mark, else UTF-16 by its zero bytes, else UTF-8 when the whole file is valid UTF-8, else the Windows code page.</summary>
    static Encoding DetectEncoding(string path, CancellationToken ct)
    {
        TextDecoding.EnsureRegistered();
        using var stream = File.OpenRead(path);
        var bytes = new byte[Block];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count >= 2 && bytes[0] + bytes[1] == 0xFF + 0xFE && bytes[0] >= 0xFE) return Encoding.Unicode; // the reader follows the mark itself
        int even = 0, odd = 0, sample = Math.Min(count, 4096);
        for (int i = 0; i < sample; i++)
            if (bytes[i] == 0) { if (i % 2 == 0) even++; else odd++; }
        if (odd > sample / 6 && even <= odd / 8) return Encoding.Unicode;
        if (even > sample / 6 && odd <= even / 8) return Encoding.BigEndianUnicode;
        if (even + odd > 0) throw new ConversionException("This isn't a text table, or the file is damaged.");

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        var chars = new char[Block + 8];
        try
        {
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                strict.GetChars(bytes, 0, count, chars, 0, flush: false);
                count = stream.Read(bytes);
            }
            strict.GetChars([], 0, 0, chars, 0, flush: true);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            int page = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(page is 0 or 65001 ? 1252 : page);
        }
    }

    /// <summary>Excel's "sep=;" first line names the delimiter (and isn't data).</summary>
    static char? Declared(string text, ref int skip)
    {
        int i = text.StartsWith('"') ? 1 : 0;
        if (text.Length < i + 5 || string.Compare(text, i, "sep=", 0, 4, StringComparison.OrdinalIgnoreCase) != 0) return null;
        char delimiter = text[i + 4];
        int end = i + 5;
        if (i > 0 && end < text.Length && text[end] == '"') end++;
        if (end < text.Length && text[end] is not ('\r' or '\n') || delimiter is '\r' or '\n' or '"' || char.IsLetterOrDigit(delimiter)) return null;
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        skip = end;
        return delimiter;
    }

    /// <summary>The character that splits the most rows into the same number of columns.</summary>
    static char Detect(string text, bool wholeFile)
    {
        char best = ',';
        double bestScore = 0;
        foreach (var candidate in Candidates)
        {
            var rows = Parse(text, null, candidate, new SheetLimits(), CancellationToken.None, maxRows: 200);
            // The sample may end in the middle of a row.
            if (!wholeFile && rows.Count is > 1 and < 200) rows.RemoveAt(rows.Count - 1);
            var counts = rows.Where(r => r.Length > 1 || r[0] != null).Select(r => r.Length).ToList();
            if (counts.Where(c => c > 1).GroupBy(c => c).MaxBy(g => g.Count()) is not { } common) continue;
            // Consistency decides; the number of columns only settles close calls.
            double consistent = common.Count() / (double)counts.Count;
            double score = consistent * consistent * (1 + Math.Min(common.Key, 50) * 0.01);
            if (candidate == ',' && DecimalCommas(text)) score *= 0.5;
            if (score > bestScore + 1e-9) (best, bestScore) = (candidate, score);
        }
        return best;
    }

    /// <summary>"1,5;2,5": commas that only ever sit between digits, in a file with another likely delimiter, may be decimal commas.</summary>
    static bool DecimalCommas(string text)
    {
        if (text.IndexOfAny([';', '\t', '|']) < 0) return false;
        int commas = 0, between = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ',') continue;
            commas++;
            if (i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1])) between++;
        }
        return commas > 0 && between >= commas * 0.9;
    }

    static List<object?[]> Parse(string start, TextReader? rest, char delimiter, SheetLimits limits, CancellationToken ct, int maxRows = int.MaxValue)
    {
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var field = new StringBuilder();
        bool quoted = false, quotedField = false, rowStarted = false, skipLineFeed = false;
        bool quote = false; // the last character was a quote inside quotes: the end, or the first half of a doubled one
        var block = start.ToCharArray();
        int length = block.Length;

        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < length; i++)
            {
                char c = block[i];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (c == '\n') continue;
                }
                rowStarted = true;
                if (quoted && !quote)
                {
                    if (c == '"') quote = true;
                    else field.Append(c);
                    continue;
                }
                if (quote)
                {
                    quote = false;
                    if (c == '"') { field.Append('"'); continue; }
                    quoted = false;
                }
                if (c == '"' && field.Length == 0 && !quotedField) quoted = quotedField = true;
                else if (c == delimiter) EndField();
                else if (c is '\r' or '\n')
                {
                    skipLineFeed = c == '\r';
                    EndRow();
                    if (rows.Count >= maxRows) return rows;
                }
                else field.Append(c);
            }
            if (rest == null) break;
            if (block.Length != Block) block = new char[Block];
            length = rest.ReadBlock(block);
        }
        if (rowStarted) EndRow();
        return rows;

        void EndField()
        {
            var value = field.ToString();
            limits.Text(value);
            row.Add(value.Length == 0 ? null : value);
            field.Clear();
            quotedField = false;
        }
        void EndRow()
        {
            EndField();
            limits.Cells(row.Count);
            rows.Add([.. row]);
            row.Clear();
            rowStarted = false;
        }
    }

    // ------------------------------------------------------------------ what the values mean

    /// <summary>
    /// For each column, how a workbook should hold its text. A value becomes a number or date only when the cell will
    /// show exactly the same characters, so "00123", "+49…", "1e5" and long IDs stay text. The exception is a column
    /// that plainly uses decimal commas ("1,5"), which becomes numbers in the spreadsheet's own notation.
    /// </summary>
    public static Func<string, object?>[] Types(Sheet sheet, CancellationToken ct)
    {
        var types = new Func<string, object?>[sheet.ColumnCount];
        for (int c = 0; c < types.Length; c++)
        {
            ct.ThrowIfCancellationRequested();
            bool code = false, other = false, point = false, comma = false;
            // The first row usually holds the headings.
            for (int r = sheet.Rows.Count > 1 ? 1 : 0; r < sheet.Rows.Count; r++)
            {
                if (c >= sheet.Rows[r].Length || sheet.Rows[r][c] is not string text) continue;
                // Three digits after the mark could be thousands; any other number can't.
                if (IsPlain(text, out int decimals)) point |= decimals > 0 && (decimals != 3 || text.TrimStart('-')[0] == '0');
                else if (IsCommaNumber(text, out decimals)) comma |= decimals > 0 && (decimals != 3 || text.TrimStart('-')[0] == '0' || text.Contains('.'));
                else
                {
                    other = true;
                    code |= ContainsOnlyDigits(text); // 00123, or too long to be a number
                }
            }
            types[c] = sheet.Delimiter != ',' && comma && !point && !other ? CommaNumber
                : code ? text => text // postcodes and IDs are text throughout, even the ones without a leading zero
                : sheet.Delimiter == ';' && !point ? text => text.Contains('.') && IsPlain(text, out _) ? text : Plain(text) // 1.234 could be either kind of number
                : Plain;
        }
        return types;
    }

    /// <summary>Digits with at most one decimal point that a cell shows back unchanged.</summary>
    static bool IsPlain(string text, out int decimals)
    {
        decimals = 0;
        int start = text.StartsWith('-') ? 1 : 0, i = start;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        int whole = i - start;
        if (whole == 0 || whole > 1 && text[start] == '0') return false;
        if (i < text.Length && text[i] == '.')
        {
            decimals = text.Length - i - 1;
            if (decimals == 0) return false;
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++) { }
        }
        // "-0" would lose its sign.
        return i == text.Length && whole + decimals <= 15 && (start == 0 || text.AsSpan().IndexOfAnyInRange('1', '9') >= 0);
    }

    /// <summary>"1,5", "1.234,56" or "-12".</summary>
    static bool IsCommaNumber(string text, out int decimals)
    {
        int comma = text.IndexOf(',');
        decimals = comma < 0 ? 0 : text.Length - comma - 1;
        if (comma >= 0 && (decimals == 0 || !ContainsOnlyDigits(text[(comma + 1)..]))) return false;
        var groups = (comma < 0 ? text : text[..comma]).TrimStart('-').Split('.');
        if (text.LastIndexOf('-') > 0 || groups.Any(g => g.Length == 0 || !ContainsOnlyDigits(g)) || groups.Skip(1).Any(g => g.Length != 3)) return false;
        if (groups.Length > 1 && groups[0].Length > 3 || groups[0][0] == '0' && (groups[0].Length > 1 || groups.Length > 1)) return false;
        return groups.Sum(g => g.Length) + decimals <= 15;
    }

    static bool ContainsOnlyDigits(string text) => text.Length > 0 && text.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    static object? CommaNumber(string text)
    {
        if (!IsCommaNumber(text, out int decimals) || !double.TryParse(text.Replace(".", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return Plain(text);
        if (decimals == 0 && !text.Contains('.')) return n;
        return new NumberCell(n, (text.Contains('.') ? "#,##0" : "0") + (decimals > 0 ? "." + new string('0', decimals) : ""));
    }

    static object? Plain(string text)
    {
        if (IsPlain(text, out int decimals))
        {
            double n = double.Parse(text, CultureInfo.InvariantCulture);
            return NumberFormat.General(n) == text ? n : new NumberCell(n, "0." + new string('0', decimals));
        }
        if (text == "TRUE") return true;
        if (text == "FALSE") return false;
        if (text.Length is 10 or 19 && text[4] == '-' && DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && NumberFormat.ToSerial(date) is { } serial)
            return new NumberCell(serial, text.Length == 10 ? NumberFormat.IsoDate : text[10] == 'T' ? "yyyy-mm-dd\"T\"hh:mm:ss" : NumberFormat.IsoDateTime);
        return text;
    }

    /// <summary>Whether the text looks like a number, for lining it up on the right.</summary>
    public static bool LooksNumeric(string text) => IsPlain(text, out _) || IsCommaNumber(text, out _);

    /// <summary>Every point in the column has three digits after it: "1.234" could be thousands, as in the German "1.234,50".</summary>
    public static bool MaybeThousands(Sheet sheet, int column)
    {
        bool any = false;
        foreach (var row in sheet.Rows.Skip(1))
        {
            if (column >= row.Length || row[column] is not string text || !IsPlain(text, out int decimals) || decimals == 0) continue;
            if (decimals != 3 || text.TrimStart('-')[0] == '0') return false;
            any = true;
        }
        return any;
    }

    /// <summary>Whether a bare JSON number would keep every character.</summary>
    public static bool IsJsonNumber(string text) => IsPlain(text, out int decimals) && (decimals == 0 || NumberFormat.General(double.Parse(text, CultureInfo.InvariantCulture)) == text);
}

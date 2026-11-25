using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Shows numbers the way Excel would for a cell's number format ("0.00%", "dd/mm/yyyy", "[h]:mm", "# ?/?"…).
/// Numbers always use a decimal point; only Excel's "system date" formats follow the PC's own date order.
/// </summary>
internal static class NumberFormat
{
    /// <summary>Excel's built-in formats 14 and 22, which follow the system's short date.</summary>
    public const string SystemDate = "mm-dd-yy", SystemDateTime = "m/d/yy h:mm";
    public const string IsoDate = "yyyy-mm-dd", IsoDateTime = "yyyy-mm-dd hh:mm:ss";

    static readonly Dictionary<int, string> BuiltIn = new()
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [5] = "\"$\"#,##0_);(\"$\"#,##0)", [6] = "\"$\"#,##0_);[Red](\"$\"#,##0)",
        [7] = "\"$\"#,##0.00_);(\"$\"#,##0.00)", [8] = "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [12] = "# ?/?", [13] = "# ??/??", [14] = SystemDate, [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = SystemDateTime, [37] = "#,##0_);(#,##0)", [38] = "#,##0_);[Red](#,##0)",
        [39] = "#,##0.00_);(#,##0.00)", [40] = "#,##0.00_);[Red](#,##0.00)", [41] = "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
        [42] = "_(\"$\"* #,##0_);_(\"$\"* (#,##0);_(\"$\"* \"-\"_);_(@_)", [43] = "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
        [44] = "_(\"$\"* #,##0.00_);_(\"$\"* (#,##0.00);_(\"$\"* \"-\"??_);_(@_)", [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0",
        [48] = "##0.0E+0", [49] = "@",
        // East Asian editions use these for dates.
        [27] = SystemDate, [28] = SystemDate, [29] = SystemDate, [30] = SystemDate, [31] = SystemDate, [36] = SystemDate,
    };

    /// <summary>The format code of a built-in number format id; null for General.</summary>
    public static string? BuiltInCode(int id) => BuiltIn.GetValueOrDefault(id);

    // ------------------------------------------------------------------ parsing

    internal enum Part
    {
        Literal, Digit, Point, Comma, Percent, Exponent, Slash, General, TextValue,
        Year, Month, Minute, Day, Hour, Second, Fraction, AmPm, ElapsedHours, ElapsedMinutes, ElapsedSeconds,
    }

    /// <param name="Text">A literal's text, a digit placeholder ('0', '#', '?' or a fixed denominator), or the letters of a date part.</param>
    internal sealed record Token(Part Part, string Text);

    internal sealed class Section
    {
        public readonly List<Token> Tokens = [];
        public string? Operator;
        public double Operand;
        public CultureInfo Culture = CultureInfo.CurrentCulture;
        public bool IsDate, SystemDate, SystemTime;
        public bool IsText => Has(Part.TextValue);
        public bool IsGeneral => Has(Part.General);
        public bool Has(Part part) => Tokens.Any(t => t.Part == part);
    }

    static readonly ConcurrentDictionary<string, Section[]> Parsed = new();

    internal static Section[] Sections(string format)
    {
        if (Parsed.TryGetValue(format, out var known)) return known;
        var result = new List<Section>();
        int start = 0;
        bool quoted = false, bracket = false;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c is '\\' or '_' or '*') i++;
            else if (c == '[') bracket = true;
            else if (c == ']') bracket = false;
            else if (c == ';' && !bracket)
            {
                result.Add(ParseSection(format[start..i]));
                start = i + 1;
            }
        }
        result.Add(ParseSection(format[Math.Min(start, format.Length)..]));
        var sections = result.Take(4).ToArray();
        // A workbook may carry thousands of junk formats; don't hold on to them for the app's lifetime.
        if (Parsed.Count > 2000) Parsed.Clear();
        Parsed[format] = sections;
        return sections;
    }

    static Section ParseSection(string code)
    {
        var section = new Section();
        var tokens = section.Tokens;
        void Literal(string text) { if (text.Length > 0) tokens.Add(new Token(Part.Literal, text)); }
        bool At(int i, string word) => string.Compare(code, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

        for (int i = 0; i < code.Length;)
        {
            char c = code[i];
            char lower = char.ToLowerInvariant(c);
            if (c == '"')
            {
                int end = code.IndexOf('"', i + 1);
                if (end < 0) end = code.Length;
                Literal(code[(i + 1)..end]);
                i = end + 1;
            }
            else if (c == '\\') { Literal(i + 1 < code.Length ? code[(i + 1)..(i + 2)] : ""); i += 2; }
            else if (c == '_') { Literal(" "); i += 2; } // a gap as wide as the next character
            else if (c == '*') i += 2; // fills the cell with the next character: nothing to fill in text
            else if (c == '[')
            {
                int end = code.IndexOf(']', i);
                if (end < 0) { Literal(code[i..]); break; }
                Bracket(code[(i + 1)..end]);
                i = end + 1;
            }
            else if (c is '0' or '#' or '?') { tokens.Add(new Token(Part.Digit, c.ToString())); i++; }
            else if (c == '.')
            {
                // ".00" after seconds is a fraction of a second.
                int j = i + 1;
                while (j < code.Length && code[j] == '0') j++;
                if (j > i + 1 && tokens.LastOrDefault(t => t.Part != Part.Literal)?.Part is Part.Second or Part.ElapsedSeconds)
                {
                    tokens.Add(new Token(Part.Fraction, code[(i + 1)..j]));
                    i = j;
                }
                else { tokens.Add(new Token(Part.Point, ".")); i++; }
            }
            else if (c == ',') { tokens.Add(new Token(Part.Comma, ",")); i++; }
            else if (c == '%') { tokens.Add(new Token(Part.Percent, "%")); i++; }
            else if (c == '@') { tokens.Add(new Token(Part.TextValue, "@")); i++; }
            else if (c == '/' && tokens.Any(t => t.Part == Part.Digit) && !tokens.Any(t => t.Part is Part.Day or Part.Month or Part.Year))
            {
                tokens.Add(new Token(Part.Slash, "/"));
                i++;
                // "# ?/8": a fixed denominator.
                int j = i;
                while (j < code.Length && char.IsAsciiDigit(code[j])) j++;
                if (j > i && code[i] != '0') { tokens.Add(new Token(Part.Digit, code[i..j])); i = j; }
            }
            else if (lower == 'e' && i + 1 < code.Length && code[i + 1] is '+' or '-') { tokens.Add(new Token(Part.Exponent, code.Substring(i, 2))); i += 2; }
            else if (At(i, "General")) { tokens.Add(new Token(Part.General, "")); i += 7; }
            else if (At(i, "AM/PM")) { tokens.Add(new Token(Part.AmPm, "AM/PM")); i += 5; }
            else if (At(i, "A/P")) { tokens.Add(new Token(Part.AmPm, "A/P")); i += 3; }
            else if (lower is 'y' or 'm' or 'd' or 'h' or 's' or 'e' or 'b' or 'g')
            {
                int j = i;
                while (j < code.Length && char.ToLowerInvariant(code[j]) == lower) j++;
                var part = lower switch { 'y' or 'e' or 'b' => Part.Year, 'm' => Part.Month, 'd' => Part.Day, 'h' => Part.Hour, _ => Part.Second };
                // "e" is the year in an era, "g" the era's name.
                if (lower != 'g') tokens.Add(new Token(part, lower == 'e' ? "yyyy" : new string(lower, j - i)));
                i = j;
            }
            else { Literal(c.ToString()); i++; }
        }

        // "m" next to hours or seconds means minutes.
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Part != Part.Month || tokens[i].Text.Length > 2) continue;
            var before = tokens.Take(i).LastOrDefault(t => t.Part is not (Part.Literal or Part.AmPm))?.Part;
            var after = tokens.Skip(i + 1).FirstOrDefault(t => t.Part is not (Part.Literal or Part.AmPm))?.Part;
            if (before is Part.Hour or Part.ElapsedHours || after is Part.Second or Part.ElapsedSeconds) tokens[i] = new Token(Part.Minute, tokens[i].Text);
        }
        section.IsDate = section.SystemDate || section.SystemTime || tokens.Any(t => t.Part is >= Part.Year and <= Part.ElapsedSeconds);
        return section;

        void Bracket(string inner)
        {
            if (inner.Length == 0) return;
            if (inner[0] == '$')
            {
                int dash = inner.LastIndexOf('-');
                Literal(dash > 0 ? inner[1..dash] : inner[1..]);
                if (dash < 0) return;
                var id = inner[(dash + 1)..];
                if (id.Equals("F800", StringComparison.OrdinalIgnoreCase)) section.SystemDate = true;
                else if (id.Equals("F400", StringComparison.OrdinalIgnoreCase)) section.SystemTime = true;
                else section.Culture = CultureOf(id) ?? section.Culture;
            }
            else if (inner[0] is '<' or '>' or '=')
            {
                int n = inner.Length > 1 && inner[1] is '=' or '>' ? 2 : 1;
                if (double.TryParse(inner[n..], NumberStyles.Float, CultureInfo.InvariantCulture, out var operand)) (section.Operator, section.Operand) = (inner[..n], operand);
            }
            else if (inner.All(ch => ch is 'h' or 'H')) tokens.Add(new Token(Part.ElapsedHours, inner));
            else if (inner.All(ch => ch is 'm' or 'M')) tokens.Add(new Token(Part.ElapsedMinutes, inner));
            else if (inner.All(ch => ch is 's' or 'S')) tokens.Add(new Token(Part.ElapsedSeconds, inner));
            // Anything else is a colour.
        }
    }

    static CultureInfo? CultureOf(string id)
    {
        try
        {
            // "409" and the like; the upper digits choose calendars and digit shapes.
            if (int.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid)) return (lcid & 0xFFFF) == 0 ? null : CultureInfo.GetCultureInfo(lcid & 0xFFFF);
            return id.Contains('-') && id.Length <= 10 ? CultureInfo.GetCultureInfo(id) : null;
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ what kind of format it is

    /// <summary>The format shows dates or times.</summary>
    public static bool IsDate(string? format) => format is { Length: > 0 } && Sections(format)[0].IsDate;

    /// <summary>Which of a date and a time the format shows, and whether the time is a running total ("[h]:mm").</summary>
    public static (bool date, bool time, bool elapsed) DateParts(string format)
    {
        var s = Sections(format)[0];
        bool elapsed = s.Tokens.Any(t => t.Part is Part.ElapsedHours or Part.ElapsedMinutes or Part.ElapsedSeconds);
        return (s.SystemDate || s.Tokens.Any(t => t.Part is Part.Year or Part.Month or Part.Day),
            elapsed || s.SystemTime || s.Tokens.Any(t => t.Part is Part.Hour or Part.Minute or Part.Second), elapsed);
    }

    /// <summary>Formats like "00000" or "000-00-0000": the digits are a code, and the leading zeros matter.</summary>
    public static bool IsCode(string? format)
    {
        if (string.IsNullOrEmpty(format)) return false;
        var s = Sections(format)[0];
        if (s.IsDate || s.IsText || s.IsGeneral || s.Tokens.Any(t => t.Part is Part.Point or Part.Comma or Part.Percent or Part.Exponent or Part.Slash)) return false;
        return s.Tokens.Count(t => t.Part == Part.Digit) > 1 && s.Tokens.First(t => t.Part == Part.Digit).Text == "0";
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>The value as Excel would show it in a cell that's wide enough.</summary>
    public static string Display(double value, string? format) => format switch
    {
        SystemDate => Format(value, LocalShortDate),
        SystemDateTime => Format(value, LocalShortDate + " h:mm"),
        _ => Format(value, format),
    };

    static string LocalShortDate => CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.Replace("'", "").Replace('M', 'm');

    public static string Format(double value, string? format)
    {
        if (string.IsNullOrEmpty(format) || !double.IsFinite(value)) return General(value);
        try
        {
            var sections = Sections(format);
            var (section, showSign) = Choose(sections, value);
            if (section == null) return General(value);
            if (section.Tokens.Count == 0 && !section.IsDate) return sections.Length == 1 ? General(value) : ""; // ";;;" hides the value
            if (section.IsDate) return Date(value, section) ?? General(value);
            var text = Number(Math.Abs(value), section).Trim();
            // A value that rounds to nothing shows no minus sign.
            return showSign && value < 0 && text.Any(c => c is >= '1' and <= '9') ? "-" + text : text;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException) { return General(value); }
    }

    /// <summary>Text under a format such as <c>@" kg"</c>; most formats leave text alone.</summary>
    public static string FormatText(string value, string? format)
    {
        if (string.IsNullOrEmpty(format) || !format.Contains('@')) return value;
        var section = Sections(format).LastOrDefault(s => s.IsText);
        if (section == null || section.Tokens.All(t => t.Part == Part.TextValue || t.Part == Part.Literal && t.Text.Trim().Length == 0)) return value;
        return string.Concat(section.Tokens.Select(t => t.Part == Part.TextValue ? value : t.Part == Part.Literal ? t.Text : "")).Trim();
    }

    static (Section? section, bool sign) Choose(Section[] sections, double value)
    {
        // The fourth section, or a last one holding "@", is for text.
        var numeric = sections.Where((s, i) => i < 3 && !s.IsText).ToArray();
        if (numeric.Length == 0 || sections[0].IsText) return (null, false);
        if (numeric.Any(s => s.Operator != null))
        {
            foreach (var s in numeric)
                if (s.Operator == null || Matches(s, value))
                    return (s, !(s.Operator is "<" or "<=" && s.Operand <= 0));
            return (numeric[^1], true);
        }
        if (numeric.Length >= 2 && value < 0) return (numeric[1], false);
        if (numeric.Length >= 3 && value == 0) return (numeric[2], false);
        return (numeric[0], true);
    }

    static bool Matches(Section s, double value) => s.Operator switch
    {
        "<" => value < s.Operand, "<=" => value <= s.Operand, ">" => value > s.Operand, ">=" => value >= s.Operand,
        "=" => value == s.Operand, "<>" => value != s.Operand, _ => true,
    };

    /// <summary>The number with no format: up to 15 significant digits, whole numbers in full, and no exponent unless it's enormous or tiny.</summary>
    public static string General(double d)
    {
        if (!double.IsFinite(d)) return d.ToString(CultureInfo.InvariantCulture);
        if (d == 0) return "0";
        double size = Math.Abs(d);
        if (size < 1e21 && d == Math.Floor(d)) return new BigInteger(d).ToString(CultureInfo.InvariantCulture);
        var text = d.ToString("G15", CultureInfo.InvariantCulture);
        if (!text.Contains('E') || size is < 1e-15 or >= 1e21) return text;
        return ((decimal)d).ToString("0.#############################", CultureInfo.InvariantCulture);
    }

    static string Number(double value, Section section)
    {
        var tokens = section.Tokens;
        if (section.IsGeneral) return string.Concat(tokens.Select(t => t.Part == Part.General ? General(value) : t.Part is Part.Literal or Part.Percent ? t.Text : ""));
        if (!section.Has(Part.Digit)) return string.Concat(tokens.Where(t => t.Part is Part.Literal or Part.Percent).Select(t => t.Text));
        // As decimal, so 0.07 × 100 is exactly 7.
        foreach (var _ in tokens.Where(t => t.Part == Part.Percent)) value = value < 1e26 ? (double)((decimal)value * 100) : value * 100;
        if (section.Has(Part.Slash)) return Fraction(value, tokens);
        if (section.Has(Part.Exponent)) return Scientific(value, tokens);

        int point = tokens.FindIndex(t => t.Part == Part.Point);
        int wholeEnd = point < 0 ? tokens.Count : point;
        int firstDigit = tokens.FindIndex(t => t.Part == Part.Digit);
        int lastWhole = tokens.Take(wholeEnd).ToList().FindLastIndex(t => t.Part == Part.Digit);
        int lastDigit = tokens.FindLastIndex(t => t.Part == Part.Digit);
        // A comma between digits groups by thousands; commas right after the digits divide by a thousand each.
        bool grouped = false;
        for (int i = firstDigit + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Part != Part.Comma) continue;
            if (i < lastWhole) grouped = true;
            else if (i > lastDigit || i > lastWhole && i < wholeEnd) value /= 1000;
        }

        var decimals = tokens.Skip(wholeEnd).Where(t => t.Part == Part.Digit).ToList();
        var (whole, fraction) = Digits(value, decimals.Count);
        var sb = new StringBuilder();
        AppendWhole(sb, tokens.Take(wholeEnd), whole, grouped);
        if (point < 0) return sb.ToString();

        // Trailing '#' digits that are zero aren't shown.
        int shown = fraction.Length;
        while (shown > 0 && fraction[shown - 1] == '0' && decimals[shown - 1].Text == "#") shown--;
        sb.Append('.');
        int digit = 0;
        foreach (var t in tokens.Skip(point + 1))
        {
            if (t.Part is Part.Literal or Part.Percent) sb.Append(t.Text);
            if (t.Part != Part.Digit) continue;
            if (digit < shown) sb.Append(t.Text == "?" && fraction[digit..].All(c => c == '0') ? ' ' : fraction[digit]);
            digit++;
        }
        return sb.ToString();
    }

    /// <summary>Fills the whole-number placeholders from the right; any extra digits go before the first placeholder.</summary>
    static void AppendWhole(StringBuilder sb, IEnumerable<Token> tokens, string whole, bool grouped)
    {
        var list = tokens.ToList();
        int placeholders = list.Count(t => t.Part == Part.Digit);
        if (whole == "0") whole = "";
        int left = Math.Max(whole.Length, placeholders); // digit positions still to write
        bool first = true;
        foreach (var t in list)
        {
            if (t.Part is Part.Literal or Part.Percent) sb.Append(t.Text);
            if (t.Part != Part.Digit) continue;
            for (int take = first ? left - placeholders + 1 : 1; take > 0; take--)
            {
                int index = whole.Length - left;
                left--;
                if (index >= 0) sb.Append(whole[index]);
                else if (t.Text == "0") sb.Append('0');
                else
                {
                    if (t.Text == "?") sb.Append(' ');
                    continue;
                }
                if (grouped && left > 0 && left % 3 == 0) sb.Append(',');
            }
            first = false;
        }
        if (placeholders == 0) sb.Append(whole); // ".00"
    }

    /// <summary>The value rounded half up to some decimals (from 15 significant digits, as Excel does), split at the point.</summary>
    static (string whole, string fraction) Digits(double value, int places)
    {
        if (value >= 7e27) return (new BigInteger(value).ToString(CultureInfo.InvariantCulture), new string('0', places));
        var rounded = Math.Round((decimal)value, Math.Min(places, 28), MidpointRounding.AwayFromZero);
        var text = rounded.ToString("F" + Math.Min(places, 28), CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        return dot < 0 ? (text, new string('0', places)) : (text[..dot], text[(dot + 1)..].PadRight(places, '0'));
    }

    static string Scientific(double value, List<Token> tokens)
    {
        int e = tokens.FindIndex(t => t.Part == Part.Exponent);
        int point = tokens.FindIndex(t => t.Part == Part.Point);
        if (point > e) point = -1;
        int wholeEnd = point < 0 ? e : point;
        int wholeDigits = Math.Max(1, tokens.Take(wholeEnd).Count(t => t.Part == Part.Digit));
        int places = point < 0 ? 0 : tokens.Skip(point).Take(e - point).Count(t => t.Part == Part.Digit);
        int exponent = value == 0 ? 0 : (int)Math.Floor(Math.Log10(value));
        // More than one whole digit is engineering notation: the exponent moves in those steps.
        exponent = (int)Math.Floor(exponent / (double)wholeDigits) * wholeDigits;
        var (whole, fraction) = Digits(value / Math.Pow(10, exponent), places);
        if (whole.Length > wholeDigits) // 9.996 rounds up to 10.00
        {
            exponent += wholeDigits;
            (whole, fraction) = Digits(value / Math.Pow(10, exponent), places);
        }
        var sb = new StringBuilder();
        AppendWhole(sb, tokens.Take(wholeEnd), whole, false);
        if (sb.Length == 0) sb.Append('0');
        if (point >= 0) sb.Append('.').Append(fraction);
        sb.Append('E').Append(exponent < 0 ? "-" : tokens[e].Text.EndsWith('+') ? "+" : "");
        int width = tokens.Skip(e + 1).Count(t => t.Part == Part.Digit && t.Text == "0");
        sb.Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, width), '0'));
        foreach (var t in tokens.Skip(e + 1).Where(t => t.Part is Part.Literal or Part.Percent)) sb.Append(t.Text);
        return sb.ToString();
    }

    static string Fraction(double value, List<Token> tokens)
    {
        int slash = tokens.FindIndex(t => t.Part == Part.Slash);
        int numeratorStart = slash;
        while (numeratorStart > 0 && tokens[numeratorStart - 1].Part == Part.Digit) numeratorStart--;
        var denominatorTokens = tokens.Skip(slash + 1).TakeWhile(t => t.Part == Part.Digit).ToList();
        bool hasWhole = tokens.Take(numeratorStart).Any(t => t.Part == Part.Digit);

        double whole = hasWhole ? Math.Floor(value) : 0, rest = value - whole;
        long numerator, denominator;
        if (denominatorTokens.Count == 1 && long.TryParse(denominatorTokens[0].Text, out var known) && known > 0)
            (numerator, denominator) = ((long)Math.Round(rest * known, MidpointRounding.AwayFromZero), known);
        else (numerator, denominator) = Nearest(rest, (long)Math.Pow(10, Math.Clamp(denominatorTokens.Count, 1, 4)) - 1);
        if (hasWhole && numerator == denominator) (whole, numerator) = (whole + 1, 0);

        var sb = new StringBuilder();
        foreach (var t in tokens.Take(numeratorStart).Where(t => t.Part == Part.Literal && t.Text.Trim().Length > 0)) sb.Append(t.Text);
        if (hasWhole && (whole != 0 || numerator == 0)) sb.Append(new BigInteger(whole).ToString(CultureInfo.InvariantCulture));
        if (numerator != 0 || !hasWhole)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(numerator.ToString(CultureInfo.InvariantCulture)).Append('/').Append(denominator.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var t in tokens.Skip(slash + 1 + denominatorTokens.Count).Where(t => t.Part is Part.Literal or Part.Percent)) sb.Append(t.Text);
        return sb.ToString();
    }

    static (long n, long d) Nearest(double value, long limit)
    {
        (long n, long d) best = ((long)Math.Round(value), 1);
        double error = Math.Abs(value - best.n);
        for (long d = 2; d <= limit && error > 1e-12; d++)
        {
            long n = (long)Math.Round(value * d, MidpointRounding.AwayFromZero);
            double e = Math.Abs(value - (double)n / d);
            if (e < error - 1e-13) (best, error) = ((n, d), e);
        }
        return best;
    }

    // ------------------------------------------------------------------ dates

    /// <summary>The end of 9999, where Excel's dates stop.</summary>
    public const double MaxSerial = 2958466;

    static readonly DateTime Epoch = DateTime.FromOADate(0);

    /// <summary>A serial number (days since 1900, counting Excel's made-up 29 February 1900) as a date.</summary>
    public static DateTime ToDate(double serial) => Epoch.AddMilliseconds(Math.Round((serial is >= 1 and < 60 ? serial + 1 : serial) * 86400000));

    /// <summary>A date as an Excel serial number, or null before 1900.</summary>
    public static double? ToSerial(DateTime date)
    {
        double days = (date - Epoch).TotalDays;
        if (days < 61) days--;
        return days >= 1 ? days : null;
    }

    static string? Date(double serial, Section section)
    {
        bool elapsed = section.Tokens.Any(t => t.Part is Part.ElapsedHours or Part.ElapsedMinutes or Part.ElapsedSeconds);
        // Excel shows ##### for dates it doesn't have.
        if (serial < 0 && !elapsed || Math.Abs(serial) >= MaxSerial) return null;

        // Round to what's shown, so 08:29:59.9999997 reads 8:30.
        int places = section.Tokens.Where(t => t.Part == Part.Fraction).Select(t => Math.Min(t.Text.Length, 3)).DefaultIfEmpty(0).Max();
        bool seconds = section.SystemTime || section.Has(Part.Second) || section.Has(Part.ElapsedSeconds);
        long step = seconds ? places switch { 0 => 1000, 1 => 100, 2 => 10, _ => 1 } : 1;
        long ms = ((long)Math.Round(Math.Abs(serial) * 86400000) + step / 2) / step * step;

        long days = ms / 86400000, ofDay = ms % 86400000;
        // Day 0 is Excel's "0 January 1900" and day 60 its "29 February 1900".
        var date = days == 0 ? new DateTime(1900, 1, 1) : Epoch.AddDays(days < 60 ? days + 1 : days);
        int day = days switch { 0 => 0, 60 => 29, _ => date.Day };
        int hour = (int)(ofDay / 3600000), minute = (int)(ofDay / 60000 % 60), second = (int)(ofDay / 1000 % 60), milli = (int)(ofDay % 1000);
        // Excel counts its first day, 1 January 1900, as a Sunday.
        var weekday = (DayOfWeek)((days + 6) % 7);
        var names = section.Culture.DateTimeFormat;
        var local = CultureInfo.CurrentCulture;
        if (section.SystemDate) return date.ToString(local.DateTimeFormat.LongDatePattern, local);
        if (section.SystemTime) return Epoch.AddMilliseconds(ofDay).ToString(local.DateTimeFormat.LongTimePattern, local);

        bool twelve = section.Has(Part.AmPm);
        var sb = new StringBuilder();
        if (serial < 0) sb.Append('-');
        foreach (var t in section.Tokens)
        {
            string Pad(long n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, t.Text.Length), '0');
            sb.Append(t.Part switch
            {
                Part.Year => t.Text.Length <= 2 ? (date.Year % 100).ToString("00", CultureInfo.InvariantCulture) : date.Year.ToString("0000", CultureInfo.InvariantCulture),
                Part.Month => t.Text.Length switch
                {
                    1 or 2 => Pad(days == 60 ? 2 : date.Month),
                    3 => names.GetAbbreviatedMonthName(date.Month),
                    5 => names.GetMonthName(date.Month)[..1],
                    _ => names.GetMonthName(date.Month),
                },
                Part.Day => t.Text.Length switch
                {
                    1 or 2 => Pad(day),
                    3 => names.GetAbbreviatedDayName(weekday),
                    _ => names.GetDayName(weekday),
                },
                Part.Hour => Pad(twelve ? (hour + 11) % 12 + 1 : hour),
                Part.Minute => Pad(minute),
                Part.Second => Pad(second),
                Part.Fraction => "." + milli.ToString("000", CultureInfo.InvariantCulture).PadRight(t.Text.Length, '0')[..t.Text.Length],
                Part.AmPm => t.Text == "A/P" ? hour < 12 ? "A" : "P" : hour < 12 ? "AM" : "PM",
                Part.ElapsedHours => Pad(ms / 3600000),
                Part.ElapsedMinutes => Pad(ms / 60000),
                Part.ElapsedSeconds => Pad(ms / 1000),
                Part.TextValue or Part.General or Part.Exponent => "",
                Part.Digit => t.Text == "0" ? "0" : "",
                _ => t.Text,
            });
        }
        return sb.ToString().Trim();
    }
}

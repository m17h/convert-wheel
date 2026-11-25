using System.Globalization;
using System.Text;
using System.Xml.Linq;
using static ConvertWheel.Conversion.Documents.NumberFormat;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Translates number formats between Excel's codes and OpenDocument's data styles.</summary>
internal static class OdfNumberStyles
{
    public static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";

    // ------------------------------------------------------------------ OpenDocument -> Excel

    /// <summary>The Excel code of a data style; null when it's General or can't be expressed.</summary>
    public static string? ToExcel(XElement style, Func<string, XElement?> find)
    {
        var main = Section(style);
        if (main == null) return null;
        string? Mapped(string condition)
        {
            var map = style.Elements(St + "map").FirstOrDefault(m => ((string?)m.Attribute(St + "condition"))?.Replace(" ", "") == condition);
            return (string?)map?.Attribute(St + "apply-style-name") is { } name && find(name) is { } other ? Section(other) : null;
        }
        if (Mapped("value()>=0") is { } positive) return $"{positive};{main}";
        if (Mapped("value()>0") is { } above && Mapped("value()<0") is { } below) return $"{above};{below};{main}";
        return main.Length == 0 ? null : main;
    }

    static string? Section(XElement style)
    {
        if (style.Name == Number + "text-style" || style.Name == Number + "boolean-style") return null;
        bool elapsed = (string?)style.Attribute(Number + "truncate-on-overflow") == "false";
        var sb = new StringBuilder();
        foreach (var e in style.Elements())
        {
            bool longStyle = (string?)e.Attribute(Number + "style") == "long";
            int Count(string name, int fallback) => int.TryParse((string?)e.Attribute(Number + name), out int n) ? Math.Clamp(n, 0, 20) : fallback;
            if (e.Name.Namespace != Number) continue;
            switch (e.Name.LocalName)
            {
                case "number":
                    int decimals = Count("decimal-places", -1), digits = Count("min-integer-digits", 1);
                    if (decimals < 0 && !Is(e, "grouping") && digits <= 1) { sb.Append("General"); break; }
                    sb.Append(Is(e, "grouping")
                        ? digits switch { 0 => "#,###", 1 => "#,##0", 2 => "#,#00", 3 => "#,000", _ => new string('0', digits - 3) + ",000" }
                        : digits == 0 ? "#" : new string('0', digits));
                    int shown = Count("min-decimal-places", decimals);
                    if (decimals > 0) sb.Append('.').Append('0', Math.Min(shown, decimals)).Append('#', decimals - Math.Min(shown, decimals));
                    for (double f = Factor(e); f >= 1000; f /= 1000) sb.Append(',');
                    break;
                case "scientific-number":
                    sb.Append('0', Math.Max(1, Count("min-integer-digits", 1)));
                    if (Count("decimal-places", 0) is > 0 and var places) sb.Append('.').Append('0', places);
                    sb.Append("E+").Append('0', Math.Max(1, Count("min-exponent-digits", 2)));
                    break;
                case "fraction":
                    if (Count("min-integer-digits", -1) >= 0) sb.Append("# ");
                    sb.Append('?', Math.Max(1, Count("min-numerator-digits", 1))).Append('/');
                    if ((string?)e.Attribute(Number + "denominator-value") is { } denominator && denominator.All(char.IsAsciiDigit)) sb.Append(denominator);
                    else sb.Append('?', Math.Max(1, Count("min-denominator-digits", 1)));
                    break;
                case "text": sb.Append(Quote(e.Value)); break;
                case "currency-symbol": sb.Append(Quote(e.Value)); break;
                case "year": sb.Append(longStyle ? "yyyy" : "yy"); break;
                case "month": sb.Append(Is(e, "textual") ? longStyle ? "mmmm" : "mmm" : longStyle ? "mm" : "m"); break;
                case "day": sb.Append(longStyle ? "dd" : "d"); break;
                case "day-of-week": sb.Append(longStyle ? "dddd" : "ddd"); break;
                case "hours": sb.Append(elapsed ? longStyle ? "[hh]" : "[h]" : longStyle ? "hh" : "h"); elapsed = false; break;
                case "minutes": sb.Append(elapsed ? "[mm]" : longStyle ? "mm" : "m"); elapsed = false; break;
                case "seconds":
                    sb.Append(elapsed ? "[ss]" : longStyle ? "ss" : "s");
                    elapsed = false;
                    if (Count("decimal-places", 0) is > 0 and var parts) sb.Append('.').Append('0', parts);
                    break;
                case "am-pm": sb.Append("AM/PM"); break;
                case "text-content": sb.Append('@'); break;
            }
        }
        return sb.ToString() is "General" ? "" : sb.ToString();
    }

    static bool Is(XElement e, string name) => (string?)e.Attribute(Number + name) == "true";

    static double Factor(XElement e) => double.TryParse((string?)e.Attribute(Number + "display-factor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1e12 ? f : 1;

    /// <summary>Characters Excel shows as they are need no quotes.</summary>
    static string Quote(string text) =>
        text.All(c => c is '-' or '/' or ':' or ' ' or '(' or ')' or '%' or '$' or '+') ? text : "\"" + text.Replace("\"", "") + "\"";

    // ------------------------------------------------------------------ Excel -> OpenDocument

    /// <summary>Data styles for a format code: the style itself (last) and any it refers to. Empty for General.</summary>
    public static List<XElement> ToOdf(string code, string name)
    {
        var sections = Sections(code);
        // LibreOffice's own shape: the last section is the style, earlier ones are chosen by conditions.
        var numeric = sections.Take(3).Where(s => !s.IsText).ToList();
        if (numeric.Count == 0 || numeric.Any(s => s.Operator != null || s.Tokens.Count == 0)) numeric = numeric.Take(1).ToList();
        var styles = new List<XElement>();
        for (int i = 0; i < numeric.Count; i++)
        {
            bool last = i == numeric.Count - 1;
            if (Style(numeric[i], last ? name : $"{name}P{i}") is not { } style) return [];
            if (last)
                for (int k = 0; k < i; k++)
                    style.Add(new XElement(St + "map", new XAttribute(St + "condition", k == 1 ? "value()<0" : numeric.Count == 3 ? "value()>0" : "value()>=0"),
                        new XAttribute(St + "apply-style-name", $"{name}P{k}")));
            styles.Add(style);
        }
        return styles;
    }

    static XElement? Style(Section section, string name)
    {
        var nameAttribute = new XAttribute(St + "name", name);
        if (section.IsGeneral || section.Tokens.Count == 0) return null;
        static XElement Literal(string text) => new(Number + "text", text);
        static XAttribute Long() => new(Number + "style", "long");

        if (section.IsDate)
        {
            if (section.SystemDate || section.SystemTime) return null;
            var style = new XElement(Number + (section.Tokens.Any(t => t.Part is Part.Year or Part.Month or Part.Day) ? "date-style" : "time-style"), nameAttribute);
            foreach (var t in section.Tokens)
            {
                object? lengthy = t.Text.Length is 2 or 4 ? Long() : null;
                XElement? part = t.Part switch
                {
                    Part.Year => new XElement(Number + "year", t.Text.Length > 2 ? Long() : null),
                    Part.Month => new XElement(Number + "month", lengthy, t.Text.Length > 2 ? new XAttribute(Number + "textual", "true") : null),
                    Part.Day => new XElement(Number + (t.Text.Length > 2 ? "day-of-week" : "day"), lengthy),
                    Part.Hour or Part.ElapsedHours => new XElement(Number + "hours", lengthy),
                    Part.Minute or Part.ElapsedMinutes => new XElement(Number + "minutes", lengthy),
                    Part.Second or Part.ElapsedSeconds => new XElement(Number + "seconds", lengthy),
                    Part.AmPm => new XElement(Number + "am-pm"),
                    Part.Fraction => null,
                    _ => Literal(t.Text),
                };
                if (t.Part == Part.Fraction) style.Elements(Number + "seconds").LastOrDefault()?.SetAttributeValue(Number + "decimal-places", t.Text.Length);
                else style.Add(part);
            }
            if (section.Tokens.Any(t => t.Part is Part.ElapsedHours or Part.ElapsedMinutes or Part.ElapsedSeconds)) style.SetAttributeValue(Number + "truncate-on-overflow", "false");
            return style;
        }

        var tokens = section.Tokens;
        var result = new XElement(Number + (section.Has(Part.Percent) ? "percentage-style" : "number-style"), nameAttribute);
        int first = tokens.FindIndex(t => t.Part is Part.Digit or Part.Point);
        int last = tokens.FindLastIndex(t => t.Part is Part.Digit or Part.Point);
        foreach (var t in tokens.Take(first < 0 ? tokens.Count : first)) result.Add(Literal(t.Text));
        if (first < 0) return result;

        var body = tokens.Skip(first).Take(last - first + 1).ToList();
        int point = body.FindIndex(t => t.Part == Part.Point), exponent = body.FindIndex(t => t.Part == Part.Exponent), slash = body.FindIndex(t => t.Part == Part.Slash);
        int Zeros(IEnumerable<Token> part) => part.Count(t => t.Part == Part.Digit && t.Text == "0");
        int DigitCount(IEnumerable<Token> part) => part.Count(t => t.Part == Part.Digit);
        if (slash >= 0)
        {
            int numerator = slash;
            while (numerator > 0 && body[numerator - 1].Part == Part.Digit) numerator--;
            var fraction = new XElement(Number + "fraction", new XAttribute(Number + "min-numerator-digits", Math.Max(1, slash - numerator)));
            if (DigitCount(body.Take(numerator)) > 0) fraction.Add(new XAttribute(Number + "min-integer-digits", 0));
            if (body.Count > slash + 1 && body[slash + 1].Text is { } fixedText && int.TryParse(fixedText, out int fixedDenominator) && fixedDenominator > 0)
                fraction.Add(new XAttribute(Number + "denominator-value", fixedDenominator));
            else fraction.Add(new XAttribute(Number + "min-denominator-digits", Math.Max(1, DigitCount(body.Skip(slash)))));
            result.Add(fraction);
        }
        else if (exponent >= 0)
        {
            int wholeEnd = point >= 0 && point < exponent ? point : exponent;
            result.Add(new XElement(Number + "scientific-number",
                new XAttribute(Number + "decimal-places", DigitCount(body.Take(exponent).Skip(wholeEnd))),
                new XAttribute(Number + "min-integer-digits", Math.Max(1, Zeros(body.Take(wholeEnd)))),
                new XAttribute(Number + "min-exponent-digits", Math.Max(1, Zeros(body.Skip(exponent))))));
        }
        else
        {
            var whole = body.Take(point < 0 ? body.Count : point).ToList();
            var decimals = point < 0 ? [] : body.Skip(point).ToList();
            var number = new XElement(Number + "number",
                new XAttribute(Number + "decimal-places", DigitCount(decimals)), new XAttribute(Number + "min-decimal-places", Zeros(decimals)),
                new XAttribute(Number + "min-integer-digits", Zeros(whole)));
            if (whole.Any(t => t.Part == Part.Comma)) number.Add(new XAttribute(Number + "grouping", "true"));
            int scale = tokens.Skip(last + 1).TakeWhile(t => t.Part == Part.Comma).Count();
            if (scale is > 0 and < 4) number.Add(new XAttribute(Number + "display-factor", Math.Pow(1000, scale).ToString("0", CultureInfo.InvariantCulture)));
            // Text between the digits ("000-00-0000") sits a number of digits from the right.
            for (int i = 0; i < whole.Count; i++)
                if (whole[i].Part == Part.Literal)
                    number.Add(new XElement(Number + "embedded-text", new XAttribute(Number + "position", DigitCount(whole.Skip(i))), whole[i].Text));
            result.Add(number);
        }
        foreach (var t in tokens.Skip(last + 1).Where(t => t.Part is Part.Literal or Part.Percent)) result.Add(Literal(t.Text));
        return result;
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion;

/// <summary>Subtitles: SRT, WebVTT, ASS/SSA and YouTube SBV in; SRT, VTT, ASS and plain text out.</summary>
internal static partial class Subtitles
{
    // Styling inside a cue's text is marked with control characters, so a literal "<" stays a "<".
    const char Open = (char)1, Close = (char)2;

    /// <summary>Times are whole milliseconds. Text uses \n for line breaks and marked b, i and u tags.</summary>
    /// <param name="Align">Where the cue sits, as on a number pad (8 is top centre); 2 is the usual bottom centre.</param>
    sealed record Cue(long Start, long End, string Text, int Align = 2);

    public static void Convert(string input, string output, string ext)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(input)).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
        text = text.Replace(Open.ToString(), "").Replace(Close.ToString(), "");
        var cues = (Path.GetExtension(input).ToLowerInvariant() switch
        {
            ".vtt" => ReadVtt(text),
            ".ass" or ".ssa" => ReadAss(text),
            ".sbv" => ReadSbv(text),
            _ => ReadSrt(text),
        }).OrderBy(c => c.Start).ToList();
        if (cues.Count == 0) throw new ConversionException("No subtitles were found in this file.");

        var result = ext switch
        {
            "srt" => WriteSrt(cues),
            "vtt" => WriteVtt(cues),
            "ass" => WriteAss(cues),
            _ => string.Join("\n\n", cues.Select(c => Tags(c.Text, _ => "")).Where(t => t.Length > 0)) + "\n",
        };
        File.WriteAllText(output, result.Replace("\n", "\r\n"), new UTF8Encoding(ext == "srt"));
    }

    // ------------------------------------------------------------------ reading

    [GeneratedRegex(@"(?<s>(\d{1,4}:)?\d{1,3}:\d{1,2}[,.]\d{1,9})\s*--?>\s*(?<e>(\d{1,4}:)?\d{1,3}:\d{1,2}[,.]\d{1,9})(?<rest>.*)")]
    private static partial Regex ArrowTimes();

    /// <summary>Goes by the time lines, so a blank line inside a cue doesn't cut it short.</summary>
    static List<Cue> ReadSrt(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ArrowTimes().Match(lines[i]);
            if (!m.Success) continue;
            int end = i + 1;
            while (end < lines.Length && !ArrowTimes().IsMatch(lines[end])) end++;
            int next = end;
            // The number above the next time line belongs to that cue.
            if (end < lines.Length && end - 1 > i && lines[end - 1].Trim() is { Length: > 0 } number && number.All(char.IsAsciiDigit)) end--;
            var body = string.Join("\n", lines[(i + 1)..end].Select(l => l.TrimEnd()).SkipWhile(l => l.Length == 0)).Trim();
            if (Time(m.Groups["s"].Value) is long start && Time(m.Groups["e"].Value) is long stop)
            {
                int align = 2;
                body = Overrides().Replace(body, o =>
                {
                    if (AlignTag().Match(o.Value) is { Success: true } a) align = a.Groups[1].Value[0] - '0';
                    return AssTags(o.Value);
                });
                cues.Add(new Cue(start, stop, FromHtml(body, entities: false), align));
            }
            i = next - 1;
        }
        return cues;
    }

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex Overrides();

    [GeneratedRegex(@"\\an([1-9])")]
    private static partial Regex AlignTag();

    static List<Cue> ReadVtt(string text)
    {
        var cues = new List<Cue>();
        foreach (var lines in Blocks(text))
        {
            int timeLine = lines.FindIndex(l => ArrowTimes().IsMatch(l));
            // NOTE, STYLE and REGION blocks have no times.
            if (timeLine < 0 || timeLine > 1 && !lines[0].StartsWith("WEBVTT")) continue;
            var m = ArrowTimes().Match(lines[timeLine]);
            if (Time(m.Groups["s"].Value) is not long start || Time(m.Groups["e"].Value) is not long stop) continue;
            var line = LineSetting().Match(m.Groups["rest"].Value);
            int align = line.Success && double.TryParse(line.Groups[1].Value, CultureInfo.InvariantCulture, out var position) && position >= 0 && position < (line.Groups[2].Length > 0 ? 40 : 4) ? 8 : 2;
            cues.Add(new Cue(start, stop, FromHtml(string.Join("\n", lines.Skip(timeLine + 1)).Trim(), entities: true), align));
        }
        return cues;
    }

    [GeneratedRegex(@"\bline:(-?[\d.]+)(%?)")]
    private static partial Regex LineSetting();

    static List<Cue> ReadSbv(string text)
    {
        var cues = new List<Cue>();
        foreach (var lines in Blocks(text))
        {
            var times = lines[0].Split(',');
            if (times.Length != 2 || Time(times[0]) is not long start || Time(times[1]) is not long stop) continue;
            cues.Add(new Cue(start, stop, FromHtml(string.Join("\n", lines.Skip(1)).Replace("[br]", "\n").Trim(), entities: false)));
        }
        return cues;
    }

    /// <summary>Runs of non-blank lines.</summary>
    static IEnumerable<List<string>> Blocks(string text)
    {
        var block = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0) { block.Add(line); continue; }
            if (block.Count > 0) yield return block;
            block = [];
        }
        if (block.Count > 0) yield return block;
    }

    static List<Cue> ReadAss(string text)
    {
        var cues = new List<Cue>();
        string[]? format = null;
        bool events = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!events) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                format = line[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToArray();
            else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) && format != null)
            {
                var fields = line[9..].Split(',', format.Length);
                string Field(string name) { int i = Array.IndexOf(format, name); return i >= 0 && i < fields.Length ? fields[i].Trim() : ""; }
                if (Time(Field("start")) is not long start || Time(Field("end")) is not long stop) continue;
                int align = 2;
                bool drawing = false;
                var sb = new StringBuilder();
                // Override blocks: keep italic/bold/underline and the corner, drop other positioning, effects and vector drawings.
                foreach (var part in AssParts().Split(Field("text")))
                {
                    if (!part.StartsWith('{') || !part.EndsWith('}'))
                    {
                        if (!drawing) sb.Append(part);
                        continue;
                    }
                    if (AlignTag().Match(part) is { Success: true } a) align = a.Groups[1].Value[0] - '0';
                    if (DrawingTag().Match(part) is { Success: true } d) drawing = d.Groups[1].Value != "0";
                    sb.Append(AssTags(part));
                }
                var body = sb.ToString().Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
                if (body.Length > 0) cues.Add(new Cue(start, stop, CloseTags(body), align));
            }
        }
        return cues;
    }

    [GeneratedRegex(@"(\{[^}]*\})")]
    private static partial Regex AssParts();

    [GeneratedRegex(@"\\p(\d+)")]
    private static partial Regex DrawingTag();

    [GeneratedRegex(@"\\([ibu])(\d+)")]
    private static partial Regex StyleTag();

    /// <summary>The styling in an ASS override block such as {\i1}, as marked tags.</summary>
    static string AssTags(string block) =>
        string.Concat(StyleTag().Matches(block).Select(tag => $"{Open}{(tag.Groups[2].Value is "0" or "400" ? "/" : "")}{tag.Groups[1].Value}{Close}"));

    [GeneratedRegex(@"<(?<end>/?)(?<tag>b|i|u|strong|em)(\.[^>]*|\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlStyle();

    // Colours, voices (<v Bob>), classes (<c.yellow>) and karaoke times are dropped; other "<" is just text.
    [GeneratedRegex(@"</?(font|span|s|strike|v|c|lang|ruby|rt|p|div)([\s.][^<>]*)?/?>|<\d+:\d{2}[:.][\d.:]+>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOther();

    /// <summary>Keeps b/i/u (closing any left open), turns font colours etc. into plain text.</summary>
    static string FromHtml(string text, bool entities)
    {
        text = HtmlStyle().Replace(text, m => $"{Open}{m.Groups["end"].Value}{m.Groups["tag"].Value.ToLowerInvariant() switch { "strong" => "b", "em" => "i", var t => t }}{Close}");
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = HtmlOther().Replace(text, "");
        // "&amp;" last, so "&amp;lt;" stays "&lt;".
        text = text.Replace("&nbsp;", " ");
        if (entities || text.Contains("&lt;") || text.Contains("&gt;") || text.Contains("&amp;"))
            text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&lrm;", ((char)0x200E).ToString()).Replace("&rlm;", ((char)0x200F).ToString()).Replace("&amp;", "&");
        return CloseTags(text);
    }

    [GeneratedRegex("\u0001(/?)([biu])\u0002")]
    private static partial Regex MarkedTag();

    static string CloseTags(string text)
    {
        foreach (var tag in new[] { "b", "i", "u" })
        {
            int open = MarkedTag().Matches(text).Where(m => m.Groups[2].Value == tag).Sum(m => m.Groups[1].Length == 0 ? 1 : -1);
            for (int i = 0; i < open; i++) text += $"{Open}/{tag}{Close}";
        }
        return text;
    }

    /// <summary>Milliseconds, or null when it isn't a time.</summary>
    static long? Time(string value)
    {
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3 || !decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds > 100_000) return null;
        int minutes = 0, hours = 0;
        if (parts.Length >= 2 && !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return null;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return null;
        return hours * 3_600_000L + minutes * 60_000L + (long)Math.Round(seconds * 1000);
    }

    // ------------------------------------------------------------------ writing

    static string Stamp(long ms, char separator) => $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}";

    static string Tags(string text, Func<string, string> write) => MarkedTag().Replace(text, m => write(m.Groups[1].Value + m.Groups[2].Value));

    /// <summary>A cue's text without blank lines, which would end the cue early in SRT and WebVTT.</summary>
    static string Body(string text) => BlankLines().Replace(text.Trim('\n'), "\n");

    [GeneratedRegex(@"\n[ \t]*(?=\n)")]
    private static partial Regex BlankLines();

    static string WriteSrt(List<Cue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
            sb.Append(i + 1).Append('\n')
              .Append(Stamp(cues[i].Start, ',')).Append(" --> ").Append(Stamp(cues[i].End, ',')).Append('\n')
              .Append(cues[i].Align == 2 ? "" : $"{{\\an{cues[i].Align}}}")
              .Append(Tags(Body(cues[i].Text), t => $"<{t}>")).Append("\n\n");
        return sb.ToString();
    }

    static string WriteVtt(List<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
            sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append(c.Align switch { >= 7 => " line:0", >= 4 => " line:50%", _ => "" }).Append('\n')
              .Append(Tags(Body(c.Text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), t => $"<{t}>")).Append("\n\n");
        return sb.ToString();
    }

    static string WriteAss(List<Cue> cues)
    {
        static string AssTime(long ms)
        {
            long cs = (ms + 5) / 10;
            return $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
        }
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n");
        sb.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        sb.Append("Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1\n\n");
        sb.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var c in cues)
        {
            // Players hide anything in braces, so literal ones become look-alikes.
            var text = c.Text.Replace('{', (char)0xFF5B).Replace('}', (char)0xFF5D).Replace("\n", "\\N");
            text = Tags(text, t => $"{{\\{t.TrimStart('/')}{(t[0] == '/' ? 0 : 1)}}}");
            sb.Append($"Dialogue: 0,{AssTime(c.Start)},{AssTime(c.End)},Default,,0,0,0,,{(c.Align == 2 ? "" : $"{{\\an{c.Align}}}")}{text}\n");
        }
        return sb.ToString();
    }
}

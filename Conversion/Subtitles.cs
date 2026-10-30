using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion;

/// <summary>Subtitles: SRT, WebVTT, ASS/SSA and YouTube SBV in; SRT, VTT, ASS and plain text out.</summary>
internal static partial class Subtitles
{
    /// <summary>Text uses &lt;b&gt;, &lt;i&gt; and &lt;u&gt; tags for styling and \n for line breaks.</summary>
    sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public static void Convert(string input, string output, string ext)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(input)).Replace("\r\n", "\n").Replace('\r', '\n');
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
            _ => string.Join("\n\n", cues.Select(c => StripTags(c.Text))) + "\n",
        };
        File.WriteAllText(output, result.Replace("\n", "\r\n"), new UTF8Encoding(ext == "srt"));
    }

    // ------------------------------------------------------------------ reading

    [GeneratedRegex(@"(?<s>(\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<e>(\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex ArrowTimes();

    static List<Cue> ReadSrt(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            int timeLine = lines.FindIndex(l => ArrowTimes().IsMatch(l));
            if (timeLine < 0) continue;
            var m = ArrowTimes().Match(lines[timeLine]);
            var body = string.Join("\n", lines.Skip(timeLine + 1)).Trim();
            cues.Add(new Cue(Time(m.Groups["s"].Value), Time(m.Groups["e"].Value), NormalizeHtml(body)));
        }
        return cues;
    }

    static List<Cue> ReadVtt(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            if (block.StartsWith("WEBVTT") || block.StartsWith("NOTE") || block.StartsWith("STYLE") || block.StartsWith("REGION")) continue;
            var lines = block.Split('\n').ToList();
            int timeLine = lines.FindIndex(l => ArrowTimes().IsMatch(l));
            if (timeLine < 0) continue;
            var m = ArrowTimes().Match(lines[timeLine]);
            var body = string.Join("\n", lines.Skip(timeLine + 1)).Trim();
            // Drop voice/class spans (<v Bob>, <c.yellow>) and karaoke timestamps, keep b/i/u.
            body = Regex.Replace(body, @"</?(v|c|lang|ruby|rt)([ .][^>]*)?>|<\d+:\d{2}[:.][\d.:]+>", "");
            cues.Add(new Cue(Time(m.Groups["s"].Value), Time(m.Groups["e"].Value), NormalizeHtml(body)));
        }
        return cues;
    }

    static List<Cue> ReadSbv(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var lines = block.Split('\n');
            var times = lines[0].Split(',');
            if (times.Length != 2) continue;
            cues.Add(new Cue(Time(times[0]), Time(times[1]), NormalizeHtml(string.Join("\n", lines.Skip(1)).Trim())));
        }
        return cues;
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
                var body = Field("text");
                // Override blocks: keep italic/bold/underline, drop positioning and effects.
                body = Regex.Replace(body, @"\{([^}]*)\}", m =>
                {
                    var sb = new StringBuilder();
                    foreach (Match tag in Regex.Matches(m.Groups[1].Value, @"\\([ibu])([01])"))
                        sb.Append(tag.Groups[2].Value == "1" ? $"<{tag.Groups[1].Value}>" : $"</{tag.Groups[1].Value}>");
                    return sb.ToString();
                });
                body = body.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
                cues.Add(new Cue(Time(Field("start")), Time(Field("end")), NormalizeHtml(body)));
            }
        }
        return cues;
    }

    /// <summary>Keeps b/i/u (closing any left open), turns font colours etc. into plain text.</summary>
    static string NormalizeHtml(string text)
    {
        text = Regex.Replace(text, @"<(/?)(b|i|u)\b[^>]*>", m => $"\u0001{m.Groups[1].Value}{m.Groups[2].Value.ToLowerInvariant()}\u0002", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = text.Replace("\u0001", "<").Replace("\u0002", ">").Replace("&amp;", "&").Replace("&lt;", "‹").Replace("&gt;", "›").Replace("&nbsp;", " ");
        foreach (var tag in new[] { "b", "i", "u" })
        {
            int open = Regex.Matches(text, $"<{tag}>", RegexOptions.IgnoreCase).Count, close = Regex.Matches(text, $"</{tag}>", RegexOptions.IgnoreCase).Count;
            for (int i = close; i < open; i++) text += $"</{tag}>";
        }
        return text;
    }

    static string StripTags(string text) => Regex.Replace(text, "<[^>]+>", "");

    static TimeSpan Time(string value)
    {
        value = value.Trim().Replace(',', '.');
        var parts = value.Split(':');
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int minutes = parts.Length >= 2 ? int.Parse(parts[^2], CultureInfo.InvariantCulture) : 0;
        int hours = parts.Length >= 3 ? int.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
    }

    // ------------------------------------------------------------------ writing

    static string Stamp(TimeSpan t, char separator, bool hours = true) =>
        (hours ? $"{(int)t.TotalHours:00}:" : "") + $"{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";

    static string WriteSrt(List<Cue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
            sb.Append(i + 1).Append('\n')
              .Append(Stamp(cues[i].Start, ',')).Append(" --> ").Append(Stamp(cues[i].End, ',')).Append('\n')
              .Append(cues[i].Text).Append("\n\n");
        return sb.ToString();
    }

    static string WriteVtt(List<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
            sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append('\n')
              .Append(c.Text.Replace("&", "&amp;")).Append("\n\n");
        return sb.ToString();
    }

    static string WriteAss(List<Cue> cues)
    {
        static string AssTime(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n");
        sb.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        sb.Append("Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1\n\n");
        sb.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var c in cues)
        {
            var text = Regex.Replace(c.Text, "<(/?)([biu])>", m => $"{{\\{m.Groups[2].Value}{(m.Groups[1].Value == "/" ? 0 : 1)}}}").Replace("\n", "\\N");
            sb.Append($"Dialogue: 0,{AssTime(c.Start)},{AssTime(c.End)},Default,,0,0,0,,{text}\n");
        }
        return sb.ToString();
    }
}

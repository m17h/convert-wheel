using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// RTF through WPF's RTF engine, plus the parts WPF ignores: page breaks, headers and footers,
/// page-number fields and footnotes/endnotes. On the way in they're cut out of the RTF and replaced
/// with placeholder tokens; on the way out they're spliced back in as proper RTF groups.
/// </summary>
internal static partial class RichText
{
    const string PageBreak = "CWPAGEBREAKX", FieldPage = "CWFLDPAGEX", FieldPages = "CWFLDNUMPAGESX";
    static string NoteToken(int i) => $"CWNOTE{i}X";
    static string SegmentStart(int i) => $"CWSEGSTART{i}X";
    const string SegmentEnd = "CWSEGENDX";

    // ------------------------------------------------------------------ reading

    public static DDocument Read(string path)
    {
        var rtf = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var extracted = Extract(rtf);
        var doc = Load(extracted.Body, Path.GetFileNameWithoutExtension(path));

        DHeaderFooter? Part(params string[] names)
        {
            var content = names.Select(n => extracted.Groups.GetValueOrDefault(n)).FirstOrDefault(c => c != null);
            if (content == null) return null;
            var part = new DHeaderFooter();
            part.Blocks.AddRange(Restore(Load(extracted.Preamble + "{" + content + "}}", null).Blocks, []));
            return part;
        }
        doc.Header = Part("header", "headerr");
        doc.Footer = Part("footer", "footerr");
        doc.EvenHeader = extracted.Groups.ContainsKey("headerl") && extracted.FacingPages ? Part("headerl") : null;
        doc.EvenFooter = extracted.Groups.ContainsKey("footerl") && extracted.FacingPages ? Part("footerl") : null;
        if (extracted.TitlePage)
        {
            doc.DifferentFirstPage = true;
            doc.FirstHeader = Part("headerf") ?? new DHeaderFooter();
            doc.FirstFooter = Part("footerf") ?? new DHeaderFooter();
        }

        int footnote = 0, endnote = 0;
        var notes = extracted.Notes.Select(n =>
        {
            var note = new DNote(n.Endnote ? ++endnote : ++footnote, n.Endnote, TextStyle.Plain);
            note.Blocks.AddRange(Load(extracted.Preamble + "{" + n.Content + "}}", null).Blocks);
            return note;
        }).ToList();

        var blocks = Restore(doc.Blocks, notes);
        doc.Blocks.Clear();
        doc.Blocks.AddRange(blocks);
        doc.NormalizeDefaults();
        return doc;
    }

    static DDocument Load(string rtf, string? title)
    {
        var flow = new FlowDocument();
        using (var stream = new MemoryStream(Encoding.Latin1.GetBytes(rtf)))
            new TextRange(flow.ContentStart, flow.ContentEnd).Load(stream, DataFormats.Rtf);
        return FlowBridge.FromFlow(flow, title, normalize: false);
    }

    sealed record Extraction(string Body, string Preamble, Dictionary<string, string> Groups,
        List<(bool Endnote, string Content)> Notes, bool TitlePage, bool FacingPages);

    /// <summary>
    /// Walks the RTF once, lifting out header/footer and footnote groups, replacing page-number fields
    /// and page breaks with tokens WPF will keep as plain text.
    /// </summary>
    static Extraction Extract(string rtf)
    {
        var body = new StringBuilder(rtf.Length);
        var groups = new Dictionary<string, string>();
        var notes = new List<(bool, string)>();
        int preambleEnd = -1, depth = 0;
        bool titlePage = false, facing = false;

        for (int i = 0; i < rtf.Length;)
        {
            char c = rtf[i];
            if (c == '{')
            {
                var word = ControlWordAfter(rtf, i + 1);
                if (word is "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf" or "footnote" or "field")
                {
                    int end = MatchingBrace(rtf, i);
                    var inner = rtf[(i + 1)..end];
                    if (word == "footnote")
                    {
                        bool isEndnote = FindControlWord(inner, "ftnalt") >= 0;
                        // Fields and page breaks inside notes and headers need the same treatment as the body.
                        notes.Add((isEndnote, Extract(StripControlWord(inner, "footnote")).Body));
                        body.Append(' ').Append(NoteToken(notes.Count - 1)).Append(' ');
                    }
                    else if (word == "field")
                    {
                        var kind = FieldToken(inner);
                        if (kind == null) body.Append(rtf, i, end - i + 1);
                        else body.Append(' ').Append(kind).Append(' ');
                    }
                    else
                    {
                        if (preambleEnd < 0) preambleEnd = body.Length;
                        groups.TryAdd(word, Extract(StripControlWord(inner, word)).Body);
                    }
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '\\')
            {
                int binaryEnd = BinaryEnd(rtf, i);
                if (binaryEnd > i)
                {
                    body.Append(rtf, i, binaryEnd - i);
                    i = binaryEnd;
                    continue;
                }
                var word = ControlWordAfter(rtf, i);
                if (word is "page" or "sect")
                {
                    if (preambleEnd < 0 && word == "sect") { body.Append(c); i++; continue; }
                    body.Append(' ').Append(PageBreak).Append(' ');
                    i += 1 + word.Length;
                    if (i < rtf.Length && rtf[i] == ' ') i++;
                    continue;
                }
                if (word == "pagebb")
                {
                    body.Append("\\pagebb ").Append(PageBreak).Append(' ');
                    i += 1 + word.Length;
                    if (i < rtf.Length && rtf[i] == ' ') i++;
                    continue;
                }
                if (word == "titlepg") titlePage = true;
                if (word == "facingp") facing = true;
                // The body starts at the first top-level paragraph (Word also has \pard inside preamble groups).
                if (word == "pard" && preambleEnd < 0 && depth == 1) preambleEnd = body.Length;
                // Copy escapes whole, so an escaped brace doesn't look like a group.
                if (i + 1 < rtf.Length && rtf[i + 1] is '{' or '}' or '\\')
                {
                    body.Append(rtf, i, 2);
                    i += 2;
                    continue;
                }
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            body.Append(c);
            i++;
        }

        var text = body.ToString();
        var preamble = preambleEnd > 0 ? text[..preambleEnd] : "{\\rtf1\\ansi ";
        return new Extraction(text, preamble, groups, notes, titlePage, facing);
    }

    static string? ControlWordAfter(string rtf, int i)
    {
        if (i < rtf.Length && rtf[i] == '\\' && i + 2 < rtf.Length && rtf[i + 1] == '*') i += 2; // \* destination
        while (i < rtf.Length && rtf[i] is ' ' or '\r' or '\n') i++;
        if (i >= rtf.Length || rtf[i] != '\\') return null;
        int start = ++i;
        while (i < rtf.Length && char.IsAsciiLetterLower(rtf[i])) i++;
        return i > start ? rtf[start..i] : null;
    }

    static int BinaryEnd(string rtf, int at)
    {
        if (!rtf.AsSpan(at).StartsWith(@"\bin", StringComparison.Ordinal)) return at;
        int i = at + 4;
        if (i >= rtf.Length || !char.IsAsciiDigit(rtf[i])) return at;
        long count = 0;
        while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
            count = Math.Min(rtf.Length, count * 10 + rtf[i++] - '0');
        if (i < rtf.Length && rtf[i] == ' ') i++;
        return i + (int)Math.Min(count, rtf.Length - i);
    }

    static int FindControlWord(string rtf, string word)
    {
        for (int i = 0; i < rtf.Length; i++)
        {
            if (rtf[i] != '\\') continue;
            int end = BinaryEnd(rtf, i);
            if (end > i) { i = end - 1; continue; }
            if (rtf.AsSpan(i + 1).StartsWith(word, StringComparison.Ordinal)
                && (i + word.Length + 1 == rtf.Length || !char.IsAsciiLetterLower(rtf[i + word.Length + 1]))) return i;
            if (i + 1 < rtf.Length && !char.IsAsciiLetterLower(rtf[i + 1])) i++;
        }
        return -1;
    }

    static int MatchingBrace(string rtf, int open)
    {
        int depth = 0;
        for (int i = open; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '\\') { int end = BinaryEnd(rtf, i); i = end > i ? end - 1 : i + 1; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return rtf.Length - 1;
    }

    static string StripControlWord(string inner, string word)
    {
        int at = FindControlWord(inner, word);
        return at < 0 ? inner : inner[(at + word.Length + 1)..];
    }

    /// <summary>PAGE / NUMPAGES fields become tokens; anything else keeps its displayed result.</summary>
    static string? FieldToken(string field)
    {
        int at = FindControlWord(field, "fldinst");
        if (at < 0) return null;
        var instruction = InstructionRegex().Match(field[(at + 8)..]).Groups[1].Value.ToUpperInvariant();
        return instruction switch { "PAGE" => FieldPage, "NUMPAGES" or "SECTIONPAGES" => FieldPages, _ => null };
    }

    [GeneratedRegex(@"^(?:\s|\{|\\[a-z]+-?\d*\s?)*([A-Za-z]+)")]
    private static partial Regex InstructionRegex();

    [GeneratedRegex(@"\s?(CWPAGEBREAKX|CWFLDPAGEX|CWFLDNUMPAGESX|CWNOTE(\d+)X)\s?")]
    private static partial Regex TokenRegex();

    /// <summary>Turns tokens back into page breaks, fields and notes.</summary>
    static List<DBlock> Restore(List<DBlock> blocks, List<DNote> notes)
    {
        var result = new List<DBlock>();
        bool breakNext = false;
        foreach (var block in blocks)
        {
            if (block is DTable table)
            {
                foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                {
                    var restored = Restore(cell.Blocks, notes);
                    cell.Blocks.Clear();
                    cell.Blocks.AddRange(restored);
                }
                result.Add(table);
                continue;
            }
            if (block is not DParagraph p) { result.Add(block); continue; }

            var current = Clone(p);
            current.PageBreakBefore |= breakNext;
            breakNext = false;
            result.Add(current);
            foreach (var inline in p.Inlines)
            {
                if (inline is not DText t || !t.Text.Contains("CW"))
                {
                    current.Inlines.Add(inline);
                    continue;
                }
                int last = 0;
                foreach (Match m in TokenRegex().Matches(t.Text))
                {
                    if (m.Index > last) current.Inlines.Add(new DText(t.Text[last..m.Index], t.Style, t.Link));
                    last = m.Index + m.Length;
                    switch (m.Groups[1].Value)
                    {
                        case PageBreak:
                            if (current.Inlines.Count == 0 || current.Inlines.All(i => i is DText { Text: var s } && string.IsNullOrWhiteSpace(s)))
                            {
                                current.Inlines.Clear();
                                current.PageBreakBefore = true;
                            }
                            else
                            {
                                current = Clone(p);
                                current.PageBreakBefore = true;
                                result.Add(current);
                            }
                            break;
                        case FieldPage: current.Inlines.Add(new DField(FieldKind.Page, t.Style)); break;
                        case FieldPages: current.Inlines.Add(new DField(FieldKind.NumPages, t.Style)); break;
                        default:
                            int index = int.Parse(m.Groups[2].Value);
                            if (index < notes.Count) current.Inlines.Add(notes[index]);
                            break;
                    }
                }
                if (last < t.Text.Length) current.Inlines.Add(new DText(t.Text[last..], t.Style, t.Link));
            }
        }
        // A paragraph that only held a page break: move the break to the next paragraph.
        for (int i = 0; i < result.Count - 1; i++)
            if (result[i] is DParagraph { Inlines.Count: 0, PageBreakBefore: true } empty && result[i + 1] is DParagraph next && i > 0)
            {
                next.PageBreakBefore = true;
                result.RemoveAt(i--);
            }
        return result;
    }

    static DParagraph Clone(DParagraph p) => new()
    {
        Heading = p.Heading, List = p.List, ListLevel = p.ListLevel, ListLabel = p.ListLabel, Align = p.Align,
        SpaceBefore = p.SpaceBefore, SpaceAfter = p.SpaceAfter, IndentLeft = p.IndentLeft, IndentFirst = p.IndentFirst,
        LineSpacing = p.LineSpacing, PageBreakBefore = p.PageBreakBefore, MarkSize = p.MarkSize,
    };

    // ------------------------------------------------------------------ writing

    public static void Write(DDocument doc, string path, CancellationToken ct = default)
    {
        // Lay out a copy with tokens for what WPF can't write, and every header, footer and note
        // appended as a marked segment so it shares the document's font and colour tables.
        var copy = new DDocument
        {
            Font = doc.Font, FontSize = doc.FontSize, PageWidth = doc.PageWidth, PageHeight = doc.PageHeight, Margins = doc.Margins, Title = doc.Title,
        };
        var notes = new List<DNote>();
        copy.Blocks.AddRange(Tokenize(doc.Blocks, notes, ct));

        var segments = new List<(string kind, int index)>();
        void Segment(string kind, IEnumerable<DBlock> blocks)
        {
            int index = segments.Count;
            segments.Add((kind, index));
            copy.Blocks.Add(Marker(SegmentStart(index)));
            copy.Blocks.AddRange(blocks);
            copy.Blocks.Add(Marker(SegmentEnd));
        }
        void HeaderFooter(string kind, DHeaderFooter? part)
        {
            if (part != null) Segment(kind, Tokenize(part.Blocks, notes, ct));
        }
        bool facing = doc.EvenHeader != null || doc.EvenFooter != null;
        HeaderFooter(facing ? "headerr" : "header", doc.Header);
        HeaderFooter(facing ? "footerr" : "footer", doc.Footer);
        HeaderFooter("headerl", doc.EvenHeader);
        HeaderFooter("footerl", doc.EvenFooter);
        if (doc.DifferentFirstPage)
        {
            HeaderFooter("headerf", doc.FirstHeader ?? new DHeaderFooter());
            HeaderFooter("footerf", doc.FirstFooter ?? new DHeaderFooter());
        }
        for (int i = 0; i < notes.Count; i++) Segment("note" + i, Tokenize(FlowBridge.WithoutMark(notes[i]), notes, ct));

        var flow = FlowBridge.ToFlow(copy, forRtf: true, ct: ct);
        string rtf;
        using (var ms = new MemoryStream())
        {
            new TextRange(flow.ContentStart, flow.ContentEnd).Save(ms, DataFormats.Rtf);
            rtf = Encoding.Latin1.GetString(ms.ToArray());
        }

        // Cut the segments back out.
        var contents = new Dictionary<string, string>();
        foreach (var (kind, index) in segments.AsEnumerable().Reverse())
        {
            int startMark = rtf.IndexOf(SegmentStart(index), StringComparison.Ordinal);
            if (startMark < 0) continue;
            int endMark = rtf.IndexOf(SegmentEnd, startMark, StringComparison.Ordinal);
            var (startGroup, startGroupEnd) = ParagraphGroup(rtf, startMark);
            var (endGroup, endGroupEnd) = ParagraphGroup(rtf, endMark);
            contents[kind] = rtf[(startGroupEnd + 1)..endGroup];
            rtf = rtf[..startGroup] + rtf[(endGroupEnd + 1)..];
        }

        // Headers and footers go in the section formatting before the first paragraph.
        var section = new StringBuilder("\\sectd ");
        if (doc.DifferentFirstPage) section.Append("\\titlepg ");
        foreach (var (kind, _) in segments.Where(s => !s.kind.StartsWith("note")))
            if (contents.TryGetValue(kind, out var content)) section.Append('{').Append('\\').Append(kind).Append(' ').Append(content).Append('}');
        int firstPard = rtf.IndexOf("\\pard", StringComparison.Ordinal);
        if (firstPard >= 0) rtf = rtf.Insert(firstPard, section.ToString());
        if (facing) rtf = rtf.Insert(rtf.IndexOf("\\ansi", StringComparison.Ordinal) + 5, "\\facingp");
        if (notes.Any(n => n.Endnote)) rtf = rtf.Insert(rtf.IndexOf("\\ansi", StringComparison.Ordinal) + 5, "\\fet2");

        // Notes, fields and page breaks.
        for (int i = 0; i < notes.Count; i++)
        {
            var noteContent = contents.GetValueOrDefault("note" + i, "");
            var group = "{\\super\\chftn}{\\footnote" + (notes[i].Endnote ? "\\ftnalt" : "") + "\\pard\\plain{\\super\\chftn} " + noteContent + "}";
            rtf = rtf.Replace(NoteToken(i), group);
        }
        rtf = rtf.Replace(FieldPage, "{\\field{\\*\\fldinst PAGE }{\\fldrslt 1}}")
                 .Replace(FieldPages, "{\\field{\\*\\fldinst NUMPAGES }{\\fldrslt 1}}")
                 .Replace(PageBreak, "\\page ");
        ct.ThrowIfCancellationRequested();
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(rtf));
    }

    static DParagraph Marker(string token)
    {
        var p = new DParagraph();
        p.Inlines.Add(new DText(token, TextStyle.Plain));
        return p;
    }

    /// <summary>Copies blocks, replacing notes, fields and page breaks with tokens.</summary>
    static List<DBlock> Tokenize(List<DBlock> blocks, List<DNote> notes, CancellationToken ct)
    {
        var result = new List<DBlock>();
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block is DTable t)
            {
                var table = new DTable { Borders = t.Borders, ColumnWidths = t.ColumnWidths };
                foreach (var row in t.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = new DRow { Header = row.Header };
                    foreach (var cell in row.Cells)
                    {
                        var c = new DCell { ColSpan = cell.ColSpan, Shading = cell.Shading };
                        c.Blocks.AddRange(Tokenize(cell.Blocks, notes, ct));
                        r.Cells.Add(c);
                    }
                    table.Rows.Add(r);
                }
                result.Add(table);
                continue;
            }
            if (block is not DParagraph p) continue;
            var copy = Clone(p);
            copy.PageBreakBefore = false;
            if (p.PageBreakBefore) copy.Inlines.Add(new DText(PageBreak, TextStyle.Plain));
            foreach (var inline in p.Inlines)
            {
                switch (inline)
                {
                    case DField f: copy.Inlines.Add(new DText(f.Kind == FieldKind.Page ? FieldPage : FieldPages, f.Style)); break;
                    case DNote n:
                        notes.Add(n);
                        copy.Inlines.Add(new DText(NoteToken(notes.Count - 1), TextStyle.Plain));
                        break;
                    default: copy.Inlines.Add(inline); break;
                }
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>The "{ … \par}" group WPF writes for the paragraph containing <paramref name="position"/>.</summary>
    static (int start, int end) ParagraphGroup(string rtf, int position)
    {
        var opens = new Stack<int>();
        for (int i = 0; i < position; i++)
        {
            if (rtf[i] == '\\') { int end = BinaryEnd(rtf, i); i = end > i ? end - 1 : i + 1; continue; }
            if (rtf[i] == '{') opens.Push(i);
            else if (rtf[i] == '}' && opens.Count > 0) opens.Pop();
        }
        foreach (int open in opens)
        {
            int end = MatchingBrace(rtf, open);
            var group = rtf[open..(end + 1)];
            if (group.TrimEnd('}', ' ', '\r', '\n').EndsWith("\\par", StringComparison.Ordinal)) return (open, end);
        }
        return (position, position);
    }
}

using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Reads RTF through WPF's RTF engine, plus the parts WPF ignores: page breaks, headers and footers,
/// page-number fields and footnotes/endnotes, which are cut out of the RTF and replaced with placeholder
/// tokens. (Writing is up to <see cref="RtfWriter"/>.)
/// </summary>
internal static partial class RichText
{
    const string PageBreak = "CWPAGEBREAKX", FieldPage = "CWFLDPAGEX", FieldPages = "CWFLDNUMPAGESX";
    static string NoteToken(int i) => $"CWNOTE{i}X";

    // ------------------------------------------------------------------ reading

    public static DDocument Read(string path) => DocumentFiles.Open(path, DocumentShape.Rtf, () => ReadRtf(path));

    internal static DDocument ReadRtf(string path) => ReadRtf(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    internal static DDocument ReadRtf(byte[] bytes, string? title)
    {
        // RTF is 7-bit, but some programs save it as UTF-16 or with a mark in front.
        var rtf = TextDecoding.HasMark(bytes) || TextDecoding.LooksLikeUtf16(bytes)
            ? string.Concat(TextDecoding.Decode(bytes).Select(c => c < 0x80 ? c.ToString() : $"\\u{(short)c}?"))
            : Encoding.Latin1.GetString(bytes);
        var extracted = Extract(rtf, 0);
        var doc = Load(extracted.Body, title);

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
            note.Blocks.AddRange(Restore(Load(extracted.Preamble + "{" + n.Content + "}}", null).Blocks, []));
            return note;
        }).ToList();

        var blocks = Restore(doc.Blocks, notes);
        doc.Blocks.Clear();
        doc.Blocks.AddRange(blocks);
        (doc.PageWidth, doc.PageHeight, doc.Margins) = Page(extracted.Preamble, doc);
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    /// <summary>The paper and margins, which WPF doesn't pass on.</summary>
    static (double, double, Thickness) Page(string preamble, DDocument doc)
    {
        double? Twips(string word)
        {
            int at = FindControlWord(preamble, word);
            if (at < 0) return null;
            var digits = new string(preamble.Skip(at + word.Length + 1).TakeWhile(char.IsAsciiDigit).Take(6).ToArray());
            return int.TryParse(digits, out int n) ? n / 20.0 : null;
        }
        double w = Twips("paperw") ?? doc.PageWidth, h = Twips("paperh") ?? doc.PageHeight;
        if (w < 72 || h < 72) (w, h) = (doc.PageWidth, doc.PageHeight);
        return (w, h, new Thickness(Twips("margl") ?? doc.Margins.Left, Twips("margt") ?? doc.Margins.Top, Twips("margr") ?? doc.Margins.Right, Twips("margb") ?? doc.Margins.Bottom));
    }

    static DDocument Load(string rtf, string? title)
    {
        var flow = new FlowDocument { TextAlignment = TextAlignment.Left }; // what RTF starts with; WPF's own default is justified
        using (var stream = new MemoryStream(Encoding.Latin1.GetBytes(rtf)))
            new TextRange(flow.ContentStart, flow.ContentEnd).Load(stream, DataFormats.Rtf);
        Limit(flow.Blocks, 0);
        return FlowBridge.FromFlow(flow, title, normalize: false);
    }

    /// <summary>Tables inside tables beyond what a page can be laid out with become their text.</summary>
    static void Limit(BlockCollection blocks, int depth)
    {
        foreach (var block in blocks.ToList())
        {
            if (block is Section s) Limit(s.Blocks, depth + 1);
            else if (block is List l) foreach (var item in l.ListItems) Limit(item.Blocks, depth + 1);
            else if (block is Table t)
            {
                if (depth >= DocumentLimits.TableDepth * 2 || !DocumentLimits.CanGoDeeper(depth))
                {
                    blocks.InsertBefore(t, new Paragraph(new Run(new TextRange(t.ContentStart, t.ContentEnd).Text)));
                    blocks.Remove(t);
                }
                else foreach (var cell in t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells)) Limit(cell.Blocks, depth + 2);
            }
        }
    }

    sealed record Extraction(string Body, string Preamble, Dictionary<string, string> Groups,
        List<(bool Endnote, string Content)> Notes, bool TitlePage, bool FacingPages);

    /// <summary>
    /// Walks the RTF once, lifting out header/footer and footnote groups, replacing page-number fields
    /// and page breaks with tokens WPF will keep as plain text.
    /// </summary>
    static Extraction Extract(string rtf, int level)
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
                // (Nested deeper than anything real, they're left for WPF to make of what it can.)
                if (level < 3 && word is ("header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf" or "footnote" or "field"))
                {
                    int end = MatchingBrace(rtf, i);
                    var inner = rtf[(i + 1)..end];
                    if (word == "footnote")
                    {
                        bool isEndnote = FindControlWord(inner, "ftnalt") >= 0;
                        // Fields and page breaks inside notes and headers need the same treatment as the body.
                        notes.Add((isEndnote, Extract(StripControlWord(inner, "footnote"), level + 1).Body));
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
                        groups.TryAdd(word, Extract(StripControlWord(inner, word), level + 1).Body);
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
        var rtf = RtfWriter.Write(doc, ct);
        ct.ThrowIfCancellationRequested();
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(rtf));
    }
}

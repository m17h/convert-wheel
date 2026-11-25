using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Writes RTF with headings, tables (merged and nested cells too), pictures, links, headers and footers, and notes.</summary>
internal sealed class RtfWriter
{
    readonly DDocument _doc;
    readonly CancellationToken _ct;
    readonly StringBuilder _out = new();
    readonly Dictionary<string, int> _fonts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Color, int> _colors = [];
    readonly HashSet<string> _bookmarks = new(StringComparer.Ordinal);
    static readonly double[] HeadingSizes = [0, 20, 16, 14, 13, 12, 11];

    RtfWriter(DDocument doc, CancellationToken ct) => (_doc, _ct) = (doc, ct);

    public static string Write(DDocument doc, CancellationToken ct) => new RtfWriter(doc, ct).Build();

    string Build()
    {
        Font(_doc.Font);
        // The body first: it decides which fonts and colours the header lists.
        var notes = _doc.Notes().ToList();
        bool facing = _doc.EvenHeader != null || _doc.EvenFooter != null;
        var section = new PageSetup(_doc.PageWidth, _doc.PageHeight, _doc.Margins, _doc.Columns, _doc.ColumnGap);
        Section(section, first: true);
        HeaderFooter(facing ? "headerr" : "header", _doc.Header);
        HeaderFooter(facing ? "footerr" : "footer", _doc.Footer);
        HeaderFooter("headerl", _doc.EvenHeader);
        HeaderFooter("footerl", _doc.EvenFooter);
        if (_doc.DifferentFirstPage)
        {
            HeaderFooter("headerf", _doc.FirstHeader ?? new DHeaderFooter());
            HeaderFooter("footerf", _doc.FirstFooter ?? new DHeaderFooter());
        }
        double available = section.ContentWidth;
        for (int i = 0; i < _doc.Blocks.Count; i++)
        {
            if (i > 0 && _doc.Blocks[i].Section is { } next)
            {
                _out.Append("\\sect\n");
                Section(next, first: false);
                available = next.ContentWidth;
            }
            Block(_doc.Blocks[i], available, 0, null);
            Between(_doc.Blocks, i, 0);
        }
        var body = _out.ToString();

        var rtf = new StringBuilder(body.Length + 4096);
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\uc1\deff0");
        if (notes.Any(n => n.Endnote)) rtf.Append(@"\fet2\aenddoc");
        rtf.Append("{\\fonttbl");
        foreach (var (name, index) in _fonts)
        {
            rtf.Append($"{{\\f{index}\\fnil\\fcharset0 ");
            Text(rtf, name);
            rtf.Append(";}");
        }
        rtf.Append("}\n{\\colortbl;");
        foreach (var color in _colors.Keys) rtf.Append($"\\red{color.R}\\green{color.G}\\blue{color.B};");
        rtf.Append("}\n{\\stylesheet{\\s0 Normal;}");
        for (int i = 1; i <= 6; i++) rtf.Append($"{{\\s{i}\\outlinelevel{i - 1} heading {i};}}");
        rtf.Append("}\n");
        rtf.Append($"\\paperw{Units.ToTwips(_doc.PageWidth)}\\paperh{Units.ToTwips(_doc.PageHeight)}\\margl{Units.ToTwips(_doc.Margins.Left)}\\margr{Units.ToTwips(_doc.Margins.Right)}")
           .Append($"\\margt{Units.ToTwips(_doc.Margins.Top)}\\margb{Units.ToTwips(_doc.Margins.Bottom)}");
        if (_doc.PageWidth > _doc.PageHeight) rtf.Append("\\landscape");
        if (facing) rtf.Append("\\facingp");
        rtf.Append('\n').Append(body).Append("}\n");
        return rtf.ToString();
    }

    void Section(PageSetup s, bool first)
    {
        _out.Append("\\sectd");
        if (!first) _out.Append("\\sbkpage");
        _out.Append($"\\pgwsxn{Units.ToTwips(s.Width)}\\pghsxn{Units.ToTwips(s.Height)}\\marglsxn{Units.ToTwips(s.Margins.Left)}\\margrsxn{Units.ToTwips(s.Margins.Right)}")
            .Append($"\\margtsxn{Units.ToTwips(s.Margins.Top)}\\margbsxn{Units.ToTwips(s.Margins.Bottom)}\\headery{Units.ToTwips(_doc.HeaderDistance)}\\footery{Units.ToTwips(_doc.FooterDistance)}");
        if (s.Width > s.Height) _out.Append("\\lndscpsxn");
        if (s.Columns > 1) _out.Append($"\\cols{s.Columns}\\colsx{Units.ToTwips(s.ColumnGap)}");
        if (_doc.DifferentFirstPage) _out.Append("\\titlepg");
        _out.Append('\n');
    }

    void HeaderFooter(string kind, DHeaderFooter? part)
    {
        if (part == null) return;
        _out.Append("{\\").Append(kind).Append(' ');
        Blocks(part.Blocks, _doc.ContentWidth, 0, null);
        if (part.Blocks.Count == 0) _out.Append("\\pard\\plain\\par");
        _out.Append("}\n");
    }

    // ------------------------------------------------------------------ blocks

    void Blocks(List<DBlock> blocks, double available, int tableLevel, string? end)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            Block(blocks[i], available, tableLevel, i == blocks.Count - 1 ? end : null);
            Between(blocks, i, tableLevel);
        }
        // A cell ends with a paragraph, and so may have to have one.
        if (end != null && (blocks.Count == 0 || blocks[^1] is not DParagraph))
        {
            _out.Append("\\pard\\plain").Append(Level(tableLevel)).Append(' ').Append(end).Append('\n');
        }
    }

    /// <summary>One table straight after another would become part of it.</summary>
    void Between(List<DBlock> blocks, int i, int tableLevel)
    {
        if (i + 1 < blocks.Count && blocks[i] is DTable && blocks[i + 1] is DTable && blocks[i + 1].Section == null) _out.Append("\\pard\\plain").Append(Level(tableLevel)).Append("\\fs8\\par\n");
    }

    /// <param name="end">What ends the block instead of a paragraph mark: the end of a cell.</param>
    void Block(DBlock block, double available, int tableLevel, string? end)
    {
        _ct.ThrowIfCancellationRequested();
        if (block is DParagraph p) Paragraph(p, available, tableLevel, end);
        else if (block is DTable t) Table(t, available, tableLevel + 1);
    }

    static string Level(int tableLevel) => tableLevel == 0 ? "" : tableLevel == 1 ? "\\intbl" : $"\\intbl\\itap{tableLevel}";

    void Paragraph(DParagraph p, double available, int tableLevel, string? end)
    {
        _out.Append("\\pard\\plain");
        if (p.Heading is >= 1 and <= 6) _out.Append($"\\s{p.Heading}\\outlinelevel{p.Heading - 1}\\keepn");
        _out.Append(Level(tableLevel));
        _out.Append(p.RightToLeft ? "\\rtlpar" : "");
        _out.Append(p.Align switch
        {
            TextAlignment.Center => "\\qc",
            TextAlignment.Right => "\\qr",
            TextAlignment.Justify => "\\qj",
            _ => "\\ql",
        });
        bool hangs = p.ListLabel is { Length: > 0 };
        double left = p.IndentLeft, first = p.IndentFirst;
        if (hangs && first >= 0) (left, first) = (Math.Max(left, 18), -18);
        var sides = p.RightToLeft ? "\\ri" : "\\li";
        if (left != 0) _out.Append(sides).Append(Units.ToTwips(left));
        if (first != 0) _out.Append("\\fi").Append(Units.ToTwips(first));
        if (p.SpaceBefore > 0) _out.Append("\\sb").Append(Units.ToTwips(p.SpaceBefore));
        if (p.SpaceAfter > 0) _out.Append("\\sa").Append(Units.ToTwips(p.SpaceAfter));
        if (Math.Abs(p.LineSpacing - 1) > 0.01) _out.Append("\\sl").Append((int)Math.Round(p.LineSpacing * 240)).Append("\\slmult1");
        if (hangs) _out.Append("\\tx").Append(Units.ToTwips(Math.Max(1, left)));
        _out.Append("\\f0\\fs").Append(HalfPoints(p.Inlines.Count == 0 ? p.MarkSize ?? _doc.FontSize : _doc.FontSize)).Append(' ');
        if (p.PageBreakBefore && tableLevel == 0) _out.Append("\\page ");
        if (hangs)
        {
            var look = p.Inlines.OfType<DText>().FirstOrDefault()?.Style ?? TextStyle.Plain;
            Run(p.ListLabel + "\t", new TextStyle { Size = look.Size, Font = look.Font, Color = look.Color, Bold = p.Heading > 0 }, p.Heading);
        }
        foreach (var inline in p.Inlines) Inline(inline, p.Heading, available);
        _out.Append(end ?? "\\par").Append('\n');
        // Text boxes follow the paragraph they're anchored in.
        foreach (var f in p.Inlines.OfType<DFloat>().Where(f => f.Blocks.Count > 0))
        {
            if (end != null) continue; // the cell has ended; see Cell
            Blocks(f.Blocks, available, tableLevel, null);
        }
    }

    void Table(DTable t, double available, int level)
    {
        int columns = Math.Max(1, t.ColumnCount);
        var widths = t.ColumnWidths != null && t.ColumnWidths.Count >= columns ? t.ColumnWidths.Take(columns).ToList() : Enumerable.Repeat(available / columns, columns).ToList();
        double total = widths.Sum();
        if (total > available && total > 0) widths = widths.Select(w => w * available / total).ToList();
        // The right edge of each column, in twips; every one at least a little further on.
        var edges = new int[columns + 1];
        double at = 0;
        for (int i = 0; i < columns; i++)
        {
            at += widths[i];
            edges[i + 1] = Math.Max(edges[i] + 20, Units.ToTwips(at));
        }
        int rowsLeft = t.Rows.Count;
        foreach (var (row, rowSlots) in t.Slots())
        {
            _ct.ThrowIfCancellationRequested();
            rowsLeft--;
            // Every column has to be there, filled or not.
            var slots = new List<DSlot>();
            int col = 0;
            foreach (var slot in rowSlots)
            {
                if (slot.Column > col) slots.Add(new DSlot(new DCell(), col, slot.Column - col, false));
                slots.Add(slot);
                col = slot.Column + slot.Span;
            }
            if (slots.Count == 0) slots.Add(new DSlot(new DCell(), 0, columns, false));

            var look = new StringBuilder();
            look.Append("\\trowd\\trgaph108\\trleft0").Append(row.Header ? "\\trhdr" : "").Append(level == 1 ? "\\trpaddl108\\trpaddr108\\trpaddfl3\\trpaddfr3" : "");
            foreach (var slot in slots)
            {
                if (slot.Covered) look.Append("\\clvmrg");
                else if (slot.Cell.RowSpan > 1 && rowsLeft > 0) look.Append("\\clvmgf");
                if (t.Borders)
                    foreach (var side in new[] { "t", "l", "b", "r" }) look.Append("\\clbrdr").Append(side).Append("\\brdrs\\brdrw10");
                if (slot.Cell.Shading is { } shade) look.Append("\\clcbpat").Append(ColorIndex(shade));
                look.Append("\\cellx").Append(edges[Math.Min(columns, slot.Column + slot.Span)]);
            }

            if (level == 1) _out.Append(look).Append('\n');
            foreach (var slot in slots)
            {
                var blocks = slot.Covered ? [] : CellBlocks(slot.Cell.Blocks);
                double width = (edges[Math.Min(columns, slot.Column + slot.Span)] - edges[slot.Column]) / 20.0;
                Blocks(blocks, Math.Max(20, width - 10.8), level, level == 1 ? "\\cell" : "\\nestcell{\\nonesttables\\par}");
            }
            if (level == 1) _out.Append("\\row\n");
            else _out.Append("{\\*\\nesttableprops").Append(look).Append("\\nestrow}{\\nonesttables\\par}\n");
        }
        if (level == 1) _out.Append("\\pard\n");
    }

    /// <summary>A cell's blocks with its text boxes' contents after the paragraphs they're anchored in.</summary>
    static List<DBlock> CellBlocks(List<DBlock> blocks) =>
        !blocks.OfType<DParagraph>().Any(p => p.Inlines.Any(i => i is DFloat { Blocks.Count: > 0 })) ? blocks
        : blocks.SelectMany(b => b is DParagraph p ? p.Inlines.OfType<DFloat>().SelectMany(f => f.Blocks).Prepend(b) : [b]).ToList();

    // ------------------------------------------------------------------ inlines

    void Inline(DInline inline, int heading, double available)
    {
        switch (inline)
        {
            case DText t:
                var target = t.Link is ['#', _, ..] ? t.Link : Links.ForDocument(t.Link);
                if (target == null) { Run(t.Text, t.Style, heading); break; }
                _out.Append("{\\field{\\*\\fldinst{HYPERLINK ");
                if (target[0] == '#') _out.Append("\\\\l ");
                _out.Append('"');
                Text(_out, (target[0] == '#' ? Bookmark(target[1..]) : target).Replace("\"", "%22").Replace(@"\", @"\\"));
                _out.Append("\"}}{\\fldrslt{");
                var style = t.Style with { Underline = true, Color = t.Style.Color ?? Color.FromRgb(0x05, 0x63, 0xC1) };
                Run(t.Text, style, heading);
                _out.Append("}}}");
                break;
            case DLineBreak: _out.Append("\\line "); break;
            case DAnchor a when _bookmarks.Add(a.Name):
                _out.Append("{\\*\\bkmkstart ").Append(Bookmark(a.Name)).Append("}{\\*\\bkmkend ").Append(Bookmark(a.Name)).Append('}');
                break;
            case DImage img: Picture(img, available); break;
            case DFloat { Image: { } picture }: Picture(picture, available); break;
            case DField f:
                _out.Append("{\\field{\\*\\fldinst ").Append(f.Kind == FieldKind.Page ? "PAGE" : "NUMPAGES").Append(" }{\\fldrslt ");
                Run("1", f.Style, heading);
                _out.Append("}}");
                break;
            case DNote n:
                _out.Append("{\\super\\chftn}{\\footnote").Append(n.Endnote ? "\\ftnalt" : "").Append("\\pard\\plain{\\super\\chftn} ");
                Blocks(FlowBridge.WithoutMark(n), _doc.ContentWidth, 0, null);
                _out.Append('}');
                break;
        }
    }

    static string Bookmark(string name)
    {
        var clean = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return clean == name && name.Length <= 40 ? name : $"b{Math.Abs((long)Hash(name))}_{(clean.Length > 24 ? clean[..24] : clean)}";

        static int Hash(string s)
        {
            int h = 17;
            foreach (var c in s) h = unchecked(h * 31 + c);
            return h;
        }
    }

    void Run(string text, TextStyle s, int heading)
    {
        if (text.Length == 0) return;
        bool styledHeading = heading is >= 1 and <= 6;
        _out.Append('{');
        _out.Append("\\f").Append(Font(s.Font ?? _doc.Font));
        _out.Append("\\fs").Append(HalfPoints(s.Size ?? (styledHeading ? HeadingSizes[heading] : _doc.FontSize)));
        // A heading nobody styled looks like one.
        if (s.Bold || styledHeading && s.Size == null) _out.Append("\\b");
        if (s.Italic) _out.Append("\\i");
        if (s.Underline) _out.Append("\\ul");
        if (s.Strike) _out.Append("\\strike");
        if (s.Superscript) _out.Append("\\super");
        else if (s.Subscript) _out.Append("\\sub");
        if (s.Color is { } color) _out.Append("\\cf").Append(ColorIndex(color));
        if (s.Highlight is { } highlight) _out.Append($"\\chshdng0\\chcbpat{ColorIndex(highlight)}\\cb{ColorIndex(highlight)}");
        _out.Append(' ');
        Text(_out, text);
        _out.Append('}');
    }

    /// <summary>Text as RTF spells it: anything but plain ASCII as a character number.</summary>
    static void Text(StringBuilder sb, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}': sb.Append('\\').Append(c); break;
                case '\t': sb.Append("\\tab "); break;
                case '\n': sb.Append("\\line "); break;
                case '\u00A0': sb.Append("\\~"); break;
                case '\u2011': sb.Append("\\_"); break;
                case '\u00AD': sb.Append("\\-"); break;
                case >= ' ' and < '\x7F': sb.Append(c); break;
                case < ' ' or '\x7F': break;
                default: sb.Append("\\u").Append((short)c).Append('?'); break;
            }
        }
    }

    void Picture(DImage img, double available)
    {
        var kind = HtmlReader.ImageExt(img.Data);
        var data = img.Data;
        if (kind is not ("png" or "jpeg"))
        {
            // RTF has no GIF, BMP or TIFF.
            if (DocumentPictures.ToPng(data) is not { } png) { if (img.Alt != null) Run(img.Alt, TextStyle.Plain, 0); return; }
            (data, kind) = (png, "png");
        }
        double scale = Math.Min(1, available / Math.Max(1, img.Width));
        var (pixelsWide, pixelsHigh) = DocxReader.ImageSize(data);
        _out.Append("{\\pict\\").Append(kind).Append("blip")
            .Append($"\\picw{(int)Math.Clamp(pixelsWide * 26.46, 1, 32767)}\\pich{(int)Math.Clamp(pixelsHigh * 26.46, 1, 32767)}")
            .Append($"\\picwgoal{Math.Clamp(Units.ToTwips(img.Width * scale), 1, 32767)}\\pichgoal{Math.Clamp(Units.ToTwips(img.Height * scale), 1, 32767)}\n");
        const string hex = "0123456789abcdef";
        _out.EnsureCapacity(_out.Length + data.Length * 2 + data.Length / 64 + 16);
        for (int i = 0; i < data.Length; i++)
        {
            _out.Append(hex[data[i] >> 4]).Append(hex[data[i] & 15]);
            if (i % 64 == 63) _out.Append('\n');
        }
        _out.Append('}');
    }

    static int HalfPoints(double size) => (int)Math.Clamp(Math.Round(size * 2), 2, 3276);

    int Font(string name)
    {
        // A name is all a font can be: WPF, for one, gives up on a file with anything stranger in its font list.
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '@').Take(31).ToArray()).Trim();
        if (name.Length == 0) name = "Calibri";
        if (!_fonts.TryGetValue(name, out int index)) _fonts[name] = index = _fonts.Count;
        return index;
    }

    int ColorIndex(Color color)
    {
        color.A = 255;
        if (!_colors.TryGetValue(color, out int index)) _colors[color] = index = _colors.Count + 1;
        return index;
    }
}

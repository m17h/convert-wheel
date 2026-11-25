using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Converts between <see cref="DDocument"/> and WPF's FlowDocument. WPF provides RTF import/export
/// and a real pagination engine, which <see cref="PdfRenderer"/> uses to lay out pages.
/// </summary>
internal static class FlowBridge
{
    static readonly double[] HeadingSizes = [0, 20, 16, 14, 13, 12, 11];
    static HashSet<string>? _installedFonts;

    // ------------------------------------------------------------------ model -> FlowDocument

    /// <summary>A float the PDF renderer paints itself (behind or in front of the text) once pages are laid out.</summary>
    internal sealed record Overlay(DFloat Float, TextElement Anchor);

    /// <summary>On a text brush: the address its text links to. Page drawings keep the brush but not the Hyperlink around it.</summary>
    internal static readonly DependencyProperty LinkProperty = DependencyProperty.RegisterAttached("Link", typeof(string), typeof(FlowBridge), new PropertyMetadata(null));

    /// <summary>State for one conversion to a FlowDocument.</summary>
    internal sealed class FlowContext(DDocument doc)
    {
        public DDocument Doc { get; } = doc;
        public CancellationToken Cancellation { get; init; }
        /// <summary>RTF can't hold WPF controls or figures, so list markers become text and notes go to the end.</summary>
        public bool ForRtf { get; init; }
        /// <summary>Page number and count, for page fields in headers and footers.</summary>
        public int Page { get; init; } = 1;
        public int Pages { get; init; } = 1;
        /// <summary>In headers/footers every float is painted as an overlay at its page position.</summary>
        public bool AllFloatsAsOverlays { get; init; }
        public List<Overlay> Overlays { get; } = [];
        public List<DNote> Endnotes { get; } = [];
        /// <summary>The reference mark of each footnote, so a first layout pass can find which page it landed on.</summary>
        public Dictionary<DNote, TextElement> NoteAnchors { get; } = [];
        /// <summary>From a first pass: the page of each footnote. Notes sharing a page are then laid out together.</summary>
        public Dictionary<DNote, int>? NotePages { get; init; }
        /// <summary>Multi-section documents: endnotes wait for the last section, which also receives the earlier ones.</summary>
        public bool HoldEndnotes { get; init; }
        public List<DNote> EarlierEndnotes { get; init; } = [];
    }

    public static FlowDocument ToFlow(DDocument doc, bool forRtf = false, CancellationToken ct = default) => ToFlow(new FlowContext(doc) { ForRtf = forRtf, Cancellation = ct });

    public static FlowDocument ToFlow(FlowContext ctx)
    {
        var doc = ctx.Doc;
        var flow = NewFlow(doc, doc.PageWidth, doc.PageHeight, doc.Margins);
        foreach (var block in doc.Blocks)
        {
            if (block is DTable { Rows.Count: > LongTable } table && !ctx.ForRtf) flow.Blocks.AddRange(InPieces(table, ctx, doc.ContentWidth));
            else flow.Blocks.Add(ToBlock(block, ctx, doc.ContentWidth));
        }
        if (doc.Columns > 1)
        {
            // Newspaper columns: WPF fits as many columns of this width as the page holds.
            double gap = Units.ToDip(doc.ColumnGap);
            flow.ColumnGap = gap;
            flow.ColumnWidth = (Units.ToDip(doc.ContentWidth) - gap * (doc.Columns - 1)) / doc.Columns - 0.5;
            flow.IsColumnWidthFlexible = true;
        }
        if (ctx.HoldEndnotes) return flow;
        ctx.Endnotes.InsertRange(0, ctx.EarlierEndnotes);
        AppendEndnotes(flow, ctx);
        return flow;
    }

    /// <summary>A header, footer or text box laid out on its own, <paramref name="width"/> points wide.</summary>
    public static FlowDocument PartFlow(FlowContext ctx, IEnumerable<DBlock> blocks, double width, Thickness padding = default)
    {
        var flow = NewFlow(ctx.Doc, width, ctx.Doc.PageHeight, padding);
        foreach (var block in blocks) flow.Blocks.Add(ToBlock(block, ctx, width - padding.Left - padding.Right));
        return flow;
    }

    static FlowDocument NewFlow(DDocument doc, double width, double height, Thickness margins) => new()
    {
        FontFamily = Family(doc.Font),
        FontSize = Units.ToDip(doc.FontSize),
        PageWidth = Units.ToDip(width),
        PageHeight = Units.ToDip(height),
        PagePadding = new Thickness(Units.ToDip(margins.Left), Units.ToDip(margins.Top), Units.ToDip(margins.Right), Units.ToDip(margins.Bottom)),
        ColumnWidth = double.PositiveInfinity,
        TextAlignment = TextAlignment.Left,
        Foreground = Brushes.Black,
        Background = null, // the PDF page is already white; a fill would hide what lies beneath and skew measurements
    };

    static Block ToBlock(DBlock block, FlowContext ctx, double available)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        return block switch
        {
            DTable t => ToTable(t, ctx, available),
            DParagraph p => ToParagraph(p, ctx, available),
            _ => new Paragraph(),
        };
    }

    static Paragraph ToParagraph(DParagraph p, FlowContext ctx, double available)
    {
        var doc = ctx.Doc;
        double baseSize = p.Inlines.OfType<DText>().Select(t => t.Style.Size ?? HeadingSize(p) ?? doc.FontSize).DefaultIfEmpty(p.MarkSize ?? doc.FontSize).Max();
        var para = new Paragraph
        {
            Margin = new Thickness(Units.ToDip(p.IndentLeft), Units.ToDip(p.SpaceBefore), 0, Units.ToDip(p.SpaceAfter)),
            TextIndent = Units.ToDip(p.IndentFirst),
            TextAlignment = p.Align,
            BreakPageBefore = p.PageBreakBefore,
            KeepWithNext = p.Heading > 0,
            // Line breaks and empty lines take the size of the paragraph's text, not the document default.
            FontSize = Units.ToDip(baseSize),
        };
        if (p.RightToLeft && !ctx.ForRtf)
        {
            // WPF turns "left" and "right" around inside right-to-left text.
            para.FlowDirection = FlowDirection.RightToLeft;
            para.TextAlignment = p.Align switch { TextAlignment.Left => TextAlignment.Right, TextAlignment.Right => TextAlignment.Left, var a => a };
        }
        // A fixed line height would clip pictures, so lines with images keep their natural height.
        if (Math.Abs(p.LineSpacing - 1) > 0.01 && !p.Inlines.OfType<DImage>().Any())
        {
            para.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            para.LineHeight = Units.ToDip(baseSize * 1.22 * p.LineSpacing);
        }

        if (p.ListLabel != null)
        {
            double hang = p.IndentFirst < 0 ? -p.IndentFirst : 18;
            if (ctx.ForRtf)
            {
                para.Inlines.Add(new Run(p.ListLabel + "\t"));
            }
            else
            {
                // A fixed-width box keeps item text aligned with the wrapped lines, like a tab stop would.
                // The marker takes the colour and font of the item's first text, as Word and PowerPoint draw it.
                var first = p.Inlines.OfType<DText>().FirstOrDefault(t => t.Text.Length > 0)?.Style;
                var label = new TextBlock
                {
                    Text = p.ListLabel,
                    Width = Units.ToDip(Math.Max(hang, 6)),
                    FontFamily = Family(p.ListFont ?? first?.Font ?? doc.Font),
                    FontSize = Units.ToDip(baseSize * (p.ListSize ?? 1)),
                    Foreground = new SolidColorBrush(p.ListColor ?? first?.Color ?? Colors.Black),
                };
                // A long label ("2.2.1.") pushes the text along, as Word's tab after the number does.
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double needed = label.DesiredSize.Width + Units.ToDip(baseSize * 0.35);
                if (needed > label.Width) label.Width = needed;
                para.Inlines.Add(new InlineUIContainer(label) { BaselineAlignment = BaselineAlignment.TextBottom });
            }
        }

        // The indent is on the side the text starts from.
        if (p.RightToLeft && !ctx.ForRtf) para.Margin = new Thickness(0, para.Margin.Top, para.Margin.Left, para.Margin.Bottom);

        var heading = HeadingSize(p);
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var run = Styled(new Run(t.Text), t.Style, heading);
                    if (t.Style.Size == null && heading == null) run.FontSize = Units.ToDip(doc.FontSize) * (t.Style.Superscript || t.Style.Subscript ? 0.65 : 1);
                    if (t.Link != null && Uri.TryCreate(t.Link, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(run) { NavigateUri = uri, Foreground = run.Foreground };
                        if (t.Style.Color == null) link.Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x63, 0xC1));
                        if (uri.Scheme is "http" or "https" or "mailto" && link.Foreground is SolidColorBrush { IsFrozen: false } marked) marked.SetValue(LinkProperty, uri.AbsoluteUri);
                        para.Inlines.Add(link);
                    }
                    else para.Inlines.Add(run);
                    break;
                case DLineBreak:
                    para.Inlines.Add(new LineBreak());
                    break;
                case DImage img when ImageElement(img, available) is { } image:
                    para.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Baseline });
                    break;
                case DField f:
                    para.Inlines.Add(Styled(new Run((f.Kind == FieldKind.Page ? ctx.Page : ctx.Pages).ToString()), f.Style, heading));
                    break;
                case DNote n:
                    var noteMark = Styled(new Run(NoteMark(n)), n.Style with { Superscript = true }, heading);
                    para.Inlines.Add(noteMark);
                    ctx.NoteAnchors[n] = noteMark;
                    if (n.Endnote || ctx.ForRtf) ctx.Endnotes.Add(n);
                    else if (FootnotesToPlace(n, ctx) is { Count: > 0 } group) para.Inlines.Add(FootnoteFigure(group, ctx));
                    break;
                case DFloat f:
                    AddFloat(para, f, ctx, available, crowded: p.Inlines.OfType<DFloat>().Count(o => o.VerticalFrom != FloatFrom.Paragraph) > 1);
                    break;
            }
        }

        if (para.Inlines.Count == 0 || para.Inlines.All(i => i is Figure or Floater))
            para.Inlines.Add(new Run(ctx.ForRtf ? "" : "​") { FontSize = Units.ToDip(p.MarkSize ?? doc.FontSize) });
        return para;
    }

    internal static string NoteMark(DNote n) => n.Endnote ? ListLabels.Format(n.Number, "lowerRoman") : n.Number.ToString();

    static Image? ImageElement(DImage img, double available)
    {
        if (Decode(img.Data) is not { } source) return null;
        double scale = Math.Min(1, available / Math.Max(1, img.Width));
        return new Image
        {
            Source = source,
            Width = Units.ToDip(img.Width * scale),
            Height = Units.ToDip(img.Height * scale),
            Stretch = Stretch.Fill,
        };
    }

    static BlockUIContainer NoteRule(double spaceBefore) => new(new System.Windows.Shapes.Rectangle
    {
        Width = Units.ToDip(144), Height = 0.75, Fill = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Left,
    })
    { Margin = new Thickness(0, Units.ToDip(spaceBefore), 0, Units.ToDip(3)) };

    /// <summary>Footnotes sit at the bottom of the page that references them, under a short rule.</summary>
    /// <summary>
    /// Without page information each note gets its own figure. With it, the first note on a page carries
    /// all of that page's notes in order (others carry none), under a single separator.
    /// </summary>
    static List<DNote> FootnotesToPlace(DNote note, FlowContext ctx)
    {
        if (ctx.NotePages == null || !ctx.NotePages.TryGetValue(note, out int page)) return [note];
        var group = ctx.NotePages.Where(kv => kv.Value == page).Select(kv => kv.Key).OrderBy(n => n.Number).ToList();
        return group[0] == note ? group : [];
    }

    static Figure FootnoteFigure(List<DNote> notes, FlowContext ctx)
    {
        var figure = new Figure
        {
            VerticalAnchor = FigureVerticalAnchor.ContentBottom, // bottom of the text area, above the footer
            HorizontalAnchor = FigureHorizontalAnchor.ContentLeft,
            Width = new FigureLength(1, FigureUnitType.Content),
            WrapDirection = WrapDirection.None,
            Padding = new Thickness(0),
            Margin = new Thickness(0, Units.ToDip(4), 0, 0),
            CanDelayPlacement = false,
        };
        figure.Blocks.Add(NoteRule(0));
        foreach (var note in notes)
            foreach (var block in WithMark(note)) figure.Blocks.Add(ToBlock(block, ctx, ctx.Doc.ContentWidth));
        return figure;
    }

    /// <summary>Notes from some formats don't repeat their number inside; add it so the note reads "1 Text…".</summary>
    internal static List<DBlock> WithMark(DNote note)
    {
        var blocks = note.Blocks.ToList();
        var mark = NoteMark(note);
        if (blocks.FirstOrDefault() is DParagraph first && first.PlainText().TrimStart().StartsWith(mark)) return blocks;
        var p = new DParagraph();
        if (blocks.FirstOrDefault() is DParagraph original)
        {
            p = new DParagraph { Align = original.Align, SpaceAfter = original.SpaceAfter, MarkSize = original.MarkSize };
            p.Inlines.AddRange(original.Inlines);
            blocks[0] = p;
        }
        else blocks.Insert(0, p);
        p.Inlines.Insert(0, new DText(mark + " ", TextStyle.Plain with { Superscript = true }));
        return blocks;
    }

    /// <summary>
    /// For formats that draw the note number themselves (Word, ODF, RTF, the HTML notes list): the note
    /// content without a number the source spelled out at its start.
    /// </summary>
    internal static List<DBlock> WithoutMark(DNote note)
    {
        var blocks = note.Blocks.ToList();
        if (blocks.FirstOrDefault() is DParagraph p && p.Inlines.FirstOrDefault() is DText { Style.Superscript: true } t &&
            t.Text.Trim() == NoteMark(note))
        {
            var copy = new DParagraph { Align = p.Align, SpaceAfter = p.SpaceAfter, SpaceBefore = p.SpaceBefore, MarkSize = p.MarkSize };
            copy.Inlines.AddRange(p.Inlines.Skip(1));
            if (copy.Inlines.FirstOrDefault() is DText next) copy.Inlines[0] = new DText(next.Text.TrimStart(), next.Style, next.Link);
            blocks[0] = copy;
        }
        return blocks;
    }

    static void AppendEndnotes(FlowDocument flow, FlowContext ctx)
    {
        if (ctx.Endnotes.Count == 0) return;
        if (!ctx.ForRtf) flow.Blocks.Add(NoteRule(12));
        var notes = ctx.Endnotes.ToList();
        ctx.Endnotes.Clear();
        foreach (var note in notes)
            foreach (var block in WithMark(note)) flow.Blocks.Add(ToBlock(block, ctx, ctx.Doc.ContentWidth));
    }

    /// <param name="crowded">Several page/margin-positioned floats share this paragraph.</param>
    static void AddFloat(Paragraph para, DFloat f, FlowContext ctx, double available, bool crowded)
    {
        if (ctx.ForRtf)
        {
            // RTF output keeps the picture (inline) and a text box's text.
            if (f.Image != null && ImageElement(f.Image, available) is { } image) para.Inlines.Add(new InlineUIContainer(image));
            foreach (var p in f.Blocks.OfType<DParagraph>())
            {
                para.Inlines.Add(new LineBreak());
                para.Inlines.Add(new Run(p.PlainText()));
            }
            return;
        }
        // Floats that sit behind/in front of the text at a page position are painted by the PDF renderer;
        // a zero-width anchor tells it which page they belong to.
        // Several floats placed against the page or margin in one paragraph are painted at their exact spots too:
        // WPF's figures can't overlap one another, so they would otherwise be pushed apart.
        if (ctx.AllFloatsAsOverlays || (f.Wrap is FloatWrap.Behind or FloatWrap.InFront || crowded) && f.VerticalFrom != FloatFrom.Paragraph)
        {
            var anchor = new Run("​");
            para.Inlines.Add(anchor);
            ctx.Overlays.Add(new Overlay(f, anchor));
            return;
        }
        para.Inlines.Add(FloatFigure(f, ctx));
    }

    static Figure FloatFigure(DFloat f, FlowContext ctx)
    {
        bool textBox = f.Blocks.Count > 0;
        var figure = new Figure
        {
            Width = new FigureLength(Units.ToDip(f.Width)),
            Height = new FigureLength(Units.ToDip(f.Height)),
            Padding = textBox ? new Thickness(Units.ToDip(7.2), Units.ToDip(3.6), Units.ToDip(7.2), Units.ToDip(3.6)) : new Thickness(0),
            Margin = new Thickness(0),
            CanDelayPlacement = false,
            WrapDirection = f.Wrap == FloatWrap.TopAndBottom ? WrapDirection.None : WrapDirection.Both,
            HorizontalAnchor = (f.HorizontalFrom, f.HorizontalAlign) switch
            {
                (FloatFrom.Page, FloatAlign.Center) => FigureHorizontalAnchor.PageCenter,
                (FloatFrom.Page, FloatAlign.End) => FigureHorizontalAnchor.PageRight,
                (FloatFrom.Page, _) => FigureHorizontalAnchor.PageLeft,
                (FloatFrom.Margin, FloatAlign.Center) => FigureHorizontalAnchor.ContentCenter,
                (FloatFrom.Margin, FloatAlign.End) => FigureHorizontalAnchor.ContentRight,
                (FloatFrom.Margin, _) => FigureHorizontalAnchor.ContentLeft,
                (_, FloatAlign.Center) => FigureHorizontalAnchor.ColumnCenter,
                (_, FloatAlign.End) => FigureHorizontalAnchor.ColumnRight,
                _ => FigureHorizontalAnchor.ColumnLeft,
            },
            VerticalAnchor = (f.VerticalFrom, f.VerticalAlign) switch
            {
                (FloatFrom.Page, FloatAlign.Center) => FigureVerticalAnchor.PageCenter,
                (FloatFrom.Page, FloatAlign.End) => FigureVerticalAnchor.PageBottom,
                (FloatFrom.Page, _) => FigureVerticalAnchor.PageTop,
                (FloatFrom.Margin, FloatAlign.Center) => FigureVerticalAnchor.ContentCenter,
                (FloatFrom.Margin, FloatAlign.End) => FigureVerticalAnchor.ContentBottom,
                (FloatFrom.Margin, _) => FigureVerticalAnchor.ContentTop,
                _ => FigureVerticalAnchor.ParagraphTop,
            },
        };
        if (f.HorizontalAlign == FloatAlign.None) figure.HorizontalOffset = Units.ToDip(f.X);
        if (f.VerticalAlign == FloatAlign.None) figure.VerticalOffset = Units.ToDip(f.Y);
        if (f.Fill is { } fill) figure.Background = new SolidColorBrush(fill);
        if (f.Border is { } border)
        {
            figure.BorderBrush = new SolidColorBrush(border);
            figure.BorderThickness = new Thickness(0.75);
        }

        if (f.Image != null && Decode(f.Image.Data) is { } source)
        {
            // A shape with text: its drawing becomes the background the text sits on.
            if (f.Blocks.Count > 0) figure.Background = new ImageBrush(source) { Stretch = Stretch.Fill };
            else figure.Blocks.Add(new BlockUIContainer(new Image { Source = source, Stretch = Stretch.Fill, Width = Units.ToDip(f.Width), Height = Units.ToDip(f.Height) }));
        }
        foreach (var block in f.Blocks) figure.Blocks.Add(ToBlock(block, ctx, Math.Max(20, f.Width - 14.4)));
        if (figure.Blocks.Count == 0) figure.Blocks.Add(new Paragraph());
        return figure;
    }

    static double? HeadingSize(DParagraph p) => p.Heading is > 0 and < 7 ? HeadingSizes[p.Heading] : null;

    static Run Styled(Run run, TextStyle s, double? headingSize)
    {
        // Default heading looks only apply when the source didn't style the heading itself.
        if (s.Bold || (headingSize != null && s.Size == null)) run.FontWeight = FontWeights.Bold;
        if (s.Italic) run.FontStyle = FontStyles.Italic;
        var size = s.Size ?? headingSize;
        if (size != null) run.FontSize = Units.ToDip(size.Value);
        if (s.Font != null) run.FontFamily = Family(s.Font);
        if (s.Color is { } c) run.Foreground = new SolidColorBrush(c);
        if (s.Highlight is { } h) run.Background = new SolidColorBrush(h);
        if (s.Underline || s.Strike)
        {
            var decorations = new TextDecorationCollection();
            if (s.Underline) decorations.Add(TextDecorations.Underline);
            if (s.Strike) decorations.Add(TextDecorations.Strikethrough);
            run.TextDecorations = decorations;
        }
        if (s.Superscript || s.Subscript)
        {
            run.BaselineAlignment = s.Superscript ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
            run.FontSize = (size != null ? Units.ToDip(size.Value) : run.FontSize) * 0.65;
        }
        return run;
    }

    const int LongTable = 300, TablePiece = 100;

    /// <summary>WPF's layout time grows with the square of a table's rows, so a long one is laid out as a stack of tables that touch.</summary>
    static IEnumerable<Table> InPieces(DTable t, FlowContext ctx, double available)
    {
        int columns = t.ColumnCount;
        int left = t.Rows.Count;
        foreach (var piece in t.Pieces(TablePiece))
        {
            var table = ToTable(piece, ctx, available, columns);
            if ((left -= piece.Rows.Count) > 0) table.Margin = new Thickness(0);
            yield return table;
        }
    }

    static Table ToTable(DTable t, FlowContext ctx, double available, int columns = 0)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, Units.ToDip(6)) };
        columns = Math.Max(Math.Max(1, columns), t.ColumnCount);
        var widths = t.ColumnWidths != null && t.ColumnWidths.Count >= columns ? t.ColumnWidths.Take(columns).ToList() : null;
        if (widths != null)
        {
            double total = widths.Sum();
            if (total > available) widths = widths.Select(w => w * available / total).ToList();
        }
        for (int i = 0; i < columns; i++)
            table.Columns.Add(new TableColumn { Width = widths != null ? new GridLength(Units.ToDip(widths[i])) : new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        var border = new SolidColorBrush(t.BorderColor ?? Color.FromRgb(0x80, 0x80, 0x80));
        var padding = t.CellPadding is { } pad
            ? new Thickness(Units.ToDip(pad.Left), Units.ToDip(pad.Top), Units.ToDip(pad.Right), Units.ToDip(pad.Bottom))
            : new Thickness(Units.ToDip(5.4), Units.ToDip(1), Units.ToDip(5.4), Units.ToDip(1));
        int rowsLeft = t.Rows.Count;
        foreach (var (r, slots) in t.Slots())
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var row = new TableRow();
            foreach (var (c, col, span, covered) in slots)
            {
                if (covered) continue;
                double cellWidth = widths != null ? widths.Skip(col).Take(span).Sum() : available / columns * span;
                var cell = new TableCell
                {
                    ColumnSpan = span,
                    RowSpan = Math.Clamp(c.RowSpan, 1, rowsLeft),
                    Padding = padding,
                    BorderBrush = border,
                    BorderThickness = t.Borders ? new Thickness(0.67) : new Thickness(0),
                };
                if (c.Shading is { } shade) cell.Background = new SolidColorBrush(shade);
                foreach (var b in c.Blocks) cell.Blocks.Add(ToBlock(b, ctx, Math.Max(20, cellWidth - Units.ToPoints(padding.Left + padding.Right))));
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                if (r.Header) foreach (var p in cell.Blocks.OfType<Paragraph>()) p.FontWeight = FontWeights.Bold;
                row.Cells.Add(cell);
            }
            rowsLeft--;
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>A font list with fallbacks, so documents using fonts this PC lacks (e.g. Aptos) still render sensibly.</summary>
    static FontFamily Family(string name)
    {
        _installedFonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_installedFonts.Contains(name)) return new FontFamily(name);
        var fallback = name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            ? "Consolas"
            : name.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? "Cambria" : "Calibri";
        return new FontFamily($"{name}, {fallback}, Segoe UI");
    }

    internal static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return MagickImages.Decode(data);
        }
    }

    // ------------------------------------------------------------------ FlowDocument -> model

    /// <param name="normalize">False when the result will be merged with other parts and normalised once afterwards.</param>
    public static DDocument FromFlow(FlowDocument flow, string? title, bool normalize = true)
    {
        var doc = new DDocument { Title = title };
        foreach (var block in flow.Blocks) FromBlock(block, doc.Blocks, 0);
        if (normalize) doc.NormalizeDefaults();
        return doc;
    }

    static void FromBlock(Block block, List<DBlock> output, int listLevel)
    {
        switch (block)
        {
            case Paragraph p:
                output.Add(FromParagraph(p));
                break;
            case Section s:
                foreach (var b in s.Blocks) FromBlock(b, output, listLevel);
                break;
            case List list:
                int index = list.StartIndex;
                foreach (var item in list.ListItems)
                {
                    bool first = true;
                    foreach (var b in item.Blocks)
                    {
                        if (b is List nested)
                        {
                            FromBlock(nested, output, listLevel + 1);
                            continue;
                        }
                        int before = output.Count;
                        FromBlock(b, output, listLevel);
                        if (first && output.Count > before && output[before] is DParagraph lp)
                        {
                            lp.List = IsBullet(list.MarkerStyle) ? ListKind.Bullet : ListKind.Number;
                            lp.ListLevel = listLevel;
                            lp.ListLabel = Marker(list.MarkerStyle, index);
                            lp.IndentLeft = Math.Max(lp.IndentLeft, 36 + listLevel * 18);
                            lp.IndentFirst = -18;
                            first = false;
                        }
                    }
                    index++;
                }
                break;
            case Table t:
                var table = new DTable();
                foreach (var group in t.RowGroups)
                    foreach (var r in group.Rows)
                    {
                        var row = new DRow();
                        foreach (var c in r.Cells)
                        {
                            var cell = new DCell { ColSpan = c.ColumnSpan, RowSpan = c.RowSpan, Shading = (c.Background as SolidColorBrush)?.Color };
                            if (c.BorderThickness.Left > 0 || c.BorderThickness.Bottom > 0) table.Borders = true;
                            foreach (var b in c.Blocks) FromBlock(b, cell.Blocks, 0);
                            row.Cells.Add(cell);
                        }
                        table.Rows.Add(row);
                    }
                var widths = t.Columns.Select(c => c.Width.IsAbsolute ? Units.ToPoints(c.Width.Value) : 0).ToList();
                if (widths.Count > 0 && widths.All(w => w > 0)) table.ColumnWidths = widths;
                output.Add(table);
                break;
            case BlockUIContainer ui when ui.Child is Image { Source: BitmapSource bmp } img:
                var para = new DParagraph();
                para.Inlines.Add(ImageFrom(bmp, img.Width, img.Height));
                output.Add(para);
                break;
        }
    }

    static DParagraph FromParagraph(Paragraph p)
    {
        var para = new DParagraph
        {
            Align = p.TextAlignment,
            SpaceBefore = Units.ToPoints(Safe(p.Margin.Top)),
            SpaceAfter = Units.ToPoints(Safe(p.Margin.Bottom)),
            IndentLeft = Units.ToPoints(Safe(p.Margin.Left)),
            IndentFirst = Units.ToPoints(Safe(p.TextIndent)),
            PageBreakBefore = p.BreakPageBefore,
            MarkSize = Units.ToPoints(p.FontSize),
            RightToLeft = p.FlowDirection == FlowDirection.RightToLeft,
        };
        // (Inside right-to-left text WPF's "left" and "right" are the other way round.)
        if (para.RightToLeft) para.Align = para.Align switch { TextAlignment.Left => TextAlignment.Right, TextAlignment.Right => TextAlignment.Left, var a => a };
        FromInlines(p.Inlines, para, null);
        return para;
    }

    static double Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    static void FromInlines(InlineCollection inlines, DParagraph para, string? link)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    if (run.Text.Length > 0) para.Inlines.Add(new DText(run.Text, StyleOf(run), link));
                    break;
                case LineBreak:
                    para.Inlines.Add(new DLineBreak());
                    break;
                case Hyperlink h:
                    FromInlines(h.Inlines, para, h.NavigateUri?.ToString() ?? link);
                    break;
                case Span span:
                    FromInlines(span.Inlines, para, link);
                    break;
                case InlineUIContainer { Child: Image { Source: BitmapSource bmp } img }:
                    para.Inlines.Add(ImageFrom(bmp, img.Width, img.Height));
                    break;
            }
        }
    }

    static TextStyle StyleOf(Run run)
    {
        bool underline = false, strike = false;
        BaselineAlignment baseline = run.BaselineAlignment;
        for (DependencyObject? e = run; e is Inline inline; e = inline.Parent)
        {
            foreach (var d in inline.TextDecorations ?? [])
            {
                if (d.Location == TextDecorationLocation.Underline) underline = true;
                if (d.Location == TextDecorationLocation.Strikethrough) strike = true;
            }
            if (baseline == BaselineAlignment.Baseline) baseline = inline.BaselineAlignment;
            // WPF's RTF import marks sub/superscript as font variants rather than baseline shifts.
            if (baseline == BaselineAlignment.Baseline)
                baseline = Typography.GetVariants(inline) switch
                {
                    FontVariants.Superscript => BaselineAlignment.Superscript,
                    FontVariants.Subscript => BaselineAlignment.Subscript,
                    _ => baseline,
                };
        }
        return new TextStyle
        {
            Bold = run.FontWeight.ToOpenTypeWeight() >= 600,
            Italic = run.FontStyle != FontStyles.Normal,
            Underline = underline,
            Strike = strike,
            Superscript = baseline == BaselineAlignment.Superscript,
            Subscript = baseline == BaselineAlignment.Subscript,
            Size = Math.Round(Units.ToPoints(run.FontSize) * 2) / 2,
            Color = (run.Foreground as SolidColorBrush)?.Color,
            Highlight = (run.Background as SolidColorBrush)?.Color is { A: > 0 } bg ? bg : null,
            Font = run.FontFamily.Source.Split(',')[0].Trim(),
        };
    }

    static DImage ImageFrom(BitmapSource bmp, double width, double height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        double w = double.IsNaN(width) ? bmp.Width : width, h = double.IsNaN(height) ? bmp.Height : height;
        return new DImage(ms.ToArray(), "png", Units.ToPoints(w), Units.ToPoints(h));
    }

    static bool IsBullet(TextMarkerStyle style) => style is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None;

    static string Marker(TextMarkerStyle style, int index) => style switch
    {
        TextMarkerStyle.Decimal => $"{index}.",
        TextMarkerStyle.LowerLatin => ListLabels.Format(index, "lowerLetter") + ".",
        TextMarkerStyle.UpperLatin => ListLabels.Format(index, "upperLetter") + ".",
        TextMarkerStyle.LowerRoman => ListLabels.Format(index, "lowerRoman") + ".",
        TextMarkerStyle.UpperRoman => ListLabels.Format(index, "upperRoman") + ".",
        TextMarkerStyle.Circle => "◦",
        TextMarkerStyle.Square or TextMarkerStyle.Box => "▪",
        _ => "•",
    };
}

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

    /// <param name="forRtf">RTF can't hold WPF controls, so list markers become plain text.</param>
    public static FlowDocument ToFlow(DDocument doc, bool forRtf = false)
    {
        var flow = new FlowDocument
        {
            FontFamily = Family(doc.Font),
            FontSize = Units.ToDip(doc.FontSize),
            PageWidth = Units.ToDip(doc.PageWidth),
            PageHeight = Units.ToDip(doc.PageHeight),
            PagePadding = new Thickness(Units.ToDip(doc.Margins.Left), Units.ToDip(doc.Margins.Top),
                Units.ToDip(doc.Margins.Right), Units.ToDip(doc.Margins.Bottom)),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
            Foreground = Brushes.Black,
            Background = Brushes.White,
        };
        foreach (var block in doc.Blocks) flow.Blocks.Add(ToBlock(block, doc, doc.ContentWidth, forRtf));
        return flow;
    }

    static Block ToBlock(DBlock block, DDocument doc, double available, bool forRtf) => block switch
    {
        DTable t => ToTable(t, doc, available, forRtf),
        DParagraph p => ToParagraph(p, doc, available, forRtf),
        _ => new Paragraph(),
    };

    static Paragraph ToParagraph(DParagraph p, DDocument doc, double available, bool forRtf)
    {
        double baseSize = p.Inlines.OfType<DText>().Select(t => t.Style.Size ?? HeadingSize(p) ?? doc.FontSize).DefaultIfEmpty(p.MarkSize ?? doc.FontSize).Max();
        var para = new Paragraph
        {
            Margin = new Thickness(Units.ToDip(p.IndentLeft), Units.ToDip(p.SpaceBefore), 0, Units.ToDip(p.SpaceAfter)),
            TextIndent = Units.ToDip(p.IndentFirst),
            TextAlignment = p.Align,
            BreakPageBefore = p.PageBreakBefore,
            KeepWithNext = p.Heading > 0,
        };
        // A fixed line height would clip pictures, so lines with images keep their natural height.
        if (Math.Abs(p.LineSpacing - 1) > 0.01 && !p.Inlines.OfType<DImage>().Any())
        {
            para.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            para.LineHeight = Units.ToDip(baseSize * 1.22 * p.LineSpacing);
        }

        if (p.ListLabel != null)
        {
            double hang = p.IndentFirst < 0 ? -p.IndentFirst : 18;
            if (forRtf)
            {
                para.Inlines.Add(new Run(p.ListLabel + "\t"));
            }
            else
            {
                // A fixed-width box keeps item text aligned with the wrapped lines, like a tab stop would.
                var label = new TextBlock
                {
                    Text = p.ListLabel,
                    Width = Units.ToDip(Math.Max(hang, 6)),
                    FontFamily = Family(doc.Font),
                    FontSize = Units.ToDip(baseSize),
                    Foreground = Brushes.Black,
                };
                para.Inlines.Add(new InlineUIContainer(label) { BaselineAlignment = BaselineAlignment.TextBottom });
            }
        }

        var heading = HeadingSize(p);
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var run = Styled(new Run(t.Text), t.Style, heading);
                    if (t.Link != null && Uri.TryCreate(t.Link, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(run) { NavigateUri = uri, Foreground = run.Foreground };
                        if (t.Style.Color == null) link.Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x63, 0xC1));
                        para.Inlines.Add(link);
                    }
                    else para.Inlines.Add(run);
                    break;
                case DLineBreak:
                    para.Inlines.Add(new LineBreak());
                    break;
                case DImage img when Decode(img.Data) is { } source:
                    double scale = Math.Min(1, available / Math.Max(1, img.Width));
                    var image = new Image
                    {
                        Source = source,
                        Width = Units.ToDip(img.Width * scale),
                        Height = Units.ToDip(img.Height * scale),
                        Stretch = Stretch.Fill,
                    };
                    para.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Baseline });
                    break;
            }
        }

        if (para.Inlines.Count == 0)
            para.Inlines.Add(new Run(forRtf ? "" : "​") { FontSize = Units.ToDip(p.MarkSize ?? doc.FontSize) });
        return para;
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

    static Table ToTable(DTable t, DDocument doc, double available, bool forRtf)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, Units.ToDip(6)) };
        int columns = Math.Max(1, t.ColumnCount);
        var widths = t.ColumnWidths != null && t.ColumnWidths.Count >= columns ? t.ColumnWidths.Take(columns).ToList() : null;
        if (widths != null)
        {
            double total = widths.Sum();
            if (total > available) widths = widths.Select(w => w * available / total).ToList();
        }
        for (int i = 0; i < columns; i++)
            table.Columns.Add(new TableColumn { Width = widths != null ? new GridLength(Units.ToDip(widths[i])) : new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        var border = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        foreach (var r in t.Rows)
        {
            var row = new TableRow();
            int col = 0;
            foreach (var c in r.Cells)
            {
                int span = Math.Max(1, c.ColSpan);
                double cellWidth = widths != null ? widths.Skip(col).Take(span).Sum() : available / columns * span;
                col += span;
                var cell = new TableCell
                {
                    ColumnSpan = span,
                    Padding = new Thickness(Units.ToDip(5.4), Units.ToDip(1), Units.ToDip(5.4), Units.ToDip(1)),
                    BorderBrush = border,
                    BorderThickness = t.Borders ? new Thickness(0.67) : new Thickness(0),
                };
                if (c.Shading is { } shade) cell.Background = new SolidColorBrush(shade);
                foreach (var b in c.Blocks) cell.Blocks.Add(ToBlock(b, doc, Math.Max(20, cellWidth - 10.8), forRtf));
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                if (r.Header) foreach (var p in cell.Blocks.OfType<Paragraph>()) p.FontWeight = FontWeights.Bold;
                row.Cells.Add(cell);
            }
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
            return null;
        }
    }

    // ------------------------------------------------------------------ FlowDocument -> model

    public static DDocument FromFlow(FlowDocument flow, string? title)
    {
        var doc = new DDocument { Title = title };
        foreach (var block in flow.Blocks) FromBlock(block, doc.Blocks, 0);
        doc.NormalizeDefaults();
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
                            var cell = new DCell { ColSpan = c.ColumnSpan, Shading = (c.Background as SolidColorBrush)?.Color };
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
        };
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

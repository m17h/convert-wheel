using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion.Presentations;
using static ConvertWheel.Conversion.Presentations.OfficeArt;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Floating drawings in Word 97–2003 files: pictures, shapes, text boxes, WordArt and groups. Each sits at a
/// marked character in the text, with its position and wrapping in a separate table (the "SPA" list) and its
/// look in the document's OfficeArt drawing.
/// </summary>
internal sealed partial class WordBinaryReader
{
    sealed record Spa(int Spid, Rect Twips, int Flags);

    readonly Dictionary<int, Spa> _spas = [];
    readonly Dictionary<int, Rec> _drawings = [];
    readonly List<(int offset, int size, Rec? embedded)> _bstore = [];
    readonly Dictionary<int, (int start, int end)> _textboxes = [];

    void ReadDrawings()
    {
        try
        {
            // Anchors: character positions, each with the shape id, its rectangle (twips) and placement flags.
            var (spaFc, spaLcb) = FcLcb(40);
            if (spaLcb >= 4 + 26)
            {
                int n = (spaLcb - 4) / 30;
                for (int k = 0; k < n; k++)
                {
                    int cp = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(spaFc + k * 4));
                    int at = spaFc + (n + 1) * 4 + k * 26;
                    int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(at + o));
                    var rect = new Rect(I(4), I(8), Math.Max(0, I(12) - I(4)), Math.Max(0, I(16) - I(8)));
                    _spas[cp] = new Spa(I(0), rect, BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(at + 24)));
                }
            }

            // Text box text: ranges of the text-box story, each tagged with its shape id.
            var (txFc, txLcb) = FcLcb(56);
            if (txLcb >= 4 + 22)
            {
                int n = (txLcb - 4) / 26;
                for (int k = 0; k < n; k++)
                {
                    int start = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(txFc + k * 4));
                    int end = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(txFc + (k + 1) * 4));
                    int lid = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(txFc + (n + 1) * 4 + k * 22 + 14));
                    _textboxes.TryAdd(lid, (start, end));
                }
            }

            // The drawing itself: a picture store, then one drawing per story kind (0 = main text, 1 = headers).
            var (fc, lcb) = FcLcb(50);
            if (lcb < 8) return;
            int i = fc, stop = fc + lcb;
            var dgg = At(_table, i);
            if (dgg.Type == 0xF000)
            {
                if (Child(_table, dgg, 0xF001) is { } store)
                    foreach (var fbse in Children(_table, store).Where(r => r.Type == 0xF007))
                    {
                        int size = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(fbse.Start + 20));
                        int delay = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(fbse.Start + 28));
                        int nameLength = _table[fbse.Start + 33];
                        Rec? embedded = fbse.Length > 36 + nameLength ? At(_table, fbse.Start + 36 + nameLength) : null;
                        _bstore.Add((delay, size, embedded));
                    }
                i = dgg.End;
            }
            while (i + 9 <= stop)
            {
                int label = _table[i];
                var dg = At(_table, i + 1);
                if (dg.Type != 0xF002) break;
                if (label == 0 && Child(_table, dg, 0xF003) is { } tree)
                    foreach (var item in Children(_table, tree))
                        if (Spid(item) is int spid) _drawings[spid] = item;
                i = dg.End;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Log.Write($"Skipped the drawings of a Word document: {ex.Message}");
        }
    }

    /// <summary>A shape's id (for a group, its first shape: the group itself).</summary>
    int? Spid(Rec item)
    {
        var sp = item.Type == 0xF003 ? Children(_table, item).FirstOrDefault(r => r.Type == 0xF004) : item;
        if (sp.Type != 0xF004 || Child(_table, sp, 0xF00A) is not { } fsp) return null;
        return BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(fsp.Start));
    }

    readonly Dictionary<int, DImage?> _blips = [];

    DImage? BlipAt(int index)
    {
        if (index < 1 || index > _bstore.Count) return null;
        // The same picture can be used by any number of shapes.
        if (!_blips.TryGetValue(index, out var known)) _blips[index] = known = ReadBlip(index);
        return known == null ? null : new DImage(known.Data, known.Ext, known.Width, known.Height);
    }

    DImage? ReadBlip(int index)
    {
        var (offset, size, embedded) = _bstore[index - 1];
        byte[]? data; string? ext;
        if (embedded is { } e) (data, ext) = Blip(_table.AsSpan(e.Start - 8, e.Length + 8));
        else if (offset >= 0 && offset < _word.Length && size > 0) (data, ext) = Blip(_word.AsSpan(offset, Math.Min(size, _word.Length - offset)));
        else return null;
        if (data == null) return null;
        var (w, h) = DocxReader.ImageSize(data);
        return Usable(data, w, h);
    }

    /// <summary>The floating drawing anchored at a character, as a positioned picture and/or text box.</summary>
    DFloat? Drawing(int cp)
    {
        if (!_spas.TryGetValue(cp, out var spa) || !_drawings.TryGetValue(spa.Spid, out var item)) return null;
        double width = spa.Twips.Width / 20, height = spa.Twips.Height / 20;
        if (width < 1 || height < 1 || width > 4000 || height > 4000) return null;
        var f = new DFloat { Width = width, Height = height, X = spa.Twips.X / 20, Y = spa.Twips.Y / 20 };

        // Placement: relative to the margin, page or paragraph, wrapped, behind or in front of the text.
        int bx = (spa.Flags >> 1) & 3, by = (spa.Flags >> 3) & 3, wrap = (spa.Flags >> 5) & 0xF;
        bool behind = (spa.Flags & 0x4000) != 0;
        f.HorizontalFrom = bx == 1 ? FloatFrom.Page : FloatFrom.Margin;
        f.VerticalFrom = by switch { 0 => FloatFrom.Margin, 1 => FloatFrom.Page, _ => FloatFrom.Paragraph };
        f.Wrap = wrap switch { 1 => FloatWrap.TopAndBottom, 3 => behind ? FloatWrap.Behind : FloatWrap.InFront, _ => FloatWrap.Square };

        // Newer Word versions keep the true anchors in the shape's own properties, which take precedence.
        var head = item.Type == 0xF003 ? Children(_table, item).FirstOrDefault(r => r.Type == 0xF004) : item;
        if (head.Type == 0xF004)
        {
            var props = Properties(_table, head, out _);
            if (props.TryGetValue(0x0390, out var relH)) f.HorizontalFrom = relH == 1 ? FloatFrom.Page : FloatFrom.Margin;
            if (props.TryGetValue(0x0392, out var relV)) f.VerticalFrom = relV switch { 0 => FloatFrom.Margin, 1 => FloatFrom.Page, _ => FloatFrom.Paragraph };
        }

        var shapes = new List<SlideShape>();
        try { Collect(item, new Rect(0, 0, width, height), shapes, f.Blocks, 0); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Log.Write($"Skipped part of a drawing in a Word document: {ex.Message}");
        }
        if (shapes.Count == 1 && shapes[0] is { Picture: { } picture, Line: null, Rotation: 0, FlipH: false, FlipV: false })
            f.Image = new DImage(picture.Data, picture.Ext, width, height); // a plain picture keeps its own pixels
        else if (shapes.Any(s => s.Picture != null || s.Fill != null || s.Line != null))
            f.Image = SlideRenderer.ToPicture(shapes, width, height);
        return f.Image != null || f.Blocks.Count > 0 ? f : null;
    }

    /// <summary>Gathers the shapes of a drawing (recursing into groups), and the text of its text boxes and WordArt.</summary>
    void Collect(Rec item, Rect bounds, List<SlideShape> shapes, List<DBlock> text, int depth)
    {
        if (!DocumentLimits.CanGoDeeper(depth, 32) || shapes.Count >= 4000) return;
        if (item.Type == 0xF003)
        {
            // A group: its first shape defines the coordinate space its children are placed in.
            var children = Children(_table, item).ToList();
            var head = children.FirstOrDefault(r => r.Type == 0xF004);
            Rect space = bounds;
            if (head.Type == 0xF004 && Child(_table, head, 0xF009) is { } fspgr)
            {
                int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(fspgr.Start + o));
                space = new Rect(I(0), I(4), Math.Max(1, I(8) - I(0)), Math.Max(1, I(12) - I(4)));
            }
            foreach (var child in children.Skip(1))
            {
                var anchor = child.Type == 0xF003 ? Children(_table, child).FirstOrDefault(r => r.Type == 0xF004) : child;
                Rect place = bounds;
                if (anchor.Type == 0xF004 && Child(_table, anchor, 0xF00F) is { } ca)
                {
                    int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(ca.Start + o));
                    double sx = bounds.Width / space.Width, sy = bounds.Height / space.Height;
                    place = new Rect(bounds.X + (I(0) - space.X) * sx, bounds.Y + (I(4) - space.Y) * sy, Math.Max(0, (I(8) - I(0)) * sx), Math.Max(0, (I(12) - I(4)) * sy));
                }
                Collect(child, place, shapes, text, depth + 1);
            }
            return;
        }
        if (item.Type != 0xF004) return;
        var shape = OfficeArt.Shape(_table, item, bounds, BlipAt, out var wordArt, out _, out var wordArtFont);
        if (double.IsFinite(bounds.Width + bounds.Height + bounds.X + bounds.Y)) shapes.Add(shape);
        if (Spid(item) is int spid && _textboxes.TryGetValue(spid, out var box) && box.end > box.start)
            ReadStory(_textboxStory + box.start, _textboxStory + box.end, text, []);
        if (!string.IsNullOrWhiteSpace(wordArt)) WordArt(shape, wordArt, wordArtFont, bounds);
    }

    /// <summary>
    /// WordArt: the text's letter outlines, stretched to fill the box and painted with the shape's own
    /// fill and outline (so outlined, coloured and gradient WordArt all look as in Word).
    /// </summary>
    static void WordArt(SlideShape shape, string text, string? font, Rect bounds)
    {
        var face = new Typeface(new FontFamily(font ?? "Arial Black"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        var formatted = new FormattedText(lines, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 100, Brushes.Black, 1.0)
        {
            TextAlignment = TextAlignment.Center,
        };
        var glyphs = formatted.BuildGeometry(new Point(0, 0));
        var extent = glyphs.Bounds;
        if (extent.IsEmpty || extent.Width <= 0 || extent.Height <= 0) return;
        var stretch = new TransformGroup();
        stretch.Children.Add(new TranslateTransform(-extent.X, -extent.Y));
        stretch.Children.Add(new ScaleTransform(bounds.Width / extent.Width, bounds.Height / extent.Height));
        glyphs.Transform = stretch;
        shape.Geometry = ShapeGeometry.Custom;
        shape.Path = PathGeometry.CreateFromGeometry(glyphs);
        shape.Fill ??= SlideFill.Solid(Colors.Black);
        if (shape.Line != null) shape.LineWidth = Math.Min(shape.LineWidth, 1);
    }

    /// <summary>
    /// An inline drawing shape (inline WordArt, a shape "in line with text"): an OfficeArt shape after the
    /// picture header, followed by the pictures it uses.
    /// </summary>
    DImage? InlineShape(int offset, double width, double height)
    {
        if (offset + 8 > _data.Length || width < 1 || height < 1) return null;
        var sp = At(_data, offset);
        if (sp.Type != 0xF004) return null;
        // The pictures it refers to follow it, numbered from 1.
        var pictures = new List<Rec>();
        for (int i = sp.End; i + 8 <= _data.Length && pictures.Count < 64;)
        {
            var r = At(_data, i);
            if (r.Type != 0xF007) break;
            pictures.Add(r);
            i = r.End;
        }
        DImage? Picture(int index)
        {
            if (index < 1 || index > pictures.Count) return null;
            var fbse = pictures[index - 1];
            var (data, ext) = Blip(_data.AsSpan(fbse.Start, fbse.Length));
            if (data == null) return null;
            var (w, h) = DocxReader.ImageSize(data);
            return Usable(data, w, h);
        }
        var bounds = new Rect(0, 0, width, height);
        bool wantsPicture = false;
        DImage? Resolve(int index)
        {
            wantsPicture = true;
            return Picture(index);
        }
        var shape = OfficeArt.Shape(_data, sp, bounds, Resolve, out var wordArt, out _, out var font);
        if (wantsPicture && shape.Picture == null) return null; // let the caller find the picture another way
        if (string.IsNullOrWhiteSpace(wordArt) && shape.Picture == null && OfficeArt.Child(_data, sp, 0xF00A)?.Instance is 75 or 0) return null;
        if (!string.IsNullOrWhiteSpace(wordArt)) WordArt(shape, wordArt, font, bounds);
        if (shape.Picture != null && shape.Line == null && shape.Fill == null) return new DImage(shape.Picture.Data, shape.Picture.Ext, width, height);
        return shape.Picture != null || shape.Fill != null || shape.Line != null ? SlideRenderer.ToPicture([shape], width, height) : null;
    }
}

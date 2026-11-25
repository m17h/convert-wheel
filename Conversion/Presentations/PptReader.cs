using System.Buffers.Binary;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Reads PowerPoint 97–2003 (.ppt) files without PowerPoint: slides, shapes with their fills and lines,
/// pictures, backgrounds, and text with the master's default styles.
/// </summary>
internal sealed class PptReader
{
    const double MasterUnit = 72.0 / 576; // positions are in 576ths of an inch

    readonly byte[] _doc;
    readonly CancellationToken _ct;
    readonly byte[]? _pictures;
    readonly Dictionary<int, int> _persist = [];
    readonly Dictionary<int, int> _notesPersist = [];
    readonly List<string> _fonts = [];
    readonly List<(int offset, int size)> _blips = [];
    readonly Dictionary<int, DImage?> _images = [];
    Dictionary<int, TextDefaults[]> _masterStyles = [];
    int _shapes;
    Color[] _scheme = [Colors.White, Colors.Black, Colors.Gray, Colors.Black, Color.FromRgb(0x99, 0xCC, 0x00), Color.FromRgb(0x33, 0x33, 0x99), Color.FromRgb(0x00, 0x99, 0x99), Color.FromRgb(0x99, 0x99, 0x00)];

    /// <summary>A record: type, instance, and where its body lies in the stream.</summary>
    readonly record struct Rec(int Type, int Instance, int Start, int Length)
    {
        public int End => Start + Length;
    }

    sealed class TextDefaults
    {
        public double Size = 18;
        public bool Bold, Italic, Underline, Bullet;
        public char BulletChar = '•';
        public Color Color = Colors.Black;
        public string? Font;
        public TextAlignment Align = TextAlignment.Left;
        public double TextStart, BulletStart;
        /// <summary>Positive: percent of a line; negative: an absolute amount in master units.</summary>
        public int Line = 100, Before, After;
        public TextDefaults Clone() => (TextDefaults)MemberwiseClone();
    }

    PptReader(byte[] doc, byte[]? pictures, CancellationToken ct) => (_doc, _pictures, _ct) = (doc, pictures, ct);

    public static Deck Read(WordBinaryReader.CompoundFile file, string title, string protectedMessage, CancellationToken ct = default)
    {
        var doc = file.Stream("PowerPoint Document") ?? throw new ConversionException("This isn't a presentation, or it is damaged.");
        var user = file.Stream("Current User");
        if (user == null || user.Length < 24) throw SlideLimits.Damaged();
        if (BinaryPrimitives.ReadUInt32LittleEndian(user.AsSpan(12)) == 0xF3D1C4DF) throw new ConversionException(protectedMessage);
        return new PptReader(doc, file.Stream("Pictures"), ct).Build(BinaryPrimitives.ReadInt32LittleEndian(user.AsSpan(16)), title);
    }

    // ------------------------------------------------------------------ records

    Rec At(int offset)
    {
        int verInst = BinaryPrimitives.ReadUInt16LittleEndian(_doc.AsSpan(offset));
        int type = BinaryPrimitives.ReadUInt16LittleEndian(_doc.AsSpan(offset + 2));
        int length = BinaryPrimitives.ReadInt32LittleEndian(_doc.AsSpan(offset + 4));
        return new Rec(type, verInst >> 4, offset + 8, Math.Clamp(length, 0, _doc.Length - offset - 8));
    }

    IEnumerable<Rec> Children(Rec parent)
    {
        for (int i = parent.Start; i + 8 <= parent.End;)
        {
            var r = At(i);
            yield return r;
            i = r.End;
        }
    }

    Rec? Child(Rec parent, int type) => Children(parent).Where(r => r.Type == type).Select(r => (Rec?)r).FirstOrDefault();

    /// <summary>Records of a type at any depth, in document order. Containers (version 0xF) hold more records.</summary>
    IEnumerable<Rec> Descendants(Rec parent, int type)
    {
        var open = new Stack<(int at, int end)>();
        open.Push((parent.Start, parent.End));
        while (open.Count > 0)
        {
            var (at, end) = open.Pop();
            if (at + 8 > end) continue;
            var r = At(at);
            open.Push((r.End, end));
            if ((U16(at) & 0xF) == 0xF && open.Count < SlideLimits.Depth) open.Push((r.Start, r.End));
            if (r.Type == type) yield return r;
        }
    }

    int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(_doc.AsSpan(at));
    int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_doc.AsSpan(at));
    short S16(int at) => BinaryPrimitives.ReadInt16LittleEndian(_doc.AsSpan(at));

    // ------------------------------------------------------------------ document

    Deck Build(int currentEdit, string title)
    {
        // The persist directory maps object ids to stream offsets; later edits override earlier ones.
        var seen = new HashSet<int>();
        int docRef = 0;
        for (int edit = currentEdit; edit > 0 && edit + 8 < _doc.Length && seen.Add(edit);)
        {
            var atom = At(edit);
            if (atom.Type != 0x0FF5) break;
            if (docRef == 0) docRef = I32(atom.Start + 16);
            var dir = At(I32(atom.Start + 12));
            for (int i = dir.Start; i + 4 <= dir.End;)
            {
                uint head = BinaryPrimitives.ReadUInt32LittleEndian(_doc.AsSpan(i));
                int id = (int)(head & 0xFFFFF), count = (int)(head >> 20);
                for (int k = 0; k < count && i + 8 + k * 4 <= dir.End; k++) _persist.TryAdd(id + k, I32(i + 4 + k * 4));
                i += 4 + count * 4;
            }
            edit = I32(atom.Start + 8);
        }
        if (!_persist.TryGetValue(docRef, out var docOffset)) throw new ConversionException("This PowerPoint file is damaged.");
        var document = At(docOffset);

        var deck = new Deck { Title = title };
        if (Child(document, 0x03E9) is { Length: >= 8 } docAtom)
        {
            deck.Width = I32(docAtom.Start) * MasterUnit;
            deck.Height = I32(docAtom.Start + 4) * MasterUnit;
        }
        ReadFonts(document);
        ReadPictureStore(document);
        if (_fonts.Count > 0) deck.Font = _fonts[0];

        var lists = Children(document).Where(r => r.Type == 0x0FF0).ToList();
        var masterList = lists.FirstOrDefault(l => l.Instance == 1);
        // Every design in the deck, by the id its slides ask for; the first one steps in when there's no match.
        var masters = new Dictionary<int, (Rec? rec, Color[] scheme, Dictionary<int, TextDefaults[]> styles)>();
        (Rec? rec, Color[] scheme, Dictionary<int, TextDefaults[]> styles)? firstMaster = null;
        if (masterList.Length > 0)
            foreach (var mp in Descendants(masterList, 0x03F3).Where(r => r.Length >= 16))
            {
                if (!_persist.TryGetValue(I32(mp.Start), out var mo) || masters.Count >= 1000) continue;
                var rec = At(mo);
                if (rec.Type != 0x03F8 && (rec.Type != 0x03EE || firstMaster == null)) continue;
                var styles = firstMaster is { } f ? new Dictionary<int, TextDefaults[]>(f.styles) : [];
                var masterScheme = Scheme(rec) ?? firstMaster?.scheme ?? _scheme;
                if (rec.Type == 0x03F8)
                {
                    (_scheme, _masterStyles) = (masterScheme, styles);
                    ReadMasterStyles(rec);
                }
                firstMaster ??= (rec, masterScheme, styles);
                masters.TryAdd(I32(mp.Start + 12), (rec, masterScheme, styles));
            }

        foreach (var r in lists.Where(l => l.Instance == 2).SelectMany(Children).Where(r => r.Type == 0x03F3 && r.Length >= 16))
            _notesPersist[I32(r.Start + 12)] = I32(r.Start);
        var slideList = lists.FirstOrDefault(l => l.Instance == 0);
        if (slideList.Length == 0) throw new ConversionException("This presentation has no slides.");

        // Each slide's entry in the list carries the text of its placeholders, in order.
        var entries = new List<(int persist, List<Rec> texts)>();
        foreach (var r in Children(slideList))
        {
            _ct.ThrowIfCancellationRequested();
            if (r.Type == 0x03F3 && r.Length >= 4)
            {
                entries.Add((I32(r.Start), []));
                SlideLimits.CheckSlides(entries.Count);
            }
            else if (entries.Count > 0) entries[^1].texts.Add(r);
        }
        foreach (var (persist, texts) in entries)
        {
            _ct.ThrowIfCancellationRequested();
            if (!_persist.TryGetValue(persist, out var offset)) continue;
            var slide = At(offset);
            if (slide.Type != 0x03EE) continue;
            var slideAtom = Child(slide, 0x03EF);
            var master = slideAtom is { Length: >= 16 } a && masters.TryGetValue(I32(a.Start + 12), out var named) ? named : firstMaster;
            if (master is { } m) (_scheme, _masterStyles) = (m.scheme, m.styles);
            bool ownScheme = slideAtom is { Length: >= 22 } s && (U16(s.Start + 20) & 0x2) == 0;
            var scheme = (ownScheme ? Scheme(slide) : null) ?? (master?.scheme ?? Scheme(slide) ?? _scheme);
            var read = ReadSlide(slide, master?.rec, scheme, Grouped(texts));
            read.Notes.AddRange(Notes(slide));
            // The slide show settings say whether the slide is skipped.
            read.Hidden = Child(slide, 0x03F9) is { Length: >= 12 } show && (U16(show.Start + 10) & 0x4) != 0;
            deck.Slides.Add(read);
        }
        if (deck.Slides.Count == 0) throw new ConversionException("This presentation has no slides.");
        return deck;
    }

    /// <summary>Speaker notes: the notes-type text on the slide's notes page.</summary>
    List<string> Notes(Rec slide)
    {
        var notes = new List<string>();
        // The slide names its notes page by notes id; the notes list maps that id to where the page is stored.
        if (Child(slide, 0x03EF) is not { Length: >= 20 } atom || !_notesPersist.TryGetValue(I32(atom.Start + 16), out var persist)
            || !_persist.TryGetValue(persist, out var offset) || offset <= 0) return notes;
        var page = At(offset);
        if (page.Type != 0x03F0) return notes;
        foreach (var box in Descendants(page, 0xF00D))
        {
            var records = Children(box).ToList();
            if (records.FirstOrDefault() is not { Type: 0x0F9F, Length: >= 4 } header || I32(header.Start) != 2) continue;
            foreach (var r in records)
            {
                var text = r.Type switch
                {
                    0x0FA0 => Encoding.Unicode.GetString(_doc, r.Start, r.Length & ~1),
                    0x0FA8 => Encoding.Latin1.GetString(_doc, r.Start, r.Length),
                    _ => null,
                };
                if (text != null) notes.AddRange(text.Split('\r').Select(line => line.Replace('\v', '\n')));
            }
        }
        while (notes.Count > 0 && notes[^1].Length == 0) notes.RemoveAt(notes.Count - 1);
        return notes;
    }

    /// <summary>Splits a slide's text records into one group per text block (each starts with a text header).</summary>
    static List<List<Rec>> Grouped(List<Rec> records)
    {
        var groups = new List<List<Rec>>();
        foreach (var r in records)
        {
            if (r.Type == 0x0F9F) groups.Add([r]);
            else if (groups.Count > 0) groups[^1].Add(r);
        }
        return groups;
    }

    void ReadFonts(Rec document)
    {
        foreach (var font in Descendants(document, 0x0FB7).Where(r => r.Length >= 64))
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                char ch = (char)U16(font.Start + i * 2);
                if (ch == 0) break;
                sb.Append(ch);
            }
            _fonts.Add(sb.ToString());
        }
    }

    void ReadPictureStore(Rec document)
    {
        foreach (var fbse in Descendants(document, 0xF007))
        {
            if (fbse.Length < 32) { _blips.Add((-1, 0)); continue; }
            int size = I32(fbse.Start + 20), offset = I32(fbse.Start + 28);
            _blips.Add((offset, size));
        }
    }

    Color[]? Scheme(Rec container)
    {
        if (Child(container, 0x07F0) is not { } atom || atom.Length < 32) return null;
        var colors = new Color[8];
        for (int i = 0; i < 8; i++) colors[i] = Color.FromRgb(_doc[atom.Start + i * 4], _doc[atom.Start + i * 4 + 1], _doc[atom.Start + i * 4 + 2]);
        return colors;
    }

    // ------------------------------------------------------------------ master text styles

    void ReadMasterStyles(Rec master)
    {
        foreach (var atom in Children(master).Where(r => r.Type == 0x0FA3))
        {
            int levels = U16(atom.Start), at = atom.Start + 2;
            var styles = new TextDefaults[5];
            var current = new TextDefaults { Size = atom.Instance switch { 0 or 6 => 44, 4 => 18, _ => 32 } };
            for (int l = 0; l < Math.Min(levels, 5); l++)
            {
                if (atom.Instance >= 5) at += 2;
                var level = current.Clone();
                at = ParagraphProps(at, level, _scheme);
                at = CharProps(at, level, _scheme);
                styles[l] = level;
                current = level;
            }
            for (int l = levels; l < 5; l++) styles[l] = current.Clone();
            _masterStyles[atom.Instance] = styles;
        }
    }

    TextDefaults Defaults(int textType, int level)
    {
        // Center body/title and half/quarter bodies inherit from body/title.
        int baseType = textType switch { 5 or 7 or 8 => 1, 6 => 0, _ => textType };
        if (_masterStyles.TryGetValue(textType, out var own) || _masterStyles.TryGetValue(baseType, out own))
        {
            var d = own[Math.Clamp(level, 0, 4)].Clone();
            if (textType is 5 or 6) d.Align = TextAlignment.Center;
            return d;
        }
        return new TextDefaults
        {
            Size = textType is 0 or 6 ? 44 : textType == 4 ? 18 : level switch { 0 => 32, 1 => 28, 2 => 24, _ => 20 },
            Bullet = textType is 1 or 7 or 8,
            Align = textType is 5 or 6 ? TextAlignment.Center : TextAlignment.Left,
        };
    }

    /// <summary>Paragraph exception: only the fields its mask says are present, in a fixed order.</summary>
    int ParagraphProps(int at, TextDefaults d, Color[] scheme)
    {
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(_doc.AsSpan(at));
        at += 4;
        if ((mask & 0xF) != 0)
        {
            int flags = U16(at);
            if ((mask & 1) != 0) d.Bullet = (flags & 1) != 0;
            at += 2;
        }
        if ((mask & 0x80) != 0) { char c = (char)U16(at); if (c != 0) d.BulletChar = c; at += 2; }
        if ((mask & 0x10) != 0) at += 2;
        if ((mask & 0x40) != 0) at += 2;
        if ((mask & 0x20) != 0) at += 4;
        if ((mask & 0x800) != 0)
        {
            d.Align = U16(at) switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, 3 or 4 => TextAlignment.Justify, _ => TextAlignment.Left };
            at += 2;
        }
        if ((mask & 0x1000) != 0) { d.Line = S16(at); at += 2; }
        if ((mask & 0x2000) != 0) { d.Before = S16(at); at += 2; }
        if ((mask & 0x4000) != 0) { d.After = S16(at); at += 2; }
        if ((mask & 0x100) != 0) { d.TextStart = S16(at) * MasterUnit; at += 2; }
        if ((mask & 0x400) != 0) { d.BulletStart = S16(at) * MasterUnit; at += 2; }
        if ((mask & 0x8000) != 0) at += 2;
        if ((mask & 0x100000) != 0) at += 2 + U16(at) * 4;
        if ((mask & 0x10000) != 0) at += 2;
        if ((mask & 0xE0000) != 0) at += 2;
        if ((mask & 0x200000) != 0) at += 2;
        return at;
    }

    int CharProps(int at, TextDefaults d, Color[] scheme)
    {
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(_doc.AsSpan(at));
        at += 4;
        if ((mask & 0xFFFF) != 0)
        {
            int style = U16(at);
            if ((mask & 1) != 0) d.Bold = (style & 1) != 0;
            if ((mask & 2) != 0) d.Italic = (style & 2) != 0;
            if ((mask & 4) != 0) d.Underline = (style & 4) != 0;
            at += 2;
        }
        if ((mask & 0x10000) != 0) { int f = U16(at); if (f < _fonts.Count) d.Font = _fonts[f]; at += 2; }
        if ((mask & 0x200000) != 0) at += 2;
        if ((mask & 0x400000) != 0) at += 2;
        if ((mask & 0x800000) != 0) at += 2;
        if ((mask & 0x20000) != 0) { d.Size = U16(at); at += 2; }
        if ((mask & 0x40000) != 0)
        {
            byte index = _doc[at + 3];
            d.Color = index == 0xFE ? Color.FromRgb(_doc[at], _doc[at + 1], _doc[at + 2]) : index < 8 ? scheme[index] : d.Color;
            at += 4;
        }
        if ((mask & 0x80000) != 0) at += 2;
        if ((mask & 0x100000) != 0) at += 4;
        if ((mask & 0x1000000) != 0) at += 2;
        if ((mask & 0x2000000) != 0) at += 2;
        if ((mask & 0x4000000) != 0) at += 4;
        return at;
    }

    // ------------------------------------------------------------------ slides and shapes

    /// <summary>Maps a group's child coordinates (its own space) onto its place in the parent, in master units.</summary>
    sealed record GroupMap(Rect From, Rect To)
    {
        public Rect Apply(Rect r)
        {
            double sx = From.Width > 0 ? To.Width / From.Width : 1, sy = From.Height > 0 ? To.Height / From.Height : 1;
            return new Rect(To.X + (r.X - From.X) * sx, To.Y + (r.Y - From.Y) * sy, r.Width * sx, r.Height * sy);
        }
    }

    /// <summary>Reads a group's shapes (bottom to top), then mirrors and turns them as the group itself is.</summary>
    void ReadGroup(Rec group, GroupMap? parent, int depth, Color[] scheme, List<List<Rec>> listTexts, List<SlideShape> into, ref SlideFill? background, bool onMaster)
    {
        if (depth > SlideLimits.Depth) throw SlideLimits.Damaged();
        var items = Children(group).ToList();
        var map = parent;
        Rec? own = null;
        Rect placed = default;
        double turn = 0;
        if (items.Count > 0 && items[0].Type == 0xF004 && Child(items[0], 0xF009) is { Length: >= 16 } fspgr && Anchor(items[0], parent) is { } anchor)
        {
            own = items[0];
            turn = Properties(items[0]).TryGetValue(0x0004, out var angle) ? (int)angle / 65536.0 % 360 : 0;
            placed = Upright(anchor, turn);
            var space = new Rect(I32(fspgr.Start), I32(fspgr.Start + 4),
                Math.Max(0, (double)I32(fspgr.Start + 8) - I32(fspgr.Start)), Math.Max(0, (double)I32(fspgr.Start + 12) - I32(fspgr.Start + 4)));
            map = new GroupMap(space, placed);
        }
        int start = into.Count;
        foreach (var item in items)
        {
            if (item.Type == 0xF003) ReadGroup(item, map, depth + 1, scheme, listTexts, into, ref background, onMaster);
            else if (item.Type == 0xF004) Add(item, map, scheme, listTexts, into, ref background, onMaster);
        }

        if (own is not { } shape || Child(shape, 0xF00A) is not { Length: >= 8 } head) return;
        int flags = I32(head.Start + 4);
        bool flipH = (flags & 0x40) != 0, flipV = (flags & 0x80) != 0;
        if (!flipH && !flipV && Math.Abs(turn) < 0.01) return;
        var center = new Point((placed.X + placed.Width / 2) * MasterUnit, (placed.Y + placed.Height / 2) * MasterUnit);
        var about = Matrix.Identity;
        about.Translate(-center.X, -center.Y);
        about.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        about.Rotate(turn);
        about.Translate(center.X, center.Y);
        for (int i = start; i < into.Count; i++) into[i].Transform(about);
    }

    void Add(Rec sp, GroupMap? map, Color[] scheme, List<List<Rec>> listTexts, List<SlideShape> into, ref SlideFill? background, bool onMaster)
    {
        SlideLimits.CheckShapes(++_shapes);
        try
        {
            if (ReadShape(sp, map, scheme, listTexts, out var isBackground, onMaster) is not { } shape) return;
            if (isBackground) background ??= shape.Fill;
            else into.Add(shape);
        }
        catch (Exception ex) when (SlideLimits.CanSkip(ex)) { Log.Write($"Skipped a shape: {ex.Message}"); }
    }

    /// <summary>A shape's rectangle in master units: a slide anchor, or a child anchor inside its group.</summary>
    Rect? Anchor(Rec sp, GroupMap? map)
    {
        if (Child(sp, 0xF010) is { Length: >= 8 } anchor)
        {
            double top, left, right, bottom;
            if (anchor.Length >= 16) (top, left, right, bottom) = (I32(anchor.Start), I32(anchor.Start + 4), I32(anchor.Start + 8), I32(anchor.Start + 12));
            else (top, left, right, bottom) = (S16(anchor.Start), S16(anchor.Start + 2), S16(anchor.Start + 4), S16(anchor.Start + 6));
            return new Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
        }
        if (Child(sp, 0xF00F) is { Length: >= 16 } child)
        {
            double l = I32(child.Start), t = I32(child.Start + 4), r = I32(child.Start + 8), b = I32(child.Start + 12);
            var rect = new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
            return map?.Apply(rect) ?? rect;
        }
        return null;
    }

    Slide ReadSlide(Rec slideRec, Rec? master, Color[] scheme, List<List<Rec>> listTexts)
    {
        var slide = new Slide();
        var drawing = Descendants(slideRec, 0xF003).FirstOrDefault();
        var shapes = new List<SlideShape>();
        SlideFill? background = null;
        if (drawing.Length > 0) ReadGroup(drawing, null, 0, scheme, listTexts, shapes, ref background, onMaster: false);
        // The background shape sits outside the shape tree.
        if (background == null)
            foreach (var sp in Outside(slideRec)) Add(sp, null, scheme, [], [], ref background, onMaster: false);

        // Follow the master's background and decorations unless the slide says otherwise.
        var slideAtom = Child(slideRec, 0x03EF);
        int flags = slideAtom is { Length: >= 22 } a ? U16(a.Start + 20) : 0x7;
        bool followBackground = (flags & 0x4) != 0, followObjects = (flags & 0x1) != 0;
        if (master is { } m && (background == null || followBackground || followObjects))
        {
            SlideFill? masterBackground = null;
            var decorations = new List<SlideShape>();
            if (Descendants(m, 0xF003).FirstOrDefault() is { Length: > 0 } tree) ReadGroup(tree, null, 0, scheme, [], decorations, ref masterBackground, onMaster: true);
            if (masterBackground == null)
                foreach (var sp in Outside(m)) Add(sp, null, scheme, [], [], ref masterBackground, onMaster: true);
            if (masterBackground != null && (background == null || followBackground)) background = masterBackground;
            if (followObjects) slide.Shapes.AddRange(decorations);
        }
        slide.Background = background ?? SlideFill.Solid(scheme[0]);
        slide.Shapes.AddRange(shapes);
        return slide;
    }

    /// <summary>A shape or group on its side is stored with the box it takes up that way; this is its box before it was turned.</summary>
    static Rect Upright(Rect r, double rotation) => Math.Abs(rotation) % 180 is >= 45 and < 135
        ? new Rect(r.X + r.Width / 2 - r.Height / 2, r.Y + r.Height / 2 - r.Width / 2, r.Height, r.Width)
        : r;

    /// <summary>Shapes of a drawing that aren't in its shape tree: the background.</summary>
    IEnumerable<Rec> Outside(Rec slide) => Descendants(slide, 0xF002).Take(1).SelectMany(Children).Where(r => r.Type == 0xF004);

    SlideShape? ReadShape(Rec sp, GroupMap? map, Color[] scheme, List<List<Rec>> listTexts, out bool isBackground, bool onMaster = false)
    {
        isBackground = false;
        var fsp = Child(sp, 0xF00A);
        if (fsp is not { Length: >= 8 } f) return null;
        int flags = I32(f.Start + 4);
        if ((flags & (0x1 | 0x4 | 0x8)) != 0) return null; // group headers, the patriarch and deleted shapes draw nothing
        isBackground = (flags & 0x400) != 0;
        int type = f.Instance;

        var props = Properties(sp);
        var shape = new SlideShape { FlipH = (flags & 0x40) != 0, FlipV = (flags & 0x80) != 0 };
        if (Anchor(sp, map) is { } r)
            shape.Bounds = new Rect(r.X * MasterUnit, r.Y * MasterUnit, r.Width * MasterUnit, r.Height * MasterUnit);
        else if (!isBackground) return null;

        if (props.TryGetValue(0x0004, out var rotation)) shape.Rotation = (int)rotation / 65536.0 % 360;
        shape.Bounds = Upright(shape.Bounds, shape.Rotation);
        shape.Geometry = type switch
        {
            2 => ShapeGeometry.RoundRect, 3 => ShapeGeometry.Ellipse, 4 => ShapeGeometry.Diamond, 5 => ShapeGeometry.Triangle,
            6 => ShapeGeometry.RightTriangle, 7 => ShapeGeometry.Parallelogram, 8 => ShapeGeometry.Trapezoid, 9 => ShapeGeometry.Hexagon,
            10 => ShapeGeometry.Octagon, 11 => ShapeGeometry.Plus, 12 => ShapeGeometry.Star5, 13 => ShapeGeometry.RightArrow,
            66 => ShapeGeometry.LeftArrow, 67 => ShapeGeometry.DownArrow, 68 => ShapeGeometry.UpArrow, 15 => ShapeGeometry.HomePlate,
            55 => ShapeGeometry.Chevron, 56 => ShapeGeometry.Pentagon, 20 or 32 => ShapeGeometry.Line, 33 or 34 => ShapeGeometry.Elbow,
            37 or 38 => ShapeGeometry.Curve,
            _ => ShapeGeometry.Rect,
        };

        // Fill and outline: on by default for drawn shapes, off for text boxes, pictures and placeholders.
        bool placeholder = Child(sp, 0xF011) is { } data && Descendants(data, 0x0BC3).Any();
        bool plain = type is 202 or 75 or 0 || placeholder;
        bool filled = Flag(props, 0x01BF, 4, defaultValue: !plain && !isBackground) || isBackground;
        if (props.TryGetValue(0x0186, out var pib) && pib > 0 && Blip((int)pib) is { } fillImage && props.GetValueOrDefault(0x0180) is 2 or 3)
            shape.Fill = new SlideFill { Image = fillImage, Tile = props.GetValueOrDefault(0x0180) == 2 ? 1 : null };
        else if (filled && props.GetValueOrDefault(0x0180) is >= 4 and <= 8)
        {
            // Shaded fills run from the fill colour to the back colour.
            var from = ColorOf(props.TryGetValue(0x0181, out var c1) ? c1 : 0x08000004, scheme);
            var to = ColorOf(props.TryGetValue(0x0183, out var c2) ? c2 : 0x08000000, scheme);
            double angle = props.TryGetValue(0x018B, out var a) ? (int)a / 65536.0 : 0;
            shape.Fill = new SlideFill
            {
                Gradient = [(0, from), (1, to)],
                GradientAngle = 90 - angle,
                RadialGradient = props.GetValueOrDefault(0x0180) is 5 or 6,
            };
        }
        else if (filled)
            shape.Fill = SlideFill.Solid(ColorOf(props.TryGetValue(0x0181, out var fill) ? fill : 0x08000004, scheme));
        if (props.TryGetValue(0x0182, out var opacity) && shape.Fill?.Color is { } fc)
            shape.Fill = SlideFill.Solid(Color.FromArgb((byte)Math.Clamp(opacity / 65536.0 * 255, 0, 255), fc.R, fc.G, fc.B));

        bool lined = Flag(props, 0x01FF, 3, defaultValue: !plain);
        if (lined && !isBackground)
        {
            shape.Line = ColorOf(props.TryGetValue(0x01C0, out var line) ? line : 0x08000001, scheme);
            shape.LineWidth = props.TryGetValue(0x01CB, out var width) ? width / 12700.0 : 0.75;
            shape.Dashed = props.GetValueOrDefault(0x01CE) > 0;
            shape.HeadEnd = OfficeArt.LineEnd(props.GetValueOrDefault(0x01D0));
            shape.TailEnd = OfficeArt.LineEnd(props.GetValueOrDefault(0x01D1));
        }

        if (props.TryGetValue(0x0104, out var picture) && picture > 0 && Blip((int)picture) is { } image)
        {
            shape.Picture = image;
            shape.Fill = null;
            // Cropping: 16.16 fixed fractions of the picture, from the top, bottom, left and right.
            double Crop(int id) => props.TryGetValue(id, out var v) ? (int)v / 65536.0 : 0;
            shape.Crop = new Thickness(Crop(0x0102), Crop(0x0100), Crop(0x0103), Crop(0x0101));
        }

        // Text: in the shape itself, or (for placeholders) in the slide's text list.
        if (Child(sp, 0xF00D) is { } textbox)
        {
            var records = Children(textbox).ToList();
            if (records.FirstOrDefault(r => r.Type == 0x0F9E) is { Length: >= 4 } outlineRef)
            {
                int index = I32(outlineRef.Start);
                if (index >= 0 && index < listTexts.Count) records = listTexts[index];
            }
            ReadText(Grouped(records).FirstOrDefault() ?? [], shape, scheme);
        }
        if (onMaster && placeholder)
            return null; // the master's placeholders are prompts, not content
        if (props.TryGetValue(0x0081, out var li)) shape.Insets = new Thickness(li / 12700.0, shape.Insets.Top, shape.Insets.Right, shape.Insets.Bottom);
        if (props.TryGetValue(0x0087, out var anchorText)) shape.Anchor = anchorText switch { 1 or 4 or 6 or 8 => TextAnchor.Middle, 2 or 5 or 7 or 9 => TextAnchor.Bottom, _ => TextAnchor.Top };
        return shape;
    }

    /// <summary>An OfficeArt colour: an RGB value, or an index into the slide's colour scheme.</summary>
    Color ColorOf(long value, Color[] scheme)
    {
        int flags = (int)(value >> 24) & 0xFF;
        if ((flags & 0x08) != 0) return scheme[Math.Clamp((int)(value & 0xFF), 0, 7)];
        return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    static bool Flag(Dictionary<int, long> props, int id, int bit, bool defaultValue)
    {
        if (!props.TryGetValue(id, out var v)) return defaultValue;
        bool use = (v & (1L << (bit + 16))) != 0;
        return use ? (v & (1L << bit)) != 0 : defaultValue;
    }

    Dictionary<int, long> Properties(Rec sp)
    {
        var props = new Dictionary<int, long>();
        foreach (var opt in Children(sp).Where(r => r.Type is 0xF00B or 0xF121 or 0xF122))
        {
            for (int k = 0; k < opt.Instance && opt.Start + k * 6 + 6 <= opt.End; k++)
            {
                int id = U16(opt.Start + k * 6) & 0x3FFF;
                props.TryAdd(id, BinaryPrimitives.ReadUInt32LittleEndian(_doc.AsSpan(opt.Start + k * 6 + 2)));
            }
        }
        return props;
    }

    DImage? Blip(int index)
    {
        if (!_images.TryGetValue(index, out var image)) _images[index] = image = Read();
        return image;

        DImage? Read()
        {
            if (_pictures == null || index < 1 || index > _blips.Count) return null;
            var (offset, size) = _blips[index - 1];
            if (offset < 0 || size <= 0 || offset >= _pictures.Length) return null;
            var (data, ext) = WordBinaryReader.Blip(_pictures.AsSpan(offset, (int)Math.Min(size + 8L, _pictures.Length - offset)));
            if (data == null) return null;
            var (w, h) = DocxReader.ImageSize(data);
            return new DImage(data, ext!, Math.Max(1, w), Math.Max(1, h));
        }
    }

    // ------------------------------------------------------------------ text

    void ReadText(List<Rec> records, SlideShape shape, Color[] scheme)
    {
        if (records.Count == 0 || records[0].Length < 4) return;
        int textType = I32(records[0].Start);
        string text = "";
        foreach (var r in records)
        {
            if (r.Type == 0x0FA0) text = Encoding.Unicode.GetString(_doc, r.Start, r.Length & ~1);
            else if (r.Type == 0x0FA8) text = Encoding.Latin1.GetString(_doc, r.Start, r.Length);
        }
        if (text.Length == 0) return;
        if (textType is 0 or 6) shape.Role = "title";
        if (textType is 0 or 6 or 5) shape.Anchor = TextAnchor.Middle;

        // Paragraph and character runs: lengths in characters, each with its own exceptions to the master style.
        var paraRuns = new List<(int length, int level, TextDefaults props)>();
        var charRuns = new List<(int length, int offset)>();
        var style = records.FirstOrDefault(r => r.Type == 0x0FA1);
        if (style.Length > 0)
        {
            int at = style.Start, total = text.Length + 1;
            try
            {
                for (int covered = 0; covered < total && at + 6 <= style.End;)
                {
                    int count = I32(at), level = Math.Clamp(U16(at + 4), 0, 4);
                    var p = Defaults(textType, level);
                    at = ParagraphProps(at + 6, p, scheme);
                    paraRuns.Add((count, level, p));
                    covered += count;
                }
                for (int covered = 0; covered < total && at + 4 <= style.End;)
                {
                    int count = I32(at);
                    charRuns.Add((count, at + 4));
                    at = CharProps(at + 4, new TextDefaults(), scheme);
                    covered += count;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { } // cut short: keep the formatting read so far
        }

        int paraIndex = 0, paraLeft = paraRuns.Count > 0 ? paraRuns[0].length : int.MaxValue;
        int charIndex = 0, charLeft = charRuns.Count > 0 ? charRuns[0].length : int.MaxValue;
        var lines = text.Split('\r');
        foreach (var line in lines)
        {
            var (level, pp) = paraIndex < paraRuns.Count ? (paraRuns[paraIndex].level, paraRuns[paraIndex].props) : (0, Defaults(textType, 0));
            var paragraph = new DParagraph
            {
                Align = pp.Align,
                IndentLeft = pp.TextStart,
                IndentFirst = pp.BulletStart - pp.TextStart,
                ListLevel = level,
                MarkSize = pp.Size,
                LineSpacing = pp.Line > 0 ? pp.Line / 100.0 : pp.Line < 0 ? -pp.Line * MasterUnit / (pp.Size * 1.2) : 1,
                SpaceBefore = Spacing(pp.Before, pp.Size),
                SpaceAfter = Spacing(pp.After, pp.Size),
            };
            var sb = new StringBuilder();
            TextDefaults? runStyle = null;
            void Flush()
            {
                if (sb.Length == 0 || runStyle == null) return;
                paragraph.Inlines.Add(new DText(sb.ToString(), new TextStyle
                {
                    Bold = runStyle.Bold, Italic = runStyle.Italic, Underline = runStyle.Underline,
                    Size = runStyle.Size, Color = runStyle.Color, Font = runStyle.Font,
                }));
                sb.Clear();
            }
            foreach (var ch in line + "\r")
            {
                // The character style is the paragraph's master defaults with the run's exceptions on top.
                var cs = pp.Clone();
                if (charIndex < charRuns.Count) CharProps(charRuns[charIndex].offset, cs, scheme);
                if (runStyle == null || !Same(runStyle, cs)) { Flush(); runStyle = cs; }
                if (ch == '\v') { Flush(); paragraph.Inlines.Add(new DLineBreak()); }
                else if (ch != '\r') sb.Append(ch);
                if (--charLeft <= 0 && ++charIndex < charRuns.Count) charLeft = charRuns[charIndex].length;
                if (--paraLeft <= 0 && ++paraIndex < paraRuns.Count) paraLeft = paraRuns[paraIndex].length;
            }
            Flush();
            if (pp.Bullet && line.Trim().Length > 0)
            {
                paragraph.List = ListKind.Bullet;
                paragraph.ListLabel = pp.BulletChar switch
                {
                    '•' or '' or '·' => "•",
                    '–' => "–",
                    var c when c >= '' => "•",
                    var c => c.ToString(),
                };
                if (paragraph.IndentFirst >= 0) paragraph.IndentFirst = -Math.Max(12, pp.Size * 0.9);
            }
            shape.Text.Add(paragraph);
        }
    }

    static double Spacing(int value, double size) => value >= 0 ? value / 100.0 * size * 1.2 : -value * MasterUnit;

    static bool Same(TextDefaults a, TextDefaults b) =>
        a.Size == b.Size && a.Bold == b.Bold && a.Italic == b.Italic && a.Underline == b.Underline && a.Color == b.Color && a.Font == b.Font;
}

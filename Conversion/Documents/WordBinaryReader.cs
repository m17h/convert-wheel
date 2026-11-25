using System.Buffers.Binary;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Reads Word 97–2003 documents (.doc) without Word: text from the piece table, character and paragraph
/// formatting, styles (headings), lists, tables, hyperlinks, inline pictures, footnotes and page setup.
/// </summary>
internal sealed partial class WordBinaryReader
{
    readonly byte[] _word, _table, _data;
    readonly List<(int cpStart, int cpEnd, int fc, bool compressed)> _pieces = [];
    readonly List<(int fcStart, int fcEnd, byte[] grpprl)> _chpx = [];
    readonly List<(int fcStart, int fcEnd, int istd, byte[] grpprl)> _papx = [];
    readonly List<string> _fonts = [];
    readonly Dictionary<int, (int sti, int baseIstd, byte[] papx, byte[] chpx)> _styles = [];
    readonly Dictionary<int, (bool simple, List<Level> levels)> _lists = [];
    readonly List<int> _lfoToLsid = [];
    readonly Dictionary<(int lsid, int level), int> _counters = [];
    int _footnoteNumber, _endnoteNumber;
    string? _defaultFont;

    sealed record Level(int Start, int Format, string Text, double Indent, double Hanging, byte[] Chpx);

    sealed class CharProps
    {
        public bool Bold, Italic, Underline, Strike, Hidden;
        public int Lid;
        public int Position;
        public double? Size;
        public Color? Color;
        public string? Font;
        public CharProps Clone() => (CharProps)MemberwiseClone();
    }

    sealed class ParaProps
    {
        public TextAlignment Align = TextAlignment.Left;
        public double Left, First, Before, After, Line = 1;
        public bool InTable, RowEnd, Contextual;
        public TextAlignment? FrameAlign;
        /// <summary>Word 6 numbering: the paragraph's autonumber settings and level (0 = not numbered).</summary>
        public byte[]? Anld;
        public int NLvlAnm;
        public int ListLevel, ListIndex, Heading;
    }

    readonly CompoundFile? _file;

    WordBinaryReader(byte[] word, byte[] table, byte[] data, CompoundFile? file = null) => (_word, _table, _data, _file) = (word, table, data, file);

    public static DDocument Read(string path) => DocumentFiles.Open(path, DocumentShape.WordOld, () => ReadDoc(path));

    internal static DDocument ReadDoc(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var cfb = new CompoundFile(bytes);
        var word = cfb.Stream("WordDocument") ?? throw new ConversionException("This isn't a Word document.");
        if (word.Length < 0x200 || BinaryPrimitives.ReadUInt16LittleEndian(word) is not (0xA5EC or 0xA5DC)) throw new ConversionException("This isn't a Word document.");
        int nFib = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0) throw new ConversionException("This document is password-protected.");
        if (nFib < 0xC0)
        {
            // Word 6 and 95: one stream holds text, tables and pictures, and formatting uses the older one-byte codes.
            try
            {
                return new WordBinaryReader(word, word, word, cfb) { _word6 = true }.Build(Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or ConversionException)
            {
                Log.Write($"Word 6 reader fell back to plain text: {ex.Message}");
                return ReadWord6(word, Path.GetFileNameWithoutExtension(path));
            }
        }
        var table = cfb.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new ConversionException("This Word document is damaged.");
        var data = cfb.Stream("Data") ?? [];
        try
        {
            return new WordBinaryReader(word, table, data, cfb).Build(Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            Log.Write(ex);
            throw new ConversionException("This Word document is damaged or uses features that can't be read.");
        }
    }

    /// <summary>
    /// Word 6 and 95 files use an older layout; their text (when not fast-saved) is one 8-bit run,
    /// which is enough to bring the words and paragraphs across.
    /// </summary>
    static DDocument ReadWord6(byte[] word, string title)
    {
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        int fcMin = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(0x18));
        int fcMac = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(0x1C));
        if ((flags & 0x0004) != 0 || fcMin <= 0 || fcMac <= fcMin || fcMac > word.Length)
            throw new ConversionException("This is a fast-saved Word 6/95 document, which can't be read. Open it in Word or WordPad and save it again.");
        var doc = new DDocument { Title = title, Font = "Times New Roman", FontSize = 10 };
        var text = new StringBuilder();
        void Paragraph()
        {
            var p = new DParagraph();
            if (text.Length > 0) p.Inlines.Add(new DText(text.ToString(), new TextStyle()));
            doc.Blocks.Add(p);
            text.Clear();
        }
        for (int i = fcMin; i < fcMac; i++)
        {
            byte b = word[i];
            char ch = b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;
            switch (ch)
            {
                case '\r' or '\a': Paragraph(); break;
                case '\v': text.Append('\n'); break;
                case '\t': text.Append('\t'); break;
                default: if (ch >= ' ') text.Append(ch); break;
            }
        }
        if (text.Length > 0) Paragraph();
        return doc;
    }

    // ------------------------------------------------------------------ FIB

    int _fcLcbOffset, _ccpText, _ccpFtn;

    (int fc, int lcb) FcLcb(int index)
    {
        int at = _fcLcbOffset + index * 8;
        if (at + 8 > _word.Length) return (0, 0);
        return (BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(at + 4)));
    }

    /// <summary>A Word 6/95 file: fixed header layout, one-byte formatting codes, 8-bit text.</summary>
    bool _word6;

    int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(at));

    DDocument Build(string title)
    {
        if (_word6) return Build6(title);
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(32));
        int lwAt = 34 + csw * 2;
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(lwAt));
        int rgLw = lwAt + 2;
        _ccpText = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(rgLw + 3 * 4));
        _ccpFtn = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(rgLw + 4 * 4));
        _ccpHdd = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(rgLw + 5 * 4));
        _ccpAtn = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(rgLw + 7 * 4));
        _ccpEdn = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(rgLw + 8 * 4));
        _fcLcbOffset = rgLw + cslw * 4 + 2;

        ReadPieces();
        // Only the text has to be intact; formatting that's damaged is left out.
        Try("character formatting", () => ReadFkps(FcLcb(12), chars: true));
        Try("paragraph formatting", () => ReadFkps(FcLcb(13), chars: false));
        Try("fonts", () => ReadFonts(FcLcb(15)));
        Try("styles", () => ReadStyles(FcLcb(1)));
        Try("lists", () => ReadLists(FcLcb(73), FcLcb(74)));

        var doc = new DDocument { Title = title };
        Try("page setup", () => ReadPageSetup(FcLcb(6), doc));
        if (_styles.Values.FirstOrDefault(s => s.sti == 0) is { chpx: { } normal })
        {
            var baseChars = new CharProps();
            ApplyChar(baseChars, normal);
            doc.Font = baseChars.Font ?? _defaultFont ?? "Times New Roman";
            doc.FontSize = baseChars.Size ?? 10;
        }
        else (doc.Font, doc.FontSize) = (_defaultFont ?? "Times New Roman", 10);

        // Stories follow one another: main text, footnotes, headers, comments, endnotes, text boxes.
        int footnoteStory = _ccpText, headerStory = footnoteStory + _ccpFtn, endnoteStory = headerStory + _ccpHdd + _ccpAtn;
        _textboxStory = endnoteStory + _ccpEdn;
        ReadDrawings();
        var notes = new Dictionary<int, (List<DBlock> blocks, bool endnote)>();
        Try("footnotes", () => ReadNotes(FcLcb(2), FcLcb(3), footnoteStory, _ccpFtn, endnote: false, notes));
        Try("endnotes", () => ReadNotes(FcLcb(46), FcLcb(47), endnoteStory, _ccpEdn, endnote: true, notes));
        Try("headers", () => ReadHeaders(doc, headerStory));
        ReadStory(0, _ccpText, doc.Blocks, notes);
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    static void Try(string what, Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or ConversionException)
        {
            Log.Write($"Skipped the {what} of a Word document: {ex.Message}");
        }
    }

    int _ccpHdd, _ccpAtn, _ccpEdn, _textboxStory;

    // ------------------------------------------------------------------ text and formatting runs

    void ReadPieces()
    {
        var (fc, lcb) = FcLcb(33);
        if (fc < 0 || lcb < 0 || fc > _table.Length - lcb) throw new ConversionException("This Word document has a damaged piece table.");
        int i = fc, end = fc + lcb;
        while (i < end)
        {
            byte type = _table[i];
            if (type == 1)
            {
                if (end - i < 3) throw new ConversionException("This Word document has a damaged piece table.");
                int length = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i + 1));
                if (length > end - i - 3) throw new ConversionException("This Word document has a damaged piece table.");
                i += 3 + length;
                continue;
            }
            if (type != 2) break;
            if (end - i < 5) throw new ConversionException("This Word document has a damaged piece table.");
            int size = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(i + 1));
            if (size < 4 || size > end - i - 5 || (size - 4) % 12 != 0) throw new ConversionException("This Word document has a damaged piece table.");
            int plc = i + 5, n = (size - 4) / 12;
            for (int k = 0; k < n; k++)
            {
                int cpStart = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plc + k * 4));
                int cpEnd = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plc + (k + 1) * 4));
                uint fcValue = BinaryPrimitives.ReadUInt32LittleEndian(_table.AsSpan(plc + (n + 1) * 4 + k * 8 + 2));
                if (cpStart < 0 || cpEnd < cpStart || k == 0 && cpStart != 0)
                    throw new ConversionException("This Word document has a damaged piece table.");
                if (_word6)
                {
                    // Word 6 text is always 8-bit, and its file offsets carry no flags.
                    if ((long)fcValue + cpEnd - cpStart > _word.Length) throw new ConversionException("This Word document has a damaged piece table.");
                    _pieces.Add((cpStart, cpEnd, (int)fcValue, true));
                    continue;
                }
                bool compressed = (fcValue & 0x40000000) != 0;
                int pieceFc = (int)(fcValue & 0x3FFFFFFF);
                if ((compressed ? pieceFc / 2L : pieceFc) + (long)(cpEnd - cpStart) * (compressed ? 1 : 2) > _word.Length)
                    throw new ConversionException("This Word document has a damaged piece table.");
                _pieces.Add((cpStart, cpEnd, compressed ? pieceFc / 2 : pieceFc, compressed));
            }
            break;
        }
        if (_pieces.Count == 0) throw new ConversionException("This Word document has no readable text.");
    }

    // Windows-1252's 0x80–0x9F block, used by 8-bit text pieces.
    static readonly string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>The character at a position, and the file offset its formatting is keyed by.</summary>
    int _lastPiece;

    (char ch, int fc) CharAt(int cp)
    {
        for (int k = 0; k < _pieces.Count; k++)
        {
            // Text is read in order, so the piece used last is almost always the right one.
            var p = _pieces[(_lastPiece + k) % _pieces.Count];
            if (cp < p.cpStart || cp >= p.cpEnd) continue;
            _lastPiece = (_lastPiece + k) % _pieces.Count;
            if (p.compressed)
            {
                int at = p.fc + (cp - p.cpStart);
                byte b = _word[at];
                if (_byteChars != null) return (_byteChars[b], at);
                return (b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b, at);
            }
            int offset = p.fc + (cp - p.cpStart) * 2;
            return ((char)BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(offset)), offset);
        }
        return ('\0', -1);
    }

    /// <param name="pageCount">Word 6: how many formatting pages there are (more may follow the listed ones).</param>
    void ReadFkps((int fc, int lcb) plc, bool chars, int pageCount = 0)
    {
        if (plc.lcb < 6) return;
        // Page numbers are 4 bytes each (Word 6: 2 bytes).
        int pnSize = _word6 ? 2 : 4;
        int n = (plc.lcb - 4) / (4 + pnSize);
        var pages = new List<int>();
        for (int k = 0; k < n; k++)
            pages.Add(_word6 ? BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(plc.fc + (n + 1) * 4 + k * 2))
                             : (int)(BinaryPrimitives.ReadUInt32LittleEndian(_table.AsSpan(plc.fc + (n + 1) * 4 + k * 4)) & 0x3FFFFF));
        while (pages.Count > 0 && pages.Count < pageCount) pages.Add(pages[^1] + 1);
        foreach (var pn in pages)
        {
            int page = pn * 512;
            if (page + 512 > _word.Length) continue;
            int crun = _word[page + 511];
            for (int r = 0; r < crun; r++)
            {
                int fcStart = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(page + r * 4));
                int fcEnd = BinaryPrimitives.ReadInt32LittleEndian(_word.AsSpan(page + (r + 1) * 4));
                if (chars)
                {
                    int b = _word[page + (crun + 1) * 4 + r] * 2;
                    if (b == 0) { _chpx.Add((fcStart, fcEnd, [])); continue; }
                    int cb = _word[page + b];
                    var grpprl = _word.AsSpan(page + b + 1, cb).ToArray();
                    _chpx.Add((fcStart, fcEnd, _word6 ? Translate6(grpprl) : grpprl));
                }
                else
                {
                    // Each run's entry: a word offset and a layout hint (13 bytes; Word 6: 7).
                    int b = _word[page + (crun + 1) * 4 + r * (_word6 ? 7 : 13)] * 2;
                    if (b == 0) { _papx.Add((fcStart, fcEnd, 0, [])); continue; }
                    int cb = _word[page + b], at = page + b + 1;
                    int length = _word6 ? cb * 2 : cb == 0 ? _word[at++] * 2 : cb * 2 - 1;
                    if (length < 2) continue;
                    int istd = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(at));
                    var grpprl = _word.AsSpan(at + 2, Math.Min(length - 2, _word.Length - at - 2)).ToArray();
                    _papx.Add((fcStart, fcEnd, istd, _word6 ? Translate6(grpprl) : grpprl));
                }
            }
        }
        if (chars) _chpx.Sort((a, b) => a.fcStart.CompareTo(b.fcStart));
        else _papx.Sort((a, b) => a.fcStart.CompareTo(b.fcStart));
    }

    static T? Find<T>(List<T> runs, Func<T, int> start, Func<T, int> end, int fc) where T : struct
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (fc < start(runs[mid])) hi = mid - 1;
            else if (fc >= end(runs[mid])) lo = mid + 1;
            else return runs[mid];
        }
        return null;
    }

    // ------------------------------------------------------------------ sprms

    /// <summary>Walks a list of property modifiers (sprm + operand).</summary>
    static IEnumerable<(ushort sprm, int at, int size)> Sprms(byte[] grpprl)
    {
        int i = 0;
        while (i + 2 <= grpprl.Length)
        {
            ushort sprm = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(i));
            int at = i + 2;
            int size = (sprm >> 13) switch
            {
                0 or 1 => 1,
                2 or 4 or 5 => 2,
                3 => 4,
                7 => 3,
                _ => at < grpprl.Length ? (sprm == 0xD608 ? BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)) + 1 : grpprl[at] + 1) : 0,
            };
            if (at + size > grpprl.Length) yield break;
            yield return (sprm, at, size);
            i = at + size;
        }
    }

    int _styleDepth;

    void ApplyChar(CharProps c, byte[] grpprl)
    {
        foreach (var (sprm, at, size) in Sprms(grpprl))
        {
            if (size == 0) continue;
            byte b = grpprl[at];
            bool Toggle(bool current) => b switch { 0 => false, 1 => true, 0x81 => !current, _ => current };
            switch (sprm)
            {
                case 0x4A41: c.Lid = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)); break;
                case 0x4A30: // a character style (which, in a damaged file, could name itself)
                    if (_styleDepth >= 4) break;
                    _styleDepth++;
                    StyleChars(BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)), c);
                    _styleDepth--;
                    break;
                case 0x0835: c.Bold = Toggle(c.Bold); break;
                case 0x0836: c.Italic = Toggle(c.Italic); break;
                case 0x0837 or 0x2A53: c.Strike = Toggle(c.Strike); break;
                case 0x083C: c.Hidden = Toggle(c.Hidden); break;
                case 0x2A3E: c.Underline = b != 0; break;
                case 0x2A48: c.Position = b == 1 ? 1 : b == 2 ? -1 : 0; break;
                case 0x4A43: c.Size = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)) / 2.0; break;
                case 0x2A42: c.Color = IcoColor(b); break;
                case 0x6870 when size >= 4:
                    c.Color = grpprl[at + 3] != 0 ? null : Color.FromRgb(grpprl[at], grpprl[at + 1], grpprl[at + 2]);
                    break;
                case 0x4A4F or 0x4A51:
                    int ftc = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at));
                    if (sprm == 0x4A4F || c.Font == null) c.Font = ftc < _fonts.Count ? _fonts[ftc] : c.Font;
                    break;
            }
        }
    }

    static void ApplyPara(ParaProps p, byte[] grpprl)
    {
        foreach (var (sprm, at, size) in Sprms(grpprl))
        {
            if (size == 0) continue;
            short S16() => BinaryPrimitives.ReadInt16LittleEndian(grpprl.AsSpan(at));
            switch (sprm)
            {
                case 0x2403 or 0x2461:
                    p.Align = grpprl[at] switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, 3 or 4 or 5 => TextAlignment.Justify, _ => TextAlignment.Left };
                    break;
                case 0x2416: p.InTable = grpprl[at] != 0; break;
                case 0x2417: p.RowEnd = grpprl[at] != 0; break;
                case 0x6649 when size >= 4: p.InTable |= BinaryPrimitives.ReadInt32LittleEndian(grpprl.AsSpan(at)) > 0; break;
                case 0x840F or 0x845E: p.Left = S16() / 20.0; break;
                case 0x8411 or 0x8460: p.First = S16() / 20.0; break;
                case 0xA413: p.Before = (ushort)S16() / 20.0; break;
                case 0xA414: p.After = (ushort)S16() / 20.0; break;
                case 0x6412 when size >= 4:
                    short line = S16(), mult = BinaryPrimitives.ReadInt16LittleEndian(grpprl.AsSpan(at + 2));
                    if (mult != 0 && line > 0) p.Line = line / 240.0;
                    break;
                case 0x260A: p.ListLevel = grpprl[at]; break;
                case 0x460B: p.ListIndex = S16(); break;
                case 0x246D: p.Contextual = grpprl[at] != 0; break;
                // A framed paragraph (e.g. a page number) positioned centre/right of its column.
                case 0xC63E: p.Anld = grpprl.AsSpan(at + 1, Math.Min(grpprl[at], grpprl.Length - at - 1)).ToArray(); break;
                case 0x26FE: p.NLvlAnm = grpprl[at]; break;
                case 0x8418: p.FrameAlign = S16() switch { -4 => TextAlignment.Center, -8 or -16 => TextAlignment.Right, _ => null }; break;
            }
        }
    }

    static Color? IcoColor(byte ico) => ico switch
    {
        1 => Colors.Black, 2 => Color.FromRgb(0, 0, 255), 3 => Color.FromRgb(0, 255, 255), 4 => Color.FromRgb(0, 255, 0),
        5 => Color.FromRgb(255, 0, 255), 6 => Color.FromRgb(255, 0, 0), 7 => Color.FromRgb(255, 255, 0), 8 => Colors.White,
        9 => Color.FromRgb(0, 0, 128), 10 => Color.FromRgb(0, 128, 128), 11 => Color.FromRgb(0, 128, 0), 12 => Color.FromRgb(128, 0, 128),
        13 => Color.FromRgb(128, 0, 0), 14 => Color.FromRgb(128, 128, 0), 15 => Color.FromRgb(128, 128, 128), 16 => Color.FromRgb(192, 192, 192),
        _ => null,
    };

    // ------------------------------------------------------------------ fonts, styles and lists

    void ReadFonts((int fc, int lcb) sttb)
    {
        if (sttb.lcb < 4) return;
        if (_word6)
        {
            // Word 6: a byte count, then records with an 8-bit name at offset 6.
            int stop = sttb.fc + Math.Min(sttb.lcb, BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(sttb.fc)));
            for (int p = sttb.fc + 2; p < stop;)
            {
                int size = _table[p] + 1;
                var name = new StringBuilder();
                for (int j = p + 6; j < p + size && _table[j] != 0; j++) name.Append((char)_table[j]);
                _fonts.Add(name.ToString());
                p += size;
            }
            return;
        }
        int i = sttb.fc;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i));
        i += 4; // count, extra-data size
        for (int k = 0; k < count && i < sttb.fc + sttb.lcb; k++)
        {
            int len = _table[i] + 1;
            int nameAt = i + 1 + 39, end = i + len;
            var sb = new StringBuilder();
            for (int j = nameAt; j + 1 < end; j += 2)
            {
                char ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(j));
                if (ch == 0) break;
                sb.Append(ch);
            }
            _fonts.Add(sb.ToString());
            i = end;
        }
    }

    void ReadStyles((int fc, int lcb) stsh)
    {
        if (stsh.lcb < 4) return;
        int i = stsh.fc;
        int cbStshi = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i));
        int stdCount = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i + 2));
        int baseSize = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i + 4));
        // The stylesheet header names the default font for runs that don't set one.
        int ftcAscii = cbStshi >= 14 ? BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i + 2 + 12)) : -1;
        if (ftcAscii >= 0 && ftcAscii < _fonts.Count) _defaultFont = _fonts[ftcAscii];
        i += 2 + cbStshi;
        for (int istd = 0; istd < stdCount && i + 2 <= stsh.fc + stsh.lcb; istd++)
        {
            int cbStd = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i));
            int std = i + 2;
            i = std + cbStd;
            if (cbStd == 0) continue;
            int sti = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(std)) & 0x0FFF;
            int w2 = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(std + 2));
            int stk = w2 & 0xF, istdBase = w2 >> 4;
            int cupx = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(std + 4)) & 0xF;

            // Name (length-prefixed UTF-16 with a terminator), then the formatting blocks on even offsets.
            int at = std + baseSize;
            // (Word 6: a one-byte length and 8-bit characters.)
            if (_word6) at += 1 + _table[at] + 1;
            else at += 2 + (BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(at)) + 1) * 2;
            byte[] papx = [], chpx = [];
            for (int u = 0; u < cupx && at + 2 <= i; u++)
            {
                if ((at - std) % 2 != 0) at++;
                int cb = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(at));
                var block = _table.AsSpan(at + 2, Math.Min(cb, Math.Max(0, i - at - 2))).ToArray();
                if (_word6) block = u == 0 && stk == 1 ? [.. block.Take(2), .. Translate6(block.Length >= 2 ? block[2..] : [])] : Translate6(block);
                if (stk == 1 && u == 0) papx = block.Length >= 2 ? block[2..] : [];
                else if (stk == 1 && u == 1 || stk == 2 && u == 0) chpx = block;
                at += 2 + cb;
            }
            _styles[istd] = (sti, istdBase == 0x0FFF ? -1 : istdBase, papx, chpx);
        }
    }

    /// <summary>A style's formatting with everything it is based on applied first.</summary>
    void StyleChars(int istd, CharProps c, int depth = 0)
    {
        if (depth > 10 || !_styles.TryGetValue(istd, out var s)) return;
        if (s.baseIstd >= 0) StyleChars(s.baseIstd, c, depth + 1);
        ApplyChar(c, s.chpx);
    }

    void StylePara(int istd, ParaProps p, int depth = 0)
    {
        if (depth > 10 || !_styles.TryGetValue(istd, out var s)) return;
        if (s.baseIstd >= 0) StylePara(s.baseIstd, p, depth + 1);
        ApplyPara(p, s.papx);
    }

    void ReadLists((int fc, int lcb) plfLst, (int fc, int lcb) plfLfo)
    {
        if (plfLst.lcb < 2) return;
        int i = plfLst.fc;
        int count = BinaryPrimitives.ReadInt16LittleEndian(_table.AsSpan(i));
        i += 2;
        var defs = new List<(int lsid, bool simple)>();
        for (int k = 0; k < count; k++)
        {
            int lsid = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(i));
            bool simple = (_table[i + 26] & 1) != 0;
            defs.Add((lsid, simple));
            i += 28;
        }
        // The levels of every list follow the list headers, in order.
        foreach (var (lsid, simple) in defs)
        {
            var levels = new List<Level>();
            for (int l = 0; l < (simple ? 1 : 9); l++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(i));
                int nfc = _table[i + 4];
                int cbChpx = _table[i + 24], cbPapx = _table[i + 25];
                var papx = _table.AsSpan(i + 28, cbPapx).ToArray();
                var chpx = _table.AsSpan(i + 28 + cbPapx, cbChpx).ToArray();
                i += 28 + cbPapx + cbChpx;
                int cch = BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i));
                var text = new StringBuilder();
                for (int c = 0; c < cch; c++) text.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(_table.AsSpan(i + 2 + c * 2)));
                i += 2 + cch * 2;
                var indents = new ParaProps();
                ApplyPara(indents, papx);
                levels.Add(new Level(start, nfc, text.ToString(), indents.Left, indents.First, chpx));
            }
            _lists[lsid] = (simple, levels);
        }

        if (plfLfo.lcb < 4) return;
        int lfoCount = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plfLfo.fc));
        for (int k = 0; k < lfoCount; k++) _lfoToLsid.Add(BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plfLfo.fc + 4 + k * 16)));
    }

    Level? ListLevelOf(ParaProps props)
    {
        if (props.ListIndex <= 0 || props.ListIndex > _lfoToLsid.Count || !_lists.TryGetValue(_lfoToLsid[props.ListIndex - 1], out var list)) return null;
        return list.levels[Math.Clamp(props.ListLevel, 0, list.levels.Count - 1)];
    }

    /// <summary>Bullets drawn with Symbol or Wingdings (stored as U+F0xx) as the Unicode characters they look like.</summary>
    static string BulletGlyph(char ch)
    {
        if (ch < 0xF000 || ch > 0xF0FF) return ch == 'o' ? "◦" : ch.ToString();
        return (ch - 0xF000) switch
        {
            0xB7 => "•", 0xA7 => "▪", 0xA8 => "◆", 0x76 => "❖", 0xD8 => "➢", 0xFC => "✓", 0x6C => "●", 0x6E => "■",
            0x75 => "◆", 0x71 => "❑", 0x6F => "□", 0xE0 => "→", 0xF0 => "⇨", 0x9F => "•", 0x77 => "⬥",
            _ => "•",
        };
    }

    void ApplyList(DParagraph p, ParaProps props, CharProps chars)
    {
        if (_word6) { ApplyList6(p, props, chars); return; }
        if (props.ListIndex <= 0 || props.ListIndex > _lfoToLsid.Count || !_lists.TryGetValue(_lfoToLsid[props.ListIndex - 1], out var list)) return;
        int lsid = _lfoToLsid[props.ListIndex - 1];
        int level = Math.Clamp(props.ListLevel, 0, list.levels.Count - 1);
        var lvl = list.levels[level];
        if (lvl.Format == 255) return; // "no number"

        int n = _counters.TryGetValue((lsid, level), out var c) ? c + 1 : lvl.Start;
        _counters[(lsid, level)] = n;
        foreach (var deeper in _counters.Keys.Where(k => k.lsid == lsid && k.level > level).ToList()) _counters.Remove(deeper);

        string label;
        if (lvl.Format == 23)
        {
            p.List = ListKind.Bullet;
            label = lvl.Text.Length > 0 ? BulletGlyph(lvl.Text[0]) : "•";
        }
        else
        {
            p.List = ListKind.Number;
            var sb = new StringBuilder();
            foreach (var ch in lvl.Text)
            {
                if (ch < 9)
                {
                    int value = ch == level ? n : _counters.TryGetValue((lsid, ch), out var outer) ? outer : list.levels[Math.Min(ch, list.levels.Count - 1)].Start;
                    sb.Append(ListLabels.Format(value, list.levels[Math.Min(ch, list.levels.Count - 1)].Format switch
                    {
                        1 => "upperRoman", 2 => "lowerRoman", 3 => "upperLetter", 4 => "lowerLetter", _ => "decimal",
                    }));
                }
                else sb.Append(ch);
            }
            label = sb.ToString();
        }
        p.ListLabel = label;
        p.ListLevel = level;
        var labelChars = chars.Clone();
        ApplyChar(labelChars, lvl.Chpx);
        if (labelChars.Color != chars.Color) p.ListColor = labelChars.Color;
    }

    // ------------------------------------------------------------------ section, footnotes

    void ReadPageSetup((int fc, int lcb) plcfSed, DDocument doc)
    {
        var dop = FcLcb(31);
        _facingPages = dop.lcb > 0 && dop.fc < _table.Length && (_table[dop.fc] & 1) != 0;
        if (plcfSed.lcb < 16) return;
        // One entry per section: where it starts, and where its settings are stored.
        int n = (plcfSed.lcb - 4) / 16;
        for (int k = 0; k < n; k++)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plcfSed.fc + k * 4));
            int fcSepx = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(plcfSed.fc + (n + 1) * 4 + k * 12 + 2));
            var (setup, newPage) = Section(fcSepx, k == 0 ? doc : null);
            if (k == 0)
            {
                (doc.PageWidth, doc.PageHeight, doc.Margins, doc.Columns, doc.ColumnGap) = (setup.Width, setup.Height, setup.Margins, setup.Columns, setup.ColumnGap);
                continue;
            }
            // A continuous section break keeps to the same page (its settings can't change mid-page here).
            _sectionStarts[start] = newPage ? setup : null;
        }
    }

    /// <summary>Section starts in the main text: the settings of a section that begins on a new page, or null if it continues.</summary>
    readonly Dictionary<int, PageSetup?> _sectionStarts = [];

    (PageSetup setup, bool newPage) Section(int fcSepx, DDocument? first)
    {
        double width = 612, height = 792, left = 90, right = 90, top = 72, bottom = 72, gap = 36;
        int columns = 1, breakKind = 2;
        if (fcSepx > 0 && fcSepx + 2 <= _word.Length)
        {
            int cb = BinaryPrimitives.ReadInt16LittleEndian(_word.AsSpan(fcSepx));
            var grpprl = _word.AsSpan(fcSepx + 2, Math.Clamp(cb, 0, _word.Length - fcSepx - 2)).ToArray();
            if (_word6) grpprl = Translate6(grpprl);
            foreach (var (sprm, at, _) in Sprms(grpprl))
            {
                double Twips() => BinaryPrimitives.ReadInt16LittleEndian(grpprl.AsSpan(at)) / 20.0;
                double UTwips() => BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)) / 20.0;
                switch (sprm)
                {
                    case 0xB01F: width = UTwips(); break;
                    case 0xB020: height = UTwips(); break;
                    case 0xB021: left = UTwips(); break;
                    case 0xB022: right = UTwips(); break;
                    case 0x9023: top = Math.Abs(Twips()); break;
                    case 0x9024: bottom = Math.Abs(Twips()); break;
                    case 0x500B: columns = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(at)) + 1; break;
                    case 0x900C: gap = UTwips(); break;
                    case 0x3009: breakKind = grpprl[at]; break;
                    case 0xB017 when first != null: first.HeaderDistance = UTwips(); break;
                    case 0xB018 when first != null: first.FooterDistance = UTwips(); break;
                    case 0x300A when first != null: _titlePage = grpprl[at] != 0; break;
                    case 0x3014 when first != null: _sectionHeaders = grpprl[at]; break;
                }
            }
        }
        if (width <= 72 || height <= 72) (width, height) = (612, 792);
        return (new PageSetup(width, height, new Thickness(left, top, right, bottom), Math.Clamp(columns, 1, 12), gap), breakKind != 0);
    }

    /// <summary>Footnote or endnote texts keyed by the position of their reference mark in the body.</summary>
    void ReadNotes((int fc, int lcb) refs, (int fc, int lcb) texts, int story, int storyLength, bool endnote, Dictionary<int, (List<DBlock> blocks, bool endnote)> notes)
    {
        if (refs.lcb < 4 || texts.lcb < 8 || storyLength <= 0) return;
        int n = (refs.lcb - 4) / 6;
        for (int k = 0; k < n; k++)
        {
            int refCp = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(refs.fc + k * 4));
            int start = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(texts.fc + k * 4));
            int end = BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(texts.fc + (k + 1) * 4));
            var blocks = new List<DBlock>();
            // The note's text opens with its own reference character: print the number there, in its own style.
            _noteMark = endnote ? ListLabels.Format(k + 1, "lowerRoman") : (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            try { ReadStory(story + start, story + Math.Min(end, storyLength), blocks, []); }
            finally { _noteMark = null; }
            // The note's own copy of its number is dropped; the reference mark carries it.
            if (blocks.FirstOrDefault() is DParagraph first && first.Inlines.FirstOrDefault() is DText { Text: var t } mark && t.Trim().Length == 0)
                first.Inlines.Remove(mark);
            notes[refCp] = (blocks, endnote);
        }
    }

    /// <summary>
    /// Headers and footers of the first section. The header story starts with six separator stories, then
    /// six per section: even header, odd header, even footer, odd footer, first-page header and footer.
    /// </summary>
    void ReadHeaders(DDocument doc, int story)
    {
        var (fc, lcb) = FcLcb(11);
        if (_ccpHdd <= 0 || lcb < (_word6 ? 2 : 13) * 4) return;
        var cps = Enumerable.Range(0, lcb / 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(_table.AsSpan(fc + i * 4))).ToList();
        // Word 6 stores only the stories that exist: separators flagged in the document settings, then the
        // section's own, in the same order. Map the fixed story numbers onto that compact list.
        var dopRange = FcLcb(31);
        int separators = _word6 && dopRange.lcb > 1 ? System.Numerics.BitOperations.PopCount((uint)_table[dopRange.fc + 1]) : 6;
        int Slot(int i)
        {
            if (!_word6) return i;
            int bit = i - 6;
            if ((_sectionHeaders & (1 << bit)) == 0) return -1;
            return separators + System.Numerics.BitOperations.PopCount((uint)(_sectionHeaders & ((1 << bit) - 1)));
        }
        DHeaderFooter? Part(int fixedIndex)
        {
            int i = Slot(fixedIndex);
            if (i < 0) return null;
            if (i + 1 >= cps.Count || cps[i + 1] - cps[i] <= 1) return null;
            var part = new DHeaderFooter();
            ReadStory(story + cps[i], story + cps[i + 1], part.Blocks, []);
            while (part.Blocks.Count > 0 && part.Blocks[^1] is DParagraph { Inlines.Count: 0 }) part.Blocks.RemoveAt(part.Blocks.Count - 1);
            return part.Blocks.Count > 0 ? part : null;
        }
        doc.Header = Part(7);
        doc.Footer = Part(9);
        if (_facingPages)
        {
            doc.EvenHeader = Part(6);
            doc.EvenFooter = Part(8);
        }
        if (_titlePage)
        {
            doc.DifferentFirstPage = true;
            doc.FirstHeader = Part(10);
            doc.FirstFooter = Part(11);
        }
    }

    bool _titlePage, _facingPages;
    /// <summary>Word 6: which header/footer stories the first section has (bit per story).</summary>
    int _sectionHeaders;
    string? _noteMark;

    // ------------------------------------------------------------------ the story

    /// <summary>The stories being read: a text box anchored in its own text would otherwise go round for ever.</summary>
    readonly HashSet<(int start, int end)> _reading = [];

    void ReadStory(int cpStart, int cpEnd, List<DBlock> into, Dictionary<int, (List<DBlock> blocks, bool endnote)> notes)
    {
        if (_reading.Count > 16 || !DocumentLimits.CanGoDeeper(0) || !_reading.Add((cpStart, cpEnd))) return;
        try { Story(cpStart, cpEnd, into, notes); }
        finally { _reading.Remove((cpStart, cpEnd)); }
    }

    void Story(int cpStart, int cpEnd, List<DBlock> into, Dictionary<int, (List<DBlock> blocks, bool endnote)> notes)
    {
        if (cpStart < 0 || cpEnd < cpStart || cpEnd > _pieces.Select(p => p.cpEnd).DefaultIfEmpty(0).Max())
            throw new ConversionException("This Word document has an invalid text range.");
        var paragraph = new DParagraph();
        var row = new List<DCell>();
        var cell = new DCell();
        DTable? table = null;
        bool pageBreak = false;
        PageSetup? section = null; // a new section starts with the next block
        DParagraph? previous = null;
        int previousIstd = -1;
        var fields = new Stack<(StringBuilder code, bool inResult, string? link, FieldKind? kind)>();

        void Finish(char mark, int fc, CharProps chars)
        {
            var props = new ParaProps();
            var papx = Find(_papx, r => r.fcStart, r => r.fcEnd, fc);
            int istd = papx?.istd ?? 0;
            if (papx is { } px)
            {
                // Indents: the paragraph's own beat its list level's, which beat its style's.
                StylePara(px.istd, props);
                var probe = new ParaProps { ListIndex = props.ListIndex, ListLevel = props.ListLevel };
                ApplyPara(probe, px.grpprl);
                if (ListLevelOf(probe) is { } level) (props.Left, props.First) = (level.Indent, level.Hanging);
                ApplyPara(props, px.grpprl);
                if (_styles.TryGetValue(px.istd, out var style) && style.sti is >= 1 and <= 9) props.Heading = style.sti;
            }
            paragraph.Align = props.FrameAlign ?? props.Align;
            paragraph.IndentLeft = props.Left;
            paragraph.IndentFirst = props.First;
            paragraph.SpaceBefore = props.Before;
            paragraph.SpaceAfter = props.After;
            paragraph.LineSpacing = props.Line;
            paragraph.Heading = props.Heading is >= 1 and <= 6 ? props.Heading : 0;
            // "Don't add space between paragraphs of the same style" (list items, mostly).
            if (props.Contextual && previousIstd == istd && previous != null)
            {
                paragraph.SpaceBefore = 0;
                previous.SpaceAfter = 0;
            }
            previous = paragraph;
            previousIstd = istd;
            paragraph.PageBreakBefore = pageBreak;
            if (section != null && !props.InTable) { paragraph.Section = section; section = null; }
            paragraph.MarkSize = chars.Size;
            pageBreak = false;
            if (paragraph.Inlines.OfType<DText>().Any(t => t.Text.Trim().Length > 0)) ApplyList(paragraph, props, chars);

            if (props.InTable)
            {
                if (props.RowEnd)
                {
                    if (table == null)
                    {
                        table = new DTable { Borders = true, Section = section };
                        section = null;
                    }
                    var r = new DRow();
                    r.Cells.AddRange(row);
                    if (r.Cells.Count > 0 && table.Rows.Count < DocumentLimits.Rows) table.Rows.Add(r);
                    row = [];
                    cell = new DCell();
                }
                else
                {
                    cell.Blocks.Add(paragraph);
                    if (mark == '\a')
                    {
                        row.Add(cell);
                        cell = new DCell();
                    }
                }
            }
            else
            {
                if (table != null) { into.Add(table); table = null; }
                into.Add(paragraph);
            }
            paragraph = new DParagraph();
        }

        var text = new StringBuilder();
        CharProps? runProps = null;
        string? runLink = null;
        void Flush()
        {
            if (text.Length == 0 || runProps == null) return;
            if (!runProps.Hidden)
                paragraph.Inlines.Add(new DText(text.ToString(), new TextStyle
                {
                    Bold = runProps.Bold, Italic = runProps.Italic, Underline = runProps.Underline || runLink != null, Strike = runProps.Strike,
                    Superscript = runProps.Position > 0, Subscript = runProps.Position < 0,
                    Size = runProps.Size, Color = runProps.Color ?? (runLink != null ? Color.FromRgb(0x05, 0x63, 0xC1) : null), Font = runProps.Font,
                }, runLink));
            text.Clear();
        }

        long lastChpx = long.MinValue;
        var chars = new CharProps();
        for (int cp = cpStart; cp < cpEnd; cp++)
        {
            var (ch, fc) = CharAt(cp);
            if (fc < 0) continue;

            // Character formatting: the paragraph style's, then the run's own.
            var chpx = Find(_chpx, r => r.fcStart, r => r.fcEnd, fc);
            var papx = Find(_papx, r => r.fcStart, r => r.fcEnd, fc);
            long key = ((long)(chpx?.fcStart ?? -1) << 16) ^ (papx?.istd ?? 0xFFFF);
            if (key != lastChpx)
            {
                chars = new CharProps();
                if (papx is { } px) StyleChars(px.istd, chars);
                if (chpx is { } cx) ApplyChar(chars, cx.grpprl);
                lastChpx = key;
            }

            if (_word6 && ch >= 0x80 && chars.Lid != 0) ch = Decode6(_word[fc], chars.Lid);
            bool inCode = fields.Count > 0 && !fields.Peek().inResult;
            switch (ch)
            {
                case '\x13':
                    Flush();
                    fields.Push((new StringBuilder(), false, null, null));
                    continue;
                case '\x14' when fields.Count > 0:
                    var f = fields.Pop();
                    var code = f.code.ToString().Trim();
                    string? link = null;
                    if (code.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))
                    {
                        var quoted = System.Text.RegularExpressions.Regex.Match(code, "\"([^\"]+)\"");
                        var place = System.Text.RegularExpressions.Regex.Match(code, "\\\\l\\s+\"([^\"]+)\"");
                        if (place.Success && place.Index < quoted.Index) link = "#" + place.Groups[1].Value;
                        else if (quoted.Success && Uri.TryCreate(quoted.Groups[1].Value, UriKind.Absolute, out _)) link = quoted.Groups[1].Value;
                    }
                    // Page numbers are live fields; their saved result is replaced when laid out.
                    var word = code.Split(' ', 2)[0].ToUpperInvariant();
                    FieldKind? kind = word switch { "PAGE" => FieldKind.Page, "NUMPAGES" or "SECTIONPAGES" => FieldKind.NumPages, _ => null };
                    fields.Push((f.code, true, link, kind));
                    continue;
                case '\x15' when fields.Count > 0:
                    Flush();
                    if (fields.Pop() is { kind: { } fieldKind })
                        paragraph.Inlines.Add(new DField(fieldKind, new TextStyle { Size = chars.Size, Bold = chars.Bold, Italic = chars.Italic, Font = chars.Font, Color = chars.Color }));
                    continue;
            }
            if (inCode)
            {
                fields.Peek().code.Append(ch);
                continue;
            }
            if (fields.Any(x => x.inResult && x.kind != null)) continue; // the stale page number
            var currentLink = fields.FirstOrDefault(x => x.inResult && x.link != null).link;
            if (!ReferenceEquals(runProps, chars) || currentLink != runLink)
            {
                Flush();
                runProps = chars;
                runLink = currentLink;
            }

            switch (ch)
            {
                case '\r':
                    Flush();
                    Finish('\r', fc, chars);
                    break;
                case '\a':
                    Flush();
                    Finish('\a', fc, chars);
                    break;
                case '\v':
                    Flush();
                    paragraph.Inlines.Add(new DLineBreak());
                    break;
                case '\f':
                    Flush();
                    if (paragraph.Inlines.Count > 0) Finish('\r', fc, chars);
                    // A section mark: the next section may bring its own page settings (or continue on this page).
                    if (cpStart == 0 && _sectionStarts.TryGetValue(cp + 1, out var next))
                    {
                        if (next != null) section = next;
                    }
                    else pageBreak = true;
                    break;
                case '\x01':
                    Flush();
                    if (!chars.Hidden && Picture(chpx?.grpprl) is { } image) paragraph.Inlines.Add(image);
                    break;
                case '\x02':
                    Flush();
                    if (_noteMark != null) text.Append(_noteMark);
                    if (notes.TryGetValue(cp, out var note))
                    {
                        var dnote = new DNote(note.endnote ? ++_endnoteNumber : ++_footnoteNumber, note.endnote, new TextStyle { Size = chars.Size });
                        dnote.Blocks.AddRange(note.blocks);
                        paragraph.Inlines.Add(dnote);
                    }
                    break;
                case '':
                    Flush();
                    if (Drawing(cp) is { } drawing) paragraph.Inlines.Add(drawing);
                    break;
                case '\t':
                    text.Append('\t');
                    break;
                case '\x1E':
                    text.Append('‑');
                    break;
                case '\x1F' or '\x05' or '\x0E' or '\0':
                    break;
                default:
                    if (ch >= ' ' || ch == ' ') text.Append(ch);
                    break;
            }
        }
        Flush();
        if (paragraph.Inlines.Count > 0) into.Add(paragraph);
        if (table != null) into.Add(table);
    }

    /// <summary>
    /// An inline picture: a picture header in the Data stream (with its display size) wrapping an OfficeArt
    /// record that holds the PNG, JPEG, bitmap or metafile.
    /// </summary>
    DImage? Picture(byte[]? grpprl)
    {
        if (grpprl == null) return null;
        int? location = null;
        bool ole = false;
        foreach (var (sprm, at, size) in Sprms(grpprl))
        {
            if (sprm == 0x6A03 && size >= 4) location = BinaryPrimitives.ReadInt32LittleEndian(grpprl.AsSpan(at));
            if (sprm == 0x080A) ole = grpprl[at] != 0;
        }
        // An embedded object (classic WordArt, Equation, a chart): its preview lives in the object's own storage.
        if (ole && location is int objectId && _file != null)
        {
            var folder = new[] { "ObjectPool", "_" + (uint)objectId };
            if (_file.Stream([.. folder, "\u0003META"]) is { Length: > 0 } meta)
            {
                var (mw, mh) = DocxReader.ImageSize(meta);
                if (Usable(meta, mw, mh) is { } preview) return preview;
            }
            if (_file.Stream([.. folder, "\u0003PIC"]) is { Length: > 68 } pic)
            {
                int header = BinaryPrimitives.ReadUInt16LittleEndian(pic.AsSpan(4));
                short P(int o) => BinaryPrimitives.ReadInt16LittleEndian(pic.AsSpan(o));
                double pw = (P(28) - P(36) - P(40)) * BinaryPrimitives.ReadUInt16LittleEndian(pic.AsSpan(32)) / 1000.0 / 20;
                double ph = (P(30) - P(38) - P(42)) * BinaryPrimitives.ReadUInt16LittleEndian(pic.AsSpan(34)) / 1000.0 / 20;
                if (header > 0 && header < pic.Length && Usable(pic[header..], pw > 1 ? pw : 144, ph > 1 ? ph : 72) is { } drawn)
                    return drawn;
            }
        }
        if (location is not int start || start < 0 || start + 44 > _data.Length) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(start));
        int end = Math.Min(_data.Length, start + Math.Max(0, length));
        var block = _data.AsSpan(start, end - start);

        // Display size: the goal size in twips, scaled (per mille) and cropped.
        short S(int o) => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(start + o));
        ushort U(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(start + o));
        double width = (S(28) - S(36) - S(40)) * U(32) / 1000.0 / 20;
        double height = (S(30) - S(38) - S(42)) * U(34) / 1000.0 / 20;

        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(block[4..]), mapMode = BinaryPrimitives.ReadUInt16LittleEndian(block[6..]);
        // Word 97+ stores inline pictures and shapes as an OfficeArt shape after the header (0x66: with a name first).
        if (mapMode is 0x64 or 0x66 && headerSize < block.Length)
        {
            int shapeAt = start + headerSize + (mapMode == 0x66 ? 1 + block[headerSize] : 0);
            if (InlineShape(shapeAt, width, height) is { } drawn) return drawn;
        }
        var (data, ext) = Blip(block);
        // Older pictures and embedded objects' previews are a bare Windows metafile after the header.
        if (data == null && mapMode is >= 1 and <= 8 && headerSize >= 44 && headerSize < block.Length)
            (data, ext) = (block[headerSize..].ToArray(), "wmf");
        if (data == null) return null;
        var (w, h) = DocxReader.ImageSize(data);
        if (width > 1 && height > 1) (w, h) = (width, height);
        return Usable(data, w, h);
    }

    readonly Dictionary<byte[], (byte[] data, string ext)?> _usable = new(ReferenceEqualityComparer.Instance);

    /// <summary>A picture every output can hold: metafiles become PNG.</summary>
    DImage? Usable(byte[] data, double w, double h)
    {
        if (!_usable.TryGetValue(data, out var picture)) _usable[data] = picture = DocumentPictures.Embeddable(data);
        return picture is { } p ? new DImage(p.data, p.ext, Math.Max(1, w), Math.Max(1, h)) : null;
    }

    const long MetafileLimit = 64L * 1024 * 1024;

    /// <summary>Finds the first OfficeArt picture record (BLIP) in a block and returns its image file.</summary>
    internal static (byte[]? data, string? ext) Blip(ReadOnlySpan<byte> block)
    {
        for (int i = 0; i + 8 <= block.Length; i++)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(block[(i + 2)..]);
            if (type is < 0xF01A or > 0xF02A || (block[i] & 0x0F) != 0) continue;
            int instance = BinaryPrimitives.ReadUInt16LittleEndian(block[i..]) >> 4;
            int recordLength = BinaryPrimitives.ReadInt32LittleEndian(block[(i + 4)..]);
            if (recordLength <= 0 || i + 8 + recordLength > block.Length) continue;
            var body = block.Slice(i + 8, recordLength);
            // One unique ID (16 bytes), or two for the odd instance numbers.
            int uids = (instance & 1) == 1 ? 32 : 16;
            switch (type)
            {
                case 0xF01A or 0xF01B: // EMF, WMF: a metafile header, then the (usually deflated) file
                    if (body.Length < uids + 34) continue;
                    var header = body.Slice(uids, 34);
                    var payload = body[(uids + 34)..].ToArray();
                    if (header[32] == 0)
                    {
                        try
                        {
                            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(payload), System.IO.Compression.CompressionMode.Decompress);
                            using var ms = new MemoryStream();
                            // A few bytes can inflate to gigabytes; no real metafile is anywhere near this big.
                            var buffer = new byte[81920];
                            for (int read; (read = z.Read(buffer)) > 0 && ms.Length <= MetafileLimit;) ms.Write(buffer, 0, read);
                            if (ms.Length > MetafileLimit) continue;
                            payload = ms.ToArray();
                        }
                        catch (InvalidDataException) { continue; }

                    }
                    return (payload, type == 0xF01A ? "emf" : "wmf");
                case 0xF01D or 0xF02A:
                    if (body.Length <= uids + 1) continue;
                    return (body[(uids + 1)..].ToArray(), "jpg");
                case 0xF01E:
                    if (body.Length <= uids + 1) continue;
                    return (body[(uids + 1)..].ToArray(), "png");
                case 0xF029:
                    if (body.Length <= uids + 1) continue;
                    return (body[(uids + 1)..].ToArray(), "tiff");
                case 0xF01F: // a bare device-independent bitmap: give it a .bmp file header
                    if (body.Length <= uids + 1) continue;
                    var dib = body[(uids + 1)..];
                    if (dib.Length < 40) continue;
                    int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
                    int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
                    int colors = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
                    if (headerSize is < 12 or > 1024 || colors is < 0 or > 256 || bits > 32) continue;
                    int palette = (colors == 0 && bits <= 8 ? 1 << bits : colors) * 4;
                    var bmp = new byte[14 + dib.Length];
                    bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
                    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 14 + headerSize + palette);
                    dib.CopyTo(bmp.AsSpan(14));
                    return (bmp, "bmp");
            }
        }
        return (null, null);
    }

    // ------------------------------------------------------------------ OLE compound file

    /// <summary>The "structured storage" container Office 97–2003 files use: a little FAT file system in a file.</summary>
    internal sealed class CompoundFile
    {
        readonly byte[] _file;
        readonly int _sectorSize, _miniCutoff;
        readonly List<int> _fat = [], _miniFat = [];
        readonly List<(string name, int type, int start, long size, int left, int right, int child)> _entries = [];
        readonly byte[] _miniStream;

        public CompoundFile(byte[] file)
        {
            _file = file;
            if (file.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(file) != 0xE11AB1A1E011CFD0)
                throw new ConversionException("This isn't an Office 97–2003 file, or it is damaged.");
            int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x1E));
            if (sectorShift is not (9 or 12) || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x20)) != 6)
                throw new ConversionException("This Office file has an invalid sector size.");
            _sectorSize = 1 << sectorShift;
            _miniCutoff = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x38));
            int firstDir = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
            int firstMiniFat = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
            int firstDifat = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x44));

            var fatSectors = new List<int>();
            for (int i = 0; i < 109; i++)
            {
                int s = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x4C + i * 4));
                if (s >= 0) fatSectors.Add(s);
            }
            var seenDifat = new HashSet<int>();
            for (int d = firstDifat; d >= 0 && seenDifat.Add(d);)
            {
                int at = Offset(d);
                if (at < 0) break;
                for (int i = 0; i < _sectorSize / 4 - 1; i++)
                {
                    int s = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + i * 4));
                    if (s >= 0) fatSectors.Add(s);
                }
                d = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + _sectorSize - 4));
            }
            foreach (var s in fatSectors.Distinct())
            {
                int at = Offset(s);
                if (at < 0) continue;
                for (int i = 0; i < _sectorSize / 4 && at + i * 4 + 4 <= file.Length; i++)
                    _fat.Add(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + i * 4)));
            }

            var dir = Chain(firstDir, long.MaxValue);
            for (int i = 0; i + 128 <= dir.Length; i += 128)
            {
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(i + 0x40));
                var name = Encoding.Unicode.GetString(dir, i, Math.Clamp(nameLength - 2, 0, 64));
                _entries.Add((name, dir[i + 0x42], BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(i + 0x74)),
                    (long)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i + 0x78)),
                    BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(i + 0x44)), BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(i + 0x48)),
                    BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(i + 0x4C))));
            }

            var miniFat = Chain(firstMiniFat, long.MaxValue);
            for (int i = 0; i + 4 <= miniFat.Length; i += 4) _miniFat.Add(BinaryPrimitives.ReadInt32LittleEndian(miniFat.AsSpan(i)));
            var root = _entries.FirstOrDefault(e => e.type == 5);
            _miniStream = root.size > 0 ? Chain(root.start, root.size) : [];
        }

        int Offset(int sector)
        {
            long at = ((long)sector + 1) * _sectorSize;
            return at >= 0 && at <= _file.Length - _sectorSize ? (int)at : -1;
        }

        byte[] Chain(int start, long size)
        {
            using var ms = new MemoryStream();
            var seen = new HashSet<int>();
            for (int s = start; s >= 0 && s < _fat.Count + 1 && seen.Add(s) && ms.Length < size; s = s < _fat.Count ? _fat[s] : -2)
            {
                int at = Offset(s);
                if (at < 0) break;
                ms.Write(_file, at, (int)Math.Min(_sectorSize, Math.Min(_file.Length - at, size - ms.Length)));
            }
            return ms.ToArray();
        }

        byte[] MiniChain(int start, long size)
        {
            using var ms = new MemoryStream();
            var seen = new HashSet<int>();
            for (int s = start; s >= 0 && s < _miniFat.Count && seen.Add(s) && ms.Length < size; s = _miniFat[s])
            {
                long offset = (long)s * 64;
                if (offset >= _miniStream.Length) break;
                int at = (int)offset;
                ms.Write(_miniStream, at, (int)Math.Min(64, Math.Min(_miniStream.Length - at, size - ms.Length)));
            }
            return ms.ToArray();
        }

        IEnumerable<(string name, int type, int start, long size, int left, int right, int child)> RootChildren() => ChildrenOf(0);

        /// <summary>The entries directly inside a storage (its children form a little binary tree).</summary>
        List<(string name, int type, int start, long size, int left, int right, int child)> ChildrenOf(int storage)
        {
            if (storage < 0 || storage >= _entries.Count) return [];
            var found = new List<(string, int, int, long, int, int, int)>();
            var pending = new Stack<int>();
            var seen = new HashSet<int>();
            pending.Push(_entries[storage].child);
            while (pending.Count > 0)
            {
                int id = pending.Pop();
                if (id < 0 || id >= _entries.Count || !seen.Add(id)) continue;
                var e = _entries[id];
                found.Add(e);
                pending.Push(e.left);
                pending.Push(e.right);
            }
            return found;
        }

        /// <summary>A stream inside nested storages, e.g. ("ObjectPool", "_1234", "\u0003META").</summary>
        public byte[]? Stream(string[] path)
        {
            int storage = 0;
            for (int i = 0; i < path.Length; i++)
            {
                int index = -1;
                foreach (var e in ChildrenOf(storage))
                    if (string.Equals(e.name, path[i], StringComparison.OrdinalIgnoreCase)) { index = _entries.IndexOf(e); break; }
                if (index < 0) return null;
                var entry = _entries[index];
                if (i == path.Length - 1)
                    return entry.type != 2 ? null : entry.size < _miniCutoff ? MiniChain(entry.start, entry.size) : Chain(entry.start, entry.size);
                storage = index;
            }
            return null;
        }

        public byte[]? Stream(string name)
        {
            // Only the root storage's own streams: embedded objects carry streams with the same names.
            var entry = RootChildren().FirstOrDefault(e => e.type == 2 && e.name == name);
            if (entry.name == null) return null;
            return entry.size < _miniCutoff ? MiniChain(entry.start, entry.size) : Chain(entry.start, entry.size);
        }
    }
}

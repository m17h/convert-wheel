using System.Buffers.Binary;
using System.Text;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Word 6 and Word 95 (.doc files from 1993–1996). Their layout is close to Word 97's, so they share the
/// reader: the header has fixed offsets, formatting uses one-byte codes (translated here into the Word 97
/// codes the rest of the reader understands), text is 8-bit, and numbering uses per-paragraph autonumbers.
/// </summary>
internal sealed partial class WordBinaryReader
{
    DDocument Build6(string title)
    {
        _fcLcbOffset = 88;
        _ccpText = I32(52);
        _ccpFtn = I32(56);
        _ccpHdd = I32(60);
        int ccpMcr = I32(64);
        _ccpAtn = I32(68);
        _ccpEdn = I32(72);

        // Word 6 text is in the code page of the document's language (Central European, Cyrillic, Greek…).
        int lid = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(6));
        try
        {
            int codePage = System.Globalization.CultureInfo.GetCultureInfo(lid).TextInfo.ANSICodePage;
            if (codePage is not (1252 or 0) && codePage < 1300)
            {
                TextDecoding.EnsureRegistered();
                _byteChars = Encoding.GetEncoding(codePage).GetChars(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
            }
        }
        catch (Exception ex) when (ex is System.Globalization.CultureNotFoundException or ArgumentException or NotSupportedException) { }
        int fcMin = I32(0x18);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(0x0A));
        if ((flags & 0x0004) != 0) ReadPieces(); // fast-saved: a piece table, as in Word 97
        else
        {
            // Otherwise all text is one run from fcMin.
            int total = _ccpText + _ccpFtn + _ccpHdd + ccpMcr + _ccpAtn + _ccpEdn + I32(76) + I32(80) + 1;
            _pieces.Add((0, total, fcMin, true));
        }
        int cpnChp = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(398)), cpnPap = BinaryPrimitives.ReadUInt16LittleEndian(_word.AsSpan(400));
        ReadFkps(FcLcb(12), chars: true, cpnChp);
        ReadFkps(FcLcb(13), chars: false, cpnPap);
        ReadFonts(FcLcb(15));
        ReadStyles(FcLcb(1));

        var doc = new DDocument { Title = title };
        ReadPageSetup(FcLcb(6), doc);
        if (_styles.Values.FirstOrDefault(s => s.sti == 0) is { chpx: { } normal })
        {
            var baseChars = new CharProps();
            ApplyChar(baseChars, normal);
            doc.Font = baseChars.Font ?? _defaultFont ?? "Times New Roman";
            doc.FontSize = baseChars.Size ?? 10;
        }
        else (doc.Font, doc.FontSize) = (_defaultFont ?? "Times New Roman", 10);

        int footnoteStory = _ccpText, headerStory = footnoteStory + _ccpFtn, endnoteStory = headerStory + _ccpHdd + ccpMcr + _ccpAtn;
        var notes = ReadNotes(FcLcb(2), FcLcb(3), footnoteStory, _ccpFtn, endnote: false);
        foreach (var (cp, note) in ReadNotes((I32(466), I32(470)), (I32(474), I32(478)), endnoteStory, _ccpEdn, endnote: true)) notes[cp] = note;
        ReadHeaders(doc, headerStory);
        ReadStory(0, _ccpText, doc.Blocks, notes);
        doc.NormalizeDefaults();
        return doc;
    }

    // ------------------------------------------------------------------ formatting codes

    /// <summary>Word 6 code → Word 97 code (0: not used by the reader, kept only to skip it correctly).</summary>
    static readonly Dictionary<int, ushort> Word6Codes = new()
    {
        [2] = 0x4600, [5] = 0x2403, [9] = 0x2407, [12] = 0xC63E, [13] = 0x26FE, [16] = 0x840E, [17] = 0x840F, [19] = 0x8411,
        [20] = 0x6412, [21] = 0xA413, [22] = 0xA414, [24] = 0x2416, [25] = 0x2417, [26] = 0x8418, [29] = 0x261B,
        [68] = 0x6A03, [75] = 0x080A, [80] = 0x4A30, [85] = 0x0835, [86] = 0x0836, [87] = 0x0837, [91] = 0x083B, [92] = 0x083C,
        [93] = 0x4A4F, [94] = 0x2A3E, [97] = 0x4A41, [98] = 0x2A42, [99] = 0x4A43, [104] = 0x2A48,
        [143] = 0x300A, [153] = 0x3014, [156] = 0xB017, [157] = 0xB018, [164] = 0xB01F, [165] = 0xB020, [166] = 0xB021,
        [167] = 0xB022, [168] = 0x9023, [169] = 0x9024,
    };

    /// <summary>Operand sizes of Word 6 codes: bytes, -1 = a length byte follows, -2 = a two-byte length follows, 0 = unknown.</summary>
    static readonly sbyte[] Word6Sizes = BuildSizes();

    static sbyte[] BuildSizes()
    {
        var s = new sbyte[256];
        void Set(int size, params int[] codes) { foreach (var c in codes) s[c] = (sbyte)size; }
        Set(1, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 24, 25, 29, 37, 44, 50, 51, 65, 66, 67, 71, 75, 82, 83, 85, 86, 87, 88, 89, 90, 91, 92,
            94, 98, 100, 102, 104, 117, 118, 119, 131, 132, 138, 139, 142, 143, 146, 147, 150, 151, 152, 153, 159, 162, 163, 185, 186);
        Set(2, 2, 16, 17, 18, 19, 21, 22, 26, 27, 28, 30, 31, 32, 33, 34, 35, 36, 38, 39, 40, 41, 42, 43, 45, 46, 47, 48, 49,
            69, 72, 80, 93, 96, 97, 99, 101, 107, 109, 110, 121, 122, 123, 124, 140, 141, 144, 145, 148, 149, 154, 155, 156, 157,
            160, 161, 164, 165, 166, 167, 168, 169, 170, 171, 182, 183, 184, 189, 195, 197, 198);
        Set(3, 73, 74, 95, 136, 137);
        Set(4, 20, 68, 70, 192, 194, 196, 200);
        Set(5, 193, 199);
        Set(12, 187);
        Set(-1, 3, 12, 15, 23, 81, 103, 105, 106, 108, 120, 133, 191);
        Set(-2, 188, 190);
        return s;
    }

    /// <summary>
    /// Rewrites Word 6 formatting (one-byte codes) as Word 97 formatting. Codes the reader doesn't use become
    /// placeholders of the same size, so everything after them still lines up.
    /// </summary>
    static byte[] Translate6(byte[] grpprl)
    {
        var output = new List<byte>(grpprl.Length * 2);
        int i = 0;
        while (i < grpprl.Length)
        {
            int code = grpprl[i++];
            int size = Word6Sizes[code];
            int header = 0;
            if (size == -1 && i < grpprl.Length) { header = 1; size = grpprl[i]; }
            else if (size == -2 && i + 1 < grpprl.Length) { header = 2; size = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(i)); }
            if (size <= 0 && header == 0) break; // unknown code: its size is unknown, so nothing after it can be trusted
            if (i + header + size > grpprl.Length) break;
            var operand = grpprl.AsSpan(i + header, size);
            i += header + size;

            ushort target = Word6Codes.GetValueOrDefault(code);
            bool variable = header > 0;
            // The Word 97 code's size class must match what we copy; otherwise use a placeholder.
            int targetSize = target == 0 ? -99 : (target >> 13) switch { 0 or 1 => 1, 2 or 4 or 5 => 2, 3 => 4, 7 => 3, _ => -1 };
            if (variable ? targetSize != -1 : targetSize != size)
                target = (ushort)(variable || size is not (1 or 2 or 3 or 4) ? 0xCBFF : size switch { 1 => 0x2BFF, 2 => 0x4BFF, 3 => 0xEBFF, _ => 0x6BFF });
            output.Add((byte)target);
            output.Add((byte)(target >> 8));
            if (target >> 13 == 6)
            {
                if (operand.Length > 255) break;
                output.Add((byte)operand.Length);
            }
            output.AddRange(operand.ToArray());
        }
        return [.. output];
    }

    // ------------------------------------------------------------------ autonumbering

    /// <summary>8-bit text decoding when the document isn't in the Western code page.</summary>
    char[]? _byteChars;

    readonly Dictionary<int, char[]?> _codePages = [];

    /// <summary>A byte of 8-bit text in the code page of its run's language.</summary>
    char Decode6(byte b, int lid)
    {
        if (!_codePages.TryGetValue(lid, out var table))
        {
            try
            {
                int codePage = System.Globalization.CultureInfo.GetCultureInfo(lid).TextInfo.ANSICodePage;
                TextDecoding.EnsureRegistered();
                table = codePage is > 0 and < 1300 ? Encoding.GetEncoding(codePage).GetChars(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()) : null;
            }
            catch (Exception ex) when (ex is System.Globalization.CultureNotFoundException or ArgumentException or NotSupportedException) { table = null; }
            _codePages[lid] = table;
        }
        return table?[b] ?? (_byteChars?[b] ?? (b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b));
    }

    string? _anldSignature;
    int _anldCounter;

    /// <summary>
    /// Word 6 bullets and numbers: each paragraph carries its own autonumber settings (format, text around
    /// the number, start value); consecutive numbered paragraphs count up.
    /// </summary>
    void ApplyList6(DParagraph p, ParaProps props, CharProps chars)
    {
        var anld = props.Anld;
        if (anld == null || anld.Length < 20 || props.NLvlAnm == 0)
        {
            _anldSignature = null;
            return;
        }
        int nfc = anld[0], before = anld[1], after = anld[2];
        int start = BinaryPrimitives.ReadUInt16LittleEndian(anld.AsSpan(12));
        string text = Encoding.Latin1.GetString(anld, 20, Math.Min(anld.Length - 20, 32)).TrimEnd('\0');
        string prefix = text[..Math.Min(before, text.Length)], suffix = text[Math.Min(before, text.Length)..Math.Min(before + after, text.Length)];
        int ftc = BinaryPrimitives.ReadUInt16LittleEndian(anld.AsSpan(6));
        if (nfc == 23)
        {
            p.List = ListKind.Bullet;
            char bullet = prefix.Length > 0 ? prefix[0] : '•';
            // Symbol/Wingdings bullets are stored as their 8-bit codes.
            string font = ftc < _fonts.Count ? _fonts[ftc] : "";
            p.ListLabel = font is "Symbol" or "Wingdings" ? BulletGlyph((char)(0xF000 + bullet)) : bullet.ToString();
            _anldSignature = null;
        }
        else
        {
            var signature = $"{nfc}|{prefix}|{suffix}";
            _anldCounter = signature == _anldSignature ? _anldCounter + 1 : Math.Max(1, start);
            _anldSignature = signature;
            p.List = ListKind.Number;
            p.ListLabel = prefix + ListLabels.Format(_anldCounter, nfc switch
            {
                1 => "upperRoman", 2 => "lowerRoman", 3 => "upperLetter", 4 => "lowerLetter", _ => "decimal",
            }) + suffix;
        }
        if (p.IndentFirst >= 0)
        {
            double indent = BinaryPrimitives.ReadInt16LittleEndian(anld.AsSpan(14)) / 20.0;
            p.IndentLeft = Math.Max(p.IndentLeft, indent > 0 ? indent : 18);
            p.IndentFirst = -(indent > 0 ? indent : 18);
        }
    }
}

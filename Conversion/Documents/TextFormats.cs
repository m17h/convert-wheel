using System.Text;
using System.Text.RegularExpressions;

namespace ConvertWheel.Conversion.Documents;

internal static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void EnsureRegistered() { }

    /// <summary>
    /// Honours a BOM, otherwise spots UTF-16, otherwise UTF-8 if that's what the bytes are, otherwise the Windows
    /// ANSI code page.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return new UTF32Encoding(true, false).GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (Utf16Order(bytes) is { } order) return (order ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes, 0, bytes.Length & ~1);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // A damaged byte or two (or a file cut off mid-character) doesn't make it anything but UTF-8.
            if (MostlyUtf8(bytes)) return Encoding.UTF8.GetString(bytes);
            int page = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(page is 0 or 65001 ? 1252 : page).GetString(bytes);
        }
    }

    /// <summary>Whether the bytes say themselves what encoding they're in.</summary>
    public static bool HasMark(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF || b.Length >= 2 && (b[0] == 0xFF && b[1] == 0xFE || b[0] == 0xFE && b[1] == 0xFF)
        || b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF;

    public static bool LooksLikeUtf16(byte[] bytes) =>
        bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) || Utf16Order(bytes) != null;

    /// <summary>UTF-16 without a mark: true for big-endian, null if it isn't UTF-16. Text has every other byte zero, or nearly.</summary>
    static bool? Utf16Order(byte[] bytes)
    {
        int n = Math.Min(bytes.Length & ~1, 8192);
        if (n < 8) return null;
        int even = 0, odd = 0, spacesLittle = 0, spacesBig = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (bytes[i] == 0) even++;
            if (bytes[i + 1] == 0) odd++;
            if (bytes[i] == 0 && bytes[i + 1] == 0) return null; // binary
            if (bytes[i + 1] == 0 && bytes[i] is 0x20 or 0x0A or 0x0D) spacesLittle++;
            if (bytes[i] == 0 && bytes[i + 1] is 0x20 or 0x0A or 0x0D) spacesBig++;
        }
        int pairs = n / 2;
        if (even == 0 && odd > 0 && (odd > pairs * 0.6 || spacesLittle > pairs / 40 && odd >= spacesLittle)) return false;
        if (odd == 0 && even > 0 && (even > pairs * 0.6 || spacesBig > pairs / 40 && even >= spacesBig)) return true;
        return null;
    }

    static bool MostlyUtf8(byte[] bytes)
    {
        int good = 0, bad = 0;
        for (int i = 0; i < bytes.Length;)
        {
            byte b = bytes[i];
            if (b < 0x80) { i++; continue; }
            int length = b is >= 0xC2 and <= 0xDF ? 2 : b is >= 0xE0 and <= 0xEF ? 3 : b is >= 0xF0 and <= 0xF4 ? 4 : 0;
            int k = 1;
            while (k < length && i + k < bytes.Length && (bytes[i + k] & 0xC0) == 0x80) k++;
            if (length > 0 && k == length) { good++; i += length; }
            else { bad++; i += Math.Max(1, k); }
        }
        return good > 4 && good >= bad * 10 || good > 0 && bad == 1 && bytes.Length > 0 && bytes[^1] >= 0x80 && good >= 2;
    }
}

internal static partial class PlainText
{
    public static DDocument Read(string path)
    {
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path), FontSize = 11 };
        doc.Blocks.AddRange(Paragraphs(TextDecoding.Decode(File.ReadAllBytes(path))));
        return doc;
    }

    /// <summary>One paragraph per line; a form feed starts a new page, as it does on a printer.</summary>
    internal static IEnumerable<DParagraph> Paragraphs(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u0085', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\v', '\n');
        if (text.Contains('\x1B')) text = TerminalCodes().Replace(text, ""); // the colours in a log
        if (text.EndsWith('\n')) text = text[..^1];
        bool breakNext = false;
        foreach (var line in text.Split('\n'))
        {
            var pages = line.Split('\f');
            for (int i = 0; i < pages.Length; i++)
            {
                if (i > 0) breakNext = true;
                // A form feed on a line of its own isn't an empty line too.
                if (pages.Length > 1 && pages[i].Length == 0) continue;
                var p = new DParagraph { PageBreakBefore = breakNext, RightToLeft = Bidi.StartsRightToLeft(pages[i]) };
                if (p.RightToLeft) p.Align = System.Windows.TextAlignment.Right;
                breakNext = false;
                if (pages[i].Length > 0) p.Inlines.Add(new DText(SafeXml.Text(pages[i]), TextStyle.Plain));
                yield return p;
            }
        }
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\^_])")]
    private static partial Regex TerminalCodes();

    public static void Write(DDocument doc, string path, CancellationToken ct = default) =>
        File.WriteAllText(path, ToText(doc, ct), new UTF8Encoding(false));

    public static string ToText(DDocument doc, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var (header, footer) = doc.HeaderFooterFor(1);
        if (PartText(header) is { Length: > 0 } top) sb.Append(top).Append(Environment.NewLine).Append(Environment.NewLine);
        Blocks(doc.Blocks, sb, ct);

        // Notes are listed at the end, after a short rule.
        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0)
        {
            sb.Append(Environment.NewLine).Append("----").Append(Environment.NewLine);
            foreach (var note in notes)
            {
                var text = BlocksText(note.Blocks);
                var mark = FlowBridge.NoteMark(note);
                if (text.StartsWith(mark, StringComparison.Ordinal)) text = text[mark.Length..].TrimStart();
                sb.Append('[').Append(mark).Append("] ").Append(text).Append(Environment.NewLine);
            }
        }
        if (PartText(footer) is { Length: > 0 } bottom) sb.Append(Environment.NewLine).Append(bottom).Append(Environment.NewLine);
        return sb.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    /// <summary>A header/footer as text, skipping page-number fields (there are no pages in a text file).</summary>
    static string PartText(DHeaderFooter? part) => part == null ? "" :
        string.Join(Environment.NewLine, DDocument.Paragraphs(part.Blocks).Select(p => p.PlainText().Trim()).Where(s => s.Length > 0 && !IsOnlyPageWords(s)));

    static bool IsOnlyPageWords(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => w.Equals("page", StringComparison.OrdinalIgnoreCase) || w.Equals("of", StringComparison.OrdinalIgnoreCase));

    static void Blocks(IEnumerable<DBlock> blocks, StringBuilder sb, CancellationToken ct)
    {
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block is DParagraph p)
            {
                if (p.ListLabel != null) sb.Append(' ', Math.Clamp(p.ListLevel, 0, 40) * 2).Append(p.ListLabel).Append(' ');
                sb.Append(p.PlainText().Replace("\n", Environment.NewLine)).Append(Environment.NewLine);
            }
            else if (block is DTable t)
            {
                foreach (var (_, slots) in t.Slots())
                {
                    ct.ThrowIfCancellationRequested();
                    // A place taken by a cell from a row above stays empty, so columns keep lining up.
                    sb.Append(string.Join('\t', slots.Select(s => s.Covered ? "" : CellText(s.Cell)))).Append(Environment.NewLine);
                }
                sb.Append(Environment.NewLine);
            }
        }
    }

    internal static string CellText(DCell cell) => BlocksText(cell.Blocks);

    /// <summary>Blocks on one line, including what's in tables inside them.</summary>
    static string BlocksText(List<DBlock> blocks) => string.Join(" ", blocks.Select(b => b switch
    {
        DParagraph p => (p.ListLabel != null && p.Inlines.Count > 0 ? p.ListLabel + " " : "") + p.PlainText().Replace('\n', ' ').Trim(),
        DTable t => string.Join(" ", t.Rows.SelectMany(r => r.Cells).Select(CellText).Where(s => s.Length > 0)),
        _ => "",
    }).Where(s => s.Length > 0));
}

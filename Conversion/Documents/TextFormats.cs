using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace ConvertWheel.Conversion.Documents;

internal static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void EnsureRegistered() { }

    /// <summary>Honours a BOM, otherwise UTF-8 if the bytes are valid UTF-8, otherwise the Windows ANSI code page.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }
}

internal static class PlainText
{
    public static DDocument Read(string path)
    {
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path), FontSize = 11 };
        var text = TextDecoding.Decode(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.EndsWith('\n')) text = text[..^1];
        foreach (var line in text.Split('\n'))
        {
            var p = new DParagraph();
            if (line.Length > 0) p.Inlines.Add(new DText(line, TextStyle.Plain));
            doc.Blocks.Add(p);
        }
        return doc;
    }

    public static void Write(DDocument doc, string path) =>
        File.WriteAllText(path, ToText(doc), new UTF8Encoding(false));

    public static string ToText(DDocument doc)
    {
        var sb = new StringBuilder();
        var (header, footer) = doc.HeaderFooterFor(1);
        if (PartText(header) is { Length: > 0 } top) sb.Append(top).Append(Environment.NewLine).Append(Environment.NewLine);
        Blocks(doc.Blocks, sb);

        // Notes are listed at the end, after a short rule.
        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0)
        {
            sb.Append(Environment.NewLine).Append("----").Append(Environment.NewLine);
            foreach (var note in notes)
            {
                var text = string.Join(" ", note.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Trim()).Where(s => s.Length > 0));
                var mark = FlowBridge.NoteMark(note);
                if (text.StartsWith(mark)) text = text[mark.Length..].TrimStart();
                sb.Append('[').Append(mark).Append("] ").Append(text).Append(Environment.NewLine);
            }
        }
        if (PartText(footer) is { Length: > 0 } bottom) sb.Append(Environment.NewLine).Append(bottom).Append(Environment.NewLine);
        return sb.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    /// <summary>A header/footer as text, skipping page-number fields (there are no pages in a text file).</summary>
    static string PartText(DHeaderFooter? part) => part == null ? "" :
        string.Join(Environment.NewLine, part.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Trim()).Where(s => s.Length > 0 && !IsOnlyPageWords(s)));

    static bool IsOnlyPageWords(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => w.Equals("page", StringComparison.OrdinalIgnoreCase) || w.Equals("of", StringComparison.OrdinalIgnoreCase));

    static void Blocks(IEnumerable<DBlock> blocks, StringBuilder sb)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p)
            {
                if (p.ListLabel != null) sb.Append(new string(' ', p.ListLevel * 2)).Append(p.ListLabel).Append(' ');
                sb.Append(p.PlainText().Replace("\n", Environment.NewLine)).Append(Environment.NewLine);
            }
            else if (block is DTable t)
            {
                foreach (var row in t.Rows)
                    sb.Append(string.Join('\t', row.Cells.Select(c => CellText(c)))).Append(Environment.NewLine);
                sb.Append(Environment.NewLine);
            }
        }
    }

    internal static string CellText(DCell cell) =>
        string.Join(" ", cell.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Replace('\n', ' ')).Where(s => s.Length > 0));
}


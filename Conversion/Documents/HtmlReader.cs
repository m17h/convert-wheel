using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Reads HTML into the document model: headings, paragraphs, lists, tables, images, links and
/// inline styles. Markdown (via Markdig) and EPUB e-books (a zip of XHTML chapters) come in this way too.
/// </summary>
internal sealed partial class HtmlReader
{
    readonly Source _source;
    readonly double _maxImageWidth;
    readonly int _tableDepth;
    readonly List<DBlock> _output;
    readonly NoteState _notes;
    DParagraph? _current;
    bool _breakBeforeNext;
    bool _preParagraph;
    /// <summary>The current paragraph is a list item nothing has been written to yet.</summary>
    bool _freshItem;
    /// <summary>Anchors waiting for the next paragraph, so a link to an empty place doesn't make a blank line.</summary>
    readonly List<string> _anchors = [];

    sealed record Context(TextStyle Style, string? Link, bool Pre, TextAlignment? Align, double Indent, int ListLevel, bool Rtl, int Depth);

    static readonly Context Start = new(TextStyle.Plain, null, false, null, 0, 0, false, 0);

    /// <summary>Where a page's pictures come from, and what its links mean.</summary>
    /// <param name="Anchor">Makes an id in this page unique in the whole document (a book has many pages).</param>
    /// <param name="Link">Turns an address into a link within the document, if that's where it leads.</param>
    sealed record Source(Func<string, byte[]?> Picture, Func<string, string> Anchor, Func<string, string?> Link, Budget Budget)
    {
        public static Source Of(Func<string, byte[]?> picture) => new(picture, id => id, href => href is ['#', _, ..] ? "#" + Unescape(href[1..]) : null, new Budget());
    }

    sealed class Budget
    {
        long _left = 512L * 1024 * 1024;
        public bool Take(long bytes) => (_left -= bytes) >= 0;
        public int Cells;
    }

    /// <summary>Footnote bodies found in the page (Markdown and EPUB style), keyed by the id links point at.</summary>
    sealed class NoteState
    {
        public readonly Dictionary<string, IElement> Bodies = [];
        public int Count;
    }

    HtmlReader(List<DBlock> output, Source source, double maxImageWidth, NoteState notes, int tableDepth = 0)
    {
        _output = output;
        _source = source;
        _maxImageWidth = maxImageWidth;
        _notes = notes;
        _tableDepth = tableDepth;
    }

    // ------------------------------------------------------------------ entry points

    public static DDocument ReadHtml(string path) => DocumentFiles.Open(path, DocumentShape.Html, () => ReadPage(path));

    public static DDocument ReadMarkdown(string path) => DocumentFiles.Open(path, DocumentShape.Unknown, () => ReadMarkdownText(path));

    public static DDocument ReadEpub(string path) => DocumentFiles.Open(path, DocumentShape.Epub, () => ReadBook(path));

    internal static DDocument ReadPage(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return FromHtml(Decode(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path), src => DocumentPictures.FromFolder(dir, src));
    }

    /// <summary>A page of HTML that's part of something else.</summary>
    internal static DDocument FromHtml(string html, string? title, Func<string, byte[]?> picture)
    {
        var doc = new DDocument { Title = title };
        Parse(doc, html, Source.Of(picture), pageBreak: false);
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    static DDocument ReadMarkdownText(string path)
    {
        var markdown = TextDecoding.Decode(File.ReadAllBytes(path));
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        string html;
        // Lines indented by hundreds of columns aren't Markdown anyone wrote, and would take minutes.
        if (DeepLine().Count(markdown) > 20) return PlainText.Read(path);
        try { html = Markdown.ToHtml(markdown, pipeline); }
        catch (ArgumentException ex)
        {
            // Nested beyond what Markdig will follow: the text is still worth having.
            Log.Write($"Read {path} as plain text: {ex.Message}");
            return PlainText.Read(path);
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path), Font = "Calibri", FontSize = 11 };
        Parse(doc, html, Source.Of(src => DocumentPictures.FromFolder(dir, src)), pageBreak: false);
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    [GeneratedRegex(@"^[ \t>]{500,}", RegexOptions.Multiline)]
    private static partial Regex DeepLine();

    // ------------------------------------------------------------------ e-books

    internal static DDocument ReadBook(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) byName.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        byte[]? Bytes(string name, bool media = false)
        {
            // Names should be percent-encoded in links and plain in the zip, but books get either wrong.
            if (!byName.TryGetValue(name, out var entry) && !byName.TryGetValue(Unescape(name), out entry)) return null;
            using var s = PackageResources.Open(entry, media);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        XDocument? Xml(string name)
        {
            if (Bytes(name) is not { } bytes) return null;
            using var ms = new MemoryStream(bytes);
            return PackageResources.LoadXml(ms);
        }

        if (Protected(Xml("META-INF/encryption.xml"))) throw new ConversionException("This e-book is copy-protected (DRM), so it can't be converted.");
        var opfPath = Xml("META-INF/container.xml")?.Descendants().Where(e => e.Name.LocalName == "rootfile").Select(e => (string?)e.Attribute("full-path"))
                          .FirstOrDefault(p => !string.IsNullOrEmpty(p) && Path.GetExtension(p).Equals(".opf", StringComparison.OrdinalIgnoreCase))
                      ?? byName.Keys.FirstOrDefault(k => k.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                      ?? throw new ConversionException("This EPUB has no table of contents.");
        var opf = Xml(Combine("", opfPath)) ?? throw new ConversionException("This EPUB is damaged.");
        var opfDir = Folder(Combine("", opfPath));

        var manifest = new Dictionary<string, (string path, string type)>(StringComparer.Ordinal);
        foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
            if ((string?)item.Attribute("id") is { } id && (string?)item.Attribute("href") is { } href)
                manifest.TryAdd(id, (Combine(opfDir, href), (string?)item.Attribute("media-type") ?? ""));
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => (string?)e.Attribute("idref"))
            .Where(id => id != null && manifest.ContainsKey(id)).Select(id => manifest[id!]).ToList();
        // Without a reading order, the pages in the order they're listed.
        if (spine.Count == 0) spine = manifest.Values.Where(m => m.type.Contains("html", StringComparison.OrdinalIgnoreCase)).ToList();

        var doc = new DDocument { Title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(path) };
        // Each page has its own ids; links from page to page become links within the document.
        var pages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (page, _) in spine) pages.TryAdd(Unescape(page), pages.Count + 1);
        var budget = new Budget();
        bool first = true;
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chapter, type) in spine)
        {
            if (!read.Add(chapter) || Bytes(chapter, media: type.StartsWith("image/", StringComparison.Ordinal)) is not { } bytes) continue;
            var dir = Folder(chapter);
            int number = pages[Unescape(chapter)];
            string? Link(string href)
            {
                if (Uri.TryCreate(href, UriKind.Absolute, out _)) return null;
                int hash = href.IndexOf('#');
                var file = hash < 0 ? href : href[..hash];
                var fragment = hash < 0 ? "" : Unescape(href[(hash + 1)..]);
                int target = file.Length == 0 ? number : pages.GetValueOrDefault(Unescape(Combine(dir, file.Split('?')[0])));
                return target == 0 ? "" : $"#p{target}{(fragment.Length > 0 ? "-" + fragment : "")}"; // other files aren't in the document
            }
            byte[]? Picture(string src) => src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DocumentPictures.FromDataUri(src)
                : src.Contains(':') ? null : Bytes(Combine(dir, src.Split('#', '?')[0]), media: true);
            var source = new Source(Picture, id => $"p{number}{(id.Length > 0 ? "-" + id : "")}", Link, budget);

            int before = doc.Blocks.Count;
            if (type.StartsWith("image/", StringComparison.Ordinal) && !type.Contains("svg"))
            {
                // A picture as a page of its own.
                var page = new DParagraph { PageBreakBefore = !first };
                if (DocumentPictures.Embeddable(bytes) is { } picture) page.Inlines.Add(Sized(picture, null, null, doc.ContentWidth));
                if (page.Inlines.Count > 0) doc.Blocks.Add(page);
            }
            else Parse(doc, Xhtml(Decode(bytes)), source, pageBreak: !first);
            if (doc.Blocks.Count > before) first = false;
        }
        if (doc.Blocks.Count == 0) throw new ConversionException("This e-book has no pages that can be read.");
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    /// <summary>Whether more than the fonts is encrypted.</summary>
    static bool Protected(XDocument? encryption) => encryption != null && encryption.Descendants().Where(e => e.Name.LocalName == "EncryptedData").Any(e =>
        (string?)e.Elements().FirstOrDefault(m => m.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm") is not ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC"));

    static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); } catch (UriFormatException) { return s; }
    }

    static string Combine(string dir, string relative)
    {
        relative = relative.Replace('\\', '/');
        var parts = (relative.StartsWith('/') ? relative : dir.Length > 0 ? dir + "/" + relative : relative).Split('/').ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) parts.RemoveAt(--i); i--; }
            else if (parts[i] is "." or "") parts.RemoveAt(i--);
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// XHTML as HTML. In XHTML any tag may close itself, but to an HTML parser "&lt;title/&gt;" or "&lt;a id='x'/&gt;"
    /// is left open and swallows what follows; and CDATA is a comment.
    /// </summary>
    internal static string Xhtml(string text)
    {
        // Some books give the XHTML tags a prefix.
        var prefix = Regex.Match(text.Length > 4096 ? text[..4096] : text, @"xmlns:([A-Za-z]\w*)\s*=\s*[""']http://www\.w3\.org/1999/xhtml[""']");
        if (prefix.Success && Regex.IsMatch(text, $"<{prefix.Groups[1].Value}:html[\\s>]")) text = Regex.Replace(text, $"(</?){prefix.Groups[1].Value}:", "$1");
        return XhtmlTag().Replace(text, m =>
        {
            if (m.Groups["cdata"].Success) return System.Net.WebUtility.HtmlEncode(m.Groups["cdata"].Value);
            if (!m.Groups["name"].Success) return m.Value; // a comment
            var name = m.Groups["name"].Value;
            return Void.Contains(name) || Foreign.Contains(name) ? m.Value : $"<{name}{m.Groups["attrs"].Value}></{name}>";
        });
    }

    [GeneratedRegex(@"<!--.*?-->|<!\[CDATA\[(?<cdata>.*?)\]\]>|<(?<name>[A-Za-z][\w:.\-]*)(?<attrs>(?:\s[^<>""']*(?:""[^""]*""[^<>""']*|'[^']*'[^<>""']*)*)?)/>", RegexOptions.Singleline)]
    private static partial Regex XhtmlTag();

    static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };
    // Drawings and formulas keep their own rules inside HTML.
    static readonly HashSet<string> Foreign = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "use", "image", "stop", "svg", "g", "mspace", "mprescripts", "none", "maligngroup", "malignmark",
    };

    // ------------------------------------------------------------------ web archives (.mht, and Word exports that are one)

    internal static DDocument ReadWebArchive(string path)
    {
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var parts = new List<(string type, string location, byte[] data)>();
        void Split(string text, int depth)
        {
            int end = Math.Max(0, Regex.Match(text, @"\r?\n\r?\n").Index);
            var headers = Regex.Replace(text[..end], @"\r?\n[ \t]+", " ");
            string Header(string name) => Regex.Match(headers, $@"^{name}:\s*(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value;
            var type = Header("Content-Type");
            var body = text[end..].TrimStart('\r', '\n');
            var boundary = Regex.Match(type, @"boundary\s*=\s*""?([^"";]+)", RegexOptions.IgnoreCase);
            if (type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && boundary.Success && depth < 4)
            {
                foreach (var piece in body.Split("--" + boundary.Groups[1].Value.Trim()).Skip(1).Take(2000))
                    if (!piece.StartsWith("--", StringComparison.Ordinal)) Split(piece.TrimStart('\r', '\n'), depth + 1);
                return;
            }
            byte[] data = Header("Content-Transfer-Encoding").ToLowerInvariant() switch
            {
                "base64" => DocumentPictures.FromDataUri("data:;base64," + body) ?? [],
                "quoted-printable" => Encoding.Latin1.GetBytes(Regex.Replace(Regex.Replace(body, @"=[ \t]*\r?\n", ""), "=([0-9A-Fa-f]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString())),
                _ => Encoding.Latin1.GetBytes(body),
            };
            var location = Header("Content-Location");
            parts.Add((type, location.Length > 0 ? location : "cid:" + Header("Content-ID").Trim('<', '>'), data));
        }
        Split(raw, 0);
        var page = parts.FirstOrDefault(p => p.type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
        if (page.data == null) throw new ConversionException("This web archive has no page in it.");
        byte[]? Picture(string src) => src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DocumentPictures.FromDataUri(src)
            : parts.FirstOrDefault(p => p.location == src || p.location.EndsWith("/" + src.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)).data;
        var charset = Regex.Match(page.type, @"charset\s*=\s*""?([\w\-]+)", RegexOptions.IgnoreCase);
        return FromHtml(Decode(page.data, charset.Success ? charset.Groups[1].Value : null), Path.GetFileNameWithoutExtension(path), Picture);
    }

    // ------------------------------------------------------------------ text encoding

    /// <summary>The page's text: by its byte order mark, then what it says about itself, then a guess.</summary>
    internal static string Decode(byte[] bytes, string? declared = null)
    {
        if (TextDecoding.HasMark(bytes) || TextDecoding.LooksLikeUtf16(bytes)) return TextDecoding.Decode(bytes);
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        declared ??= (XmlEncoding().Match(head) is { Success: true } xml ? xml : MetaCharset().Match(head)) is { Success: true } m ? m.Groups[1].Value : null;
        if (declared != null && Named(declared) is { } encoding) return encoding.GetString(bytes);
        return TextDecoding.Decode(bytes);

        static Encoding? Named(string name)
        {
            name = name.ToLowerInvariant();
            // What browsers do: these are read as their Windows supersets. (UTF-8 is checked rather than trusted.)
            name = name switch { "iso-8859-1" or "latin1" or "us-ascii" or "ascii" => "windows-1252", "iso-8859-9" => "windows-1254", "x-sjis" or "sjis" or "windows-31j" => "shift_jis", "gb2312" or "x-gbk" => "gbk", "ks_c_5601-1987" => "euc-kr", _ => name };
            if (name.StartsWith("utf", StringComparison.Ordinal) || name.StartsWith("unicode", StringComparison.Ordinal)) return null;
            TextDecoding.EnsureRegistered();
            try { return Encoding.GetEncoding(name); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        }
    }

    [GeneratedRegex(@"^\s*<\?xml[^>]*\bencoding\s*=\s*[""']([\w\-]+)")]
    private static partial Regex XmlEncoding();

    [GeneratedRegex(@"<meta\b[^>]*?\bcharset\s*=\s*[""']?\s*([\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    // ------------------------------------------------------------------ parsing

    static void Parse(DDocument doc, string html, Source source, bool pageBreak)
    {
        // A page saved as XHTML closes tags the XML way.
        if (html.AsSpan(0, Math.Min(html.Length, 2048)).Contains("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) || html.AsSpan().TrimStart().StartsWith("<?xml", StringComparison.Ordinal))
            html = Xhtml(html);
        var dom = new HtmlParser().ParseDocument(html);
        var notes = new NoteState { Count = doc.Notes().Count(n => !n.Endnote) };
        CollectNotes(dom, notes);
        var reader = new HtmlReader(doc.Blocks, source, doc.ContentWidth, notes) { _breakBeforeNext = pageBreak };
        var body = dom.Body ?? (IElement?)dom.DocumentElement;
        if (body == null) return;
        reader._anchors.Add(source.Anchor(""));
        var ctx = Start with { Rtl = dom.DocumentElement?.GetAttribute("dir")?.Trim().ToLowerInvariant() == "rtl" };
        if (body.Id is { Length: > 0 } id) reader._anchors.Add(source.Anchor(id));
        reader.Children(body, reader.ApplyStyle(body, ctx));
        reader.EndParagraph();
        // Links to the very end of the page.
        if (reader._anchors.Count > 0 && doc.Blocks.OfType<DParagraph>().LastOrDefault() is { } last)
            last.Inlines.AddRange(reader._anchors.Where(a => a.Length > 0).Select(a => new DAnchor(a)));
    }

    /// <summary>
    /// Lifts footnote bodies out of the page so they become real notes: Markdown's "footnotes" section
    /// (list items fn:1, fn:2…) and EPUB's &lt;aside epub:type="footnote"&gt;.
    /// </summary>
    static void CollectNotes(AngleSharp.Html.Dom.IHtmlDocument dom, NoteState notes)
    {
        static bool Is(IElement e, params string[] types) =>
            (e.GetAttribute("epub:type") ?? e.GetAttribute("role") ?? "").Split(' ').Any(t => types.Contains(t.Replace("doc-", "")));
        var targets = dom.All.Where(e => e.LocalName == "a").Select(e => e.GetAttribute("href")).Where(h => h is ['#', _, ..]).Select(h => Unescape(h![1..])).ToHashSet(StringComparer.Ordinal);
        foreach (var section in dom.All.Where(e => e.ClassList.Contains("footnotes") || Is(e, "footnotes", "endnotes", "rearnotes")).ToList())
        {
            // Only notes something on this page refers to; a book's page of notes belongs where it is.
            var items = section.QuerySelectorAll("[id]").Where(i => i.LocalName is "li" or "aside" or "div" or "p" or "section" && targets.Contains(i.Id!)).ToList();
            foreach (var item in items) notes.Bodies.TryAdd(item.Id!, item);
            if (items.Count > 0 && section.LocalName is not ("body" or "html")) section.Remove();
        }
        foreach (var aside in dom.All.Where(e => e.Id != null && Is(e, "footnote", "endnote", "rearnote") && targets.Contains(e.Id)).ToList())
        {
            notes.Bodies.TryAdd(aside.Id!, aside);
            aside.Remove();
        }
    }

    void Note(IElement body, Context ctx)
    {
        var note = new DNote(++_notes.Count, false, ctx.Style with { Superscript = false });
        // Leave out the "back to text" links.
        foreach (var back in body.QuerySelectorAll("a").Where(a => a.ClassList.Contains("footnote-back-ref") || (a.GetAttribute("epub:type") ?? "").Contains("backlink") || (a.GetAttribute("role") ?? "").Contains("backlink") || a.TextContent.Trim() is "↩" or "↩FE0E").ToList())
            back.Remove();
        var reader = new HtmlReader(note.Blocks, _source, _maxImageWidth, _notes, _tableDepth);
        reader.Children(body, Start with { Rtl = ctx.Rtl, Depth = ctx.Depth + 1 });
        reader.EndParagraph();
        Paragraph(ctx).Inlines.Add(note);
        _freshItem = false;
    }

    // ------------------------------------------------------------------ walking

    static readonly HashSet<string> Skip =
    [
        "script", "style", "head", "title", "meta", "link", "base", "noscript", "noframes", "noembed", "template", "nav", "button", "select", "textarea", "datalist", "iframe", "frame",
        "object", "embed", "applet", "video", "audio", "canvas", "map", "annotation", "annotation-xml", "rp",
    ];
    static readonly HashSet<string> Blocks =
    [
        "p", "div", "section", "article", "header", "footer", "main", "aside", "figure", "figcaption", "address", "dl", "dt", "dd", "center", "form", "fieldset", "legend", "details", "summary",
        "hgroup", "search", "dialog", "caption", "option", "optgroup", "body", "menu", "dir",
    ];

    void Children(INode node, Context ctx)
    {
        foreach (var child in node.ChildNodes) Node(child, ctx);
    }

    void Node(INode node, Context ctx)
    {
        if (node is IText text)
        {
            AddText(text.Data, ctx);
            return;
        }
        if (node is not IElement e) return;
        var name = e.LocalName;
        if (Skip.Contains(name) || Hidden(e)) return;
        if (!DocumentLimits.CanGoDeeper(ctx.Depth, 400))
        {
            // Nested absurdly deep: keep the words.
            AddText(VisibleText(e), ctx);
            return;
        }

        if (e.Id is { Length: > 0 } id) _anchors.Add(_source.Anchor(id));
        if (name == "a" && e.GetAttribute("name") is { Length: > 0 } anchor && anchor != e.Id) _anchors.Add(_source.Anchor(anchor));
        ctx = ApplyStyle(e, ctx);
        switch (name)
        {
            case "br":
                Paragraph(ctx).Inlines.Add(new DLineBreak());
                _freshItem = false;
                return;
            case "wbr":
                return;
            case "img":
                Image(e.GetAttribute("src"), e, ctx);
                return;
            case "svg":
                // E-books often wrap the cover picture in a drawing.
                foreach (var picture in e.Descendants().OfType<IElement>().Where(d => d.LocalName is "image" or "img").Take(20).ToList())
                    Image(picture.GetAttribute("xlink:href") ?? picture.GetAttribute("href") ?? picture.Attributes.FirstOrDefault(a => a.LocalName == "href")?.Value ?? picture.GetAttribute("src"), picture, ctx);
                return;
            case "input":
                if (e.GetAttribute("type")?.ToLowerInvariant() is "checkbox" or "radio") AddText(e.HasAttribute("checked") ? "☑ " : "☐ ", ctx);
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                StartBlock(ctx);
                var heading = Paragraph(ctx);
                heading.Heading = name[1] - '0';
                heading.SpaceBefore = 12;
                heading.SpaceAfter = 6;
                Children(e, ctx);
                EndParagraph();
                return;
            case "ul" or "ol" or "menu" or "dir":
                EndParagraph();
                List(e, ctx, name == "ol");
                return;
            case "li":
                // Outside any list.
                EndParagraph();
                Children(e, ctx);
                EndParagraph();
                return;
            case "table":
                EndParagraph();
                Table(e, ctx);
                return;
            case "blockquote":
                EndParagraph();
                Children(e, ctx with { Indent = ctx.Indent + 36, Style = ctx.Style with { Italic = true } });
                EndParagraph();
                return;
            case "dd":
                EndParagraph();
                Children(e, ctx with { Indent = ctx.Indent + 36 });
                EndParagraph();
                return;
            case "pre" or "listing" or "xmp" or "plaintext":
                EndParagraph();
                Children(e, ctx with { Pre = true, Style = ctx.Style with { Font = "Consolas" } });
                EndParagraph();
                return;
            case "hr":
                EndParagraph();
                var rule = Paragraph(ctx);
                rule.Align = TextAlignment.Center;
                rule.Inlines.Add(new DText("⁂", TextStyle.Plain));
                EndParagraph();
                return;
            case "q":
                AddText("“", ctx);
                Children(e, ctx);
                AddText("”", ctx);
                return;
            case "rt":
                // The reading of the characters before it.
                AddText("(", ctx);
                Children(e, ctx);
                AddText(")", ctx);
                return;
            case "span" or "div" when e.ClassList.Contains("math"):
                // Markdown formulas come as "\(…\)", or "\[…\]" on a line of their own.
                var math = e.TextContent.Trim();
                if (math.Length >= 4 && math[0] == '\\' && math[^2] == '\\' && math[1] is '(' or '[') math = math[2..^2].Trim();
                if (name == "div") StartBlock(ctx);
                AddText(math, ctx);
                if (name == "div") EndParagraph();
                return;
            case "math":
                AddText(Formula(e, 0), ctx);
                return;
            case "a":
                var href = e.GetAttribute("href");
                if (href is ['#', .. var target] && _notes.Bodies.Remove(Unescape(target), out var noteBody)) { Note(noteBody, ctx); return; }
                string? link = href == null ? ctx.Link : _source.Link(href) is { } inside ? inside.Length > 0 ? inside : null : href.StartsWith('#') ? null : Links.ForDocument(href);
                Children(e, ctx with { Link = link });
                return;
        }

        bool block = Blocks.Contains(name);
        if (block) StartBlock(ctx);
        Children(e, ctx);
        if (block) EndParagraph();
    }

    static bool Hidden(IElement e)
    {
        if (e.HasAttribute("hidden") && e.LocalName != "details" || e.LocalName == "dialog" && !e.HasAttribute("open")) return true;
        if (e.GetAttribute("style") is not { Length: > 0 } css) return false;
        css = css.Replace(" ", "").ToLowerInvariant();
        return css.Contains("display:none") || css.Contains("visibility:hidden") || css.Contains("mso-hide:all");
    }

    /// <summary>An element's words without following it down the stack.</summary>
    static string VisibleText(IElement root)
    {
        var sb = new StringBuilder();
        for (INode? n = root.FirstChild; n != null && n != root;)
        {
            if (n is IText t) sb.Append(t.Data);
            bool inside = n is IElement e && !Skip.Contains(e.LocalName) && !Hidden(e);
            if (inside && Blocks.Contains(((IElement)n).LocalName)) sb.Append(' ');
            if (inside && n.FirstChild != null) { n = n.FirstChild; continue; }
            while (n != null && n != root && n.NextSibling == null) n = n.Parent;
            if (n == null || n == root) break;
            n = n.NextSibling;
        }
        return sb.ToString();
    }

    /// <summary>A formula as a line of text: a/b for fractions, x^2 for powers.</summary>
    static string Formula(IElement e, int depth)
    {
        if (depth > 60) return e.TextContent.Trim();
        string Part(int i) => i < e.Children.Length ? Formula(e.Children[i], depth + 1) : "";
        static string Group(string s) => s.Length > 1 && !s.All(char.IsLetterOrDigit) ? $"({s})" : s;
        switch (e.LocalName)
        {
            case "annotation" or "annotation-xml": return "";
            case "mfrac": return $"{Group(Part(0))}/{Group(Part(1))}";
            case "msup" or "mover": return $"{Part(0)}^{Group(Part(1))}";
            case "msub" or "munder": return $"{Part(0)}_{Group(Part(1))}";
            case "msubsup" or "munderover": return $"{Part(0)}_{Group(Part(1))}^{Group(Part(2))}";
            case "msqrt": return $"√{Group(string.Concat(e.Children.Select(c => Formula(c, depth + 1))))}";
            case "mroot": return $"{Part(1)}√{Group(Part(0))}";
            case "mi" or "mn" or "mo" or "mtext" or "ms": return WhitespaceRegex().Replace(e.TextContent, " ").Trim();
            case "mtr": return string.Join(", ", e.Children.Select(c => Formula(c, depth + 1)));
            case "mtable": return "[" + string.Join("; ", e.Children.Select(c => Formula(c, depth + 1))) + "]";
        }
        return e.Children.Length == 0 ? e.TextContent.Trim() : string.Concat(e.Children.Select(c => Formula(c, depth + 1)));
    }

    Context ApplyStyle(IElement e, Context ctx)
    {
        var s = ctx.Style;
        switch (e.LocalName)
        {
            case "b" or "strong" or "th" or "dt": s = s with { Bold = true }; break;
            case "i" or "em" or "cite" or "dfn" or "var": s = s with { Italic = true }; break;
            case "u" or "ins": s = s with { Underline = true }; break;
            case "s" or "strike" or "del": s = s with { Strike = true }; break;
            case "sup": s = s with { Superscript = true, Subscript = false }; break;
            case "sub": s = s with { Subscript = true, Superscript = false }; break;
            case "code" or "kbd" or "samp" or "tt": s = s with { Font = "Consolas" }; break;
            case "mark": s = s with { Highlight = Colors.Yellow }; break;
            case "small": s = s with { Size = Math.Max(4, (s.Size ?? 11) * 0.85) }; break;
            case "big": s = s with { Size = Math.Min(200, (s.Size ?? 11) * 1.2) }; break;
            case "font":
                if (e.GetAttribute("color") is { } named && CssColor(named) is { } fc) s = s with { Color = fc };
                if (e.GetAttribute("face") is { } face && FontName(face) is { } faceName) s = s with { Font = faceName };
                break;
        }
        var align = ctx.Align;
        bool rtl = ctx.Rtl, pre = ctx.Pre;
        if (e.GetAttribute("dir") is { } dir)
            rtl = dir.Trim().ToLowerInvariant() switch { "rtl" => true, "ltr" => false, "auto" => Bidi.StartsRightToLeft(e.TextContent), _ => rtl };
        if (e.GetAttribute("align") is { } a && e.LocalName is not ("img" or "table" or "iframe")) align = ParseAlign(a, align, rtl);
        else if (e.LocalName == "center") align = TextAlignment.Center;

        if (e.GetAttribute("style") is { } css)
            foreach (var rule in css.Split(';'))
            {
                var kv = rule.Split(':', 2);
                if (kv.Length != 2) continue;
                var value = kv[1].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "font-weight": s = s with { Bold = value is "bold" or "bolder" || (int.TryParse(value, out var w) && w >= 600) }; break;
                    case "font-style": s = s with { Italic = value is "italic" or "oblique" }; break;
                    case "text-decoration" or "text-decoration-line":
                        if (value.Contains("underline")) s = s with { Underline = true };
                        if (value.Contains("line-through")) s = s with { Strike = true };
                        if (value is "none") s = s with { Underline = false, Strike = false };
                        break;
                    case "vertical-align" when e.LocalName is "span" or "a" or "font":
                        if (value == "super") s = s with { Superscript = true, Subscript = false };
                        else if (value == "sub") s = s with { Subscript = true, Superscript = false };
                        break;
                    case "color": if (CssColor(value) is { } color) s = s with { Color = color }; break;
                    case "background-color" or "background":
                        // A block's background is the page's business; text keeps its highlight.
                        if (CssColor(value) is { } bg && bg != Colors.White && e.LocalName is not ("td" or "th" or "tr" or "table" or "body")) s = s with { Highlight = bg };
                        break;
                    case "font-size": if (CssSize(value, s.Size ?? 11) is double size && size is > 3 and < 200) s = s with { Size = size }; break;
                    case "font-family": s = s with { Font = FontName(value) ?? s.Font }; break;
                    case "direction": rtl = value == "rtl" || value != "ltr" && rtl; break;
                    case "text-align": align = ParseAlign(value, align, rtl); break;
                    case "white-space": pre = value is "pre" or "pre-wrap" or "break-spaces"; break;
                    case "page-break-before" or "break-before": if (value is "always" or "page" or "left" or "right") { EndParagraph(); _breakBeforeNext = true; } break;
                }
            }
        return ctx with { Style = s, Align = align, Rtl = rtl, Pre = pre, Depth = ctx.Depth + 1 };
    }

    static double? CssSize(string value, double parent)
    {
        if (value.EndsWith("rem")) (value, parent) = (value[..^3] + "em", 11);
        foreach (var (unit, factor) in new[] { ("em", 1.0), ("%", 0.01), ("ex", 0.5) })
            if (value.EndsWith(unit) && double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return parent * n * factor;
        return value switch
        {
            "xx-small" => 7, "x-small" => 7.5, "small" => 10, "medium" => 12, "large" => 13.5, "x-large" => 18, "xx-large" => 24,
            "smaller" => parent / 1.2, "larger" => parent * 1.2, _ => Units.ParseLength(value),
        };
    }

    static string? FontName(string css)
    {
        foreach (var part in css.Split(','))
        {
            var name = part.Trim().Trim('\'', '"').Trim();
            if (name.Length is 0 or > 31 || name.Any(c => char.IsControl(c) || c is '<' or '>' or '&' or ';' or '{' or '}')) continue;
            switch (name.ToLowerInvariant())
            {
                case "monospace" or "ui-monospace": return "Consolas";
                case "serif" or "ui-serif": return "Cambria";
                case "sans-serif" or "system-ui" or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont" or "inherit" or "initial" or "unset" or "cursive" or "fantasy": continue;
            }
            return name == name.ToLowerInvariant() ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name) : name;
        }
        return null;
    }

    static TextAlignment? ParseAlign(string value, TextAlignment? fallback, bool rtl) => value.Trim().ToLowerInvariant() switch
    {
        "center" or "middle" or "-webkit-center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        "left" => TextAlignment.Left,
        "end" => rtl ? TextAlignment.Left : TextAlignment.Right,
        "start" => rtl ? TextAlignment.Right : TextAlignment.Left,
        "justify" => TextAlignment.Justify,
        _ => fallback,
    };

    internal static Color? CssColor(string value)
    {
        value = value.Trim();
        if (value.StartsWith('#'))
        {
            if (value.Length is 4 or 5) value = "#" + string.Concat(value[1..].Select(c => $"{c}{c}"));
            if (value.Length == 9) value = value.EndsWith("00", StringComparison.Ordinal) ? "" : value[..7]; // see-through, or near enough
            return Units.ParseHex(value);
        }
        var rgb = RgbRegex().Match(value);
        if (rgb.Success) return Color.FromRgb((byte)Math.Min(255, int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture)),
            (byte)Math.Min(255, int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture)), (byte)Math.Min(255, int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture)));
        // Only plain colour names: the converter also understands "ContextColor" and a path, and would open that file.
        if (value.Length is 0 or > 24 || !value.All(char.IsAsciiLetter) || value is "transparent" or "inherit" or "initial" or "none" or "unset" or "currentcolor") return value.Length == 6 ? Units.ParseHex(value) : null;
        try { return (Color)ColorConverter.ConvertFromString(value); } catch (FormatException) { return null; }
    }

    [GeneratedRegex(@"rgba?\(\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*(?:\)|[,/]\s*(?!0(?:\.0*)?%?\s*\)))")]
    private static partial Regex RgbRegex();

    // ------------------------------------------------------------------ paragraphs and text

    DParagraph Paragraph(Context ctx)
    {
        if (_current != null) return _current;
        _current = new DParagraph
        {
            Align = ctx.Align ?? (ctx.Rtl ? TextAlignment.Right : TextAlignment.Left),
            RightToLeft = ctx.Rtl,
            IndentLeft = ctx.Indent,
            SpaceAfter = 8,
            PageBreakBefore = _breakBeforeNext,
        };
        _preParagraph = ctx.Pre;
        _breakBeforeNext = false;
        _output.Add(_current);
        foreach (var anchor in _anchors) _current.Inlines.Add(new DAnchor(anchor));
        _anchors.Clear();
        return _current;
    }

    /// <summary>A block inside a list item that hasn't any text yet goes on the item's own line.</summary>
    void StartBlock(Context ctx)
    {
        if (!_freshItem || _current == null) { EndParagraph(); return; }
        if (ctx.Align is { } align) _current.Align = align;
    }

    void EndParagraph()
    {
        _freshItem = false;
        if (_current == null) return;
        var inlines = _current.Inlines;
        // Trim whitespace left at the ends by collapsing.
        if (!_preParagraph && inlines.FirstOrDefault(i => i is not DAnchor) is DText first) first.Text = first.Text.TrimStart(' ');
        if (!_preParagraph && inlines.LastOrDefault(i => i is not DAnchor) is DText last) last.Text = last.Text.TrimEnd(' ');
        inlines.RemoveAll(i => i is DText { Text.Length: 0 });
        // A line break at the very end only ends the line; on its own it's an empty line.
        bool blank = inlines.Count > 0 && inlines.All(i => i is DLineBreak or DAnchor) && inlines.Any(i => i is DLineBreak);
        if (inlines.FindLastIndex(i => i is not DAnchor) is >= 0 and var end && inlines[end] is DLineBreak) inlines.RemoveAt(end);
        bool empty = inlines.All(i => i is DAnchor);
        if (empty && !blank && _current.ListLabel == null && _current.Heading == 0)
        {
            _output.Remove(_current);
            _anchors.InsertRange(0, inlines.OfType<DAnchor>().Select(a => a.Name));
            if (_current.PageBreakBefore) _breakBeforeNext = true;
        }
        _current = null;
    }

    void AddText(string raw, Context ctx)
    {
        if (ctx.Pre)
        {
            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length == 1 && lines[0].Length == 0) return;
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) Paragraph(ctx).Inlines.Add(new DLineBreak());
                if (lines[i].Length > 0) Append(Paragraph(ctx), lines[i], ctx);
            }
            _freshItem = false;
            return;
        }
        var text = WhitespaceRegex().Replace(raw, " ");
        if (text.Length == 0) return;
        if (text == " " && (_current == null || _freshItem)) return;
        var p = Paragraph(ctx);
        _freshItem = false;
        // Collapse a space after a space (or at the start of the paragraph).
        if (text.StartsWith(' ') && p.Inlines.LastOrDefault(i => i is not DAnchor) is null or DText { Text: [.., ' '] } or DLineBreak)
            text = text.TrimStart(' ');
        if (text.Length > 0) Append(p, text, ctx);
    }

    void Append(DParagraph p, string text, Context ctx)
    {
        foreach (var anchor in _anchors) p.Inlines.Add(new DAnchor(anchor));
        _anchors.Clear();
        if (p.Inlines.LastOrDefault() is DText last && last.Style == ctx.Style && last.Link == ctx.Link) last.Text += text;
        else p.Inlines.Add(new DText(text, ctx.Style, ctx.Link));
    }

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex WhitespaceRegex();

    void Image(string? src, IElement e, Context ctx)
    {
        if (string.IsNullOrWhiteSpace(src) || _source.Picture(src.Trim()) is not { } data || !_source.Budget.Take(data.Length)) return;
        if (DocumentPictures.Embeddable(data) is not { } picture) return;
        double? Pixels(string name)
        {
            var css = Regex.Match(e.GetAttribute("style") ?? "", $@"(?:^|;)\s*{name}\s*:\s*([\d.]+\s*(?:px|pt|in|cm|mm|em)?)\s*(?:;|$)", RegexOptions.IgnoreCase);
            if (css.Success && Units.ParseLength(css.Groups[1].Value) is > 0 and double points) return points;
            return e.GetAttribute(name) is { } value && !value.Contains('%') && double.TryParse(value.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px is > 0 and < 1e6 ? px * 0.75 : null;
        }
        var image = Sized(picture, Pixels("width"), Pixels("height"), _maxImageWidth);
        if (e.GetAttribute("alt") is { Length: > 0 and < 2000 } alt) image.Alt = alt;
        Paragraph(ctx).Inlines.Add(image);
        _freshItem = false;
    }

    /// <summary>A picture at the size asked for (or its own), in proportion, and no wider than the page.</summary>
    static DImage Sized((byte[] data, string ext) picture, double? width, double? height, double max)
    {
        var (w, h) = DocxReader.ImageSize(picture.data);
        double ratio = h / Math.Max(1, w);
        if (width is { } aw) (w, h) = (aw, height ?? aw * ratio);
        else if (height is { } ah && ratio > 0) (w, h) = (ah / ratio, ah);
        if (w > max) (w, h) = (max, h * max / w);
        return new DImage(picture.data, picture.ext, w, h);
    }

    /// <summary>The picture format from its first bytes (SVG and other formats aren't embeddable).</summary>
    internal static string? ImageExt(byte[] d) => d.Length < 8 ? null
        : d[0] == 0x89 && d[1] == 'P' ? "png"
        : d[0] == 0xFF && d[1] == 0xD8 ? "jpeg"
        : d[0] == 'G' && d[1] == 'I' && d[2] == 'F' ? "gif"
        : d[0] == 'B' && d[1] == 'M' ? "bmp"
        : (d[0] == 'I' && d[1] == 'I' && d[2] == 42) || (d[0] == 'M' && d[1] == 'M' && d[3] == 42) ? "tiff"
        : null;

    // ------------------------------------------------------------------ lists

    void List(IElement list, Context ctx, bool ordered)
    {
        int count = list.Children.Count(c => c.LocalName == "li");
        bool reversed = ordered && list.HasAttribute("reversed");
        int number = int.TryParse(list.GetAttribute("start"), out var start) ? start : reversed ? count : 1;
        var type = CssListType(list) ?? list.GetAttribute("type")?.Trim();
        int level = ctx.ListLevel;
        double indent = ctx.Indent + (level == 0 ? 36 : 18);
        bool any = false;
        foreach (var child in list.ChildNodes)
        {
            if (child is IElement { LocalName: "li" } item)
            {
                if (Hidden(item)) continue;
                EndParagraph();
                if (item.Id is { Length: > 0 } id) _anchors.Add(_source.Anchor(id));
                if (ordered && int.TryParse(item.GetAttribute("value"), out var value)) number = value;
                var itemType = CssListType(item) ?? item.GetAttribute("type")?.Trim() ?? type;
                // A marker our own HTML writes out, or that the page draws itself.
                var written = item.FirstElementChild is { LocalName: "span" } span && span.ClassList.Contains("marker") && item.FirstChild == span ? span : null;
                string? label = written?.TextContent.Trim() ?? (itemType == "none" ? null
                    : ordered && itemType is not ("disc" or "circle" or "square") ? Number(number, itemType)
                    : itemType switch { "disc" => "•", "circle" => "◦", "square" => "▪", "decimal" or "1" => Number(number, "1"), _ => (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" } });
                number += reversed ? -1 : 1;
                var inner = ApplyStyle(item, ctx with { ListLevel = level + 1, Indent = indent });
                var p = Paragraph(inner);
                p.IndentLeft = indent;
                p.SpaceAfter = 2;
                if (label != null)
                {
                    p.List = ordered || written != null ? ListKind.Number : ListKind.Bullet;
                    p.ListLevel = Math.Min(level, 30);
                    p.ListLabel = label;
                    p.IndentFirst = -18;
                }
                _freshItem = true;
                if (DocumentLimits.CanGoDeeper(inner.Depth, 400))
                    foreach (var part in item.ChildNodes)
                    {
                        if (part != written) Node(part, inner);
                    }
                else AddText(VisibleText(item), inner);
                EndParagraph();
                any = true;
            }
            else if (child is IElement { LocalName: "ul" or "ol" or "menu" or "dir" } nested)
            {
                // A list straight inside a list (not allowed, but common) belongs to the item before it.
                if (Hidden(nested)) continue;
                EndParagraph();
                var inner = ApplyStyle(nested, ctx with { ListLevel = level + 1, Indent = indent });
                if (DocumentLimits.CanGoDeeper(inner.Depth, 400)) List(nested, inner, nested.LocalName == "ol");
                else AddText(VisibleText(nested), inner);
            }
            else Node(child, ctx with { Indent = indent, Depth = ctx.Depth + 1 });
        }
        EndParagraph();
        if (any && _output.LastOrDefault() is DParagraph lastItem) lastItem.SpaceAfter = 8;
    }

    static string? CssListType(IElement e)
    {
        if (e.GetAttribute("style") is not { } css) return null;
        var m = ListTypeRegex().Match(css);
        return !m.Success ? null : m.Groups[1].Value.ToLowerInvariant() switch
        {
            "lower-alpha" or "lower-latin" => "a", "upper-alpha" or "upper-latin" => "A", "lower-roman" => "i", "upper-roman" => "I",
            "decimal" => "1", "decimal-leading-zero" => "01", "none" => "none", "disc" => "disc", "circle" => "circle", "square" => "square", _ => null,
        };
    }

    [GeneratedRegex(@"list-style(?:-type)?\s*:\s*[^;]*?\b(lower-alpha|lower-latin|upper-alpha|upper-latin|lower-roman|upper-roman|decimal-leading-zero|decimal|none|disc|circle|square)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListTypeRegex();

    static string Number(int n, string? type) => type switch
    {
        "a" => ListLabels.Format(n, "lowerLetter") + ".",
        "A" => ListLabels.Format(n, "upperLetter") + ".",
        "i" => ListLabels.Format(n, "lowerRoman") + ".",
        "I" => ListLabels.Format(n, "upperRoman") + ".",
        "01" => n.ToString("00", CultureInfo.InvariantCulture) + ".",
        _ => n.ToString(CultureInfo.InvariantCulture) + ".",
    };

    // ------------------------------------------------------------------ tables

    void Table(IElement table, Context ctx)
    {
        foreach (var caption in table.Children.Where(c => c.LocalName == "caption" && !Hidden(c)).ToList())
        {
            Children(caption, ApplyStyle(caption, ctx with { Align = ctx.Align ?? TextAlignment.Center }));
            EndParagraph();
        }
        // The head comes first and the foot last, wherever they were written.
        var sections = table.Children.Where(c => c.LocalName is "thead" or "tbody" or "tfoot" && !Hidden(c)).ToList();
        var groups = sections.Where(s => s.LocalName == "thead").Concat(sections.Where(s => s.LocalName == "tbody")).Concat(sections.Where(s => s.LocalName == "tfoot"))
            .Select(s => (section: s, rows: s.Children.Where(r => r.LocalName == "tr" && !Hidden(r)).ToList())).ToList();
        if (table.Children.Any(r => r.LocalName == "tr")) groups.Insert(0, (table, table.Children.Where(r => r.LocalName == "tr" && !Hidden(r)).ToList()));

        if (_tableDepth >= DocumentLimits.TableDepth || !DocumentLimits.CanGoDeeper(ctx.Depth, 400))
        {
            // Too deep for a grid: what's in the cells, one after another.
            foreach (var td in groups.SelectMany(g => g.rows).SelectMany(r => r.Children))
            {
                EndParagraph();
                AddText(VisibleText(td), ctx);
            }
            EndParagraph();
            return;
        }

        var t = new DTable { Borders = true }; // web tables rely on CSS for lines; gridlines keep them readable on paper
        foreach (var (section, rows) in groups)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var tr = rows[i];
                var cells = tr.Children.Where(c => c.LocalName is "td" or "th" && !Hidden(c)).ToList();
                var row = new DRow { Header = section.LocalName == "thead" || cells.Count > 0 && cells.All(c => c.LocalName == "th") && t.Rows.All(r => r.Header) };
                var rowShade = Background(tr);
                foreach (var td in cells)
                {
                    if (row.Cells.Count >= DocumentLimits.Columns * 4 || ++_source.Budget.Cells > DocumentLimits.Cells) throw new ConversionException("This file has a table that's too big.");
                    var cell = new DCell { ColSpan = int.TryParse(td.GetAttribute("colspan"), out var span) ? Math.Clamp(span, 1, 1000) : 1, Shading = Background(td) ?? rowShade };
                    // A cell can't reach past the part of the table it's in; 0 means all the way down it.
                    if (int.TryParse(td.GetAttribute("rowspan"), out var down)) cell.RowSpan = Math.Clamp(down == 0 ? int.MaxValue : down, 1, rows.Count - i);
                    var inner = new HtmlReader(cell.Blocks, _source, Math.Max(36, _maxImageWidth / Math.Max(1, cells.Count)), _notes, _tableDepth + 1);
                    inner._anchors.AddRange(_anchors);
                    _anchors.Clear();
                    if (td.Id is { Length: > 0 } id) inner._anchors.Add(_source.Anchor(id));
                    inner.Children(td, ApplyStyle(td, ApplyStyle(tr, ctx with { Indent = 0, Align = null, ListLevel = 0 })));
                    inner.EndParagraph();
                    _anchors.AddRange(inner._anchors);
                    foreach (var p in cell.Blocks.OfType<DParagraph>()) p.SpaceAfter = 0;
                    row.Cells.Add(cell);
                }
                if (row.Cells.Count > 0 || t.Rows.Count > 0 && cells.Count == 0 && Reached(t)) t.Rows.Add(row);
            }
        }
        // Text between the rows ends up before the table, as browsers show it.
        if (t.Rows.Count > 0)
        {
            DocumentLimits.TrimColumns(t);
            _output.Add(t);
        }

        static Color? Background(IElement e)
        {
            if (e.GetAttribute("bgcolor") is { } bg && CssColor(bg.ToLowerInvariant()) is { } shade) return shade;
            var css = Regex.Match(e.GetAttribute("style") ?? "", @"(?:^|;)\s*background(?:-color)?\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
            return css.Success && CssColor(css.Groups[1].Value.Trim().ToLowerInvariant()) is { } color && color != Colors.White ? color : null;
        }
    }

    /// <summary>Whether a cell of the table reaches into a row after the last (which may then have no cells of its own).</summary>
    static bool Reached(DTable t)
    {
        for (int i = Math.Max(0, t.Rows.Count - 1000); i < t.Rows.Count; i++)
            if (t.Rows[i].Cells.Any(c => i + c.RowSpan > t.Rows.Count)) return true;
        return false;
    }
}

internal static class Bidi
{
    /// <summary>Whether the first letter with a direction runs right to left (Hebrew, Arabic and their relatives).</summary>
    public static bool StartsRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRightToLeft(rune.Value)) return true;
            if (Rune.IsLetter(rune)) return false;
        }
        return false;
    }

    public static bool IsRightToLeft(int c) => c is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;
}

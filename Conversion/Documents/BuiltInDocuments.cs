namespace ConvertWheel.Conversion.Documents;

/// <summary>The document and spreadsheet converter that ships inside the app: no Office or downloads needed.</summary>
internal static class BuiltInDocuments
{
    static readonly HashSet<string> DocumentInputs = ["docx", "doc", "odt", "fodt", "rtf", "txt", "log", "html", "md", "epub"];
    static readonly HashSet<string> DocumentOutputs = ["pdf", "docx", "odt", "rtf", "txt", "html", "md", "epub"];
    static readonly HashSet<string> SheetInputs = ["csv", "tsv", "xlsx", "xls", "ods"];
    static readonly HashSet<string> SheetOutputs = ["xlsx", "ods", "csv", "tsv", "json", "pdf", "html", "txt", "md"];

    public static bool CanRead(ConversionPlan plan) => plan.SourceKind switch
    {
        FormatKind.Document => DocumentInputs.Contains(plan.SourceExt),
        FormatKind.Spreadsheet => SheetInputs.Contains(plan.SourceExt),
        _ => false,
    };

    public static bool CanConvert(ConversionPlan plan, FormatInfo target) => CanRead(plan) && plan.SourceKind switch
    {
        FormatKind.Document => DocumentOutputs.Contains(target.Ext),
        FormatKind.Spreadsheet => SheetOutputs.Contains(target.Ext),
        _ => false,
    };

    /// <summary>Must run on an STA thread (WPF does the RTF handling and page layout).</summary>
    public static void Convert(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var title = Path.GetFileNameWithoutExtension(plan.SourcePath);
        if (plan.SourceKind == FormatKind.Spreadsheet)
        {
            Spreadsheets.Write(Spreadsheets.Read(plan.SourcePath, ct, charts: target.Ext is "pdf" or "html" or "xlsx" or "ods"), output, target.Ext, title, ct);
            return;
        }
        Write(Read(plan.SourcePath, plan.SourceExt), target.Ext, output, ct);
    }

    public static DDocument Read(string path, string ext) => ext switch
    {
        "docx" => DocxReader.Read(path),
        "doc" => WordBinaryReader.Read(path),
        "odt" or "fodt" => OdtReader.Read(path),
        "rtf" => RichText.Read(path),
        "html" => HtmlReader.ReadHtml(path),
        "md" => HtmlReader.ReadMarkdown(path),
        "epub" => HtmlReader.ReadEpub(path),
        _ => PlainText.Read(path),
    };

    public static void Write(DDocument doc, string ext, string output, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        switch (ext)
        {
            case "pdf": PdfRenderer.Render(doc, output, ct); break;
            case "docx": DocxWriter.Write(doc, output, ct); break;
            case "odt": OdtWriter.Write(doc, output, ct); break;
            case "rtf": RichText.Write(doc, output, ct); break;
            case "txt": PlainText.Write(doc, output, ct); break;
            case "html": HtmlWriter.Write(doc, output, ct); break;
            case "md": MarkdownWriter.Write(doc, output, ct); break;
            case "epub": EpubWriter.Write(doc, output, ct); break;
            default: throw new ConversionException($"Can't create {ext.ToUpperInvariant()} files from documents.");
        }
        ct.ThrowIfCancellationRequested();
    }
}

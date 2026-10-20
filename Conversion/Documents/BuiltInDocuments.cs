namespace ConvertWheel.Conversion.Documents;

/// <summary>The document and spreadsheet converter that ships inside the app: no Office or downloads needed.</summary>
internal static class BuiltInDocuments
{
    static readonly HashSet<string> DocumentInputs = ["docx", "odt", "rtf", "txt"];
    static readonly HashSet<string> DocumentOutputs = ["pdf", "docx", "odt", "rtf", "txt", "html"];
    static readonly HashSet<string> SheetInputs = ["csv", "xlsx", "xls", "ods"];
    static readonly HashSet<string> SheetOutputs = ["xlsx", "ods", "csv", "pdf", "html"];

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
    public static void Convert(ConversionPlan plan, FormatInfo target, string output)
    {
        var title = Path.GetFileNameWithoutExtension(plan.SourcePath);
        if (plan.SourceKind == FormatKind.Spreadsheet)
        {
            Spreadsheets.Write(Spreadsheets.Read(plan.SourcePath), output, target.Ext, title);
            return;
        }

        var doc = plan.SourceExt switch
        {
            "docx" => DocxReader.Read(plan.SourcePath),
            "odt" => OdtReader.Read(plan.SourcePath),
            "rtf" => RichText.Read(plan.SourcePath),
            _ => PlainText.Read(plan.SourcePath),
        };
        switch (target.Ext)
        {
            case "pdf": PdfRenderer.Render(doc, output); break;
            case "docx": DocxWriter.Write(doc, output); break;
            case "odt": OdtWriter.Write(doc, output); break;
            case "rtf": RichText.Write(doc, output); break;
            case "txt": PlainText.Write(doc, output); break;
            case "html": HtmlWriter.Write(doc, output); break;
            default: throw new ConversionException($"Can't create {target.Label} files from documents.");
        }
    }
}

namespace ConvertWheel.Conversion.Documents;

/// <summary>A number, date or time with its Excel number format. Dates and times are Excel serial numbers (days since 1900).</summary>
internal sealed class NumberCell(double value, string format)
{
    public double Value { get; } = value;
    public string Format { get; } = format;
}

/// <summary>A formula error such as #DIV/0!.</summary>
internal sealed class ErrorCell(string code)
{
    public string Code { get; } = code;

    /// <summary>The errors Excel files can hold; other programs have more.</summary>
    public bool IsExcel => Code is "#NULL!" or "#DIV/0!" or "#VALUE!" or "#REF!" or "#NAME?" or "#NUM!" or "#N/A" or "#GETTING_DATA";
}

/// <summary>Cell values are string, double, <see cref="NumberCell"/>, <see cref="ErrorCell"/>, bool, DateTime, another number or null.</summary>
internal sealed record Sheet(string Name, List<object?[]> Rows)
{
    /// <summary>The sheet's charts, already drawn (for PDF and HTML output).</summary>
    public List<DImage> Charts { get; init; } = [];
    public bool Hidden { get; init; }
    public HashSet<int> HiddenRows { get; init; } = [];
    public HashSet<int> HiddenColumns { get; init; } = [];
    /// <summary>Set for delimited text, whose values are all still text (see <see cref="DelimitedText.Types"/>).</summary>
    public char? Delimiter { get; init; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);
    public bool IsBlank => Rows.Count == 0 && Charts.Count == 0;
}

/// <summary>Keeps a spreadsheet within what fits in memory, counting cells across all of its sheets.</summary>
internal sealed class SheetLimits
{
    /// <summary>The size of an Excel sheet, and the most text one cell holds.</summary>
    public const int Rows = 1_048_576, Columns = 16_384, CellText = 32_767;
    const long MaxCells = 5_000_000, MaxText = 400_000_000;
    long _cells, _text;

    public void Cells(long count)
    {
        _cells += count;
        if (_cells > MaxCells) throw TooLarge();
    }

    public void Text(string text)
    {
        _text += text.Length;
        if (_text > MaxText) throw TooLarge();
    }

    public static ConversionException TooLarge() => new("This spreadsheet is too large to convert (more than 5 million cells), or it's damaged.");

    /// <summary>Formats that spell out every blank cell can't hold a sheet with a few values scattered over an enormous area.</summary>
    public static void Grid(Sheet sheet, long most, string format)
    {
        if ((long)sheet.Rows.Count * sheet.ColumnCount > most)
            throw new ConversionException($"The sheet \"{sheet.Name}\" spreads over {sheet.Rows.Count:N0} rows and {sheet.ColumnCount:N0} columns, which is too large for {format}.");
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

internal enum ChartType { Column, Bar, Line, Area, Pie, Doughnut, Scatter, Radar }
internal enum ChartGrouping { Clustered, Stacked, Percent }

internal sealed class ChartSeries
{
    public string Name = "";
    public List<double?> Values = [];
    public List<double?>? X;
    public Color? Color;
    public Dictionary<int, Color> PointColors = [];
    public bool Markers = true, Smooth, NoLine;
}

/// <summary>One plot in a chart; combo charts have several sharing the category axis.</summary>
internal sealed class ChartGroup
{
    public ChartType Type;
    public ChartGrouping Grouping;
    public List<ChartSeries> Series = [];
    public bool VaryColors, Secondary;
    public double GapWidth = 150, Overlap, HoleSize = 50, FirstSliceAngle;
}

internal sealed class Chart
{
    public string? Title;
    public List<string> Categories = [];
    public List<ChartGroup> Groups = [];
    public string? LegendPosition = "r";
    public bool DataLabels, PercentLabels;
    public string? ValueFormat, CategoryTitle, ValueTitle;
    public double? Min, Max;
    public bool Gridlines = true, ShowValueAxis = true, ShowCategoryAxis = true;
    /// <summary>Points sit on the category tick marks (edge to edge) instead of between them.</summary>
    public bool OnTickMarks;
    public Color? Background = Colors.White;
    public Color? Border;
    /// <summary>Text sizes in points as saved with the chart (null: sized to fit the chart).</summary>
    public double? TitleSize, AxisSize, LegendSize, LabelSize;
    public Color? TextColor;
    public string Font = "Calibri";
    public Func<int, Color> Palette = DefaultPalette;

    public static Color DefaultPalette(int i)
    {
        Color[] office = [Color.FromRgb(0x44, 0x72, 0xC4), Color.FromRgb(0xED, 0x7D, 0x31), Color.FromRgb(0xA5, 0xA5, 0xA5),
            Color.FromRgb(0xFF, 0xC0, 0x00), Color.FromRgb(0x5B, 0x9B, 0xD5), Color.FromRgb(0x70, 0xAD, 0x47)];
        var c = office[i % 6];
        double shade = (i / 6) switch { 0 => 1, 1 => 0.6, _ => 1.4 };
        return Color.FromRgb((byte)Math.Clamp(c.R * shade, 0, 255), (byte)Math.Clamp(c.G * shade, 0, 255), (byte)Math.Clamp(c.B * shade, 0, 255));
    }
}

/// <summary>Reads charts saved by PowerPoint/Word/Excel (DrawingML) and LibreOffice (ODF), and draws them.</summary>
internal static class Charts
{
    const int MaxPoints = 100000;
    static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    // ------------------------------------------------------------------ DrawingML

    /// <param name="scheme">Resolves theme colour names ("accent1", "tx1"…) for this document.</param>
    public static Chart? ReadDrawingML(XElement chartSpace, Func<string, Color?> scheme, string font)
    {
        var chartEl = chartSpace.Element(C + "chart");
        var plot = chartEl?.Element(C + "plotArea");
        if (plot == null) return null;
        var chart = new Chart { Font = font };
        Color Accent(int i)
        {
            var baseColor = scheme($"accent{i % 6 + 1}") ?? Chart.DefaultPalette(i % 6);
            double shade = (i / 6) switch { 0 => 1, 1 => 0.6, _ => 1.4 };
            return Color.FromRgb((byte)Math.Clamp(baseColor.R * shade, 0, 255), (byte)Math.Clamp(baseColor.G * shade, 0, 255), (byte)Math.Clamp(baseColor.B * shade, 0, 255));
        }
        chart.Palette = Accent;

        // A title without text of its own (or no title at all, unless deleted) shows the single series' name.
        if (chartEl!.Element(C + "title") is { } title) chart.Title = RichText(title) ?? "";
        else if (Val(chartEl.Element(C + "autoTitleDeleted")) is not ("1" or "true")) chart.Title = "";
        chart.LegendPosition = chartEl.Element(C + "legend") is { } legend ? Val(legend.Element(C + "legendPos")) ?? "r" : null;
        double? Size(XElement? el) => Num((string?)el?.Descendants(A + "defRPr").FirstOrDefault()?.Attribute("sz") ?? (string?)el?.Descendants(A + "rPr").FirstOrDefault()?.Attribute("sz")) / 100;
        double? baseSize = Size(chartSpace.Element(C + "txPr"));
        chart.TitleSize = Size(chartEl.Element(C + "title")) ?? (baseSize ?? 10) * 1.4;
        chart.LegendSize = Size(chartEl.Element(C + "legend")?.Element(C + "txPr")) ?? baseSize ?? 10;
        chart.AxisSize = Size(plot.Element(C + "catAx")?.Element(C + "txPr") ?? plot.Element(C + "valAx")?.Element(C + "txPr")) ?? baseSize ?? 10;
        chart.LabelSize = Size(plot.Descendants(C + "dLbls").FirstOrDefault()?.Element(C + "txPr")) ?? chart.AxisSize;
        if ((plot.Element(C + "catAx") ?? plot.Element(C + "valAx"))?.Element(C + "txPr")?.Descendants(A + "defRPr").FirstOrDefault() is { } axisRun
            && axisRun.Element(A + "solidFill") is { } textFill) chart.TextColor = DrawingColor(textFill, scheme);
        if (Fill(chartSpace.Element(C + "spPr"), scheme) is var (has, color) && has) chart.Background = color;
        if (Fill(chartSpace.Element(C + "spPr")?.Element(A + "ln"), scheme) is (true, { } border)) chart.Border = border;

        int seriesIndex = 0;
        string? primaryAxis = null;
        foreach (var g in plot.Elements())
        {
            var name = g.Name.LocalName;
            ChartType? type = name switch
            {
                "barChart" or "bar3DChart" => Val(g.Element(C + "barDir")) == "bar" ? ChartType.Bar : ChartType.Column,
                "lineChart" or "line3DChart" or "stockChart" => ChartType.Line,
                "areaChart" or "area3DChart" => ChartType.Area,
                "pieChart" or "pie3DChart" or "ofPieChart" => ChartType.Pie,
                "doughnutChart" => ChartType.Doughnut,
                "scatterChart" or "bubbleChart" => ChartType.Scatter,
                "radarChart" => ChartType.Radar,
                _ => null,
            };
            if (type == null) continue;
            var group = new ChartGroup
            {
                Type = type.Value,
                Grouping = Val(g.Element(C + "grouping")) switch { "stacked" => ChartGrouping.Stacked, "percentStacked" => ChartGrouping.Percent, _ => ChartGrouping.Clustered },
                VaryColors = Val(g.Element(C + "varyColors")) is "1" or "true",
                GapWidth = Num(Val(g.Element(C + "gapWidth"))) ?? 150,
                Overlap = Num(Val(g.Element(C + "overlap"))) ?? (Val(g.Element(C + "grouping")) is "stacked" or "percentStacked" ? 100 : 0),
                HoleSize = Num(Val(g.Element(C + "holeSize"))) ?? 50,
                FirstSliceAngle = Num(Val(g.Element(C + "firstSliceAng"))) ?? 0,
            };
            if (type is ChartType.Pie or ChartType.Doughnut && g.Element(C + "varyColors") == null) group.VaryColors = true;
            var axis = g.Elements(C + "axId").FirstOrDefault() is { } ax ? Val(ax) : null;
            primaryAxis ??= axis;
            group.Secondary = axis != null && axis != primaryAxis && g.Elements(C + "axId").Skip(1).FirstOrDefault() is { } valAx
                && plot.Elements(C + "valAx").FirstOrDefault(v => Val(v.Element(C + "axId")) == Val(valAx)) is { } secondary
                && Val(secondary.Element(C + "axPos")) == "r";

            foreach (var ser in g.Elements(C + "ser"))
            {
                var s = new ChartSeries
                {
                    Name = SeriesName(ser) ?? $"Series {seriesIndex + 1}",
                    Values = Numbers(ser.Element(C + "val") ?? ser.Element(C + "yVal")),
                    Smooth = Val(ser.Element(C + "smooth")) is "1" or "true",
                };
                if (ser.Element(C + "xVal") is { } xVal)
                {
                    var xs = Numbers(xVal);
                    if (xs.Any(v => v != null)) s.X = xs;
                    if (chart.Categories.Count == 0 && xs.All(v => v == null)) chart.Categories = Strings(xVal);
                }
                if (chart.Categories.Count == 0 && ser.Element(C + "cat") is { } cat) chart.Categories = Strings(cat);
                var spPr = ser.Element(C + "spPr");
                var (hasFill, fill) = Fill(spPr, scheme);
                var line = spPr?.Element(A + "ln");
                var (hasLine, lineColor) = Fill(line, scheme);
                s.Color = type is ChartType.Line or ChartType.Scatter or ChartType.Radar ? (hasLine ? lineColor : null) ?? (hasFill ? fill : null) : hasFill ? fill : null;
                if (hasLine && lineColor == null && type is ChartType.Line or ChartType.Scatter) s.NoLine = true;
                s.Color ??= Accent(seriesIndex);
                var marker = ser.Element(C + "marker");
                s.Markers = Val(marker?.Element(C + "symbol")) != "none" && (type == ChartType.Scatter || marker != null || Val(g.Element(C + "marker")) is "1" or "true");
                if (type == ChartType.Scatter && Val(g.Element(C + "scatterStyle")) is "marker" or "none") s.NoLine = s.NoLine || Val(g.Element(C + "scatterStyle")) == "marker";
                foreach (var dPt in ser.Elements(C + "dPt"))
                    if (Num(Val(dPt.Element(C + "idx"))) is double idx && Fill(dPt.Element(C + "spPr"), scheme) is (true, { } pc))
                        s.PointColors[(int)idx] = pc;
                if (ser.Element(C + "dLbls") is { } labels && Val(labels.Element(C + "delete")) is not ("1" or "true"))
                {
                    chart.DataLabels |= Val(labels.Element(C + "showVal")) is "1" or "true";
                    chart.PercentLabels |= Val(labels.Element(C + "showPercent")) is "1" or "true";
                }
                group.Series.Add(s);
                seriesIndex++;
            }
            if (g.Element(C + "dLbls") is { } groupLabels)
            {
                chart.DataLabels |= Val(groupLabels.Element(C + "showVal")) is "1" or "true";
                chart.PercentLabels |= Val(groupLabels.Element(C + "showPercent")) is "1" or "true";
            }
            if (group.Series.Count > 0) chart.Groups.Add(group);
        }
        if (chart.Groups.Count == 0) return null;
        if (chart.Title == "")
            chart.Title = chart.Groups.SelectMany(g => g.Series).Count() == 1 ? chart.Groups[0].Series[0].Name
                : chartEl.Element(C + "title") != null ? "Chart Title" : null;

        var valAxis = plot.Elements(C + "valAx").FirstOrDefault(v => Val(v.Element(C + "axPos")) != "r") ?? plot.Elements(C + "valAx").FirstOrDefault();
        if (valAxis != null)
        {
            chart.ShowValueAxis = Val(valAxis.Element(C + "delete")) is not ("1" or "true");
            chart.OnTickMarks = Val(valAxis.Element(C + "crossBetween")) == "midCat" && chart.Groups.All(g => g.Type is ChartType.Line or ChartType.Area);
            chart.Gridlines = valAxis.Element(C + "majorGridlines") != null;
            chart.ValueFormat = (string?)valAxis.Element(C + "numFmt")?.Attribute("formatCode");
            chart.Min = Num(Val(valAxis.Element(C + "scaling")?.Element(C + "min")));
            chart.Max = Num(Val(valAxis.Element(C + "scaling")?.Element(C + "max")));
            chart.ValueTitle = valAxis.Element(C + "title") is { } vt ? RichText(vt) : null;
        }
        var catAxis = plot.Element(C + "catAx") ?? plot.Element(C + "dateAx") ?? plot.Elements(C + "valAx").FirstOrDefault(v => v != valAxis);
        if (catAxis != null)
        {
            chart.ShowCategoryAxis = Val(catAxis.Element(C + "delete")) is not ("1" or "true");
            chart.CategoryTitle = catAxis.Element(C + "title") is { } ct ? RichText(ct) : null;
        }
        if (chart.ValueFormat == null)
            chart.ValueFormat = (string?)plot.Descendants(C + "numCache").Elements(C + "formatCode").FirstOrDefault();
        return chart;
    }

    static string? Val(XElement? el) => (string?)el?.Attribute("val");
    static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    static int PointCount(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0) return 0;
        if (n > MaxPoints) throw new ConversionException("This chart has too many data points.");
        return (int)n;
    }

    static int PointIndex(XElement point)
    {
        if (!long.TryParse((string?)point.Attribute("idx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0) return -1;
        if (n >= MaxPoints) throw new ConversionException("This chart has too many data points.");
        return (int)n;
    }

    static string? RichText(XElement title)
    {
        var text = string.Join("\n", title.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))).Where(t => t.Length > 0));
        if (text.Length == 0) text = string.Concat(title.Descendants(C + "v").Select(v => v.Value));
        return text.Length > 0 ? text : null;
    }

    static string? SeriesName(XElement ser)
    {
        var tx = ser.Element(C + "tx");
        if (tx == null) return null;
        var v = tx.Descendants(C + "v").FirstOrDefault()?.Value;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Cached values in point order (gaps stay null).</summary>
    static List<double?> Numbers(XElement? holder)
    {
        var cache = holder?.Descendants(C + "numCache").FirstOrDefault() ?? holder?.Descendants(C + "numLit").FirstOrDefault();
        if (cache == null) return [];
        int count = PointCount(Val(cache.Element(C + "ptCount")));
        var points = cache.Elements(C + "pt").Select(p => (PointIndex(p), Num(p.Element(C + "v")?.Value))).Where(p => p.Item1 >= 0).ToList();
        count = Math.Max(count, points.Count == 0 ? 0 : points.Max(p => p.Item1) + 1);
        var values = Enumerable.Repeat<double?>(null, count).ToList();
        foreach (var (idx, value) in points) if (idx < count) values[idx] = value;
        return values;
    }

    static List<string> Strings(XElement holder)
    {
        // Multi-level categories: join each point's levels.
        var multi = holder.Descendants(C + "multiLvlStrCache").FirstOrDefault();
        if (multi != null)
        {
            var levels = multi.Elements(C + "lvl").Select(l => l.Elements(C + "pt").Where(p => PointIndex(p) >= 0)
                .GroupBy(PointIndex).ToDictionary(g => g.Key, g => g.Last().Element(C + "v")?.Value ?? "")).ToList();
            int n = Math.Max(PointCount(Val(multi.Element(C + "ptCount"))), levels.SelectMany(l => l.Keys).DefaultIfEmpty(-1).Max() + 1);
            return Enumerable.Range(0, n).Select(i => levels.Count > 0 && levels[0].TryGetValue(i, out var v) ? v : "").ToList();
        }
        var cache = holder.Descendants(C + "strCache").FirstOrDefault() ?? holder.Descendants(C + "strLit").FirstOrDefault()
                    ?? holder.Descendants(C + "numCache").FirstOrDefault() ?? holder.Descendants(C + "numLit").FirstOrDefault();
        if (cache == null) return [];
        int count = PointCount(Val(cache.Element(C + "ptCount")));
        var format = cache.Element(C + "formatCode")?.Value;
        var result = Enumerable.Repeat("", count).ToList();
        foreach (var p in cache.Elements(C + "pt"))
        {
            int idx = PointIndex(p);
            if (idx < 0) continue;
            var v = p.Element(C + "v")?.Value ?? "";
            if (cache.Name.LocalName.StartsWith("num") && Num(v) is double d) v = FormatValue(d, format, dates: true);
            while (result.Count <= idx) result.Add("");
            result[idx] = v;
        }
        return result;
    }

    static (bool has, Color? color) Fill(XElement? holder, Func<string, Color?> scheme)
    {
        if (holder == null) return (false, null);
        if (holder.Element(A + "noFill") != null) return (true, null);
        var solid = holder.Element(A + "solidFill") ?? holder.Element(A + "gradFill")?.Descendants(A + "gs").FirstOrDefault();
        if (solid == null) return (false, null);
        return (true, DrawingColor(solid, scheme));
    }

    static Color? DrawingColor(XElement holder, Func<string, Color?> scheme)
    {
        foreach (var el in holder.Elements())
        {
            Color? c = el.Name.LocalName switch
            {
                "srgbClr" => Units.ParseHex((string?)el.Attribute("val")),
                "schemeClr" => scheme((string?)el.Attribute("val") ?? ""),
                "sysClr" => Units.ParseHex((string?)el.Attribute("lastClr")),
                "prstClr" => (string?)el.Attribute("val") is { } n && ColorConverter.ConvertFromString(n) is Color named ? named : null,
                _ => null,
            };
            if (c is not { } color) continue;
            foreach (var m in el.Elements())
            {
                double v = (Num((string?)m.Attribute("val")) ?? 0) / 100000;
                color = m.Name.LocalName switch
                {
                    "lumMod" => Color.FromArgb(color.A, (byte)(color.R * v), (byte)(color.G * v), (byte)(color.B * v)),
                    "lumOff" => Color.FromArgb(color.A, (byte)Math.Min(255, color.R + 255 * v), (byte)Math.Min(255, color.G + 255 * v), (byte)Math.Min(255, color.B + 255 * v)),
                    "tint" => Color.FromArgb(color.A, (byte)(255 - (255 - color.R) * v), (byte)(255 - (255 - color.G) * v), (byte)(255 - (255 - color.B) * v)),
                    "shade" => Color.FromArgb(color.A, (byte)(color.R * v), (byte)(color.G * v), (byte)(color.B * v)),
                    "alpha" => Color.FromArgb((byte)(255 * v), color.R, color.G, color.B),
                    _ => color,
                };
            }
            return color;
        }
        return null;
    }

    // ------------------------------------------------------------------ ODF

    static readonly XNamespace Ch = "urn:oasis:names:tc:opendocument:xmlns:chart:1.0";
    static readonly XNamespace Off = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace Tb = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace Tx = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    static readonly XNamespace Sty = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    static readonly XNamespace Drw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    static readonly XNamespace Sv = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    /// <summary>A chart embedded in an OpenDocument file ("Object 1/content.xml" next to the frame that shows it).</summary>
    public static Chart? ReadOdf(ZipArchive zip, string objectHref)
    {
        var dir = objectHref.TrimStart('.', '/').TrimEnd('/');
        var entry = zip.GetEntry(dir + "/content.xml");
        if (entry == null) return null;
        XElement root;
        using (var s = PackageResources.Open(entry)) root = PackageResources.LoadXml(s).Root!;
        var chartEl = root.Descendants(Ch + "chart").FirstOrDefault();
        if (chartEl == null) return null;
        var styles = root.Descendants(Sty + "style").Where(s => s.Attribute(Sty + "name") != null).ToDictionary(s => (string)s.Attribute(Sty + "name")!, s => s);
        XElement? Props(XElement? el, string kind) =>
            (string?)el?.Attribute(Ch + "style-name") is { } n && styles.TryGetValue(n, out var st) ? st.Element(Sty + kind) : null;

        // The chart's data lives in a small table inside the chart.
        var grid = new List<List<(string text, double? value)>>();
        var table = chartEl.Descendants(Tb + "table").FirstOrDefault();
        if (table != null)
            foreach (var (_, cells) in new OdfRepetitions().Rows(table))
                grid.Add(cells.Select(cell => cell == null ? ("", (double?)null) :
                    (string.Join(" ", cell.Elements(Tx + "p").Select(p => p.Value)), Num((string?)cell.Attribute(Off + "value")))).ToList());
        (string text, double? value) Cell(int row, int col) => row >= 0 && row < grid.Count && col >= 0 && col < grid[row].Count ? grid[row][col] : ("", null);
        List<(int row, int col)> Range(string? address)
        {
            var cells = new List<(int, int)>();
            if (string.IsNullOrEmpty(address)) return cells;
            var refs = Regex.Matches(address, @"\$?([A-Z]+)\$?(\d+)").Select(m =>
                (row: int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r - 1 : -1,
                 col: ColumnIndex(m.Groups[1].Value))).ToList();
            if (refs.Count == 0) return cells;
            var (r1, c1) = refs[0];
            var (r2, c2) = refs.Count > 1 ? refs[1] : refs[0];
            if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0) return cells;
            if ((Math.Abs((long)r2 - r1) + 1) * (Math.Abs((long)c2 - c1) + 1) > MaxPoints)
                throw new ConversionException("This chart has too many data points.");
            for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++) cells.Add((r, c));
            return cells;
        }

        var chart = new Chart();
        var cls = ((string?)chartEl.Attribute(Ch + "class") ?? "chart:bar").Replace("chart:", "");
        var plotProps = Props(chartEl.Element(Ch + "plot-area"), "chart-properties");
        bool vertical = (string?)plotProps?.Attribute(Ch + "vertical") == "true";
        var grouping = (string?)plotProps?.Attribute(Ch + "percentage") == "true" ? ChartGrouping.Percent
            : (string?)plotProps?.Attribute(Ch + "stacked") == "true" ? ChartGrouping.Stacked : ChartGrouping.Clustered;
        ChartType TypeOf(string c) => c switch
        {
            "bar" => vertical ? ChartType.Bar : ChartType.Column,
            "line" => ChartType.Line,
            "area" => ChartType.Area,
            "circle" => ChartType.Pie,
            "ring" => ChartType.Doughnut,
            "scatter" or "bubble" => ChartType.Scatter,
            "radar" or "filled-radar" => ChartType.Radar,
            _ => ChartType.Column,
        };

        chart.Title = chartEl.Element(Ch + "title") is { } t ? string.Join("\n", t.Elements(Tx + "p").Select(p => p.Value)) : null;
        chart.LegendPosition = chartEl.Element(Ch + "legend") is { } legend
            ? ((string?)legend.Attribute(Ch + "legend-position")) switch { "bottom" => "b", "top" => "t", "start" => "l", _ => "r" }
            : null;
        var plotArea = chartEl.Element(Ch + "plot-area");
        var categories = plotArea?.Descendants(Ch + "categories").FirstOrDefault();
        // Category cells may hold bare numbers formatted by the axis (dates, usually).
        var xAxis = plotArea?.Elements(Ch + "axis").FirstOrDefault(a => (string?)a.Attribute(Ch + "dimension") == "x");
        var dataStyle = (string?)xAxis?.Attribute(Ch + "style-name") is { } xs && styles.TryGetValue(xs, out var xStyle) ? (string?)xStyle.Attribute(Sty + "data-style-name") : null;
        bool dateAxis = dataStyle != null && root.Descendants().Any(e => e.Name.LocalName == "date-style" && (string?)e.Attribute(Sty + "name") == dataStyle);
        chart.Categories = Range((string?)categories?.Attribute(Tb + "cell-range-address")).Select(rc => Cell(rc.row, rc.col) switch
        {
            ({ Length: > 0 } text, _) => text,
            (_, double v) when dateAxis && v is > 0 and < 2958466 => DateTime.FromOADate(v).ToShortDateString(),
            (_, double v) => FormatValue(v, null),
            _ => "",
        }).ToList();
        double? FontSize(XElement? el) => Units.ParseLength((string?)Props(el, "text-properties")?.Attribute(Fo + "font-size"));
        chart.TitleSize = FontSize(chartEl.Element(Ch + "title")) ?? 13;
        chart.LegendSize = FontSize(chartEl.Element(Ch + "legend")) ?? 10;
        chart.AxisSize = FontSize(xAxis) ?? FontSize(plotArea?.Elements(Ch + "axis").FirstOrDefault()) ?? 10;
        if (Units.ParseHex((string?)Props(xAxis, "text-properties")?.Attribute(Fo + "color")) is { } axisColor) chart.TextColor = axisColor;
        foreach (var axis in plotArea?.Elements(Ch + "axis") ?? [])
        {
            var axisTitle = axis.Element(Ch + "title") is { } at ? string.Join(" ", at.Elements(Tx + "p").Select(p => p.Value)) : null;
            if ((string?)axis.Attribute(Ch + "dimension") == "y")
            {
                chart.ValueTitle = axisTitle;
                chart.Gridlines = axis.Element(Ch + "grid") != null;
                var ap = Props(axis, "chart-properties");
                chart.Min = Num((string?)ap?.Attribute(Ch + "minimum"));
                chart.Max = Num((string?)ap?.Attribute(Ch + "maximum"));
                chart.ShowValueAxis = (string?)ap?.Attribute(Ch + "display-label") != "false";
            }
            else chart.CategoryTitle = axisTitle;
        }

        int index = 0;
        foreach (var ser in plotArea?.Elements(Ch + "series") ?? [])
        {
            var type = TypeOf(((string?)ser.Attribute(Ch + "class"))?.Replace("chart:", "") ?? cls);
            var group = chart.Groups.FirstOrDefault(g => g.Type == type);
            if (group == null)
            {
                group = new ChartGroup { Type = type, Grouping = grouping, VaryColors = type is ChartType.Pie or ChartType.Doughnut };
                chart.Groups.Add(group);
            }
            var s = new ChartSeries
            {
                Name = Range((string?)ser.Attribute(Ch + "label-cell-address")).Select(rc => Cell(rc.row, rc.col).text).FirstOrDefault() ?? $"Series {index + 1}",
                Values = Range((string?)ser.Attribute(Ch + "values-cell-range-address")).Select(rc => Cell(rc.row, rc.col).value).ToList(),
            };
            var graphic = Props(ser, "graphic-properties");
            s.Color = Units.ParseHex(type is ChartType.Line or ChartType.Scatter or ChartType.Radar
                ? (string?)graphic?.Attribute(Sv + "stroke-color") ?? (string?)graphic?.Attribute(Drw + "fill-color")
                : (string?)graphic?.Attribute(Drw + "fill-color")) ?? Chart.DefaultPalette(index);
            var seriesChart = Props(ser, "chart-properties");
            s.Markers = (string?)seriesChart?.Attribute(Ch + "symbol-type") is not (null or "none");
            s.NoLine = (string?)graphic?.Attribute(Drw + "stroke") == "none" || (string?)plotProps?.Attribute(Ch + "lines") == "false";
            if (type == ChartType.Scatter && ser.Element(Ch + "domain") is { } domain)
                s.X = Range((string?)domain.Attribute(Tb + "cell-range-address")).Select(rc => Cell(rc.row, rc.col).value).ToList();
            int point = 0;
            foreach (var dp in ser.Elements(Ch + "data-point"))
            {
                int repeat = int.TryParse((string?)dp.Attribute(Ch + "repeated"), out var r) ? Math.Clamp(r, 0, Math.Max(0, s.Values.Count - point)) : 1;
                if (Units.ParseHex((string?)Props(dp, "graphic-properties")?.Attribute(Drw + "fill-color")) is { } pc)
                    for (int i = 0; i < repeat; i++) s.PointColors[point + i] = pc;
                point += repeat;
            }
            chart.DataLabels |= (string?)seriesChart?.Attribute(Ch + "data-label-number") is "value";
            chart.PercentLabels |= (string?)seriesChart?.Attribute(Ch + "data-label-number") is "percentage";
            group.Series.Add(s);
            index++;
        }
        if (chart.Groups.Count == 0) return null;
        if (chart.Groups.Any(g => g.Type is ChartType.Pie or ChartType.Doughnut))
        {
            // Pie slices take the default palette per point unless styled.
            chart.Palette = Chart.DefaultPalette;
        }
        return chart;
    }

    static int ColumnIndex(string letters)
    {
        int n = 0;
        foreach (var ch in letters)
        {
            if (n > (int.MaxValue - 26) / 26) return -1;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>The chart as a sharp picture of the given size (points).</summary>
    public static DImage ToImage(Chart chart, double widthPt, double heightPt)
    {
        widthPt = Math.Max(RasterLimits.Dimension(widthPt, 480), 40);
        heightPt = Math.Max(RasterLimits.Dimension(heightPt, 300), 30);
        double w = Units.ToDip(widthPt), h = Units.ToDip(heightPt);
        double scale = Math.Clamp(2400 / Math.Max(w, h), 1, 4);
        if (!RasterLimits.TrySize(w, h, ref scale, out int pw, out int ph)) throw new ConversionException("This chart has an invalid size.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) new Painter(chart, dc, w, h).Draw();
        var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return new DImage(ms.ToArray(), "png", widthPt, heightPt);
    }

    internal static string FormatValue(double v, string? format, bool dates = false)
    {
        if (string.IsNullOrEmpty(format) || format == "General")
            return Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("#,##0", CultureInfo.CurrentCulture) : v.ToString("#,##0.##", CultureInfo.CurrentCulture);
        var f = Regex.Replace(format, @"\[[^\]]*\]|""[^""]*""|_.|\\.", "").Split(';')[0];
        if (dates && Regex.IsMatch(f, "[dmy]", RegexOptions.IgnoreCase) && v > 0 && v < 2958466)
        {
            var date = DateTime.FromOADate(v);
            var net = f.Replace("mmmm", "MMMM").Replace("mmm", "MMM").Replace("yyyy", "yyyy").Replace("yy", "yy");
            net = Regex.Replace(net, @"(?<![hH:])m(?!m)", "M").Replace("mm/", "MM/").Replace("/mm", "/MM").Replace("dddd", "dddd");
            try { return date.ToString(net, CultureInfo.CurrentCulture); } catch (FormatException) { return date.ToShortDateString(); }
        }
        bool percent = f.Contains('%');
        int decimals = f.Contains('.') ? f[(f.IndexOf('.') + 1)..].TakeWhile(c => c is '0' or '#').Count() : 0;
        string prefix = Regex.Match(f, @"^[^#0.,%]*").Value, suffix = Regex.Match(f, @"[^#0.,%]*$").Value;
        var number = (percent ? v * 100 : v).ToString((f.Contains(',') ? "#,##0" : "0") + (decimals > 0 ? "." + new string('0', decimals) : ""), CultureInfo.CurrentCulture);
        return prefix + number + (percent ? "%" : "") + suffix.Replace("%", "");
    }

    sealed class Painter(Chart chart, DrawingContext dc, double width, double height)
    {
        readonly Brush TextBrush = Frozen(new SolidColorBrush(chart.TextColor ?? Color.FromRgb(0x59, 0x59, 0x59)));
        static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xD9, 0xD9, 0xD9), 0.75);
        static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0xBF, 0xBF, 0xBF), 0.75);
        readonly Typeface _face = new(new FontFamily($"{chart.Font}, Calibri, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double Small => chart.AxisSize is { } s ? Units.ToDip(s) : Math.Clamp(Math.Min(width, height) / 28, 8, 13);
        double LegendText => chart.LegendSize is { } s ? Units.ToDip(s) : Small;
        double LabelText => chart.LabelSize is { } s ? Units.ToDip(s) : Small * 0.9;

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }

        FormattedText Text(string s, double size, bool bold = false, Brush? brush = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                bold ? new Typeface(_face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : _face,
                size, brush ?? TextBrush, 1.0);

        bool IsPie => chart.Groups.All(g => g.Type is ChartType.Pie or ChartType.Doughnut);

        public void Draw()
        {
            var area = new Rect(0, 0, width, height);
            if (chart.Background is { } bg) dc.DrawRectangle(new SolidColorBrush(bg), null, area);
            if (chart.Border is { } border) dc.DrawRectangle(null, new Pen(new SolidColorBrush(border), 0.75), new Rect(0.4, 0.4, width - 0.8, height - 0.8));
            area.Inflate(-Small * 0.8, -Small * 0.8);

            if (!string.IsNullOrWhiteSpace(chart.Title))
            {
                var t = Text(chart.Title!, chart.TitleSize is { } ts ? Units.ToDip(ts) : Small * 1.45);
                t.MaxTextWidth = Math.Max(10, area.Width);
                t.TextAlignment = TextAlignment.Center;
                dc.DrawText(t, new Point(area.X, area.Y));
                area = new Rect(area.X, area.Y + t.Height + Small * 0.6, area.Width, Math.Max(10, area.Height - t.Height - Small * 0.6));
            }
            area = Legend(area);
            if (IsPie) Pies(area);
            else if (chart.Groups.All(g => g.Type == ChartType.Radar)) Radar(area);
            else Axes(area);
        }

        // ---------------------------------------------------------- legend

        List<(string name, Color color, bool line, bool marker)> LegendEntries()
        {
            if (IsPie)
            {
                var s = chart.Groups[0].Series[0];
                return s.Values.Select((_, i) => (Category(i), PointColor(chart.Groups[0], s, 0, i), false, false)).ToList();
            }
            return chart.Groups.SelectMany(g => g.Series.Select(s => (s.Name, s.Color ?? Colors.Gray, g.Type is ChartType.Line or ChartType.Scatter or ChartType.Radar, s.Markers))).ToList();
        }

        Rect Legend(Rect area)
        {
            if (chart.LegendPosition == null) return area;
            var entries = LegendEntries();
            if (entries.Count == 0) return area;
            double size = LegendText, swatch = size * 0.6, gap = size * 0.4;
            var texts = entries.Select(e => Text(e.name, size)).ToList();
            bool side = chart.LegendPosition is "r" or "l" or "tr";
            if (side)
            {
                double colWidth = Math.Min(area.Width * 0.35, texts.Max(t => t.Width) + swatch + gap * 2);
                double total = texts.Sum(t => Math.Max(t.Height, swatch) + gap);
                double y = area.Y + Math.Max(0, (area.Height - total) / 2);
                double x = chart.LegendPosition == "l" ? area.X : area.Right - colWidth;
                for (int i = 0; i < entries.Count; i++)
                {
                    Swatch(entries[i], x, y + (texts[i].Height - swatch) / 2, swatch);
                    texts[i].MaxTextWidth = Math.Max(10, colWidth - swatch - gap);
                    texts[i].MaxLineCount = 1;
                    texts[i].Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(texts[i], new Point(x + swatch + gap, y));
                    y += Math.Max(texts[i].Height, swatch) + gap;
                }
                return chart.LegendPosition == "l"
                    ? new Rect(area.X + colWidth + gap, area.Y, Math.Max(10, area.Width - colWidth - gap), area.Height)
                    : new Rect(area.X, area.Y, Math.Max(10, area.Width - colWidth - gap), area.Height);
            }
            // Top or bottom: rows of entries, centred.
            var rows = new List<List<int>> { new() };
            double rowWidth = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                double itemWidth = swatch + gap + texts[i].Width + size;
                if (rowWidth + itemWidth > area.Width && rows[^1].Count > 0) { rows.Add([]); rowWidth = 0; }
                rows[^1].Add(i);
                rowWidth += itemWidth;
            }
            double lineHeight = texts.Max(t => t.Height);
            double blockHeight = rows.Count * (lineHeight + gap / 2);
            double top = chart.LegendPosition == "t" ? area.Y : area.Bottom - blockHeight;
            foreach (var row in rows)
            {
                double widthOfRow = row.Sum(i => swatch + gap + texts[i].Width + size) - size;
                double x = area.X + (area.Width - widthOfRow) / 2;
                foreach (var i in row)
                {
                    Swatch(entries[i], x, top + (lineHeight - swatch) / 2, swatch);
                    dc.DrawText(texts[i], new Point(x + swatch + gap, top));
                    x += swatch + gap + texts[i].Width + size;
                }
                top += lineHeight + gap / 2;
            }
            return chart.LegendPosition == "t"
                ? new Rect(area.X, area.Y + blockHeight + gap, area.Width, Math.Max(10, area.Height - blockHeight - gap))
                : new Rect(area.X, area.Y, area.Width, Math.Max(10, area.Height - blockHeight - gap));
        }

        void Swatch((string name, Color color, bool line, bool marker) e, double x, double y, double size)
        {
            var brush = new SolidColorBrush(e.color);
            if (e.line)
            {
                dc.DrawLine(new Pen(brush, Math.Max(1.5, size / 5)), new Point(x, y + size / 2), new Point(x + size * 1.2, y + size / 2));
                if (e.marker) dc.DrawEllipse(brush, null, new Point(x + size * 0.6, y + size / 2), size / 4, size / 4);
            }
            else dc.DrawRectangle(brush, null, new Rect(x, y, size, size));
        }

        string Category(int i) => i < chart.Categories.Count && chart.Categories[i].Length > 0 ? chart.Categories[i] : (i + 1).ToString(CultureInfo.CurrentCulture);

        Color PointColor(ChartGroup g, ChartSeries s, int seriesIndex, int point) =>
            s.PointColors.TryGetValue(point, out var c) ? c : g.VaryColors && (g.Series.Count == 1 || IsPie) ? chart.Palette(point) : s.Color ?? chart.Palette(seriesIndex);

        // ---------------------------------------------------------- pies

        void Pies(Rect area)
        {
            var g = chart.Groups[0];
            var s = g.Series[0];
            var values = s.Values.Select(v => Math.Max(0, v ?? 0)).ToList();
            double total = values.Sum();
            if (total <= 0) return;
            double radius = Math.Max(4, Math.Min(area.Width, area.Height) / 2 - Small * 0.5);
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            double hole = g.Type == ChartType.Doughnut ? radius * Math.Clamp(g.HoleSize, 10, 90) / 100 : 0;
            double angle = g.FirstSliceAngle - 90;
            var edge = new Pen(Brushes.White, Math.Max(0.75, radius / 120));
            for (int i = 0; i < values.Count; i++)
            {
                double sweep = values[i] / total * 360;
                if (sweep <= 0) continue;
                var geometry = Slice(center, radius, hole, angle, sweep);
                dc.DrawGeometry(new SolidColorBrush(PointColor(g, s, 0, i)), edge, geometry);
                if (chart.DataLabels || chart.PercentLabels)
                {
                    double mid = (angle + sweep / 2) * Math.PI / 180, r = hole > 0 ? (radius + hole) / 2 : radius * 0.65;
                    var label = chart.PercentLabels ? (values[i] / total).ToString("0%", CultureInfo.CurrentCulture) : FormatValue(values[i], chart.ValueFormat);
                    var t = Text(label, Small, brush: Brushes.White);
                    dc.DrawText(t, new Point(center.X + r * Math.Cos(mid) - t.Width / 2, center.Y + r * Math.Sin(mid) - t.Height / 2));
                }
                angle += sweep;
            }
        }

        static Geometry Slice(Point c, double r, double hole, double startDeg, double sweepDeg)
        {
            if (sweepDeg >= 359.99)
            {
                var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(c, r, r));
                if (hole > 0) ring.Children.Add(new EllipseGeometry(c, hole, hole));
                return ring;
            }
            Point At(double rad, double deg) => new(c.X + rad * Math.Cos(deg * Math.PI / 180), c.Y + rad * Math.Sin(deg * Math.PI / 180));
            var figure = new PathFigure { StartPoint = hole > 0 ? At(hole, startDeg) : c, IsClosed = true };
            figure.Segments.Add(new LineSegment(At(r, startDeg), true));
            figure.Segments.Add(new ArcSegment(At(r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
            if (hole > 0)
            {
                figure.Segments.Add(new LineSegment(At(hole, startDeg + sweepDeg), true));
                figure.Segments.Add(new ArcSegment(At(hole, startDeg), new Size(hole, hole), 0, sweepDeg > 180, SweepDirection.Counterclockwise, true));
            }
            return new PathGeometry([figure]);
        }

        // ---------------------------------------------------------- radar

        void Radar(Rect area)
        {
            var all = chart.Groups.SelectMany(g => g.Series).ToList();
            int n = Math.Max(chart.Categories.Count, all.Max(s => s.Values.Count));
            if (n < 3) return;
            var (min, max, step) = Scale(all.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).ToList(), 0);
            double radius = Math.Min(area.Width, area.Height) / 2 - Small * 2;
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            Point At(int i, double v) => new(center.X + radius * (v - min) / (max - min) * Math.Sin(2 * Math.PI * i / n), center.Y - radius * (v - min) / (max - min) * Math.Cos(2 * Math.PI * i / n));
            for (double v = min + step; v <= max + step / 2; v += step)
                dc.DrawGeometry(null, GridPen, Polygon(Enumerable.Range(0, n).Select(i => At(i, v)), true));
            for (int i = 0; i < n; i++)
            {
                dc.DrawLine(GridPen, center, At(i, max));
                var t = Text(Category(i), Small);
                var p = At(i, max + (max - min) * 0.08);
                dc.DrawText(t, new Point(p.X - t.Width / 2, p.Y - t.Height / 2));
            }
            int index = 0;
            foreach (var g in chart.Groups)
                foreach (var s in g.Series)
                {
                    var pts = Enumerable.Range(0, n).Select(i => At(i, i < s.Values.Count ? s.Values[i] ?? min : min)).ToList();
                    var color = s.Color ?? chart.Palette(index++);
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2), Polygon(pts, true));
                }
        }

        static Geometry Polygon(IEnumerable<Point> points, bool closed)
        {
            var list = points.ToList();
            var figure = new PathFigure { StartPoint = list[0], IsClosed = closed, IsFilled = closed };
            figure.Segments.Add(new PolyLineSegment(list.Skip(1), true));
            return new PathGeometry([figure]);
        }

        // ---------------------------------------------------------- axes, bars, lines, areas, scatter

        /// <summary>A "nice" axis range and step covering the values.</summary>
        (double min, double max, double step) Scale(List<double> values, double? floor)
        {
            double lo = Math.Min(chart.Min ?? double.MaxValue, values.Count > 0 ? values.Min() : 0);
            double hi = Math.Max(chart.Max ?? double.MinValue, values.Count > 0 ? values.Max() : 1);
            if (chart.Min is { } fixedMin) lo = fixedMin;
            else if (floor is { } f && lo >= f) lo = f;
            if (chart.Max == null && floor is { } top && hi <= top) hi = top;
            if (chart.Max is { } fixedMax) hi = fixedMax;
            if (hi <= lo) hi = lo + 1;
            // Like Excel: about ten steps, with a little headroom above the largest value.
            if (chart.Max == null) hi += (hi - lo) * 0.05;
            double raw = (hi - lo) / 10;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double step = new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
            if (chart.Min == null) lo = Math.Floor(lo / step) * step;
            if (chart.Max == null) hi = Math.Ceiling(hi / step) * step;
            if (hi <= lo) hi = lo + step;
            return (lo, hi, step);
        }

        /// <summary>Like Excel: start at zero unless every value sits far above it (lines only; bars always start at zero).</summary>
        static double? Floor(List<double> values, bool bars)
        {
            if (bars || values.Count == 0) return 0;
            double lo = values.Min(), hi = values.Max();
            return lo > 0 && lo > hi * 5 / 6 ? null : 0;
        }

        void Axes(Rect area)
        {
            bool horizontal = chart.Groups.Any(g => g.Type == ChartType.Bar);
            bool scatterOnly = chart.Groups.All(g => g.Type == ChartType.Scatter);
            int categories = Math.Max(chart.Categories.Count, chart.Groups.SelectMany(g => g.Series).Select(s => s.Values.Count).DefaultIfEmpty(0).Max());
            if (categories == 0) return;

            // Value ranges: stacked groups add up, percent groups run to 100%.
            List<double> Extent(bool secondary)
            {
                var values = new List<double>();
                foreach (var g in chart.Groups.Where(g => g.Secondary == secondary))
                {
                    if (g.Grouping == ChartGrouping.Percent) { values.Add(0); values.Add(1); continue; }
                    if (g.Grouping == ChartGrouping.Stacked && g.Type is ChartType.Column or ChartType.Bar or ChartType.Area or ChartType.Line)
                        for (int i = 0; i < categories; i++)
                        {
                            values.Add(g.Series.Sum(s => i < s.Values.Count ? Math.Max(0, s.Values[i] ?? 0) : 0));
                            values.Add(g.Series.Sum(s => i < s.Values.Count ? Math.Min(0, s.Values[i] ?? 0) : 0));
                        }
                    else values.AddRange(g.Series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value));
                }
                return values;
            }
            bool barsOrAreas = chart.Groups.Any(g => g.Type is ChartType.Column or ChartType.Bar or ChartType.Area);
            var primaryValues = Extent(false);
            var primary = Scale(primaryValues, Floor(primaryValues, barsOrAreas));
            var secondaryValues = Extent(true);
            var secondary = secondaryValues.Count > 0 ? Scale(secondaryValues, Floor(secondaryValues, barsOrAreas)) : primary;
            bool percent = chart.Groups.Any(g => g.Grouping == ChartGrouping.Percent);
            string Label(double v) => percent ? v.ToString("0%", CultureInfo.CurrentCulture) : FormatValue(v, chart.ValueFormat);

            // X scale for scatter charts.
            var xs = chart.Groups.Where(g => g.Type == ChartType.Scatter).SelectMany(g => g.Series).SelectMany(s => s.X ?? []).Where(v => v != null).Select(v => v!.Value).ToList();
            var xScale = scatterOnly && xs.Count > 0 ? Scale(xs, Floor(xs, false)) : default;

            // Room for labels and axis titles.
            double size = Small;
            var valueLabels = new List<double>();
            for (double v = primary.min; v <= primary.max + primary.step / 2; v += primary.step) valueLabels.Add(v);
            double valueLabelWidth = chart.ShowValueAxis ? valueLabels.Max(v => Text(Label(v), size).Width) + size * 0.5 : 0;
            double catLabelHeight = chart.ShowCategoryAxis ? Text("Ag", size).Height + size * 0.4 : 0;
            var plot = area;
            if (chart.ValueTitle != null) { var t = Text(chart.ValueTitle, size); if (horizontal) plot.Height -= t.Height; else { plot.X += t.Height + size * 0.3; plot.Width -= t.Height + size * 0.3; } }
            if (chart.CategoryTitle != null) { var t = Text(chart.CategoryTitle, size); if (horizontal) { plot.X += t.Height + size * 0.3; plot.Width -= t.Height + size * 0.3; } else plot.Height -= t.Height; }

            double categoryLabelWidth = horizontal && chart.ShowCategoryAxis ? Enumerable.Range(0, categories).Max(i => Math.Min(Text(Category(i), size).Width, area.Width * 0.3)) + size * 0.5 : 0;
            if (horizontal)
            {
                plot = new Rect(plot.X + categoryLabelWidth, plot.Y, Math.Max(10, plot.Width - categoryLabelWidth - size), Math.Max(10, plot.Height - catLabelHeight));
            }
            else
            {
                double right = secondaryValues.Count > 0 ? valueLabelWidth : size * 0.5;
                plot = new Rect(plot.X + valueLabelWidth, plot.Y + size * 0.4, Math.Max(10, plot.Width - valueLabelWidth - right), Math.Max(10, plot.Height - catLabelHeight - size * 0.4));
            }

            double ValuePos(double v, (double min, double max, double step) s) => horizontal
                ? plot.X + (v - s.min) / (s.max - s.min) * plot.Width
                : plot.Bottom - (v - s.min) / (s.max - s.min) * plot.Height;
            double band = (horizontal ? plot.Height : plot.Width) / categories;
            double edgeStep = (horizontal ? plot.Height : plot.Width) / Math.Max(1, categories - 1);
            double CategoryCenter(int i) => chart.OnTickMarks && categories > 1
                ? horizontal ? plot.Bottom - edgeStep * i : plot.X + edgeStep * i
                : horizontal ? plot.Bottom - band * (i + 0.5) : plot.X + band * (i + 0.5);
            double XPos(double x) => plot.X + (x - xScale.min) / Math.Max(1e-9, xScale.max - xScale.min) * plot.Width;

            // Gridlines and value labels.
            foreach (var v in valueLabels)
            {
                double p = ValuePos(v, primary);
                if (chart.Gridlines)
                    dc.DrawLine(GridPen, horizontal ? new Point(p, plot.Top) : new Point(plot.Left, p), horizontal ? new Point(p, plot.Bottom) : new Point(plot.Right, p));
                if (!chart.ShowValueAxis) continue;
                var t = Text(Label(v), size);
                if (horizontal) dc.DrawText(t, new Point(p - t.Width / 2, plot.Bottom + size * 0.3));
                else dc.DrawText(t, new Point(plot.Left - t.Width - size * 0.4, p - t.Height / 2));
            }
            if (secondaryValues.Count > 0 && chart.ShowValueAxis && !horizontal)
                for (double v = secondary.min; v <= secondary.max + secondary.step / 2; v += secondary.step)
                {
                    var t = Text(FormatValue(v, null), size);
                    dc.DrawText(t, new Point(plot.Right + size * 0.4, ValuePos(v, secondary) - t.Height / 2));
                }

            // Category axis line and labels (or x values for scatter charts).
            double zero = ValuePos(Math.Clamp(0, primary.min, primary.max), primary);
            if (horizontal) dc.DrawLine(AxisPen, new Point(zero, plot.Top), new Point(zero, plot.Bottom));
            else dc.DrawLine(AxisPen, new Point(plot.Left, zero), new Point(plot.Right, zero));
            if (chart.ShowCategoryAxis)
            {
                if (scatterOnly && xs.Count > 0)
                {
                    for (double v = xScale.min; v <= xScale.max + xScale.step / 2; v += xScale.step)
                    {
                        var t = Text(FormatValue(v, null), size);
                        dc.DrawText(t, new Point(XPos(v) - t.Width / 2, plot.Bottom + size * 0.3));
                    }
                }
                else
                {
                    // Skip labels that would collide.
                    double widest = Enumerable.Range(0, categories).Max(i => Text(Category(i), size).Width) + size * 0.6;
                    int every = horizontal ? Math.Max(1, (int)Math.Ceiling(Text("Ag", size).Height / band)) : Math.Max(1, (int)Math.Ceiling(widest / band));
                    for (int i = 0; i < categories; i += every)
                    {
                        var t = Text(Category(i), size);
                        if (horizontal)
                        {
                            t.MaxTextWidth = Math.Max(10, categoryLabelWidth - size * 0.4);
                            t.MaxLineCount = 1;
                            t.Trimming = TextTrimming.CharacterEllipsis;
                            t.TextAlignment = TextAlignment.Right;
                            dc.DrawText(t, new Point(plot.Left - categoryLabelWidth, CategoryCenter(i) - t.Height / 2));
                        }
                        else dc.DrawText(t, new Point(CategoryCenter(i) - t.Width / 2, plot.Bottom + size * 0.3));
                    }
                }
            }
            if (chart.ValueTitle != null)
            {
                var t = Text(chart.ValueTitle, size);
                if (horizontal) dc.DrawText(t, new Point(plot.X + (plot.Width - t.Width) / 2, area.Bottom - t.Height));
                else
                {
                    dc.PushTransform(new RotateTransform(-90, area.X, plot.Y + plot.Height / 2));
                    dc.DrawText(t, new Point(area.X - t.Width / 2, plot.Y + plot.Height / 2));
                    dc.Pop();
                }
            }
            if (chart.CategoryTitle != null)
            {
                var t = Text(chart.CategoryTitle, size);
                if (horizontal)
                {
                    dc.PushTransform(new RotateTransform(-90, area.X, plot.Y + plot.Height / 2));
                    dc.DrawText(t, new Point(area.X - t.Width / 2, plot.Y + plot.Height / 2));
                    dc.Pop();
                }
                else dc.DrawText(t, new Point(plot.X + (plot.Width - t.Width) / 2, area.Bottom - t.Height));
            }

            // Plots: areas first, then bars, then lines and points on top.
            int seriesIndex = 0;
            var order = chart.Groups.OrderBy(g => g.Type switch { ChartType.Area => 0, ChartType.Column or ChartType.Bar => 1, _ => 2 }).ToList();
            var barGroups = chart.Groups.Where(g => g.Type is ChartType.Column or ChartType.Bar).ToList();
            foreach (var g in order)
            {
                var scale = g.Secondary ? secondary : primary;
                double V(double v) => ValuePos(Math.Clamp(v, scale.min, scale.max), scale);
                double[] Totals() => Enumerable.Range(0, categories).Select(i => g.Series.Sum(s => i < s.Values.Count ? Math.Abs(s.Values[i] ?? 0) : 0)).ToArray();
                var totals = g.Grouping == ChartGrouping.Percent ? Totals() : null;
                double Value(ChartSeries s, int i) { double v = i < s.Values.Count ? s.Values[i] ?? 0 : 0; return totals != null && totals[i] > 0 ? v / totals[i] : v; }
                var positive = new double[categories];
                var negative = new double[categories];
                bool stacked = g.Grouping != ChartGrouping.Clustered;

                switch (g.Type)
                {
                    case ChartType.Column or ChartType.Bar:
                    {
                        // As Office sizes them: n bars overlapping by a fraction, plus a gap (a fraction of one bar) between groups.
                        int slots = stacked ? 1 : g.Series.Count;
                        double overlap = stacked ? 1 : Math.Clamp(g.Overlap / 100, -1, 1);
                        double barWidth = band / (slots - (slots - 1) * overlap + g.GapWidth / 100);
                        double groupWidth = barWidth * (slots - (slots - 1) * overlap);
                        for (int si = 0; si < g.Series.Count; si++)
                        {
                            var s = g.Series[si];
                            for (int i = 0; i < categories; i++)
                            {
                                if (i >= s.Values.Count || s.Values[i] is null) continue;
                                double v = Value(s, i);
                                double from = 0, to = v;
                                if (stacked)
                                {
                                    ref double acc = ref (v >= 0 ? ref positive[i] : ref negative[i]);
                                    from = acc; to = acc + v; acc = to;
                                }
                                double offset = -groupWidth / 2 + (stacked ? 0 : si) * barWidth * (1 - overlap);
                                double c = CategoryCenter(i);
                                Rect bar = horizontal
                                    ? new Rect(Math.Min(V(from), V(to)), c - offset - barWidth, Math.Abs(V(to) - V(from)), barWidth)
                                    : new Rect(c + offset, Math.Min(V(from), V(to)), barWidth, Math.Abs(V(to) - V(from)));
                                dc.DrawRectangle(new SolidColorBrush(PointColor(g, s, seriesIndex + si, i)), null, bar);
                                if (chart.DataLabels) ValueLabel(FormatValue(s.Values[i]!.Value, chart.ValueFormat), horizontal ? new Point(bar.Right + size * 0.2, bar.Y + bar.Height / 2) : new Point(bar.X + bar.Width / 2, bar.Top), horizontal);
                            }
                        }
                        break;
                    }
                    case ChartType.Area:
                    {
                        var baseline = Enumerable.Repeat(0.0, categories).ToArray();
                        for (int si = 0; si < g.Series.Count; si++)
                        {
                            var s = g.Series[si];
                            var top = new List<Point>();
                            var bottom = new List<Point>();
                            for (int i = 0; i < categories; i++)
                            {
                                double v = Value(s, i);
                                double from = stacked ? baseline[i] : 0, to = from + v;
                                if (stacked) baseline[i] = to;
                                double x = horizontal ? 0 : CategoryCenter(i);
                                top.Add(new Point(x, V(to)));
                                bottom.Add(new Point(x, V(from)));
                            }
                            bottom.Reverse();
                            var color = s.Color ?? chart.Palette(seriesIndex + si);
                            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(stacked ? (byte)255 : (byte)200, color.R, color.G, color.B)), null, Polygon(top.Concat(bottom), true));
                        }
                        break;
                    }
                    case ChartType.Line or ChartType.Scatter:
                    {
                        var running = new double[categories];
                        for (int si = 0; si < g.Series.Count; si++)
                        {
                            var s = g.Series[si];
                            var color = s.Color ?? chart.Palette(seriesIndex + si);
                            var points = new List<Point?>();
                            for (int i = 0; i < s.Values.Count && (g.Type == ChartType.Scatter || i < categories); i++)
                            {
                                if (s.Values[i] is null) { points.Add(null); continue; }
                                double v = Value(s, i);
                                if (stacked) { running[i] += v; v = running[i]; }
                                double x = g.Type == ChartType.Scatter && scatterOnly && s.X != null && i < s.X.Count && s.X[i] is double xv ? XPos(xv) : CategoryCenter(i);
                                points.Add(horizontal ? new Point(V(v), x) : new Point(x, V(v)));
                            }
                            var pen = new Pen(new SolidColorBrush(color), Math.Max(1.5, size / 5)) { LineJoin = PenLineJoin.Round };
                            if (!s.NoLine)
                                foreach (var run in Runs(points))
                                    if (run.Count > 1) dc.DrawGeometry(null, pen, s.Smooth ? Smooth(run) : Polygon(run, false));
                            if (s.Markers)
                                foreach (var p in points.OfType<Point>())
                                    dc.DrawEllipse(new SolidColorBrush(color), null, p, size / 3.2, size / 3.2);
                            if (chart.DataLabels)
                                for (int i = 0; i < points.Count; i++)
                                    if (points[i] is Point p && s.Values[i] is double raw) ValueLabel(FormatValue(raw, chart.ValueFormat), p, false);
                        }
                        break;
                    }
                }
                seriesIndex += g.Series.Count;
            }
            if (barGroups.Count == 0 && chart.Groups.All(g => g.Type == ChartType.Scatter)) dc.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        }

        void ValueLabel(string text, Point anchor, bool horizontal)
        {
            var t = Text(text, LabelText);
            dc.DrawText(t, horizontal ? new Point(anchor.X, anchor.Y - t.Height / 2) : new Point(anchor.X - t.Width / 2, anchor.Y - t.Height - Small * 0.15));
        }

        static IEnumerable<List<Point>> Runs(List<Point?> points)
        {
            var run = new List<Point>();
            foreach (var p in points)
            {
                if (p is Point q) run.Add(q);
                else if (run.Count > 0) { yield return run; run = []; }
            }
            if (run.Count > 0) yield return run;
        }

        /// <summary>A smooth curve through the points (Catmull-Rom as Béziers).</summary>
        static Geometry Smooth(List<Point> pts)
        {
            var figure = new PathFigure { StartPoint = pts[0], IsFilled = false };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
            return new PathGeometry([figure]);
        }
    }
}

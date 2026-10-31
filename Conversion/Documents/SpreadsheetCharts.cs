using System.IO.Compression;
using System.Windows.Media;
using System.Xml.Linq;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Charts (drawn as pictures) and pictures in spreadsheets (.xlsx and .ods), keyed by sheet name.</summary>
internal static class SpreadsheetCharts
{
    static readonly XNamespace SsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace SsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    static readonly XNamespace Dml = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static Dictionary<string, List<DImage>> Xlsx(string path)
    {
        var result = new Dictionary<string, List<DImage>>();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            XElement? Load(string part)
            {
                var entry = zip.GetEntry(part);
                if (entry == null) return null;
                using var s = entry.Open();
                return XDocument.Load(s).Root;
            }
            Dictionary<string, string> Rels(string part)
            {
                var dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
                var rels = Load($"{dir}/_rels/{Path.GetFileName(part)}.rels");
                return rels?.Elements(PkgRel + "Relationship")
                    .Where(r => r.Attribute("Id") != null && r.Attribute("Target") != null && (string?)r.Attribute("TargetMode") != "External")
                    .ToDictionary(r => (string)r.Attribute("Id")!, r => Resolve(dir, (string)r.Attribute("Target")!)) ?? [];
            }

            var colors = new Dictionary<string, Color>();
            string font = "Calibri";
            if (Load("xl/theme/theme1.xml") is { } theme)
            {
                foreach (var c in theme.Descendants(Dml + "clrScheme").Elements())
                    if (Units.ParseHex((string?)c.Element(Dml + "srgbClr")?.Attribute("val") ?? (string?)c.Element(Dml + "sysClr")?.Attribute("lastClr")) is { } color)
                        colors[c.Name.LocalName] = color;
                font = (string?)theme.Descendants(Dml + "minorFont").Elements(Dml + "latin").FirstOrDefault()?.Attribute("typeface") ?? font;
            }
            Color? Scheme(string name) =>
                colors.TryGetValue(name switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => name }, out var c) ? c : null;

            var workbookRels = Rels("xl/workbook.xml");
            foreach (var sheet in Load("xl/workbook.xml")?.Descendants(SsMain + "sheet") ?? [])
            {
                if ((string?)sheet.Attribute(SsRel + "id") is not { } rid || !workbookRels.TryGetValue(rid, out var sheetPath)) continue;
                var drawingId = (string?)Load(sheetPath)?.Element(SsMain + "drawing")?.Attribute(SsRel + "id");
                if (drawingId == null || !Rels(sheetPath).TryGetValue(drawingId, out var drawingPath) || Load(drawingPath) is not { } drawing) continue;
                var drawingRels = Rels(drawingPath);
                var images = new List<DImage>();
                foreach (var anchor in drawing.Elements())
                {
                    var (w, h) = AnchorSize(anchor);
                    var chartRef = anchor.Descendants().FirstOrDefault(e => e.Name.LocalName == "chart" && e.Attribute(SsRel + "id") != null);
                    if (chartRef != null && drawingRels.TryGetValue((string)chartRef.Attribute(SsRel + "id")!, out var chartPath)
                        && Load(chartPath) is { } chartRoot && Charts.ReadDrawingML(chartRoot, Scheme, font) is { } chart)
                    {
                        images.Add(Charts.ToImage(chart, w, h));
                        continue;
                    }
                    // Pictures placed on the sheet (logos, pasted charts).
                    var blip = anchor.Descendants(Dml + "blip").FirstOrDefault();
                    if ((string?)blip?.Attribute(SsRel + "embed") is { } embed && drawingRels.TryGetValue(embed, out var mediaPath)
                        && zip.GetEntry(mediaPath) is { } media && Picture(media, w, h) is { } picture)
                        images.Add(picture);
                }
                if (images.Count > 0) result[(string?)sheet.Attribute("name") ?? ""] = images;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
        {
            Log.Write($"Couldn't read the charts in {path}: {ex.Message}");
        }
        return result;
    }

    /// <summary>A drawing anchor's size in points: its own extent, or the cells it spans (default 48 × 15 pt cells).</summary>
    static (double w, double h) AnchorSize(XElement anchor)
    {
        var ext = anchor.Element(Xdr + "ext") ?? anchor.Descendants(Dml + "ext").FirstOrDefault(e => e.Attribute("cx") != null);
        if (long.TryParse((string?)ext?.Attribute("cx"), out var cx) && long.TryParse((string?)ext?.Attribute("cy"), out var cy) && cx > 0 && cy > 0)
            return (cx / 12700.0, cy / 12700.0);
        int Cell(string which, string what) => int.TryParse(anchor.Element(Xdr + which)?.Element(Xdr + what)?.Value, out var v) ? v : 0;
        double Offset(string which, string what) => long.TryParse(anchor.Element(Xdr + which)?.Element(Xdr + what)?.Value, out var v) ? v / 12700.0 : 0;
        double w = (Cell("to", "col") - Cell("from", "col")) * 48 + Offset("to", "colOff") - Offset("from", "colOff");
        double h = (Cell("to", "row") - Cell("from", "row")) * 15 + Offset("to", "rowOff") - Offset("from", "rowOff");
        return (w > 20 ? w : 360, h > 20 ? h : 216);
    }

    static string Resolve(string dir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (piece is not ("." or "")) parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    /// <summary>A picture from the package, as PNG/JPEG (metafiles and SVG converted).</summary>
    static DImage? Picture(ZipArchiveEntry entry, double w, double h)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();
        var ext = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
        if (ext is "jpg" or "jpeg" or "png" or "gif" or "bmp") return new DImage(data, ext, w, h);
        if (FlowBridge.Decode(data) is not { } bitmap) return null;
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        png.Save(output);
        return new DImage(output.ToArray(), "png", w, h);
    }

    public static Dictionary<string, List<DImage>> Ods(ZipArchive zip, XElement root)
    {
        var result = new Dictionary<string, List<DImage>>();
        foreach (var table in root.Descendants(Table + "table"))
        {
            var images = new List<DImage>();
            foreach (var frame in table.Descendants(Draw + "frame"))
            {
                double w = Units.ParseLength((string?)frame.Attribute(Svg + "width")) ?? 360, h = Units.ParseLength((string?)frame.Attribute(Svg + "height")) ?? 216;
                if ((string?)frame.Element(Draw + "object")?.Attribute(XLink + "href") is { } href && Charts.ReadOdf(zip, href) is { } chart)
                    images.Add(Charts.ToImage(chart, w, h));
                else if ((string?)frame.Element(Draw + "image")?.Attribute(XLink + "href") is { } imageHref && !imageHref.Contains("://")
                         && zip.GetEntry(imageHref.TrimStart('.', '/')) is { } media && Picture(media, w, h) is { } picture)
                    images.Add(picture);
            }
            if (images.Count > 0) result[(string?)table.Attribute(Table + "name") ?? ""] = images;
        }
        return result;
    }
}

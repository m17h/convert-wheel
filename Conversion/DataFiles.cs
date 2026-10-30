using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace ConvertWheel.Conversion;

/// <summary>
/// Structured data: JSON, YAML and XML convert into each other, and a list of records becomes a
/// spreadsheet (CSV, XLSX). Values are held as a neutral tree: ordered maps, lists and scalars.
/// </summary>
internal static class DataFiles
{
    sealed class Map : List<KeyValuePair<string, object?>>
    {
        public object? this[string key]
        {
            get => this.FirstOrDefault(kv => kv.Key == key).Value;
            set
            {
                int i = FindIndex(kv => kv.Key == key);
                if (i >= 0) this[i] = new(key, value); else Add(new(key, value));
            }
        }
        public bool Has(string key) => this.Any(kv => kv.Key == key);
    }

    public static void Convert(string input, string output, string ext)
    {
        var tree = Read(input);
        Write(tree, output, ext, Path.GetFileNameWithoutExtension(input));
    }

    static object? Read(string path)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(path));
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".yaml" or ".yml" => FromYaml(text),
                ".xml" => FromXml(XDocument.Parse(text)),
                _ => FromJson(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })),
            };
        }
        catch (Exception ex) when (ex is JsonException or XmlException or YamlDotNet.Core.YamlException)
        {
            throw new ConversionException($"This file isn't valid {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}: {ex.Message.Split('\n')[0]}");
        }
    }

    static void Write(object? tree, string output, string ext, string title)
    {
        switch (ext)
        {
            case "json":
                File.WriteAllText(output, ToJson(tree)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                break;
            case "yaml":
                var yaml = new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(ToPlain(tree));
                File.WriteAllText(output, yaml, new UTF8Encoding(false));
                break;
            case "xml":
                var root = tree is Map { Count: 1 } single && single[0].Value is not List<object?> ? ToXml(single[0].Key, single[0].Value) : ToXml("root", tree);
                new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(output);
                break;
            case "csv" or "xlsx" or "tsv" or "ods":
                Spreadsheets.Write([ToSheet(tree, title)], output, ext, title);
                break;
            default:
                throw new ConversionException($"Data can't be saved as {ext.ToUpperInvariant()}.");
        }
    }

    // ------------------------------------------------------------------ JSON

    static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => Collect(o.Select(kv => new KeyValuePair<string, object?>(kv.Key, FromJson(kv.Value)))),
        JsonArray a => a.Select(FromJson).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => node.ToString(),
    };

    static Map Collect(IEnumerable<KeyValuePair<string, object?>> items)
    {
        var map = new Map();
        map.AddRange(items);
        return map;
    }

    static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        Map m => new JsonObject(m.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToJson(kv.Value)))),
        List<object?> l => new JsonArray(l.Select(ToJson).ToArray()),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        DateTime dt => JsonValue.Create(dt.ToString("s", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(System.Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    // ------------------------------------------------------------------ YAML

    static object? FromYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var docs = stream.Documents.Select(d => FromYaml(d.RootNode)).ToList();
        return docs.Count switch { 0 => null, 1 => docs[0], _ => docs };
    }

    static object? FromYaml(YamlNode node) => node switch
    {
        YamlMappingNode m => Collect(m.Children.Select(kv => new KeyValuePair<string, object?>(((YamlScalarNode)kv.Key).Value ?? "", FromYaml(kv.Value)))),
        YamlSequenceNode s => s.Children.Select(FromYaml).ToList(),
        YamlScalarNode s when s.Style is YamlDotNet.Core.ScalarStyle.Plain => Infer(s.Value),
        YamlScalarNode s => s.Value,
        _ => null,
    };

    /// <summary>Unquoted scalars: numbers, booleans and null become typed values.</summary>
    static object? Infer(string? value)
    {
        if (value is null or "" or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return true;
        if (value is "false" or "False" or "FALSE") return false;
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && !(value.Length > 1 && value.TrimStart('-')[0] == '0')) return l;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && value.Any(char.IsDigit)) return d;
        return value;
    }

    /// <summary>Plain .NET collections for the YAML serializer.</summary>
    static object? ToPlain(object? value) => value switch
    {
        Map m => m.ToDictionary(kv => kv.Key, kv => ToPlain(kv.Value)),
        List<object?> l => l.Select(ToPlain).ToList(),
        _ => value,
    };

    // ------------------------------------------------------------------ XML

    /// <summary>Elements become maps; attributes become "@name"; repeated child names become lists.</summary>
    static object? FromXml(XDocument doc) => doc.Root == null ? null : Collect([new(doc.Root.Name.LocalName, FromXml(doc.Root))]);

    static object? FromXml(XElement e)
    {
        bool hasChildren = e.Elements().Any();
        if (!e.HasAttributes && !hasChildren) return Infer(e.Value);
        var map = new Map();
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration)) map.Add(new("@" + a.Name.LocalName, Infer(a.Value)));
        foreach (var group in e.Elements().GroupBy(c => c.Name.LocalName))
        {
            var items = group.Select(FromXml).ToList();
            map.Add(new(group.Key, items.Count == 1 ? items[0] : items));
        }
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length > 0) map.Add(new("#text", Infer(text)));
        return map;
    }

    static XElement ToXml(string name, object? value)
    {
        var element = new XElement(XmlName(name));
        switch (value)
        {
            case Map m:
                foreach (var (key, child) in m)
                {
                    if (key.StartsWith('@') && child is not (Map or List<object?>)) element.SetAttributeValue(XmlName(key[1..]), Scalar(child));
                    else if (key == "#text") element.Add(Scalar(child));
                    else if (child is List<object?> list) foreach (var item in list) element.Add(ToXml(key, item));
                    else element.Add(ToXml(key, child));
                }
                break;
            case List<object?> l:
                foreach (var item in l) element.Add(ToXml("item", item));
                break;
            case null:
                break;
            default:
                element.Value = Scalar(value);
                break;
        }
        return element;
    }

    static string Scalar(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static string XmlName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        if (sb.Length == 0 || !(char.IsLetter(sb[0]) || sb[0] == '_')) sb.Insert(0, '_');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tables

    /// <summary>The first list of records in the data, flattened into columns ("address.city").</summary>
    static Sheet ToSheet(object? tree, string title)
    {
        var records = FindRecords(tree) ?? throw new ConversionException("This data has no list of records to turn into a table.");
        var rows = records.Select(r => Flatten(r)).ToList();
        var columns = rows.SelectMany(r => r.Select(kv => kv.Key)).Distinct().ToList();
        var table = new List<object?[]> { columns.Cast<object?>().ToArray() };
        foreach (var row in rows)
            table.Add(columns.Select(c => row.FirstOrDefault(kv => kv.Key == c).Value).ToArray());
        return new Sheet(title, table);
    }

    static List<object?>? FindRecords(object? node) => node switch
    {
        List<object?> l when l.Count > 0 && l.All(i => i is Map or null) => l,
        List<object?> l when l.Count > 0 && l.All(i => i is not (Map or List<object?>)) => l.Select(i => (object?)Collect([new("value", i)])).ToList(),
        Map m => m.Select(kv => FindRecords(kv.Value)).FirstOrDefault(r => r != null) ?? (m.Count > 0 ? [m] : null),
        _ => null,
    };

    static List<KeyValuePair<string, object?>> Flatten(object? value, string prefix = "")
    {
        var result = new List<KeyValuePair<string, object?>>();
        if (value is Map m)
            foreach (var (key, child) in m) result.AddRange(Flatten(child, prefix.Length > 0 ? $"{prefix}.{key}" : key));
        else if (value is List<object?> l)
            result.Add(new(prefix, string.Join("; ", l.Select(i => i is Map or List<object?> ? ToJson(i)?.ToJsonString() : Scalar(i)))));
        else
            result.Add(new(prefix.Length > 0 ? prefix : "value", value is long n ? (double)n : value));
        return result;
    }

    /// <summary>Spreadsheet to JSON: the first row names the fields; several sheets become an object of tables.</summary>
    public static void FromSheets(List<Sheet> sheets, string output)
    {
        object? Records(Sheet sheet)
        {
            if (sheet.Rows.Count == 0) return new List<object?>();
            var headers = sheet.Rows[0].Select((h, i) => h?.ToString() is { Length: > 0 } s ? s : $"column{i + 1}").ToArray();
            return sheet.Rows.Skip(1).Select(row => (object?)Collect(headers.Select((h, i) =>
                new KeyValuePair<string, object?>(h, i < row.Length ? row[i] is double d && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (long)d : row[i] : null)))).ToList();
        }
        object? tree = sheets.Count == 1 ? Records(sheets[0]) : Collect(sheets.Select(s => new KeyValuePair<string, object?>(s.Name, Records(s))));
        Write(tree, output, "json", "");
    }
}

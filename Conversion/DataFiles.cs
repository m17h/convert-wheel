using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ConvertWheel.Conversion;

/// <summary>
/// Structured data: JSON, YAML and XML convert into each other, and a list of records becomes a
/// spreadsheet (CSV, XLSX). Values are held as a neutral tree: ordered maps, lists and scalars.
/// Numbers keep the digits they were written with, so long IDs and exact decimals survive.
/// </summary>
internal static partial class DataFiles
{
    /// <summary>Far deeper than real data, and shallow enough that a hostile file can't exhaust the stack.</summary>
    const int MaxDepth = 256;

    sealed class Map : List<KeyValuePair<string, object?>>
    {
        HashSet<string>? _keys;

        /// <summary>Adds an entry; a repeated name gets a number ("name_2") so neither value is lost.</summary>
        public void AddUnique(string key, object? value)
        {
            _keys ??= [.. this.Select(kv => kv.Key)];
            var name = key;
            for (int i = 2; !_keys.Add(name); i++) name = $"{key}_{i}";
            Add(new(name, value));
        }
    }

    /// <summary>A number, as its JSON text.</summary>
    sealed record Number(string Text);

    public static void Convert(string input, string output, string ext)
    {
        try
        {
            var tree = Read(input);
            Write(tree, output, ext, Path.GetFileNameWithoutExtension(input));
        }
        catch (InsufficientExecutionStackException) { throw TooDeep(); }
    }

    static ConversionException TooDeep() => new("This file is nested too deeply.");

    static void CheckDepth(int depth)
    {
        if (depth > MaxDepth) throw TooDeep();
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    static object? Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var bytes = File.ReadAllBytes(path);
        try
        {
            switch (ext)
            {
                case ".yaml" or ".yml": return FromYaml(TextDecoding.Decode(bytes));
                case ".xml": return FromXml(LoadXml(bytes));
                default:
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaxDepth };
                    using (var json = JsonDocument.Parse(TextDecoding.Decode(bytes), options)) return FromJson(json.RootElement, 0);
            }
        }
        catch (JsonException ex) when (ex.Message.Contains("maximum configured depth")) { throw TooDeep(); }
        catch (Exception ex) when (ex is JsonException or XmlException or YamlException)
        {
            throw new ConversionException($"This file isn't valid {ext.TrimStart('.').ToUpperInvariant()}: {ex.Message.Split('\n')[0]}");
        }
    }

    static void Write(object? tree, string output, string ext, string title)
    {
        switch (ext)
        {
            case "json":
                using (var file = File.Create(output))
                using (var writer = JsonWriter(file, indented: true))
                    ToJson(writer, tree, 0);
                break;
            case "yaml":
                using (var text = new StreamWriter(output, false, new UTF8Encoding(false)))
                    ToYaml(tree, text);
                break;
            case "xml":
                bool named = tree is Map { Count: 1 } one && one[0].Value is not List<object?> && one[0].Key != "#text" && !one[0].Key.StartsWith('@');
                var root = named ? ToXml(((Map)tree!)[0].Key, ((Map)tree)[0].Value, 0) : ToXml("root", tree, 0);
                new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(output);
                break;
            case "csv" or "xlsx" or "tsv" or "ods":
                Spreadsheets.Write([ToSheet(tree, title, asText: ext is "csv" or "tsv")], output, ext, title);
                break;
            default:
                throw new ConversionException($"Data can't be saved as {ext.ToUpperInvariant()}.");
        }
    }

    // ------------------------------------------------------------------ JSON

    static object? FromJson(JsonElement e, int depth)
    {
        CheckDepth(depth);
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Map();
                foreach (var p in e.EnumerateObject()) map.AddUnique(JsonName(p), FromJson(p.Value, depth + 1));
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>(e.GetArrayLength());
                foreach (var item in e.EnumerateArray()) list.Add(FromJson(item, depth + 1));
                return list;
            case JsonValueKind.String:
                try { return e.GetString(); }
                catch (InvalidOperationException) { return Unescape(e.GetRawText()); }
            case JsonValueKind.Number: return new Number(e.GetRawText());
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    static string JsonName(JsonProperty p)
    {
        try { return p.Name; }
        catch (InvalidOperationException) { return Unescape(p.ToString().Split("\":")[0] + "\""); }
    }

    /// <summary>A JSON string with a broken surrogate escape: the bad character becomes U+FFFD, the rest stays.</summary>
    static string Unescape(string raw)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length - 1) { sb.Append(raw[i]); continue; }
            char c = raw[++i];
            if (c == 'u' && i + 4 < raw.Length) { sb.Append((char)int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber)); i += 4; }
            else sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c });
        }
        // Going through UTF-8 replaces unpaired surrogates.
        return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    static Utf8JsonWriter JsonWriter(Stream stream, bool indented) => new(stream, new JsonWriterOptions
    {
        Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MaxDepth * 2,
    });

    static void ToJson(Utf8JsonWriter w, object? value, int depth)
    {
        CheckDepth(depth);
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case Map m:
                w.WriteStartObject();
                foreach (var (key, child) in m)
                {
                    w.WritePropertyName(key);
                    ToJson(w, child, depth + 1);
                }
                w.WriteEndObject();
                break;
            case List<object?> l:
                w.WriteStartArray();
                foreach (var item in l) ToJson(w, item, depth + 1);
                w.WriteEndArray();
                break;
            case bool b: w.WriteBooleanValue(b); break;
            case Number n: w.WriteRawValue(n.Text); break;
            default: w.WriteStringValue(AsText(value)); break;
        }
    }

    static string JsonText(object? value)
    {
        using var ms = new MemoryStream();
        using (var w = JsonWriter(ms, indented: false)) ToJson(w, value, 0);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?\z")]
    private static partial Regex JsonNumber();

    // ------------------------------------------------------------------ YAML

    /// <summary>
    /// Reads YAML events directly: anchors are shared rather than copied, so a file can't blow up in memory,
    /// and the size it would expand to when written out is limited.
    /// </summary>
    sealed class YamlReader(string text)
    {
        const long MaxExpansion = 5_000_000;
        static readonly object Unfinished = new();

        readonly Parser _parser = new(new StringReader(text));
        readonly Dictionary<AnchorName, (object? value, long size)> _anchors = [];
        long _aliased;

        public List<object?> Documents()
        {
            var docs = new List<object?>();
            while (_parser.MoveNext())
            {
                if (_parser.Current is not DocumentStart) continue;
                _anchors.Clear();
                _parser.MoveNext();
                docs.Add(Node(0, out _)); // ends on the DocumentEnd
            }
            return docs;
        }

        /// <summary>The node at the current event, leaving the parser on the event after it.</summary>
        object? Node(int depth, out long size)
        {
            CheckDepth(depth);
            size = 1;
            switch (_parser.Current)
            {
                case AnchorAlias alias:
                    if (!_anchors.TryGetValue(alias.Value, out var target)) throw new YamlException($"Line {alias.Start.Line}: unknown anchor '{alias.Value}'.");
                    if (ReferenceEquals(target.value, Unfinished)) throw new ConversionException("This YAML refers back to itself, so it can't be converted.");
                    size = target.size;
                    if ((_aliased += size) > MaxExpansion) throw new ConversionException("This YAML repeats its anchors so often that it would expand to an enormous file.");
                    _parser.MoveNext();
                    return target.value;

                case Scalar s:
                    size += s.Value.Length / 16;
                    var value = Resolve(s);
                    Remember(s.Anchor, value, size);
                    _parser.MoveNext();
                    return value;

                case SequenceStart start:
                    Remember(start.Anchor, Unfinished, 0);
                    _parser.MoveNext();
                    var list = new List<object?>();
                    while (_parser.Current is not SequenceEnd)
                    {
                        list.Add(Node(depth + 1, out long item));
                        size += item;
                    }
                    _parser.MoveNext();
                    Remember(start.Anchor, list, size);
                    return list;

                case MappingStart start:
                    Remember(start.Anchor, Unfinished, 0);
                    _parser.MoveNext();
                    var map = new Map();
                    var keys = new HashSet<string>();
                    var merged = new List<object?>();
                    while (_parser.Current is not MappingEnd)
                    {
                        var keyEvent = _parser.Current!;
                        bool merge = keyEvent is Scalar { Value: "<<", Style: ScalarStyle.Plain };
                        var keyNode = Node(depth + 1, out _);
                        if (keyNode is Map or List<object?>) throw new ConversionException("YAML table keys must be simple values.");
                        var key = keyEvent is Scalar name ? name.Value : AsText(keyNode); // as written: 1, true and 0x1F stay that way
                        var child = Node(depth + 1, out long item);
                        size += item + 1;
                        if (merge && (child is Map || child is List<object?> l && l.All(i => i is Map))) merged.Add(child);
                        else if (!keys.Add(key)) throw new YamlException($"Line {keyEvent.Start.Line}: the key '{key}' appears twice.");
                        else map.Add(new(key, child));
                    }
                    _parser.MoveNext();
                    // "<<: *base" pulls in the base's entries, except those given here (YAML's merge key).
                    foreach (var source in merged.SelectMany(m => m is List<object?> many ? many : [m]))
                        foreach (var entry in (Map)source!)
                            if (keys.Add(entry.Key)) map.Add(entry);
                    Remember(start.Anchor, map, size);
                    return map;

                default:
                    throw new YamlException("The file ends unexpectedly.");
            }
        }

        void Remember(AnchorName anchor, object? value, long size)
        {
            if (!anchor.IsEmpty) _anchors[anchor] = (value, size);
        }

        static object? Resolve(Scalar s)
        {
            if (!s.Tag.IsEmpty)
            {
                // An explicit type wins over how the text looks: "!!str 00123" is text, "!!int '5'" is a number.
                return s.Tag.Value switch
                {
                    "tag:yaml.org,2002:null" => null,
                    "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" => Infer(s.Value),
                    _ => s.Value,
                };
            }
            return s.Style is ScalarStyle.Plain ? Infer(s.Value) : s.Value;
        }
    }

    static object? FromYaml(string text)
    {
        var docs = new YamlReader(text).Documents();
        return docs.Count switch { 0 => null, 1 => docs[0], _ => docs };
    }

    /// <summary>
    /// Unquoted YAML: numbers, true/false and null become typed values. Text that would lose something as a
    /// number stays text: leading zeros, a plus sign, hex/octal, dates, infinity and NaN.
    /// </summary>
    static object? Infer(string value)
    {
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return true;
            case "false" or "False" or "FALSE": return false;
        }
        if (JsonNumber().IsMatch(value)) return new Number(value);
        // ".5" and "5." are YAML numbers that JSON spells differently.
        var m = LooseFloat().Match(value);
        if (!m.Success) return value;
        string Part(string group) => m.Groups[group].Length > 0 ? m.Groups[group].Value : "0";
        return new Number($"{m.Groups["sign"].Value}{Part("int")}.{Part("frac")}{m.Groups["exp"].Value}");
    }

    [GeneratedRegex(@"^(?<sign>-?)(?:(?<int>0|[1-9][0-9]*)\.|\.(?<frac>[0-9]+))(?<exp>([eE][-+]?[0-9]+)?)\z")]
    private static partial Regex LooseFloat();

    /// <summary>Text that some YAML reader (1.1 or 1.2) would take for another type unless it's quoted.</summary>
    [GeneratedRegex(@"^(|~|null|y|n|yes|no|on|off|true|false|=|<<|---.*|\.\.\..*|[-+]?\.(inf|nan)|[-+]?(0x[0-9a-f_]*|0o[0-7_]*|0b[01_]*)|[-+]?[0-9][0-9_,]*(:[0-9_]+)*(\.[0-9_]*)?(e[-+]?[0-9]*)?|[-+]?\.[0-9_]+(e[-+]?[0-9]*)?|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}.*)\z", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AmbiguousInYaml();

    /// <summary>Text with no YAML punctuation in a place where it could mean something.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}_/.($](?:[\p{L}\p{N}\p{M} _/.,;()$%+=<>~^!?@'|-]|(?<=\S):(?=[\p{L}\p{N}/]))*(?<! )\z")]
    private static partial Regex PlainInYaml();

    static void ToYaml(object? tree, TextWriter output)
    {
        var emitter = new Emitter(output);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        ToYaml(emitter, tree, 0);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
    }

    static void ToYaml(Emitter emitter, object? value, int depth)
    {
        CheckDepth(depth);
        switch (value)
        {
            case Map m:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, m.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, child) in m)
                {
                    EmitText(emitter, key, isKey: true);
                    ToYaml(emitter, child, depth + 1);
                }
                emitter.Emit(new MappingEnd());
                break;
            case List<object?> l:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, l.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in l) ToYaml(emitter, item, depth + 1);
                emitter.Emit(new SequenceEnd());
                break;
            case null or bool or Number:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value is null ? "null" : AsText(value), ScalarStyle.Plain, true, false));
                break;
            default:
                EmitText(emitter, AsText(value), isKey: false);
                break;
        }
    }

    static void EmitText(Emitter emitter, string text, bool isKey)
    {
        ScalarStyle style;
        // Anything a block scalar or plain text can't hold exactly goes in double quotes, with escapes.
        if (text.Any(c => c != '\n' && (char.IsControl(c) || char.IsSurrogate(c) || (int)c is 0xFEFF or 0x2028 or 0x2029 or 0xFFFE or 0xFFFF)))
            style = ScalarStyle.DoubleQuoted;
        else if (text.Contains('\n'))
            style = isKey || text[0] is '\n' or ' ' || text.Split('\n').Any(l => l.EndsWith(' ')) ? ScalarStyle.DoubleQuoted : ScalarStyle.Literal;
        else
            style = PlainInYaml().IsMatch(text) && !AmbiguousInYaml().IsMatch(text) ? ScalarStyle.Plain : ScalarStyle.SingleQuoted;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, style, true, true));
    }

    // ------------------------------------------------------------------ XML

    /// <summary>Follows the encoding the file declares. Entities only come from the file itself, within limits.</summary>
    static XDocument LoadXml(byte[] bytes)
    {
        TextDecoding.EnsureRegistered();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 10_000_000, IgnoreWhitespace = true };
        try { return Load(() => XmlReader.Create(new MemoryStream(bytes), settings)); }
        catch (XmlException)
        {
            // The declared encoding can be wrong (a UTF-16 label on a UTF-8 file): go by the bytes.
            var text = TextDecoding.Decode(bytes);
            return Load(() => XmlReader.Create(new StringReader(text), settings));
        }

        static XDocument Load(Func<XmlReader> open)
        {
            // Building the tree gets very slow for absurd nesting, so look before loading.
            using (var scan = open())
                while (scan.Read()) CheckDepth(scan.Depth);
            using var reader = open();
            return XDocument.Load(reader);
        }
    }

    /// <summary>Elements become maps; attributes become "@name"; repeated child names become lists.</summary>
    static object? FromXml(XDocument doc) => doc.Root == null ? null : new Map { new(doc.Root.Name.LocalName, FromXml(doc.Root, 0)) };

    static object? FromXml(XElement e, int depth)
    {
        CheckDepth(depth);
        var attributes = e.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        if (attributes.Count == 0 && !e.HasElements) return InferXml(e.Value);
        var map = new Map();
        foreach (var a in attributes) map.AddUnique("@" + a.Name.LocalName, InferXml(a.Value) ?? "");
        foreach (var group in e.Elements().GroupBy(c => c.Name.LocalName))
        {
            var items = group.Select(c => FromXml(c, depth + 1)).ToList();
            map.AddUnique(group.Key, items.Count == 1 ? items[0] : items);
        }
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length > 0) map.AddUnique("#text", InferXml(text));
        return map;
    }

    /// <summary>
    /// XML has no types, so only what's clearly a number or true/false becomes one. Anything over 15 digits is
    /// an ID rather than a quantity, and stays text.
    /// </summary>
    static object? InferXml(string value) => value switch
    {
        "" => null,
        "true" => true,
        "false" => false,
        _ => XmlNumber().IsMatch(value) ? new Number(value) : value,
    };

    [GeneratedRegex(@"^-?(0|[1-9][0-9]{0,14})(\.[0-9]+)?([eE][-+][0-9]{1,2})?\z")]
    private static partial Regex XmlNumber();

    static XElement ToXml(string name, object? value, int depth)
    {
        CheckDepth(depth);
        var element = new XElement(XmlName(name));
        switch (value)
        {
            case Map m:
                foreach (var (key, child) in m)
                {
                    bool simple = child is not (Map or List<object?>);
                    if (simple && key.Length > 1 && key[0] == '@' && XmlName(key[1..]) is var attribute && attribute != "xmlns" && element.Attribute(attribute) == null)
                        element.SetAttributeValue(attribute, SafeXml.Text(AsText(child)));
                    else if (simple && key == "#text") element.Add(SafeXml.Text(AsText(child)));
                    else if (child is List<object?> { Count: > 0 } list) foreach (var item in list) element.Add(ToXml(key, item, depth + 1));
                    else element.Add(ToXml(key, child, depth + 1));
                }
                break;
            case List<object?> l:
                foreach (var item in l) element.Add(ToXml("item", item, depth + 1));
                break;
            case null:
                break;
            default:
                element.Value = SafeXml.Text(AsText(value));
                break;
        }
        return element;
    }

    static string AsText(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        Number n => n.Text,
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "s", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static string XmlName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(XmlConvert.IsNCNameChar(c) ? c : '_');
        if (sb.Length == 0 || !XmlConvert.IsStartNCNameChar(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tables

    /// <summary>The first list of records in the data, flattened into columns ("address.city").</summary>
    /// <param name="asText">Plain-text tables keep every number exactly as written.</param>
    static Sheet ToSheet(object? tree, string title, bool asText)
    {
        var records = FindRecords(tree, 0) ?? FindValues(tree, 0) ?? (tree is Map { Count: > 0 } ? new List<object?> { tree } : null)
            ?? throw new ConversionException("This data has no list of records to turn into a table.");
        var rows = new List<Map>();
        var columns = new Dictionary<string, int>();
        foreach (var record in records)
        {
            var row = new Map();
            Flatten(row, record, "", 0);
            rows.Add(row);
            foreach (var (key, _) in row) columns.TryAdd(key, columns.Count);
        }
        var table = new List<object?[]> { columns.Keys.Cast<object?>().ToArray() };
        foreach (var row in rows)
        {
            var cells = new object?[columns.Count];
            foreach (var (key, value) in row) cells[columns[key]] = value is Number n ? (asText ? n.Text : Cell(n)) : value;
            table.Add(cells);
        }
        return new Sheet(title, table);
    }

    /// <summary>Spreadsheets hold 15 digits: numbers that would be rounded (long IDs, exact decimals) go in as text.</summary>
    static object Cell(Number n)
    {
        if (!double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return n.Text;
        var (digits, exponent) = Digits(n.Text);
        if (digits.Length == 0) return d;
        if (digits.Length > 15 && exponent >= 0) return n.Text;
        return (digits, exponent) == Digits(d.ToString("R", CultureInfo.InvariantCulture)) ? d : n.Text;
    }

    /// <summary>The significant digits and power of ten of a number's text, so different spellings can be compared.</summary>
    static (string digits, long exponent) Digits(string text)
    {
        int e = text.IndexOfAny(['e', 'E']);
        long exponent = e < 0 ? 0 : long.TryParse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) ? x : int.MaxValue;
        var mantissa = (e < 0 ? text : text[..e]).TrimStart('-', '+');
        int dot = mantissa.IndexOf('.');
        if (dot >= 0) exponent -= mantissa.Length - dot - 1;
        var digits = mantissa.Replace(".", "").TrimStart('0');
        var trimmed = digits.TrimEnd('0');
        return (trimmed, exponent + digits.Length - trimmed.Length);
    }

    static List<object?>? FindRecords(object? node, int depth)
    {
        CheckDepth(depth);
        return node switch
        {
            List<object?> { Count: > 0 } l when l.All(i => i is Map or null) => l,
            Map m => m.Select(kv => FindRecords(kv.Value, depth + 1)).FirstOrDefault(r => r != null),
            _ => null,
        };
    }

    /// <summary>Data that's just one list of plain values becomes a single column.</summary>
    static List<object?>? FindValues(object? node, int depth)
    {
        CheckDepth(depth);
        return node switch
        {
            List<object?> { Count: > 0 } l when l.All(i => i is not (Map or List<object?>)) => l.Select(i => (object?)new Map { new("value", i) }).ToList(),
            Map { Count: 1 } m => FindValues(m[0].Value, depth + 1),
            _ => null,
        };
    }

    static void Flatten(Map row, object? value, string prefix, int depth)
    {
        CheckDepth(depth);
        if (value is Map { Count: > 0 } m)
            foreach (var (key, child) in m) Flatten(row, child, prefix.Length > 0 ? $"{prefix}.{key}" : key, depth + 1);
        else if (value is List<object?> l)
            row.AddUnique(prefix, string.Join("; ", l.Select(i => i is Map or List<object?> ? JsonText(i) : AsText(i))));
        else if (prefix.Length > 0) row.AddUnique(prefix, value is Map ? null : value);
        else if (value is not (null or Map)) row.AddUnique("value", value);
    }
}

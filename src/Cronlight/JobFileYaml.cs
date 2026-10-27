using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Cronlight;

/// <summary>
/// Converts between YAML and the JSON a jobs file is read and written as, so both formats have the same shape and
/// rules. Unquoted values are read as YAML 1.2's core schema reads them: null, true and false, and numbers. Aliases
/// (*name) are refused, so a small file cannot expand into a huge one.
/// </summary>
internal static partial class JobFileYaml
{
    private const int MaxNodes = 1_000_000;
    // As System.Text.Json's limit for the JSON it is read as
    private const int MaxDepth = 64;

    public static JsonNode? Read(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (parser.TryConsume<StreamEnd>(out _))
            return null;
        parser.Consume<DocumentStart>();
        var nodes = 0;
        var root = ReadNode(parser, ref nodes, 0);
        parser.Consume<DocumentEnd>();
        if (!parser.TryConsume<StreamEnd>(out _))
            throw new YamlException(parser.Current!.Start, parser.Current.End, "The file has more than one YAML document");
        return root;
    }

    private static JsonNode? ReadNode(IParser parser, ref int nodes, int depth)
    {
        if (++nodes > MaxNodes)
            throw new YamlException(parser.Current!.Start, parser.Current.End, "The file is too large");
        if (depth > MaxDepth)
            throw new YamlException(parser.Current!.Start, parser.Current.End, $"The file nests lists and mappings more than {MaxDepth} deep");
        if (parser.TryConsume<Scalar>(out var scalar))
            // Quoted, or tagged as in !!str 42: text as written
            return scalar.Style == ScalarStyle.Plain && scalar.Tag.IsEmpty ? Resolve(scalar.Value) : JsonValue.Create(scalar.Value);
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var array = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _))
                array.Add(ReadNode(parser, ref nodes, depth + 1));
            return array;
        }
        if (parser.TryConsume<MappingStart>(out _))
        {
            var mapping = new JsonObject();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                if (mapping.ContainsKey(key.Value))
                    throw new YamlException(key.Start, key.End, $"'{key.Value}' is set twice");
                mapping[key.Value] = ReadNode(parser, ref nodes, depth + 1);
            }
            return mapping;
        }
        var current = parser.Current!;
        throw new YamlException(current.Start, current.End,
            current is AnchorAlias ? "Aliases (*name) are not supported: write the value out in full" : $"Unexpected {current.GetType().Name}");
    }

    /// <summary>An unquoted value, as YAML 1.2's core schema reads it.</summary>
    private static JsonNode? Resolve(string text) => text switch
    {
        "" or "~" or "null" or "Null" or "NULL" => null,
        "true" or "True" or "TRUE" => JsonValue.Create(true),
        "false" or "False" or "FALSE" => JsonValue.Create(false),
        _ when Integer().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) =>
            JsonValue.Create(whole),
        _ when Decimal().IsMatch(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) =>
            JsonValue.Create(number),
        _ => JsonValue.Create(text),
    };

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Decimal();

    public static string Write(JsonNode? node)
    {
        var writer = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(writer);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        WriteNode(emitter, node);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return writer.ToString();
    }

    private static void WriteNode(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject mapping:
                emitter.Emit(new MappingStart(null, null, isImplicit: true, mapping.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in mapping)
                {
                    Text(emitter, key);
                    WriteNode(emitter, value);
                }
                emitter.Emit(new MappingEnd());
                break;
            case JsonArray array:
                emitter.Emit(new SequenceStart(null, null, isImplicit: true, array.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array)
                    WriteNode(emitter, item);
                emitter.Emit(new SequenceEnd());
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                Text(emitter, value.GetValue<string>());
                break;
            case null:
                emitter.Emit(new Scalar("null"));
                break;
            default:
                emitter.Emit(new Scalar(node.ToJsonString()));
                break;
        }
    }

    /// <summary>
    /// Text, quoted when unquoted it would read as something else: "42" stays text. YAML 1.1's yes, no, on and off are
    /// quoted too, for the tools that still read them as true and false.
    /// </summary>
    private static void Text(IEmitter emitter, string text)
    {
        var style = Resolve(text) is not JsonValue resolved || resolved.GetValueKind() != JsonValueKind.String || YamlOneOne().IsMatch(text)
            ? ScalarStyle.DoubleQuoted
            : text.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.Any;
        emitter.Emit(new Scalar(null, null, text, style, isPlainImplicit: true, isQuotedImplicit: true));
    }

    [GeneratedRegex("^(y|Y|yes|Yes|YES|n|N|no|No|NO|on|On|ON|off|Off|OFF)$")]
    private static partial Regex YamlOneOne();
}

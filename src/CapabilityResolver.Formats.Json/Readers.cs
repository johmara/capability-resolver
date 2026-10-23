using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;
using YamlDotNet.Serialization;

namespace CapabilityResolver.Formats.Json;

/// <summary>Reads <c>*.fm.json</c> files.</summary>
public sealed class JsonModelReader : IModelReader
{
    public bool CanRead(string path) => path.EndsWith(".fm.json", StringComparison.OrdinalIgnoreCase);

    public FeatureModel Read(string text, string source) => FmJson.ParseModel(Parse(text, source), source);

    internal static JsonNode? Parse(string text, string source)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }
}

/// <summary>Reads <c>*.fm.yaml</c> / <c>*.fm.yml</c> files, which use the same schema as JSON.</summary>
public sealed class YamlModelReader : IModelReader
{
    public bool CanRead(string path) =>
        path.EndsWith(".fm.yaml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fm.yml", StringComparison.OrdinalIgnoreCase);

    public FeatureModel Read(string text, string source) => FmJson.ParseModel(YamlToJson.Parse(text, source), source);
}

/// <summary>Reads the global rules file (<c>rules.json</c>, <c>rules.yaml</c> or <c>rules.yml</c>): the paper's C_interactions.</summary>
public static class RulesReader
{
    public static IReadOnlyList<GlobalRule> Load(string path)
    {
        var text = File.ReadAllText(path);
        var node = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonModelReader.Parse(text, path)
            : YamlToJson.Parse(text, path);
        return FmJson.ParseRules(node, path);
    }
}

internal static class YamlToJson
{
    public static JsonNode? Parse(string yaml, string source)
    {
        try
        {
            return Convert(new DeserializerBuilder().Build().Deserialize<object?>(yaml));
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }

    // Scalars stay strings; FmJson reads numbers and booleans tolerantly.
    private static JsonNode? Convert(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => new JsonObject(
            map.Select(kv => KeyValuePair.Create(kv.Key.ToString()!, Convert(kv.Value)))),
        IEnumerable<object> list => new JsonArray(list.Select(Convert).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}

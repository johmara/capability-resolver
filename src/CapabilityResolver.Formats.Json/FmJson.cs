using System.Text.Json.Nodes;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Json;

/// <summary>
/// The JSON feature model schema (YAML uses the same one). It follows the de facto convention of the
/// flamapy JSON format: a nested <c>features</c> tree whose <c>relations</c> hold the children.
/// Differences from flamapy: <c>abstract</c> is a real boolean (strings are tolerated on read), a feature
/// may carry <c>requires</c>/<c>provides</c> capability lists, an optional <c>id</c> separates the stable
/// identifier from the display <c>name</c>, and <c>constraints</c> are plain strings in UVL's boolean syntax.
/// </summary>
public static class FmJson
{
    private const string MaxCardinality = "*";

    public static FeatureModel ParseModel(JsonNode? node, string source)
    {
        try
        {
            var obj = node as JsonObject ?? throw new FormatException("expected a JSON object at the top level.");
            var root = ParseFeature(obj["features"] as JsonObject
                ?? throw new FormatException("missing \"features\" (the root feature)."));

            var id = Str(obj["id"]) ?? root.Id;
            var scope = obj["scope"] as JsonObject;
            var constraints = (obj["constraints"] as JsonArray ?? [])
                .Select(c => ExprParser.Parse(Str(c is JsonObject co ? co["expr"] : c)
                    ?? throw new FormatException("a constraint needs an expression string.")))
                .ToList();

            return new FeatureModel(
                id,
                Str(obj["name"]) ?? root.Name,
                root,
                constraints,
                new Scope(Str(scope?["assetPath"]) ?? id, Str(scope?["owner"])));
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }

    public static JsonObject ToJson(FeatureModel model)
    {
        var obj = new JsonObject
        {
            ["id"] = model.Id,
            ["name"] = model.Name,
            ["scope"] = new JsonObject { ["assetPath"] = model.Scope.AssetPath, ["owner"] = model.Scope.Owner },
            ["features"] = WriteFeature(model.Root),
        };
        if (model.Constraints.Count > 0)
        {
            obj["constraints"] = new JsonArray(model.Constraints.Select(c => (JsonNode)c.ToString()).ToArray());
        }
        return obj;
    }

    private static Feature ParseFeature(JsonObject node)
    {
        var name = Str(node["name"]) ?? throw new FormatException("a feature needs a \"name\".");
        var groups = (node["relations"] as JsonArray ?? [])
            .Select(r => ParseGroup(r as JsonObject ?? throw new FormatException($"feature \"{name}\": a relation must be an object.")))
            .ToList();

        return new Feature(
            Str(node["id"]) ?? name,
            name,
            groups,
            Strings(node["requires"]),
            Strings(node["provides"]),
            Bool(node["abstract"]));
    }

    private static Group ParseGroup(JsonObject node)
    {
        var children = (node["children"] as JsonArray ?? [])
            .Select(c => ParseFeature(c as JsonObject ?? throw new FormatException("a child feature must be an object.")))
            .ToList();

        var type = Str(node["type"]) ?? throw new FormatException("a relation needs a \"type\".");
        return type.ToUpperInvariant() switch
        {
            "MANDATORY" => new Group(GroupKind.Mandatory, children),
            "OPTIONAL" => new Group(GroupKind.Optional, children),
            "OR" => new Group(GroupKind.Or, children),
            "XOR" or "ALTERNATIVE" => new Group(GroupKind.Alternative, children),
            "MUTEX" => new Group(GroupKind.Cardinality, children, 0, 1),
            "CARDINALITY" => new Group(GroupKind.Cardinality, children,
                Int(node["card_min"]) ?? 0, Int(node["card_max"]) ?? int.MaxValue),
            _ => throw new FormatException($"unknown relation type \"{type}\"."),
        };
    }

    private static JsonObject WriteFeature(Feature feature)
    {
        var obj = new JsonObject();
        if (feature.Id != feature.Name) obj["id"] = feature.Id;
        obj["name"] = feature.Name;
        if (feature.IsAbstract) obj["abstract"] = true;
        if (feature.Requires.Count > 0) obj["requires"] = ToArray(feature.Requires.Order());
        if (feature.Provides.Count > 0) obj["provides"] = ToArray(feature.Provides.Order());
        if (feature.Groups.Count > 0) obj["relations"] = new JsonArray(feature.Groups.Select(WriteGroup).ToArray());
        return obj;
    }

    private static JsonNode WriteGroup(Group group)
    {
        var obj = new JsonObject
        {
            ["type"] = group.Kind switch
            {
                GroupKind.Mandatory => "MANDATORY",
                GroupKind.Optional => "OPTIONAL",
                GroupKind.Or => "OR",
                GroupKind.Alternative => "XOR",
                _ => "CARDINALITY",
            },
        };
        if (group.Kind == GroupKind.Cardinality)
        {
            obj["card_min"] = group.Min;
            if (group.Max != int.MaxValue) obj["card_max"] = group.Max;
        }
        obj["children"] = new JsonArray(group.Children.Select(c => (JsonNode)WriteFeature(c)).ToArray());
        return obj;
    }

    public static IReadOnlyList<GlobalRule> ParseRules(JsonNode? node, string source)
    {
        try
        {
            var rules = (node as JsonObject)?["rules"] as JsonArray
                ?? throw new FormatException("expected an object with a \"rules\" array.");
            return rules.Select(r => ParseRule(r as JsonObject ?? throw new FormatException("a rule must be an object."))).ToList();
        }
        catch (FormatException e)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }

    private static GlobalRule ParseRule(JsonObject node)
    {
        string Need(string key) => Str(node[key]) ?? throw new FormatException($"rule needs \"{key}\".");
        var why = Str(node["rationale"]) ?? "";
        var type = Need("type");
        return type.ToLowerInvariant() switch
        {
            "mutex" => new CapabilityMutex(Need("a"), Need("b"), why),
            "implies" => new CapabilityImplies(Need("a"), Need("b"), why),
            "singleprovider" => new SingleProvider(Need("capability"), why),
            _ => throw new FormatException($"unknown rule type \"{type}\"."),
        };
    }

    // Values are read tolerantly (YAML scalars arrive as strings, flamapy writes booleans as "False").

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;

    private static bool Bool(JsonNode? node) =>
        Str(node) is { } s && bool.TryParse(s, out var b) && b;

    private static int? Int(JsonNode? node) => Str(node) switch
    {
        null or "" => null,
        MaxCardinality => int.MaxValue,
        var s when int.TryParse(s, out var i) => i,
        var s => throw new FormatException($"\"{s}\" is not a number."),
    };

    private static IReadOnlySet<string> Strings(JsonNode? node) => node switch
    {
        null => Feature.NoCapabilities,
        JsonArray a => a.Select(Str).OfType<string>().ToHashSet(),
        _ => Str(node) is { } s ? new HashSet<string> { s } : Feature.NoCapabilities,
    };

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)v).ToArray());
}

using System.Text;

namespace CapabilityResolver.Model;

/// <summary>
/// Deterministic text form of a model. Two models that dump to the same text are structurally identical,
/// which is how the tests compare the same model loaded from different file formats.
/// </summary>
public static class ModelDump
{
    public static string ToText(FeatureModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"model {model.Id} \"{model.Name}\" scope={model.Scope.AssetPath} owner={model.Scope.Owner}");
        Write(sb, model.Root, 0);
        foreach (var constraint in model.Constraints) sb.AppendLine($"constraint {constraint}");
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Feature feature, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.Append($"{pad}{feature.Id} \"{feature.Name}\"");
        if (feature.IsAbstract) sb.Append(" abstract");
        if (feature.Requires.Count > 0) sb.Append($" requires=[{string.Join(",", feature.Requires.Order())}]");
        if (feature.Provides.Count > 0) sb.Append($" provides=[{string.Join(",", feature.Provides.Order())}]");
        sb.AppendLine();

        foreach (var group in feature.Groups)
        {
            var bounds = group.Kind == GroupKind.Cardinality ? $" [{group.Min}..{group.Max}]" : "";
            sb.AppendLine($"{pad}  <{group.Kind}{bounds}>");
            foreach (var child in group.Children) Write(sb, child, depth + 2);
        }
    }
}

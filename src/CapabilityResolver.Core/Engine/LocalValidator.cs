using CapabilityResolver.Model;

namespace CapabilityResolver.Engine;

public sealed record LocalValidity(string ModelId, IReadOnlyList<Diagnostic> Violations)
{
    public bool Valid => Violations.Count == 0;
}

/// <summary>
/// Tree semantics plus local cross-tree constraints, checked against ONE model and that
/// model's selection alone. The signature is the decentralization invariant made literal:
/// this class never receives a second model, so it cannot leak state across scopes.
/// </summary>
public static class LocalValidator
{
    /// <summary>The root is always part of a configuration; this returns the selection with it added.</summary>
    public static HashSet<string> WithRoot(FeatureModel model, IReadOnlySet<string> selection) =>
        new(selection) { model.Root.Id };

    public static LocalValidity Check(FeatureModel model, IReadOnlySet<string> selection)
    {
        var sel = WithRoot(model, selection);
        var violations = new List<Diagnostic>();
        FeatureRef At(string id) => new(model.Id, id);

        foreach (var featureId in model.FeatureOrder.Where(sel.Contains))
        {
            var feature = model[featureId];
            var parentId = model.ParentOf(featureId);

            if (parentId is not null && !sel.Contains(parentId))
            {
                violations.Add(new(
                    DiagnosticCodes.OrphanChild,
                    $"\"{feature.Name}\" is selected, but its parent \"{model[parentId].Name}\" is not.",
                    [At(featureId), At(parentId)]));
            }
        }

        foreach (var feature in model.Features.Where(f => sel.Contains(f.Id)))
        {
            foreach (var group in feature.Groups)
            {
                CheckGroup(model, feature, group, sel, violations);
            }
        }

        foreach (var constraint in model.Constraints)
        {
            if (constraint.Evaluate(sel.Contains)) continue;

            violations.Add(new(
                DiagnosticCodes.ConstraintViolated,
                $"Constraint {constraint} is violated.",
                constraint.Features().Distinct().Select(At).ToList()));
        }

        return new LocalValidity(model.Id, violations);
    }

    public static IReadOnlyDictionary<string, LocalValidity> CheckAll(DecentralizedSystem system, Configuration config) =>
        system.Models.ToDictionary(m => m.Id, m => Check(m, config.For(m.Id)));

    private static void CheckGroup(
        FeatureModel model, Feature parent, Group group, HashSet<string> sel, List<Diagnostic> violations)
    {
        FeatureRef At(string id) => new(model.Id, id);
        var members = group.Children.Select(c => c.Id).ToList();
        var selected = members.Where(sel.Contains).ToList();
        var names = string.Join(", ", group.Children.Select(c => c.Name));

        switch (group.Kind)
        {
            case GroupKind.Mandatory:
                foreach (var child in group.Children.Where(c => !sel.Contains(c.Id)))
                {
                    violations.Add(new(
                        DiagnosticCodes.MandatoryUnselected,
                        $"\"{child.Name}\" is mandatory under \"{parent.Name}\" and must be selected.",
                        [At(child.Id), At(parent.Id)]));
                }
                break;

            case GroupKind.Alternative when selected.Count == 0:
                violations.Add(new(
                    DiagnosticCodes.XorNone,
                    $"Exactly one of {{{names}}} must be selected under \"{parent.Name}\".",
                    members.Select(At).ToList()));
                break;

            case GroupKind.Alternative when selected.Count > 1:
                violations.Add(new(
                    DiagnosticCodes.XorMultiple,
                    $"Only one of {{{names}}} may be selected under \"{parent.Name}\", but {selected.Count} are.",
                    selected.Select(At).ToList()));
                break;

            case GroupKind.Or when selected.Count == 0:
                violations.Add(new(
                    DiagnosticCodes.OrNone,
                    $"At least one of {{{names}}} must be selected under \"{parent.Name}\".",
                    members.Select(At).ToList()));
                break;

            case GroupKind.Cardinality:
                var (min, max) = group.Bounds!.Value;
                if (selected.Count < min || selected.Count > max)
                {
                    violations.Add(new(
                        DiagnosticCodes.CardinalityViolated,
                        $"Between {min} and {max} of {{{names}}} must be selected under \"{parent.Name}\", but {selected.Count} are.",
                        members.Select(At).ToList()));
                }
                break;
        }
    }
}

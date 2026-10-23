namespace CapabilityResolver.Model;

/// <summary>A feature, fully qualified by the model that owns it. Feature ids are only unique within one model.</summary>
public readonly record struct FeatureRef(string ModelId, string FeatureId)
{
    public override string ToString() => $"{ModelId}::{FeatureId}";
}

public enum GroupKind
{
    /// <summary>Every child is mandatory when the parent is selected.</summary>
    Mandatory,
    /// <summary>Every child is optional.</summary>
    Optional,
    /// <summary>At least one child (1..n).</summary>
    Or,
    /// <summary>Exactly one child (1..1).</summary>
    Alternative,
    /// <summary>Between <see cref="Group.Min"/> and <see cref="Group.Max"/> children.</summary>
    Cardinality,
}

/// <summary>A set of sibling features under one parent, related by <see cref="Kind"/>.</summary>
public sealed record Group(GroupKind Kind, IReadOnlyList<Feature> Children, int Min = 0, int Max = int.MaxValue)
{
    /// <summary>The effective (min, max) bounds on how many children may be selected, or null for per-child kinds.</summary>
    public (int Min, int Max)? Bounds => Kind switch
    {
        GroupKind.Or => (1, Children.Count),
        GroupKind.Alternative => (1, 1),
        GroupKind.Cardinality => (Min, Math.Min(Max, Children.Count)),
        _ => null,
    };
}

/// <summary>
/// One feature. <see cref="Requires"/>/<see cref="Provides"/> name capabilities, never another
/// model's features: they are the only way models refer to each other.
/// </summary>
public sealed record Feature(
    string Id,
    string Name,
    IReadOnlyList<Group> Groups,
    IReadOnlySet<string> Requires,
    IReadOnlySet<string> Provides,
    bool IsAbstract = false)
{
    public static readonly IReadOnlyList<Group> NoGroups = [];
    public static readonly IReadOnlySet<string> NoCapabilities = new HashSet<string>();

    public static Feature Leaf(string id, string? name = null) =>
        new(id, name ?? id, NoGroups, NoCapabilities, NoCapabilities);

    public IEnumerable<Feature> Children => Groups.SelectMany(g => g.Children);
}

/// <summary>
/// The paper's notion of scope: the part of the code base the model applies to,
/// e.g. a repository or folder.
/// </summary>
public sealed record Scope(string AssetPath, string? Owner = null);

/// <summary>
/// One decentralized (or, standing alone, plain) feature model owned by one scope.
/// Constraints are local: they may only mention features of this model.
/// </summary>
public sealed class FeatureModel
{
    private readonly Dictionary<string, Feature> _features = new();
    private readonly Dictionary<string, string?> _parents = new();
    private readonly Dictionary<string, Group> _groupOf = new();

    public FeatureModel(string id, string name, Feature root, IReadOnlyList<Expr>? constraints = null, Scope? scope = null)
    {
        Id = id;
        Name = name;
        Root = root;
        Constraints = constraints ?? [];
        Scope = scope ?? new Scope(id);

        var order = new List<string>();
        Index(root, null, null, order);
        FeatureOrder = order;

        foreach (var constraint in Constraints)
        {
            foreach (var featureId in constraint.Features())
            {
                if (!_features.ContainsKey(featureId))
                {
                    throw new ArgumentException(
                        $"Constraint in model '{id}' mentions unknown feature '{featureId}'. " +
                        "Constraints are local; cross-model rules must use capabilities.");
                }
            }
        }
    }

    public string Id { get; }
    public string Name { get; }
    public Feature Root { get; }
    public IReadOnlyList<Expr> Constraints { get; }
    public Scope Scope { get; }

    /// <summary>Pre-order list of every feature id: a stable basis for variable numbering.</summary>
    public IReadOnlyList<string> FeatureOrder { get; }

    public Feature this[string featureId] => _features.TryGetValue(featureId, out var f)
        ? f
        : throw new KeyNotFoundException($"Unknown feature '{featureId}' in model '{Id}'.");

    public bool Contains(string featureId) => _features.ContainsKey(featureId);

    public string? ParentOf(string featureId) => _parents[featureId];

    /// <summary>The group that holds the feature, or null for the root.</summary>
    public Group? GroupOf(string featureId) => _groupOf.GetValueOrDefault(featureId);

    public IEnumerable<Feature> Features => FeatureOrder.Select(id => _features[id]);

    private void Index(Feature feature, string? parent, Group? group, List<string> order)
    {
        if (!_features.TryAdd(feature.Id, feature))
        {
            throw new ArgumentException($"Duplicate feature id '{feature.Id}' in model '{Id}'.");
        }

        _parents[feature.Id] = parent;
        if (group is not null) _groupOf[feature.Id] = group;
        order.Add(feature.Id);

        foreach (var g in feature.Groups)
        {
            foreach (var child in g.Children) Index(child, feature.Id, g, order);
        }
    }
}

/// <summary>
/// A rule that crosses model boundaries without being a capability requirement: the paper's
/// C_interactions. Always phrased over capabilities, never over another scope's features.
/// </summary>
public abstract record GlobalRule(string Rationale)
{
    /// <summary>Compact notation: <c>A ≠ B</c>, <c>A ⇒ B</c>, <c>C ≤ 1 provider</c>.</summary>
    public abstract string Notation { get; }
}

/// <summary><paramref name="A"/> and <paramref name="B"/> are never both provided.</summary>
public sealed record CapabilityMutex(string A, string B, string Rationale = "") : GlobalRule(Rationale)
{
    public override string Notation => $"{A} ≠ {B}";
}

/// <summary>Whenever <paramref name="A"/> is provided, <paramref name="B"/> must be provided too.</summary>
public sealed record CapabilityImplies(string A, string B, string Rationale = "") : GlobalRule(Rationale)
{
    public override string Notation => $"{A} ⇒ {B}";
}

/// <summary>At most one selected feature provides <paramref name="Capability"/>.</summary>
public sealed record SingleProvider(string Capability, string Rationale = "") : GlobalRule(Rationale)
{
    public override string Notation => $"{Capability} ≤ 1 provider";
}

/// <summary>The set of models in play plus the (rare) global rules between their capabilities. There is no global feature model.</summary>
public sealed class DecentralizedSystem
{
    public DecentralizedSystem(IReadOnlyList<FeatureModel> models, IReadOnlyList<GlobalRule>? rules = null)
    {
        var duplicate = models.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate model id '{duplicate.Key}'.");

        Models = models;
        Rules = rules ?? [];
    }

    public IReadOnlyList<FeatureModel> Models { get; }
    public IReadOnlyList<GlobalRule> Rules { get; }

    public FeatureModel Model(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");
}

/// <summary>Per model, the set of selected feature ids. A missing model means nothing is selected there.</summary>
public sealed class Configuration
{
    private readonly Dictionary<string, IReadOnlySet<string>> _selected;

    public Configuration(IEnumerable<KeyValuePair<string, IReadOnlySet<string>>>? selected = null)
    {
        _selected = selected?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
    }

    public static Configuration Of(params (string ModelId, string[] Features)[] entries) =>
        new(entries.Select(e => KeyValuePair.Create(e.ModelId, (IReadOnlySet<string>)e.Features.ToHashSet())));

    public IReadOnlySet<string> For(string modelId) =>
        _selected.GetValueOrDefault(modelId) ?? new HashSet<string>();
}

using CapabilityResolver.Model;

namespace CapabilityResolver.Engine;

public sealed record CapabilityLedger(
    /// <summary>Capabilities required by currently selected features, and who requires them.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<FeatureRef>> Required,
    /// <summary>Capabilities provided by currently selected features.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<FeatureRef>> Provided,
    /// <summary>Every feature that provides a capability, selected or not. Needed to attribute a missing capability to the scopes that could supply it.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<FeatureRef>> PotentialProviders)
{
    public static CapabilityLedger Aggregate(DecentralizedSystem system, Configuration config)
    {
        var required = new Dictionary<string, List<FeatureRef>>();
        var provided = new Dictionary<string, List<FeatureRef>>();
        var potential = new Dictionary<string, List<FeatureRef>>();

        foreach (var model in system.Models)
        {
            var sel = LocalValidator.WithRoot(model, config.For(model.Id));

            foreach (var feature in model.Features)
            {
                var at = new FeatureRef(model.Id, feature.Id);
                var selected = sel.Contains(feature.Id);

                foreach (var capability in feature.Provides)
                {
                    Add(potential, capability, at);
                    if (selected) Add(provided, capability, at);
                }

                if (!selected) continue;
                foreach (var capability in feature.Requires) Add(required, capability, at);
            }
        }

        return new(Freeze(required), Freeze(provided), Freeze(potential));
    }

    public IReadOnlyList<FeatureRef> ProvidersOf(string capability) =>
        Provided.GetValueOrDefault(capability) ?? [];

    private static void Add(Dictionary<string, List<FeatureRef>> map, string key, FeatureRef value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<FeatureRef>> Freeze(Dictionary<string, List<FeatureRef>> map) =>
        map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<FeatureRef>)kv.Value);
}

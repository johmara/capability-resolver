using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Analysis.Tests;

/// <summary>Ground truth by enumeration, using only <see cref="LocalValidator"/> and <see cref="GlobalResolver"/>, never the solver.</summary>
internal static class BruteForce
{
    public static IReadOnlyList<HashSet<string>> ValidConfigs(FeatureModel model)
    {
        var others = model.FeatureOrder.Where(id => id != model.Root.Id).ToList();
        var valid = new List<HashSet<string>>();
        for (var mask = 0; mask < 1 << others.Count; mask++)
        {
            var selection = new HashSet<string> { model.Root.Id };
            for (var i = 0; i < others.Count; i++)
            {
                if ((mask & (1 << i)) != 0) selection.Add(others[i]);
            }
            if (LocalValidator.Check(model, selection).Valid) valid.Add(selection);
        }
        return valid;
    }

    /// <summary>Every globally valid configuration: the product of local ones, filtered by the resolver.</summary>
    public static IReadOnlyList<Configuration> ValidGlobalConfigs(DecentralizedSystem system)
    {
        var perModel = system.Models.Select(m => (m.Id, Configs: ValidConfigs(m))).ToList();
        var results = new List<Configuration>();

        void Extend(int index, Dictionary<string, IReadOnlySet<string>> chosen)
        {
            if (index == perModel.Count)
            {
                var config = new Configuration(chosen);
                if (GlobalResolver.Resolve(system, config).Valid) results.Add(config);
                return;
            }

            var (id, configs) = perModel[index];
            foreach (var local in configs)
            {
                chosen[id] = local;
                Extend(index + 1, chosen);
            }
            chosen.Remove(id);
        }

        Extend(0, new Dictionary<string, IReadOnlySet<string>>());
        return results;
    }

    public static IEnumerable<HashSet<string>> AllSubsets(IReadOnlyList<string> ids)
    {
        for (var mask = 0; mask < 1 << ids.Count; mask++)
        {
            yield return ids.Where((_, i) => (mask & (1 << i)) != 0).ToHashSet();
        }
    }
}

internal static class Fixtures
{
    private static string Dir(string format) => Path.Combine(AppContext.BaseDirectory, "fixtures", "phone", format);

    public static DecentralizedSystem Phone(string format = "json")
    {
        var loader = new ModelLoader(new JsonModelReader(), new YamlModelReader());
        var rules = RulesReader.Load(Directory.EnumerateFiles(Dir(format), "rules.*").Single());
        return loader.LoadSystem([Dir(format)], rules);
    }
}

/// <summary>Small models with cardinalities and constraints, beyond what the phone example exercises.</summary>
internal static class SmallModels
{
    private static Group G(GroupKind kind, params string[] ids) => new(kind, ids.Select(id => Feature.Leaf(id)).ToList());

    private static Feature Root(string id, params Group[] groups) =>
        new(id, id, groups, Feature.NoCapabilities, Feature.NoCapabilities);

    public static IEnumerable<FeatureModel> All() => [Extras(), Constrained(), Unsatisfiable()];

    public static FeatureModel Extras() => new("extras", "Extras",
        Root("Extras",
            G(GroupKind.Or, "A", "B", "C"),
            new Group(GroupKind.Cardinality, new[] { "X", "Y", "Z" }.Select(id => Feature.Leaf(id)).ToList(), 1, 2)),
        [ExprParser.Parse("A => !X")]);

    public static FeatureModel Constrained() => new("constrained", "Constrained",
        Root("Main", G(GroupKind.Mandatory, "M"), G(GroupKind.Optional, "P", "Q", "R")),
        [ExprParser.Parse("P <=> (Q | R)"), ExprParser.Parse("!(Q & R)"), ExprParser.Parse("R => M")]);

    public static FeatureModel Unsatisfiable() => new("unsat", "Unsat",
        Root("Root", G(GroupKind.Mandatory, "A")),
        [ExprParser.Parse("!A")]);
}

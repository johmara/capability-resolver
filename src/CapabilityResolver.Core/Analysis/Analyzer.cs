using CapabilityResolver.Model;
using CapabilityResolver.Solving;

namespace CapabilityResolver.Analysis;

/// <summary>What is forced, derived and impossible in one model, given a partial selection.</summary>
/// <param name="Consistent">False when no valid configuration extends the partial selection.</param>
/// <param name="Selection">Everything every valid completion selects: the partial selection plus its consequences.</param>
/// <param name="Derived"><paramref name="Selection"/> minus what was selected by hand.</param>
/// <param name="Dead">Features no valid completion can select.</param>
public sealed record ModelPropagation(
    string ModelId,
    bool Consistent,
    IReadOnlySet<string> Selection,
    IReadOnlySet<string> Derived,
    IReadOnlySet<string> Dead);

/// <summary>Propagation across all models at once, including what capability requirements force in other scopes.</summary>
public sealed record SystemPropagation(bool Consistent, IReadOnlyDictionary<string, ModelPropagation> ByModel);

/// <summary>The cheapest way to make a capability available: extra features to select, and whom to tell.</summary>
public sealed record ProviderSuggestion(string Capability, IReadOnlyList<FeatureRef> Add, IReadOnlyList<string> NotifyModels);

/// <summary>
/// SAT(FM₁), …, SAT(FMₙ), SAT(C_capabilities): small independent problems, never one merged model.
/// A failure is conclusive; success of all parts is necessary but not sufficient for a global solution.
/// </summary>
public sealed record DecomposedAnalysis(IReadOnlyDictionary<string, bool> ModelsSatisfiable, bool CapabilityLayerSatisfiable)
{
    public bool AllSatisfiable => CapabilityLayerSatisfiable && ModelsSatisfiable.Values.All(v => v);
}

/// <summary>Solver-backed analyses. Every call builds a fresh solver from <paramref name="createSolver"/>.</summary>
public sealed class Analyzer(Func<ISolver> createSolver)
{
    public bool IsSatisfiable(FeatureModel model)
    {
        using var solver = createSolver();
        CnfEncoder.Encode(model, solver);
        return solver.Solve([]).Satisfiable;
    }

    /// <summary>Features that no valid configuration of the model contains. Empty if the model itself is unsatisfiable.</summary>
    public IReadOnlySet<string> DeadFeatures(FeatureModel model) => Propagate(model, new HashSet<string>()).Dead;

    /// <summary>Features that every valid configuration of the model contains. Empty if the model itself is unsatisfiable.</summary>
    public IReadOnlySet<string> CoreFeatures(FeatureModel model) =>
        Propagate(model, new HashSet<string>()) is { Consistent: true } result ? result.Selection : new HashSet<string>();

    /// <summary>Propagation inside one model, using only that model.</summary>
    public ModelPropagation Propagate(FeatureModel model, IReadOnlySet<string> manual)
    {
        using var solver = createSolver();
        var encoding = CnfEncoder.Encode(model, solver);
        return ToPropagation(encoding, manual, ComputeBackbone(solver, encoding.Selected(manual).ToList(), encoding.Variables));
    }

    /// <summary>
    /// Can the partial configuration be completed into a globally valid one? Solves every model's clauses
    /// together with the capability clauses, which is exact; the models are still never merged into one.
    /// </summary>
    public SystemPropagation CanComplete(DecentralizedSystem system, Configuration partial)
    {
        using var solver = createSolver();
        var encodings = EncodeSystem(system, solver);

        var assumptions = system.Models.SelectMany(m => encodings[m.Id].Selected(partial.For(m.Id))).ToList();
        var backbone = ComputeBackbone(solver, assumptions, encodings.Values.SelectMany(e => e.Variables).ToList());

        var byModel = system.Models.ToDictionary(m => m.Id, m => ToPropagation(encodings[m.Id], partial.For(m.Id), backbone));
        return new SystemPropagation(backbone is not null, byModel);
    }

    /// <summary>
    /// The fewest extra features that make <paramref name="capability"/> provided while keeping the
    /// whole system valid and every already selected feature selected. Null if that is impossible.
    /// </summary>
    public ProviderSuggestion? SuggestProviders(DecentralizedSystem system, Configuration config, string capability)
    {
        using var solver = createSolver();
        var encodings = EncodeSystem(system, solver);

        var providers = system.Models
            .SelectMany(m => m.Features.Where(f => f.Provides.Contains(capability)).Select(f => encodings[m.Id].Var(f.Id)))
            .ToList();
        if (providers.Count == 0) return null;
        solver.AddClause([.. providers]);

        var assumptions = system.Models.SelectMany(m => encodings[m.Id].Selected(config.For(m.Id))).ToList();
        var assumed = assumptions.ToHashSet();
        var candidates = encodings.Values.SelectMany(e => e.Variables).Where(v => !assumed.Contains(v)).ToList();

        var result = solver.Minimize(candidates, assumptions);
        if (!result.Satisfiable) return null;

        var add = system.Models
            .SelectMany(m => encodings[m.Id].FeaturesWhere(v => result[v] && !assumed.Contains(v)).Select(id => new FeatureRef(m.Id, id)))
            .ToList();
        return new ProviderSuggestion(capability, add, add.Select(a => a.ModelId).Distinct().ToList());
    }

    public DecomposedAnalysis AnalyzeDecomposed(DecentralizedSystem system)
    {
        var perModel = system.Models.ToDictionary(m => m.Id, IsSatisfiable);

        using var solver = createSolver();
        var free = system.Models.ToDictionary(m => m.Id, m => CnfEncoder.EncodeFree(m, solver));
        foreach (var model in system.Models) solver.AddClause(free[model.Id].Var(model.Root.Id));
        CnfEncoder.EncodeCapabilities(system, solver, free);

        return new DecomposedAnalysis(perModel, solver.Solve([]).Satisfiable);
    }

    private static Dictionary<string, ModelEncoding> EncodeSystem(DecentralizedSystem system, ISolver solver)
    {
        var encodings = system.Models.ToDictionary(m => m.Id, m => CnfEncoder.Encode(m, solver));
        CnfEncoder.EncodeCapabilities(system, solver, encodings);
        return encodings;
    }

    private static ModelPropagation ToPropagation(ModelEncoding encoding, IReadOnlySet<string> manual, Backbone? backbone)
    {
        var withRoot = manual.Append(encoding.Model.Root.Id).Where(encoding.Model.Contains).ToHashSet();
        if (backbone is null) return new(encoding.Model.Id, false, withRoot, new HashSet<string>(), new HashSet<string>());

        var selection = encoding.FeaturesWhere(backbone.ForcedTrue.Contains).ToHashSet();
        return new(
            encoding.Model.Id,
            true,
            selection,
            selection.Where(id => !withRoot.Contains(id)).ToHashSet(),
            encoding.FeaturesWhere(backbone.ForcedFalse.Contains).ToHashSet());
    }

    private sealed record Backbone(HashSet<int> ForcedTrue, HashSet<int> ForcedFalse);

    /// <summary>
    /// Variables fixed to one value in every model satisfying the assumptions, or null if there is none.
    /// Each model found is used to discard candidates, so the number of solves stays near the number
    /// of variables that really are forced.
    /// </summary>
    private static Backbone? ComputeBackbone(ISolver solver, IReadOnlyList<int> assumptions, IReadOnlyCollection<int> variables)
    {
        var first = solver.Solve(assumptions);
        if (!first.Satisfiable) return null;

        var canBeForcedTrue = variables.Where(v => first[v]).ToHashSet();
        var canBeForcedFalse = variables.Where(v => !first[v]).ToHashSet();

        void Narrow(SolveResult model)
        {
            canBeForcedTrue.RemoveWhere(v => !model[v]);
            canBeForcedFalse.RemoveWhere(v => model[v]);
        }

        foreach (var v in canBeForcedTrue.ToList())
        {
            if (!canBeForcedTrue.Contains(v)) continue;
            var other = solver.Solve([.. assumptions, -v]);
            if (other.Satisfiable) Narrow(other);
        }

        foreach (var v in canBeForcedFalse.ToList())
        {
            if (!canBeForcedFalse.Contains(v)) continue;
            var other = solver.Solve([.. assumptions, v]);
            if (other.Satisfiable) Narrow(other);
        }

        return new Backbone(canBeForcedTrue, canBeForcedFalse);
    }
}

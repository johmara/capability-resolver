using CapabilityResolver.Model;

namespace CapabilityResolver.Solving;

/// <summary>The solver variable of every feature in one model.</summary>
public sealed class ModelEncoding(FeatureModel model, IReadOnlyDictionary<string, int> vars)
{
    public FeatureModel Model { get; } = model;

    public IReadOnlyCollection<int> Variables => vars.Values.ToList();

    public int Var(string featureId) => vars[featureId];

    /// <summary>Literals asserting that the given features (and the root) are selected. Ids the model does not have are ignored.</summary>
    public IEnumerable<int> Selected(IEnumerable<string> featureIds) =>
        featureIds.Append(Model.Root.Id).Distinct().Where(vars.ContainsKey).Select(id => vars[id]);

    public IEnumerable<string> FeaturesWhere(Func<int, bool> predicate) =>
        Model.FeatureOrder.Where(id => predicate(vars[id]));
}

/// <summary>
/// Turns feature models into clauses. The encoding is split along the paper's decomposition:
/// each model is encoded on its own (C_i), and the capability clauses (C_capabilities) are the only
/// ones that mention more than one model.
/// </summary>
public static class CnfEncoder
{
    /// <summary>One model's tree semantics and local constraints. Mirrors <c>LocalValidator</c> exactly.</summary>
    public static ModelEncoding Encode(FeatureModel model, ISolver solver)
    {
        var encoding = EncodeFree(model, solver);
        int V(string id) => encoding.Var(id);

        solver.AddClause(V(model.Root.Id));

        foreach (var feature in model.Features)
        {
            var self = V(feature.Id);
            if (model.ParentOf(feature.Id) is { } parent) solver.AddClause(-self, V(parent));

            foreach (var group in feature.Groups)
            {
                var members = group.Children.Select(c => V(c.Id)).ToList();
                switch (group.Kind)
                {
                    case GroupKind.Mandatory:
                        foreach (var member in members) solver.AddClause(-self, member);
                        break;

                    case GroupKind.Or:
                        solver.AddClause([-self, .. members]);
                        break;

                    case GroupKind.Alternative:
                        solver.AddClause([-self, .. members]);
                        solver.AddAtMost(1, members);
                        break;

                    case GroupKind.Cardinality:
                        var (min, max) = group.Bounds!.Value;
                        // Members imply the parent, so the upper bound needs no condition.
                        solver.AddAtMost(max, members);
                        if (min > 0) solver.AddAtLeast(min, members, onlyIf: self);
                        break;
                }
            }
        }

        foreach (var constraint in model.Constraints)
        {
            solver.AddClause(EncodeExpr(constraint, V, solver));
        }

        return encoding;
    }

    /// <summary>Variables for a model's features with no constraints at all: the interface view used by the capability layer alone.</summary>
    public static ModelEncoding EncodeFree(FeatureModel model, ISolver solver) =>
        new(model, model.FeatureOrder.ToDictionary(id => id, _ => solver.NewVar()));

    /// <summary>
    /// C_capabilities: <c>c ⇒ (p₁ ∨ … ∨ pₖ)</c> for every required capability, plus the global rules (C_interactions).
    /// A capability counts as provided exactly when one of its providers is selected.
    /// </summary>
    public static void EncodeCapabilities(
        DecentralizedSystem system, ISolver solver, IReadOnlyDictionary<string, ModelEncoding> encodings)
    {
        var providers = new Dictionary<string, List<int>>();
        var requirers = new Dictionary<string, List<int>>();

        foreach (var model in system.Models)
        {
            var encoding = encodings[model.Id];
            foreach (var feature in model.Features)
            {
                foreach (var capability in feature.Provides) Add(providers, capability, encoding.Var(feature.Id));
                foreach (var capability in feature.Requires) Add(requirers, capability, encoding.Var(feature.Id));
            }
        }

        var provided = new Dictionary<string, int>();
        int Provided(string capability)
        {
            if (provided.TryGetValue(capability, out var existing)) return existing;

            var literal = solver.NewVar();
            var list = providers.GetValueOrDefault(capability) ?? [];
            solver.AddClause([-literal, .. list]);
            foreach (var provider in list) solver.AddClause(-provider, literal);
            return provided[capability] = literal;
        }

        foreach (var (capability, requiredBy) in requirers)
        {
            foreach (var feature in requiredBy) solver.AddClause(-feature, Provided(capability));
        }

        foreach (var rule in system.Rules)
        {
            switch (rule)
            {
                case CapabilityMutex m: solver.AddClause(-Provided(m.A), -Provided(m.B)); break;
                case CapabilityImplies i: solver.AddClause(-Provided(i.A), Provided(i.B)); break;
                case SingleProvider s:
                    solver.AddAtMost(1, providers.GetValueOrDefault(s.Capability) ?? []);
                    break;
            }
        }
    }

    /// <summary>Tseitin encoding: returns a literal that is true exactly when the expression holds.</summary>
    public static int EncodeExpr(Expr expr, Func<string, int> varOf, ISolver solver)
    {
        return expr switch
        {
            FeatureRefExpr r => varOf(r.FeatureId),
            NotExpr n => -EncodeExpr(n.Operand, varOf, solver),
            AndExpr a => AndGate(EncodeExpr(a.Left, varOf, solver), EncodeExpr(a.Right, varOf, solver), solver),
            OrExpr o => -AndGate(-EncodeExpr(o.Left, varOf, solver), -EncodeExpr(o.Right, varOf, solver), solver),
            ImpliesExpr i => -AndGate(EncodeExpr(i.Left, varOf, solver), -EncodeExpr(i.Right, varOf, solver), solver),
            IffExpr f => IffGate(EncodeExpr(f.Left, varOf, solver), EncodeExpr(f.Right, varOf, solver), solver),
            _ => throw new InvalidOperationException($"Unknown expression {expr.GetType().Name}."),
        };
    }

    private static int AndGate(int a, int b, ISolver solver)
    {
        var g = solver.NewVar();
        solver.AddClause(-g, a);
        solver.AddClause(-g, b);
        solver.AddClause(g, -a, -b);
        return g;
    }

    private static int IffGate(int a, int b, ISolver solver)
    {
        var g = solver.NewVar();
        solver.AddClause(-g, -a, b);
        solver.AddClause(-g, a, -b);
        solver.AddClause(g, a, b);
        solver.AddClause(g, -a, -b);
        return g;
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }
}

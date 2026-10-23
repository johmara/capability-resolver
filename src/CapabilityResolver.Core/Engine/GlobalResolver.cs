using CapabilityResolver.Model;

namespace CapabilityResolver.Engine;

public enum MissingDiagnosis
{
    /// <summary>A provider exists but none is selected: a configuration problem in the provider's scope.</summary>
    Unprovisioned,
    /// <summary>No feature anywhere provides the capability: a governance gap, not a configuration mistake.</summary>
    Unprovidable,
}

/// <summary>
/// When a required capability is missing, <see cref="NotifyModels"/> is derived from the capability's
/// providers, never from the feature that required it. That is what lets the resolver notify only the
/// scopes that can fix the problem.
/// </summary>
public sealed record MissingCapability(
    string Capability,
    IReadOnlyList<FeatureRef> RequiredBy,
    IReadOnlyList<FeatureRef> PotentialProviders,
    IReadOnlyList<string> NotifyModels,
    MissingDiagnosis Diagnosis);

public sealed record RuleViolation(GlobalRule Rule, IReadOnlyList<FeatureRef> InvolvedProviders);

public sealed record GlobalResult(
    IReadOnlyDictionary<string, LocalValidity> LocalResults,
    CapabilityLedger Ledger,
    IReadOnlyList<MissingCapability> Missing,
    IReadOnlyList<RuleViolation> RuleViolations)
{
    /// <summary>Valid_global = ⋀ᵢ Valid(FMᵢ) ∧ ⋀_{c∈Req} Provided(c) ∧ C_interactions</summary>
    public bool Valid =>
        LocalResults.Values.All(r => r.Valid) && Missing.Count == 0 && RuleViolations.Count == 0;
}

public static class GlobalResolver
{
    public static GlobalResult Resolve(DecentralizedSystem system, Configuration config)
    {
        var localResults = LocalValidator.CheckAll(system, config);
        var ledger = CapabilityLedger.Aggregate(system, config);

        var missing = new List<MissingCapability>();
        foreach (var (capability, requiredBy) in ledger.Required)
        {
            if (ledger.ProvidersOf(capability).Count > 0) continue;

            var potential = ledger.PotentialProviders.GetValueOrDefault(capability) ?? [];
            missing.Add(new(
                capability,
                requiredBy,
                potential,
                potential.Select(p => p.ModelId).Distinct().ToList(),
                potential.Count == 0 ? MissingDiagnosis.Unprovidable : MissingDiagnosis.Unprovisioned));
        }

        var violations = system.Rules
            .Select(rule => CheckRule(rule, ledger))
            .OfType<RuleViolation>()
            .ToList();

        return new(localResults, ledger, missing, violations);
    }

    private static RuleViolation? CheckRule(GlobalRule rule, CapabilityLedger ledger) => rule switch
    {
        CapabilityMutex m when ledger.ProvidersOf(m.A).Count > 0 && ledger.ProvidersOf(m.B).Count > 0
            => new(rule, [.. ledger.ProvidersOf(m.A), .. ledger.ProvidersOf(m.B)]),
        CapabilityImplies i when ledger.ProvidersOf(i.A).Count > 0 && ledger.ProvidersOf(i.B).Count == 0
            => new(rule, ledger.ProvidersOf(i.A)),
        SingleProvider s when ledger.ProvidersOf(s.Capability).Count > 1
            => new(rule, ledger.ProvidersOf(s.Capability)),
        _ => null,
    };

    public static Diagnostic Explain(MissingCapability missing) => missing.Diagnosis switch
    {
        MissingDiagnosis.Unprovidable => new(
            DiagnosticCodes.CapabilityUnprovidable,
            $"No feature anywhere provides \"{missing.Capability}\". This is a capability-governance gap, not a configuration mistake.",
            missing.RequiredBy,
            missing.Capability),
        _ => new(
            DiagnosticCodes.CapabilityMissing,
            $"\"{missing.Capability}\" is required but not currently provided.",
            missing.RequiredBy,
            missing.Capability,
            missing.NotifyModels),
    };

    public static Diagnostic Explain(RuleViolation violation) => new(
        DiagnosticCodes.GlobalRuleViolated,
        $"{violation.Rule.Notation}. {violation.Rule.Rationale}".TrimEnd(),
        violation.InvolvedProviders);
}

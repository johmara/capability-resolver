using CapabilityResolver.Model;

namespace CapabilityResolver.Engine;

/// <summary>Stable machine-readable codes, shared with the TypeScript reference engine.</summary>
public static class DiagnosticCodes
{
    public const string OrphanChild = "E_ORPHAN_CHILD";
    public const string MandatoryUnselected = "E_MANDATORY_UNSELECTED";
    public const string XorNone = "E_XOR_NONE";
    public const string XorMultiple = "E_XOR_MULTIPLE";
    public const string OrNone = "E_OR_NONE";
    public const string CardinalityViolated = "E_CARDINALITY_VIOLATED";
    public const string ConstraintViolated = "E_CONSTRAINT_VIOLATED";
    public const string CapabilityMissing = "E_CAP_MISSING";
    public const string CapabilityUnprovidable = "E_CAP_UNPROVIDABLE";
    public const string GlobalRuleViolated = "E_GLOBAL_RULE_VIOLATED";
}

/// <summary>One finding: what is wrong, which features are involved, and which models should hear about it.</summary>
public sealed record Diagnostic(
    string Code,
    string Detail,
    IReadOnlyList<FeatureRef> Involves,
    string? Capability = null,
    IReadOnlyList<string>? Models = null);

using CapabilityResolver.Model;

namespace CapabilityResolver.Core.Tests;

/// <summary>Small phone-style system: messaging requires a transport that a separately owned network model provides.</summary>
internal static class TestModels
{
    private static IReadOnlySet<string> Caps(params string[] caps) => caps.ToHashSet();

    private static Feature F(string id, IReadOnlyList<Group>? groups = null, string[]? requires = null, string[]? provides = null) =>
        new(id, id, groups ?? Feature.NoGroups, Caps(requires ?? []), Caps(provides ?? []));

    private static Group G(GroupKind kind, params Feature[] children) => new(kind, children);

    public static FeatureModel Messaging() => new("messaging", "Messaging",
        F("Messaging", [
            G(GroupKind.Mandatory, F("Chat", requires: ["net.transport"])),
            G(GroupKind.Optional, F("Encryption", provides: ["crypto.e2e"])),
        ]));

    public static FeatureModel Network() => new("network", "Network",
        F("Network", [
            G(GroupKind.Alternative,
                F("Wifi", provides: ["net.transport"]),
                F("Cellular", provides: ["net.transport", "net.metered"])),
        ]));

    public static FeatureModel Extras() => new("extras", "Extras",
        F("Extras", [
            new Group(GroupKind.Or, [F("A"), F("B"), F("C")]),
            new Group(GroupKind.Cardinality, [F("X"), F("Y"), F("Z")], Min: 1, Max: 2),
        ]),
        constraints: [Expr.Implies(Expr.Ref("A"), Expr.Not(Expr.Ref("X")))]);

    public static DecentralizedSystem Phone(params GlobalRule[] rules) =>
        new([Messaging(), Network()], rules);
}

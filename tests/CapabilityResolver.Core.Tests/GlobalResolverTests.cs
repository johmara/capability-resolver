using CapabilityResolver.Engine;
using CapabilityResolver.Model;

namespace CapabilityResolver.Core.Tests;

public class GlobalResolverTests
{
    [Fact]
    public void RequiredCapabilityWithActiveProviderIsValid()
    {
        var config = Configuration.Of(("messaging", ["Chat"]), ("network", ["Wifi"]));
        var result = GlobalResolver.Resolve(TestModels.Phone(), config);

        Assert.True(result.Valid);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void MissingCapabilityNotifiesProviderScopesNotTheRequirer()
    {
        var config = Configuration.Of(("messaging", ["Chat"]), ("network", []));
        var result = GlobalResolver.Resolve(TestModels.Phone(), config);

        var missing = Assert.Single(result.Missing);
        Assert.Equal("net.transport", missing.Capability);
        Assert.Equal(MissingDiagnosis.Unprovisioned, missing.Diagnosis);
        Assert.Equal(["network"], missing.NotifyModels);
        Assert.Equal([new FeatureRef("messaging", "Chat")], missing.RequiredBy);
        Assert.False(result.Valid);
    }

    [Fact]
    public void CapabilityNobodyProvidesIsUnprovidable()
    {
        var needy = new FeatureModel("needy", "Needy",
            new Feature("Needy", "Needy", [], new HashSet<string> { "ghost.cap" }, Feature.NoCapabilities));
        var result = GlobalResolver.Resolve(new DecentralizedSystem([needy]), new Configuration());

        var missing = Assert.Single(result.Missing);
        Assert.Equal(MissingDiagnosis.Unprovidable, missing.Diagnosis);
        Assert.Empty(missing.NotifyModels);
        Assert.Equal(DiagnosticCodes.CapabilityUnprovidable, GlobalResolver.Explain(missing).Code);
    }

    [Fact]
    public void UnselectedRequirerDoesNotRequireAnything()
    {
        // Chat is mandatory, so locally invalid, but without it nothing requires net.transport.
        var result = GlobalResolver.Resolve(TestModels.Phone(), Configuration.Of(("network", ["Wifi"])));
        Assert.Empty(result.Missing);
        Assert.False(result.Valid);
    }

    [Fact]
    public void LocalInvalidityMakesGlobalInvalid()
    {
        var config = Configuration.Of(("messaging", ["Chat"]), ("network", ["Wifi", "Cellular"]));
        var result = GlobalResolver.Resolve(TestModels.Phone(), config);

        Assert.False(result.LocalResults["network"].Valid);
        Assert.False(result.Valid);
    }

    [Fact]
    public void MutexRuleFiresWhenBothCapabilitiesProvided()
    {
        var rules = new GlobalRule[] { new CapabilityMutex("crypto.e2e", "net.metered", "No E2E over metered links.") };
        var system = TestModels.Phone(rules);

        var both = Configuration.Of(("messaging", ["Chat", "Encryption"]), ("network", ["Cellular"]));
        var violation = Assert.Single(GlobalResolver.Resolve(system, both).RuleViolations);
        Assert.Equal("crypto.e2e ≠ net.metered", violation.Rule.Notation);

        var one = Configuration.Of(("messaging", ["Chat", "Encryption"]), ("network", ["Wifi"]));
        Assert.True(GlobalResolver.Resolve(system, one).Valid);
    }

    [Fact]
    public void ImpliesRuleFiresWhenConsequentMissing()
    {
        var system = TestModels.Phone(new CapabilityImplies("crypto.e2e", "net.metered"));
        var config = Configuration.Of(("messaging", ["Chat", "Encryption"]), ("network", ["Wifi"]));

        Assert.Single(GlobalResolver.Resolve(system, config).RuleViolations);
        Assert.Empty(GlobalResolver.Resolve(system, Configuration.Of(("messaging", ["Chat"]), ("network", ["Wifi"]))).RuleViolations);
    }

    [Fact]
    public void SingleProviderRuleFiresOnSecondProvider()
    {
        var a = new FeatureModel("a", "A", new Feature("A", "A", [], Feature.NoCapabilities, new HashSet<string> { "x" }));
        var b = new FeatureModel("b", "B", new Feature("B", "B", [], Feature.NoCapabilities, new HashSet<string> { "x" }));
        var system = new DecentralizedSystem([a, b], [new SingleProvider("x")]);

        var violation = Assert.Single(GlobalResolver.Resolve(system, new Configuration()).RuleViolations);
        Assert.Equal(2, violation.InvolvedProviders.Count);
        Assert.Equal("x ≤ 1 provider", violation.Rule.Notation);
    }

    [Fact]
    public void DuplicateModelIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new DecentralizedSystem([TestModels.Network(), TestModels.Network()]));
    }
}

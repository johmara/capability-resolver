using CapabilityResolver.Engine;
using CapabilityResolver.Model;

namespace CapabilityResolver.Core.Tests;

public class LocalValidatorTests
{
    private static LocalValidity Check(FeatureModel model, params string[] selected) =>
        LocalValidator.Check(model, selected.ToHashSet());

    [Fact]
    public void RootIsImplicitlySelected()
    {
        Assert.True(Check(TestModels.Messaging(), "Chat").Valid);
    }

    [Fact]
    public void MissingMandatoryChildIsReported()
    {
        var result = Check(TestModels.Messaging());
        var violation = Assert.Single(result.Violations);
        Assert.Equal(DiagnosticCodes.MandatoryUnselected, violation.Code);
    }

    [Fact]
    public void OptionalChildMayBeLeftOut()
    {
        Assert.True(Check(TestModels.Messaging(), "Chat").Valid);
        Assert.True(Check(TestModels.Messaging(), "Chat", "Encryption").Valid);
    }

    [Fact]
    public void AlternativeGroupNeedsExactlyOne()
    {
        var network = TestModels.Network();
        Assert.Equal(DiagnosticCodes.XorNone, Assert.Single(Check(network).Violations).Code);
        Assert.True(Check(network, "Wifi").Valid);
        Assert.Equal(DiagnosticCodes.XorMultiple, Assert.Single(Check(network, "Wifi", "Cellular").Violations).Code);
    }

    [Fact]
    public void OrGroupNeedsAtLeastOne()
    {
        var extras = TestModels.Extras();
        Assert.Contains(Check(extras, "X").Violations, v => v.Code == DiagnosticCodes.OrNone);
        Assert.True(Check(extras, "B", "C", "X").Valid);
    }

    [Fact]
    public void CardinalityGroupEnforcesBounds()
    {
        var extras = TestModels.Extras();
        Assert.Contains(Check(extras, "B").Violations, v => v.Code == DiagnosticCodes.CardinalityViolated);
        Assert.Contains(Check(extras, "B", "X", "Y", "Z").Violations, v => v.Code == DiagnosticCodes.CardinalityViolated);
        Assert.True(Check(extras, "B", "X", "Y").Valid);
    }

    [Fact]
    public void LocalConstraintsAreEnforced()
    {
        var violation = Assert.Single(Check(TestModels.Extras(), "A", "X").Violations);
        Assert.Equal(DiagnosticCodes.ConstraintViolated, violation.Code);
    }

    [Fact]
    public void ChildWithoutParentIsAnOrphan()
    {
        var model = new FeatureModel("m", "M",
            new Feature("Root", "Root", [new Group(GroupKind.Optional, [
                new Feature("P", "P", [new Group(GroupKind.Optional, [Feature.Leaf("C")])], Feature.NoCapabilities, Feature.NoCapabilities)])],
                Feature.NoCapabilities, Feature.NoCapabilities));

        var violation = Assert.Single(Check(model, "C").Violations);
        Assert.Equal(DiagnosticCodes.OrphanChild, violation.Code);
    }

    [Fact]
    public void ConstraintOnForeignFeatureIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new FeatureModel("m", "M", Feature.Leaf("Root"),
            [Expr.Implies(Expr.Ref("Root"), Expr.Ref("network::Wifi"))]));
    }

    [Fact]
    public void DuplicateFeatureIdsAreRejected()
    {
        var root = new Feature("Root", "Root", [new Group(GroupKind.Optional, [Feature.Leaf("A"), Feature.Leaf("A")])],
            Feature.NoCapabilities, Feature.NoCapabilities);
        Assert.Throws<ArgumentException>(() => new FeatureModel("m", "M", root));
    }
}

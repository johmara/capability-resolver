using CapabilityResolver.Analysis;
using CapabilityResolver.Model;
using CapabilityResolver.Solvers.OrTools;

namespace CapabilityResolver.Analysis.Tests;

public class ModelAnalysisTests
{
    private static readonly Analyzer Analyzer = new(OrToolsSolver.Create);

    public static IEnumerable<object[]> Models() =>
        Fixtures.Phone().Models.Concat(SmallModels.All()).Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Models))]
    public void SatisfiabilityMatchesEnumeration(FeatureModel model)
    {
        Assert.Equal(BruteForce.ValidConfigs(model).Count > 0, Analyzer.IsSatisfiable(model));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void DeadAndCoreFeaturesMatchEnumeration(FeatureModel model)
    {
        var valid = BruteForce.ValidConfigs(model);
        var expectedDead = model.FeatureOrder.Where(id => valid.All(c => !c.Contains(id))).ToHashSet();
        var expectedCore = valid.Count == 0
            ? []
            : model.FeatureOrder.Where(id => valid.All(c => c.Contains(id))).ToHashSet();

        // An unsatisfiable model has no configurations, so "dead" is reported as empty rather than everything.
        Assert.Equal(valid.Count == 0 ? [] : expectedDead, Analyzer.DeadFeatures(model));
        Assert.Equal(expectedCore, Analyzer.CoreFeatures(model));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void PropagationMatchesEnumerationForEveryPartialSelection(FeatureModel model)
    {
        var valid = BruteForce.ValidConfigs(model);
        var others = model.FeatureOrder.Where(id => id != model.Root.Id).ToList();

        foreach (var manual in BruteForce.AllSubsets(others))
        {
            var withRoot = new HashSet<string>(manual) { model.Root.Id };
            var completions = valid.Where(c => withRoot.IsSubsetOf(c)).ToList();
            var actual = Analyzer.Propagate(model, manual);

            Assert.Equal(completions.Count > 0, actual.Consistent);
            if (completions.Count == 0) continue;

            var forced = model.FeatureOrder.Where(id => completions.All(c => c.Contains(id))).ToHashSet();
            var dead = model.FeatureOrder.Where(id => completions.All(c => !c.Contains(id))).ToHashSet();
            Assert.Equal(forced, actual.Selection);
            Assert.Equal(forced.Where(id => !withRoot.Contains(id)).ToHashSet(), actual.Derived);
            Assert.Equal(dead, actual.Dead);
        }
    }

    [Fact]
    public void SelectingALayoutForcesItsMandatoryAncestorsAndKillsTheAlternatives()
    {
        var keyboard = Fixtures.Phone().Model("phone.keyboard");

        var result = Analyzer.Propagate(keyboard, new HashSet<string> { "QWERTY" });

        Assert.True(result.Consistent);
        Assert.Equal(new HashSet<string> { "Layouts", "BaseKeyboard" }, result.Derived);
        Assert.Equal(new HashSet<string> { "AZERTY", "QWERTZ" }, result.Dead);
    }

    [Fact]
    public void ContradictorySelectionIsInconsistent()
    {
        var keyboard = Fixtures.Phone().Model("phone.keyboard");

        var result = Analyzer.Propagate(keyboard, new HashSet<string> { "QWERTY", "AZERTY" });

        Assert.False(result.Consistent);
        Assert.Empty(result.Derived);
    }

    [Fact]
    public void UnknownFeatureIdsInThePartialSelectionAreIgnored()
    {
        var keyboard = Fixtures.Phone().Model("phone.keyboard");
        Assert.True(Analyzer.Propagate(keyboard, new HashSet<string> { "Nonexistent" }).Consistent);
    }
}

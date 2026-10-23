using CapabilityResolver.Analysis;
using CapabilityResolver.Model;
using CapabilityResolver.Solvers.OrTools;

namespace CapabilityResolver.Analysis.Tests;

public class SystemAnalysisTests
{
    private static readonly Analyzer Analyzer = new(OrToolsSolver.Create);
    private static readonly DecentralizedSystem Phone = Fixtures.Phone();
    private static readonly IReadOnlyList<Configuration> ValidGlobal = BruteForce.ValidGlobalConfigs(Phone);

    [Fact]
    public void PhoneHasGloballyValidConfigurations()
    {
        Assert.NotEmpty(ValidGlobal);
    }

    [Fact]
    public void CanCompleteMatchesEnumerationForRandomPartialConfigurations()
    {
        var random = new Random(42);
        for (var round = 0; round < 150; round++)
        {
            var partial = new Configuration(Phone.Models.Select(m => KeyValuePair.Create(
                m.Id,
                (IReadOnlySet<string>)m.FeatureOrder.Where(id => random.Next(5) == 0).ToHashSet())));

            var completions = ValidGlobal
                .Where(c => Phone.Models.All(m => LocalWithRoot(m, partial).IsSubsetOf(LocalWithRoot(m, c))))
                .ToList();
            var actual = Analyzer.CanComplete(Phone, partial);

            Assert.Equal(completions.Count > 0, actual.Consistent);
            if (completions.Count == 0) continue;

            foreach (var model in Phone.Models)
            {
                var forced = model.FeatureOrder
                    .Where(id => completions.All(c => LocalWithRoot(model, c).Contains(id))).ToHashSet();
                var dead = model.FeatureOrder
                    .Where(id => completions.All(c => !LocalWithRoot(model, c).Contains(id))).ToHashSet();
                Assert.Equal(forced, actual.ByModel[model.Id].Selection);
                Assert.Equal(dead, actual.ByModel[model.Id].Dead);
            }
        }
    }

    [Fact]
    public void RequirementsForceFeaturesInOtherScopes()
    {
        // Comic Email needs EmojiRendering, which only Keyboard's EmojiSupport provides.
        var partial = Configuration.Of(("phone.messaging", ["ComicEmail"]));

        var result = Analyzer.CanComplete(Phone, partial);

        Assert.True(result.Consistent);
        Assert.Contains("EmojiSupport", result.ByModel["phone.keyboard"].Derived);
        Assert.Contains("Email", result.ByModel["phone.messaging"].Derived);
    }

    [Fact]
    public void MutexRuleMakesTheCombinationImpossible()
    {
        // Encrypted messaging needs SecureStorage, but Legacy Storage provides InsecureStorage, which is mutex with it.
        var partial = Configuration.Of(
            ("phone.messaging", ["EncryptedMessaging"]),
            ("phone.storage", ["LegacyStorage"]));

        Assert.False(Analyzer.CanComplete(Phone, partial).Consistent);
    }

    [Fact]
    public void SuggestsTheSingleProviderScopeForMissingInternet()
    {
        var config = Configuration.Of(("phone.messaging", ["Email"]));

        var suggestion = Analyzer.SuggestProviders(Phone, config, "InternetConnectivity");

        Assert.NotNull(suggestion);
        var provider = Assert.Single(suggestion.Add, a => a.ModelId == "phone.connectivity");
        Assert.Contains(provider.FeatureId, new[] { "MobileData", "Wifi" });
        Assert.Contains("phone.connectivity", suggestion.NotifyModels);
    }

    [Fact]
    public void SuggestionIsMinimalAndYieldsAValidSystemProvidingTheCapability()
    {
        var config = Configuration.Of(("phone.messaging", ["Email", "ComicEmail"]));

        var suggestion = Analyzer.SuggestProviders(Phone, config, "EmojiRendering");

        Assert.NotNull(suggestion);

        var added = suggestion.Add.Select(a => (a.ModelId, a.FeatureId)).ToHashSet();
        var expectedMinimum = ValidGlobal
            .Where(c => Phone.Models.All(m => LocalWithRoot(m, config).IsSubsetOf(LocalWithRoot(m, c))))
            .Where(c => Provides(c, "EmojiRendering"))
            .Min(c => Phone.Models.Sum(m => LocalWithRoot(m, c).Except(LocalWithRoot(m, config)).Count()));
        Assert.Equal(expectedMinimum, added.Count);

        var completed = new Configuration(Phone.Models.Select(m => KeyValuePair.Create(
            m.Id,
            (IReadOnlySet<string>)LocalWithRoot(m, config).Concat(added.Where(a => a.ModelId == m.Id).Select(a => a.FeatureId)).ToHashSet())));
        Assert.True(Engine.GlobalResolver.Resolve(Phone, completed).Valid);
        Assert.True(Provides(completed, "EmojiRendering"));
    }

    [Fact]
    public void NoSuggestionWhenNobodyProvidesTheCapability()
    {
        Assert.Null(Analyzer.SuggestProviders(Phone, new Configuration(), "NoSuchCapability"));
    }

    [Fact]
    public void NoSuggestionWhenTheProviderConflictsWithTheCurrentConfiguration()
    {
        var config = Configuration.Of(("phone.storage", ["LegacyStorage"]));
        Assert.Null(Analyzer.SuggestProviders(Phone, config, "SecureStorage"));
    }

    [Fact]
    public void PhoneIsSatisfiableInEveryDecomposedPart()
    {
        var result = Analyzer.AnalyzeDecomposed(Phone);

        Assert.True(result.AllSatisfiable);
        Assert.Equal(Phone.Models.Count, result.ModelsSatisfiable.Count);
    }

    [Fact]
    public void CapabilityLayerAloneCanBeUnsatisfiable()
    {
        // The root is always selected, so this model always provides both capabilities the rule forbids together.
        var root = new Feature("R", "R", [], Feature.NoCapabilities, new HashSet<string> { "a", "b" });
        var system = new DecentralizedSystem([new FeatureModel("m", "M", root)], [new CapabilityMutex("a", "b")]);

        var result = Analyzer.AnalyzeDecomposed(system);

        Assert.True(result.ModelsSatisfiable["m"]);
        Assert.False(result.CapabilityLayerSatisfiable);
        Assert.False(result.AllSatisfiable);
    }

    private static HashSet<string> LocalWithRoot(FeatureModel model, Configuration config) =>
        new(config.For(model.Id)) { model.Root.Id };

    private static bool Provides(Configuration config, string capability) =>
        Phone.Models.Any(m => m.Features.Any(f =>
            f.Provides.Contains(capability) && LocalWithRoot(m, config).Contains(f.Id)));
}

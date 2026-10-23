using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Tests;

/// <summary>
/// The phone example from the paper (Fig. 2), loaded from per-scope files, resolved with the same
/// presets as the companion site's <c>phone.example.ts</c>. The site derives forced features by
/// propagation; these tests pass explicit, complete selections instead.
/// </summary>
public class PhoneFixtureTests
{
    private static string Dir(string format) => Path.Combine(AppContext.BaseDirectory, "fixtures", "phone", format);

    private static readonly ModelLoader Loader = new(new JsonModelReader(), new YamlModelReader());

    private static DecentralizedSystem LoadPhone(string format)
    {
        var rulesFile = Directory.EnumerateFiles(Dir(format), "rules.*").Single();
        return Loader.LoadSystem([Dir(format)], RulesReader.Load(rulesFile));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void LoadsOneModelPerScope(string format)
    {
        var system = LoadPhone(format);

        Assert.Equal(
            ["phone.connectivity", "phone.keyboard", "phone.messaging", "phone.storage"],
            system.Models.Select(m => m.Id).Order());
        Assert.Single(system.Rules);
    }

    [Fact]
    public void YamlAndJsonDescribeTheSameModels()
    {
        var json = LoadPhone("json").Models.OrderBy(m => m.Id).Select(ModelDump.ToText);
        var yaml = LoadPhone("yaml").Models.OrderBy(m => m.Id).Select(ModelDump.ToText);

        Assert.Equal(json, yaml);
    }

    [Fact]
    public void WriterRoundTripsEveryModel()
    {
        foreach (var model in LoadPhone("json").Models)
        {
            var again = FmJson.ParseModel(FmJson.ToJson(model), "roundtrip");
            Assert.Equal(ModelDump.ToText(model), ModelDump.ToText(again));
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void ComicEmailIsGloballyValid(string format)
    {
        var config = Configuration.Of(
            ("phone.messaging", ["Email", "ComicEmail"]),
            ("phone.connectivity", ["MobileData"]),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY", "EmojiSupport"]),
            ("phone.storage", ["LegacyStorage"]));

        var result = GlobalResolver.Resolve(LoadPhone(format), config);

        Assert.True(result.Valid);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void MissingInternetNotifiesOnlyConnectivity(string format)
    {
        var config = Configuration.Of(
            ("phone.messaging", ["Email", "ComicEmail"]),
            ("phone.connectivity", []),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY", "EmojiSupport"]),
            ("phone.storage", ["LegacyStorage"]));

        var result = GlobalResolver.Resolve(LoadPhone(format), config);

        var missing = Assert.Single(result.Missing);
        Assert.Equal("InternetConnectivity", missing.Capability);
        Assert.Equal(["phone.connectivity"], missing.NotifyModels);
        Assert.False(result.Valid);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void EncryptedMessagingOverLegacyStorageViolatesTheMutexRule(string format)
    {
        var config = Configuration.Of(
            ("phone.messaging", ["Email", "EncryptedMessaging"]),
            ("phone.connectivity", ["MobileData"]),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY"]),
            ("phone.storage", ["LegacyStorage", "EncryptedVolume"]));

        var result = GlobalResolver.Resolve(LoadPhone(format), config);

        Assert.Empty(result.Missing);
        var violation = Assert.Single(result.RuleViolations);
        Assert.Equal("SecureStorage ≠ InsecureStorage", violation.Rule.Notation);
        Assert.False(result.Valid);
    }

    [Fact]
    public void UnknownRelationTypeNamesTheSourceFile()
    {
        var reader = new JsonModelReader();
        var text = """{ "features": { "name": "R", "relations": [ { "type": "SOMETIMES", "children": [] } ] } }""";

        var e = Assert.Throws<ModelFormatException>(() => reader.Read(text, "bad.fm.json"));
        Assert.Equal("bad.fm.json", e.SourceName);
        Assert.Contains("SOMETIMES", e.Message);
    }

    [Fact]
    public void ConstraintOnUnknownFeatureIsRejected()
    {
        var text = """{ "features": { "name": "R" }, "constraints": ["R => Ghost"] }""";
        Assert.Throws<ModelFormatException>(() => new JsonModelReader().Read(text, "bad.fm.json"));
    }

    [Fact]
    public void MalformedYamlIsReportedAsModelFormatError()
    {
        Assert.Throws<ModelFormatException>(() => new YamlModelReader().Read("features: [unclosed", "bad.fm.yaml"));
    }

    [Fact]
    public void ConstraintsAndCardinalityGroupsSurviveYamlAndJson()
    {
        const string yaml = """
            name: Extras
            features:
              name: Extras
              relations:
                - type: CARDINALITY
                  card_min: 1
                  card_max: 2
                  children: [{ name: X }, { name: Y }, { name: Z }]
            constraints:
              - X => !Y
            """;

        var model = new YamlModelReader().Read(yaml, "extras.fm.yaml");
        var group = Assert.Single(model.Root.Groups);
        Assert.Equal((1, 2), (group.Min, group.Max));
        Assert.False(LocalValidator.Check(model, new HashSet<string> { "X", "Y" }).Valid);
        Assert.True(LocalValidator.Check(model, new HashSet<string> { "X", "Z" }).Valid);
    }
}

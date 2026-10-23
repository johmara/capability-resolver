using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Formats.Uvl;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Tests;

public class UvlTests
{
    private static readonly UvlModelReader Reader = new();

    private static FeatureModel Read(string text) => Reader.Read(text, "test.uvl");

    private static ModelFormatException Fails(string text) =>
        Assert.Throws<ModelFormatException>(() => Reader.Read(text, "test.uvl"));

    [Fact]
    public void PhoneModelsAreStructurallyIdenticalToTheJsonOnes()
    {
        var loader = new ModelLoader(new JsonModelReader(), new UvlModelReader());
        var uvl = loader.LoadSystem([PhoneFixtures.Dir("uvl")]).Models.OrderBy(m => m.Id).Select(ModelDump.ToStructureText);
        var json = loader.LoadSystem([PhoneFixtures.Dir("json")]).Models.OrderBy(m => m.Id).Select(ModelDump.ToStructureText);

        Assert.Equal(json, uvl);
    }

    [Theory]
    [InlineData("comic-email", true)]
    [InlineData("missing-internet", false)]
    public void PhonePresetsResolveLikeTheJsonFixtures(string preset, bool valid)
    {
        var system = PhoneFixtures.Load("uvl");
        var connectivity = preset == "comic-email" ? new[] { "MobileData" } : [];
        var config = Configuration.Of(
            ("phone.messaging", ["Email", "ComicEmail"]),
            ("phone.connectivity", connectivity),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY", "EmojiSupport"]),
            ("phone.storage", ["LegacyStorage"]));

        Assert.Equal(valid, GlobalResolver.Resolve(system, config).Valid);
    }

    [Fact]
    public void ReadsGroupsCardinalitiesAttributesAndConstraints()
    {
        var model = Read("""
            // a comment
            namespace shop.checkout

            include
                Boolean.group-cardinality

            features
                Checkout {abstract, provides ['order.place'], requires ['pay.any', 'net'], cost 5, tags ['a', 'b']}
                    [1..2]
                        Card
                        Invoice
                    mandatory
                        "Order Summary"
                    alternative
                        Standard
                        Express

            constraints
                Card => !Invoice
                "Order Summary" | Express
            """);

        Assert.Equal("shop.checkout", model.Id);
        Assert.Equal("Checkout", model.Name);
        Assert.True(model.Root.IsAbstract);
        Assert.Equal(new[] { "order.place" }, model.Root.Provides);
        Assert.Equal(new[] { "net", "pay.any" }, model.Root.Requires.Order());

        Assert.Equal(
            [GroupKind.Cardinality, GroupKind.Mandatory, GroupKind.Alternative],
            model.Root.Groups.Select(g => g.Kind));
        var cardinality = model.Root.Groups[0];
        Assert.Equal((1, 2), (cardinality.Min, cardinality.Max));
        Assert.True(model.Contains("Order Summary"));
        Assert.Equal(2, model.Constraints.Count);
    }

    [Fact]
    public void SingleStringCapabilityIsAccepted()
    {
        var model = Read("features\n    A {provides 'x'}");
        Assert.Equal(new[] { "x" }, model.Root.Provides);
    }

    [Fact]
    public void WithoutANamespaceTheRootNameIsTheModelId()
    {
        Assert.Equal("Solo", Read("features\n    Solo").Id);
    }

    [Fact]
    public void TabsAndSpacesBothIndent()
    {
        var model = Read("features\n\tRoot\n\t\tmandatory\n\t\t\tChild");
        Assert.True(model.Contains("Child"));
    }

    [Fact]
    public void CommentsInsideQuotedStringsAreKept()
    {
        var model = Read("features\n    A {provides ['a//b']}");
        Assert.Equal(new[] { "a//b" }, model.Root.Provides);
    }

    [Theory]
    [InlineData("imports\n    other.Model as o\nfeatures\n    A", "imports are not supported")]
    [InlineData("include\n    Arithmetic\nfeatures\n    A", "language level")]
    [InlineData("features\n    A\n        sometimes\n            B", "line 3")]
    [InlineData("features\n    A\n        or\n            B\n          C", "line 5")]
    [InlineData("features\n    A\n        or", "line 3")]
    [InlineData("features\n    A\n    B", "line 3")]
    [InlineData("features\n    String A", "typed features")]
    [InlineData("features\n    A cardinality [1..2]", "feature cardinality")]
    [InlineData("features\n    A {requires ['x'}", "line 2")]
    [InlineData("features\n    A {requires [1]}", "line 2")]
    [InlineData("features\n    A\nconstraints\n    A =>", "line 4")]
    [InlineData("features\n    A\nconstraints\n    A => Ghost", "unknown feature")]
    [InlineData("features\n    A\n        or\n            B\n            B", "Duplicate feature")]
    [InlineData("nonsense", "line 1")]
    [InlineData("namespace x", "no features")]
    public void RejectsUnsupportedOrMalformedInput(string text, string expectedMessagePart)
    {
        Assert.Contains(expectedMessagePart, Fails(text).Message);
    }

    [Fact]
    public void NonStringCapabilityListsAreRejected()
    {
        Assert.Contains("list of strings", Fails("features\n    A {requires [true, 'x']}").Message);
    }

    [Fact]
    public void LeadingBlankAndCommentLinesAreIgnored()
    {
        Assert.Equal("ns", Read("\n// one\n\n// two\nnamespace ns\nfeatures\n    A").Id);
    }

    [Fact]
    public void ErrorsNameTheSourceAndLine()
    {
        var e = Fails("features\n    A\n        bogus\n            B");
        Assert.Equal("test.uvl", e.SourceName);
        Assert.Contains("line 3", e.Message);
    }
}

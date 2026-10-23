using CapabilityResolver.Engine;
using CapabilityResolver.Formats.Clafer;
using CapabilityResolver.Formats.Json;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Tests;

public class ClaferTests
{
    private static readonly ClaferModelReader Reader = new();

    private static FeatureModel Read(string text, string source = "test.cfr") => Reader.Read(text, source);

    private static ModelFormatException Fails(string text) =>
        Assert.Throws<ModelFormatException>(() => Reader.Read(text, "test.cfr"));

    private static readonly ModelLoader Loader = new(new JsonModelReader(), new ClaferModelReader());

    [Fact]
    public void ModelsWithoutMixedGroupsAreStructurallyIdenticalToTheJsonOnes()
    {
        var json = Loader.LoadSystem([PhoneFixtures.Dir("json")]).Models.ToDictionary(m => m.Id);
        var clafer = Loader.LoadSystem([PhoneFixtures.Dir("clafer")]).Models.ToDictionary(m => m.Id);

        Assert.Equal(json.Keys.Order(), clafer.Keys.Order());
        foreach (var id in new[] { "phone.connectivity", "phone.keyboard", "phone.storage" })
        {
            Assert.Equal(ModelDump.ToStructureText(json[id]), ModelDump.ToStructureText(clafer[id]));
        }
    }

    [Fact]
    public void MessagingNeedsAnIntermediateClaferForItsOrGroup()
    {
        var messaging = Loader.LoadSystem([PhoneFixtures.Dir("clafer")]).Model("phone.messaging");

        var type = messaging["MessageType"];
        Assert.Equal(GroupKind.Or, Assert.Single(type.Groups).Kind);
        Assert.Equal(["SMS", "Email"], type.Groups[0].Children.Select(c => c.Id));
        Assert.Equal(["MessageType"], messaging.Root.Groups[0].Children.Select(c => c.Id));
        Assert.Equal(["EncryptedMessaging"], messaging.Root.Groups[1].Children.Select(c => c.Id));
    }

    [Fact]
    public void ClaferPhoneResolvesTheSamePresetsAsJson()
    {
        var system = new DecentralizedSystem(
            Loader.LoadSystem([PhoneFixtures.Dir("clafer")]).Models,
            PhoneFixtures.Load("json").Rules);

        var valid = Configuration.Of(
            ("phone.messaging", ["MessageType", "Email", "ComicEmail"]),
            ("phone.connectivity", ["MobileData"]),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY", "EmojiSupport"]),
            ("phone.storage", ["LegacyStorage"]));
        Assert.True(GlobalResolver.Resolve(system, valid).Valid);

        var noInternet = Configuration.Of(
            ("phone.messaging", ["MessageType", "Email", "ComicEmail"]),
            ("phone.connectivity", []),
            ("phone.keyboard", ["BaseKeyboard", "Layouts", "QWERTY", "EmojiSupport"]),
            ("phone.storage", ["LegacyStorage"]));
        var missing = Assert.Single(GlobalResolver.Resolve(system, noInternet).Missing);
        Assert.Equal(["phone.connectivity"], missing.NotifyModels);
    }

    [Fact]
    public void ModelIdIsTheFileNameWithoutExtension()
    {
        Assert.Equal("my.scope", Read("A", "/some/dir/my.scope.cfr").Id);
    }

    [Fact]
    public void ReadsEveryGroupKindAndInstanceCardinality()
    {
        var model = Read("""
            Root
                xor Alt
                    A1
                    A2
                or Any
                    O1
                    O2
                mux Zero
                    M1
                    M2
                opt Free
                    F1
                1..2 Some
                    S1
                    S2
                    S3
                Maybe ?
                Glued?
                Zero2 0..1
                One 1
            """);

        Group GroupOf(string id) => Assert.Single(model[id].Groups);

        Assert.Equal(GroupKind.Alternative, GroupOf("Alt").Kind);
        Assert.Equal(GroupKind.Or, GroupOf("Any").Kind);
        Assert.Equal((GroupKind.Cardinality, 0, 1), (GroupOf("Zero").Kind, GroupOf("Zero").Min, GroupOf("Zero").Max));
        Assert.Equal(GroupKind.Optional, GroupOf("Free").Kind);
        Assert.Equal((GroupKind.Cardinality, 1, 2), (GroupOf("Some").Kind, GroupOf("Some").Min, GroupOf("Some").Max));

        Assert.Equal(GroupKind.Mandatory, model.Root.Groups[0].Kind);
        Assert.Equal(["Alt", "Any", "Zero", "Free", "Some", "One"], model.Root.Groups[0].Children.Select(c => c.Id));
        Assert.Equal(GroupKind.Optional, model.Root.Groups[1].Kind);
        Assert.Equal(["Maybe", "Glued", "Zero2"], model.Root.Groups[1].Children.Select(c => c.Id));
    }

    [Fact]
    public void ReadsAbstractCapabilitiesAndConstraints()
    {
        var model = Read("""
            /* block
               comment */
            abstract Shop
                provides -> string = "order.place"
                provides -> string = "order.track"
                requires -> string = "pay"
                Card ?
                Invoice ?
                Cart
                [ Card => !Invoice ]
            [ Invoice && Cart || !Card ]
            [ Shop.Card => this.Cart ]
            [ some Cart ]
            [ no Invoice ]
            """);

        Assert.True(model.Root.IsAbstract);
        Assert.Equal(new[] { "order.place", "order.track" }, model.Root.Provides.Order());
        Assert.Equal(new[] { "pay" }, model.Root.Requires);
        Assert.Equal(
            ["(Card => !Invoice)", "((Invoice & Cart) | !Card)", "(Card => Cart)", "Cart", "!Invoice"],
            model.Constraints.Select(c => c.ToString()));
    }

    [Theory]
    [InlineData("A : B", "inheritance")]
    [InlineData("A -> B", "references")]
    [InlineData("A\n    B *", "multiple instances")]
    [InlineData("A\n    B +", "multiple instances")]
    [InlineData("A\n    B 2..3", "not supported")]
    [InlineData("A\nB", "only one root")]
    [InlineData("xor A", "no children")]
    // A dedent to a column that matches no open level is accepted by the official layout rule: C starts a second root.
    [InlineData("A\n    B\n  C", "only one root")]
    [InlineData("A\n    [ B ", "line 2")]
    [InlineData("A\n    [ B in C ]", "set membership is not supported")]
    [InlineData("A\n    [ one B ]", "quantifier 'one' is not supported")]
    [InlineData("A\n    [ B + C ]", "arithmetic and comparisons is not supported")]
    [InlineData("enum E = P | Q", "enum declarations")]
    [InlineData("A\n    provides -> string", "line 2")]
    [InlineData("A\n    provides -> integer = \"x\"", "must be written as")]
    [InlineData("A\n    requires -> string = \"x\"\n        B", "must be written as")]
    [InlineData("A\n    [ Ghost ]", "unknown feature")]
    [InlineData("A\n    B\n    B", "Duplicate feature")]
    [InlineData("A b!", "line 1")]
    [InlineData("", "no clafer")]
    public void RejectsUnsupportedOrMalformedInput(string text, string expectedMessagePart)
    {
        Assert.Contains(expectedMessagePart, Fails(text).Message);
    }

    [Fact]
    public void ConstraintsMayContinueOnFollowingLinesInsideTheBrackets()
    {
        var model = Read("A\n    B ?\n    C ?\n    [ B =>\n        C ]");
        Assert.Equal(["(B => C)"], model.Constraints.Select(c => c.ToString()));
    }

    [Fact]
    public void SeveralExpressionsInOneBracketAreSeveralConstraints()
    {
        var model = Read("A\n    B ?\n    C ?\n    [ B => C  C => B ]");
        Assert.Equal(["(B => C)", "(C => B)"], model.Constraints.Select(c => c.ToString()));
    }

    [Fact]
    public void TabsIndentLikeSpaces()
    {
        var model = Read("A\n\tB\n\t\tC ?\n\tD ?");
        Assert.Equal(["B"], model.Root.Groups[0].Children.Select(c => c.Id));
        Assert.Equal(["D"], model.Root.Groups[1].Children.Select(c => c.Id));
        Assert.True(model.Contains("C"));
    }

    [Fact]
    public void EscapedQuotesInCapabilityStringsAreUnescaped()
    {
        var model = Read("A\n    provides -> string = \"say \\\"hi\\\"\"");
        Assert.Equal(new[] { "say \"hi\"" }, model.Root.Provides);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("U")]
    [InlineData("W")]
    [InlineData("F")]
    [InlineData("G")]
    [InlineData("next")]
    [InlineData("in")]
    public void ReservedWordsOfTheClaferGrammarCannotBeFeatureNames(string name)
    {
        // Single capitals are the LTL operators and reserved in the official grammar, as are its keywords.
        Assert.Contains("line 2", Fails($"A\n    {name}").Message);
    }

    [Fact]
    public void ErrorsNameTheSourceAndLine()
    {
        var e = Fails("A\n    B\n    C : D");
        Assert.Equal("test.cfr", e.SourceName);
        Assert.Contains("line 3", e.Message);
    }
}
